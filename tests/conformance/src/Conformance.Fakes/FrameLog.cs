using System.Text.Json;

namespace Conformance.Fakes;

/// <summary>
/// Thread-safe, append-only log of every frame a fake has received on a socket, with an
/// async-signalled wait so tests can block on "a frame matching X arrives" without polling
/// or sleeping — a new arrival releases every waiter immediately via a replaced
/// <see cref="TaskCompletionSource"/> gate.
/// </summary>
public sealed class FrameLog
{
    private readonly List<RecordedFrame> _frames = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FrameLog(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Records a raw JSON frame, deriving its "type" field for convenient matching.</summary>
    public RecordedFrame Add(JsonElement json)
    {
        var type = json.ValueKind == JsonValueKind.Object &&
                    json.TryGetProperty("type", out var typeProp) &&
                    typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString() ?? ""
            : "";

        TaskCompletionSource released;
        RecordedFrame frame;
        lock (_gate)
        {
            frame = new RecordedFrame(_frames.Count, type, json, _timeProvider.GetUtcNow());
            _frames.Add(frame);
            released = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        released.TrySetResult();
        return frame;
    }

    /// <summary>Immutable point-in-time copy of every frame recorded so far.</summary>
    public IReadOnlyList<RecordedFrame> Snapshot()
    {
        lock (_gate)
        {
            return [.. _frames];
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>
    /// Awaits the first already-recorded or future frame matching <paramref name="predicate"/>.
    /// Reacts immediately to new arrivals (no fixed-interval polling); returns null on timeout
    /// or cancellation rather than throwing, so callers can assert with a clear message.
    /// </summary>
    public async Task<RecordedFrame?> WaitForAsync(
        Func<RecordedFrame, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            Task signalTask;
            lock (_gate)
            {
                var match = _frames.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
                signalTask = _signal.Task;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var delayTask = Task.Delay(remaining, _timeProvider, cancellationToken);
            var completed = await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
            if (completed != signalTask)
            {
                return null;
            }
        }
    }
}
