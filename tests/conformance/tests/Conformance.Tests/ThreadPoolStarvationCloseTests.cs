using Xunit;
using Conformance.Harness;

namespace Conformance.Tests;

/// <summary>
/// PR #52 CI follow-up round 3 (swigerb/SonicAIDriveThru#28 N10 aftermath, isolation fix): the two
/// tests below deterministically reproduce the round-2 finding (see
/// <see cref="BrowserClientLifecycleTests.Explicit_close_does_not_throw_when_the_peer_aborts_first"/>'s
/// doc comment for the full history) by forcing genuine .NET thread-pool starvation via
/// <see cref="ThreadPool.SetMinThreads"/> rather than relying on full-suite contention to happen to
/// reproduce it. That call is process-wide, not scoped to this class or even this collection --
/// setting it down to 1 and then occupying every worker thread throttles thread injection for
/// *every* concurrently-running test in the process, in every other xUnit collection, for the
/// duration these tests hold it. xUnit v3 runs collections in parallel with each other by default,
/// so leaving these two tests in the default (parallel) <see cref="ConformanceCollection"/> would
/// perturb every other collection's own timing while these tests are starving the pool -- exactly
/// the kind of extra timing noise that bites on a slower CI runner, which is what prompted this
/// isolation fix. Declaring this collection with <c>DisableParallelization = true</c> makes xUnit
/// v3 run it by itself, after every parallel collection has finished, so the starvation window
/// here can never overlap another collection's tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolStarvationConformanceCollection
{
    public const string Name = "ThreadPoolStarvation";
}

/// <summary>
/// See <see cref="ThreadPoolStarvationConformanceCollection"/>'s doc comment for why these two
/// tests live in their own <c>DisableParallelization</c> collection instead of alongside the rest
/// of <see cref="BrowserClientLifecycleTests"/> (whose <see cref="AbruptPeerFakeBackend"/> fake is
/// reused here, marked <c>internal</c> rather than duplicated).
/// </summary>
[Collection(ThreadPoolStarvationConformanceCollection.Name)]
public sealed class ThreadPoolStarvationCloseTests
{
    /// <summary>
    /// PR #52 CI follow-up round 2 (swigerb/SonicAIDriveThru#28 N10 aftermath): after commit
    /// 3c03ab4 was pushed, <c>Explicit_close_does_not_throw_when_the_peer_aborts_first</c> failed
    /// 4/4 full-suite runs on the reporter's machine (passing alone) with
    /// <see cref="OperationCanceledException"/> wrapping an <see cref="IOException"/>/
    /// <see cref="System.Net.Sockets.SocketException"/> out of <c>CloseAsync</c>'s
    /// <c>CloseOutputAsync</c> call -- a different .NET exception type than the
    /// <see cref="WebSocketException"/> that fix originally caught. Round-1's own doc comment
    /// honestly noted the race "could not be forced" alone on this machine either, even under 24
    /// CPU-busy background processes; that also held true here for the *full suite* (5/5 runs
    /// passed even with the round-2 fix reverted) and even under heavier pressure (60 high-priority
    /// CPU-busy processes plus 3 concurrent full-suite <c>dotnet test</c> invocations).
    ///
    /// What *does* reliably force it: genuine .NET thread-pool starvation, not CPU competition.
    /// The differentiator between "alone" and "under full-suite load" is that the background
    /// reader loop's post-abort continuation needs a thread-pool worker thread to resume on, and
    /// under full-suite load many other tests' continuations are already queued ahead of it --
    /// CPU-busy *processes* don't reproduce that because they don't touch *this process's* .NET
    /// thread pool. Setting <see cref="ThreadPool.SetMinThreads"/> down to 1 and then occupying
    /// every worker thread with long-running blocking work starves this process's own pool
    /// directly and deterministically wins the race for <c>CloseOutputAsync</c>'s send against
    /// the reader loop's <c>ReceiveAsync</c> continuation, reproducing the reported exception
    /// type/stack verbatim (<c>ManagedWebSocket.SendFrameFallbackAsync</c> &lt;- <c>SendCloseFrameAsync</c>
    /// &lt;- <c>CloseOutputAsync</c>) on the very first attempt.
    ///
    /// Mutation-check: removing <c>CloseAsync</c>'s <c>catch (OperationCanceledException) when
    /// (!cancellationToken.IsCancellationRequested)</c> clause turns this test red under the
    /// starved pool below (first attempt, every run observed); restoring it turns it green (0
    /// failures across all attempts, every run observed). See the PR #52 CI follow-up report for
    /// both run logs.
    ///
    /// Round 3 (isolation fix): the pool is always restored inside <c>finally</c> -- covering even
    /// an unexpected infrastructure exception, not just the fault under test -- and the
    /// <c>Assert.Empty</c> call below is placed *after* that <c>finally</c> block has already run
    /// (rather than throwing/asserting from inside the loop while the pool is still throttled), so
    /// a failing assertion can never leave <see cref="ThreadPool.SetMinThreads"/> reduced for
    /// longer than the minimum time needed to run the 30 attempts.
    /// </summary>
    [Fact]
    public async Task Explicit_close_does_not_throw_when_the_peer_resets_under_thread_pool_starvation()
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = new List<string>();

        // Starve this process's own thread pool so the reader loop's post-abort continuation has
        // to queue behind other work instead of running immediately -- see this class's doc
        // comment for why this (not CPU pressure) is what actually reproduces the race.
        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
        ThreadPool.GetMaxThreads(out var workerMax, out _);
        ThreadPool.SetMinThreads(1, 1);
        var occupySignal = new ManualResetEventSlim(false);
        var occupyTasks = new Task[workerMax * 2];
        for (var i = 0; i < occupyTasks.Length; i++)
        {
            occupyTasks[i] = Task.Factory.StartNew(() => occupySignal.Wait(), TaskCreationOptions.LongRunning);
        }

        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    await using var fakeBackend = new BrowserClientLifecycleTests.AbruptPeerFakeBackend(abortDelay: TimeSpan.Zero);
                    var browser = await RealtimeBrowserClient.ConnectAsync(fakeBackend.BaseUri, cancellationToken: ct);
                    await browser.CloseAsync(cancellationToken: ct);
                    await browser.DisposeAsync();
                }
                catch (Exception ex)
                {
                    failures.Add($"Attempt {attempt}: {ex}");
                }
            }
        }
        finally
        {
            occupySignal.Set();
            await Task.WhenAll(occupyTasks);
            ThreadPool.SetMinThreads(workerMin, ioMin);
        }

        // Asserted only after the pool is guaranteed restored above -- see the round-3 note in the
        // doc comment.
        Assert.Empty(failures);
    }

    /// <summary>
    /// Same technique and same PR #52 CI follow-up round-2 finding as
    /// <see cref="Explicit_close_does_not_throw_when_the_peer_resets_under_thread_pool_starvation"/>,
    /// but exercising <see cref="RealtimeBrowserClient.DisposeAsync"/>'s own independent attempt at
    /// a graceful <c>CloseOutputAsync</c> directly (skipping the explicit <c>CloseAsync</c> call,
    /// which would otherwise already have transitioned the socket out of <c>Open</c>/
    /// <c>CloseReceived</c> before <c>DisposeAsync</c> ran, making its own catch clauses
    /// unreachable in this test). <c>DisposeAsync</c>'s <c>CloseOutputAsync</c> call always passes
    /// <see cref="CancellationToken.None"/>, so its added <c>catch (OperationCanceledException)</c>
    /// needs no <c>when</c> guard (see that method's doc comment).
    ///
    /// Mutation-check: removing <c>DisposeAsync</c>'s <c>catch (OperationCanceledException)</c>
    /// clause turns this test red under the starved pool below (first attempt, every run
    /// observed); restoring it turns it green.
    ///
    /// Round 3 (isolation fix): same restore-before-assert structure as
    /// <see cref="Explicit_close_does_not_throw_when_the_peer_resets_under_thread_pool_starvation"/>
    /// -- see that test's doc comment.
    /// </summary>
    [Fact]
    public async Task Plain_disposal_does_not_throw_when_the_peer_resets_under_thread_pool_starvation()
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = new List<string>();

        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
        ThreadPool.GetMaxThreads(out var workerMax, out _);
        ThreadPool.SetMinThreads(1, 1);
        var occupySignal = new ManualResetEventSlim(false);
        var occupyTasks = new Task[workerMax * 2];
        for (var i = 0; i < occupyTasks.Length; i++)
        {
            occupyTasks[i] = Task.Factory.StartNew(() => occupySignal.Wait(), TaskCreationOptions.LongRunning);
        }

        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    await using var fakeBackend = new BrowserClientLifecycleTests.AbruptPeerFakeBackend(abortDelay: TimeSpan.Zero);
                    var browser = await RealtimeBrowserClient.ConnectAsync(fakeBackend.BaseUri, cancellationToken: ct);
                    await browser.DisposeAsync();
                }
                catch (Exception ex)
                {
                    failures.Add($"Attempt {attempt}: {ex}");
                }
            }
        }
        finally
        {
            occupySignal.Set();
            await Task.WhenAll(occupyTasks);
            ThreadPool.SetMinThreads(workerMin, ioMin);
        }

        Assert.Empty(failures);
    }
}
