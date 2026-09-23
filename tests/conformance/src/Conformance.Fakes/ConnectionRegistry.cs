namespace Conformance.Fakes;

/// <summary>
/// Tracks every <see cref="FakeRealtimeConnection"/> a <see cref="FakeRealtimeUpstreamServer"/>
/// has accepted, with an async-signalled wait so tests can block on "the next connection this
/// call causes" or "no connections are open" without polling or sleeping — mirrors the
/// TaskCompletionSource-swap pattern in <see cref="FrameLog"/>.
/// </summary>
internal sealed class ConnectionRegistry
{
    private readonly List<FakeRealtimeConnection> _connections = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConnectionRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public FakeRealtimeConnection Add(string? apiKeyHeader, string? modelQueryParam)
    {
        var connection = new FakeRealtimeConnection(apiKeyHeader, modelQueryParam, _timeProvider);
        Signal(() => _connections.Add(connection));
        return connection;
    }

    public void NotifyClosed(FakeRealtimeConnection connection) => Signal(connection.MarkClosed);

    /// <summary>Number of accepted connections whose socket loop hasn't exited yet.</summary>
    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return CountOpenUnlocked();
            }
        }
    }

    /// <summary>
    /// Waits for the next connection accepted after this call is made (not one already open when
    /// called), returning it as soon as its socket handshake completes. Returns null on timeout.
    /// </summary>
    public async Task<FakeRealtimeConnection?> WaitForNextAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        int baseline;
        lock (_gate)
        {
            baseline = _connections.Count;
        }
        return await WaitUntilAsync(
            () => _connections.Count > baseline ? _connections[baseline] : null,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until no accepted connection has an open socket loop. Returns false on timeout —
    /// tests should treat that as "a prior test leaked an open connection" and fail loudly.
    /// </summary>
    public async Task<bool> WaitForNoneOpenAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await WaitUntilAsync(
            () => CountOpenUnlocked() == 0 ? (bool?)true : null,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return result ?? false;
    }

    private int CountOpenUnlocked() => _connections.Count(c => !c.IsClosed);

    private void Signal(Action mutate)
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            mutate();
            released = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        released.TrySetResult();
    }

    /// <summary>
    /// Polls <paramref name="tryGetResult"/> once per new-arrival signal (never on a fixed
    /// interval). <paramref name="tryGetResult"/> must be called while <see cref="_gate"/> is
    /// already held — it is invoked from inside the lock below, not by the caller.
    /// </summary>
    private async Task<T?> WaitUntilAsync<T>(Func<T?> tryGetResultUnderLock, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            Task signalTask;
            lock (_gate)
            {
                var result = tryGetResultUnderLock();
                if (result is not null)
                {
                    return result;
                }
                signalTask = _signal.Task;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return default;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var delayTask = Task.Delay(remaining, _timeProvider, cancellationToken);
            var completed = await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
            if (completed != signalTask)
            {
                return default;
            }
        }
    }
}
