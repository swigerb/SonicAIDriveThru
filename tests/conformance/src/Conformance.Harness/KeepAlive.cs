using System.Net.WebSockets;

namespace Conformance.Harness;

/// <summary>
/// PR #52 CI follow-up round 5 (Rick's PR #52 review, S2): extracted from three previously
/// hand-duplicated copies (<c>ResumeRehydrationClientVisibilityTests</c>,
/// <c>WholeSessionLeakTests</c>, and <c>BrowserClientLifecycleTests</c>'
/// <c>Keepalive_style_loop_survives_the_peer_aborting_mid_loop</c> self-test) into one shared
/// implementation. Before this, each copy had its own <c>catch</c> clauses, so deleting one
/// scenario's fault handling left the self-test -- which exercised its own separate copy -- still
/// green; a single shared helper makes that impossible by construction.
/// </summary>
public static class KeepAlive
{
    /// <summary>
    /// Starts a background loop that sends an inert, already-false
    /// <c>extension.set_verbose_logging</c> frame on <paramref name="client"/> every
    /// <paramref name="interval"/>, to generate best-effort activity on a connection -- for
    /// example so a backend's idle-sweep timer doesn't beat a slower timer a test is waiting on
    /// (<c>ResumeMargin</c>'s nudge), or simply to exercise a keepalive-style send cadence across
    /// a peer abort. Call <see cref="KeepAliveLoop.StopAsync"/> once the caller no longer needs
    /// the keepalive traffic; that cancels the loop and awaits its completion.
    /// </summary>
    public static KeepAliveLoop RunAsync(RealtimeBrowserClient client, TimeSpan interval, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await client.SendExtensionSetVerboseLoggingAsync(false, cts.Token).ConfigureAwait(false);
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected once StopAsync cancels this loop's own dedicated linked token (never
                // the caller's cancellationToken directly, and never the outer test's token in
                // isolation). This also already covers the PR #52 CI follow-up round 2 finding
                // (see RealtimeBrowserClient.CloseAsync's doc comment): a peer reset during a send
                // can surface as OperationCanceledException rather than WebSocketException.
                // Swallowing it here can't mask a genuine caller cancel -- cts is this loop's own
                // linked token, so either source means the loop has nothing more useful to do.
            }
            catch (WebSocketException)
            {
                // PR #52 CI follow-up (swigerb/SonicAIDriveThru#28 N10 aftermath): this loop's
                // only job is best-effort activity -- if the socket is already gone (closed or
                // aborted by the peer), there is nothing left to keep alive. Swallowing it here
                // lets the caller's own real assertions report what actually happened instead of
                // this unrelated send exception pre-empting them.
            }
        }, CancellationToken.None);

        return new KeepAliveLoop(cts, loopTask);
    }
}

/// <summary>Handle returned by <see cref="KeepAlive.RunAsync"/>.</summary>
public sealed class KeepAliveLoop : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _loopTask;

    internal KeepAliveLoop(CancellationTokenSource cts, Task loopTask)
    {
        _cts = cts;
        _loopTask = loopTask;
    }

    /// <summary>Cancels the loop and awaits its (already-fault-swallowed) completion.</summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _loopTask.ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>
    /// #28 N28: lets a caller hold a <see cref="KeepAliveLoop"/> in an <c>await using</c> block
    /// instead of always needing an explicit <see cref="StopAsync"/> call (and a try/finally to
    /// guarantee it runs on every exit path, including test failures). Just forwards to
    /// <see cref="StopAsync"/> -- both are safe to call at most once; <see cref="_cts"/>'s own
    /// disposal there would throw <see cref="ObjectDisposedException"/> on a second call, so
    /// callers should pick one style (either <c>await using</c>, or an explicit
    /// <see cref="StopAsync"/>) rather than both for the same instance.
    /// </summary>
    public ValueTask DisposeAsync() => new(StopAsync());
}
