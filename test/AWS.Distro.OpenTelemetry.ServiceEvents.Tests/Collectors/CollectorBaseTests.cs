// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Tests for <see cref="CollectorBase" />'s shutdown contract: the final drain on
/// <c>Dispose</c> is the only thing that flushes the window in progress, so losing it
/// loses up to one whole flush interval of telemetry on every graceful shutdown.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class CollectorBaseTests
{
    /// <summary>
    /// Regression test for the interaction between the timer and the non-reentrancy guard.
    /// <para>
    /// The parameterless <c>Timer.Dispose()</c> returns without waiting for a callback that is
    /// already running. When <c>Dispose</c> landed during a tick, the <c>collecting</c> guard made
    /// the final <c>RunCollectSafely()</c> a no-op — so the last window was dropped, and the caller
    /// went on to dispose the OTLP providers underneath a <c>Collect()</c> that was still emitting.
    /// </para>
    /// <para>
    /// The collector below blocks its first tick for well under the shutdown budget, so
    /// <c>Dispose</c> is guaranteed to be called while a tick is in flight and guaranteed to be able
    /// to wait it out. Against the old implementation the observed collect count stays at 1; the fix
    /// must produce 2.
    /// </para>
    /// </summary>
    [Fact]
    public void Dispose_WhenATickIsInFlight_StillPerformsTheFinalDrain()
    {
        // Long enough that the handoff from WaitForFirstTick to Dispose cannot plausibly consume the
        // whole block on a loaded CI runner (which would leave nothing to wait for and make the
        // guard below fail for scheduling reasons), still well under the drain wait so the wait
        // succeeds and the final drain happens.
        var collector = new BlockingCollector(flushIntervalMs: 50, firstTickBlockMs: 300);

        collector.Start();

        collector.WaitForFirstTick(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the timer should have fired at least one tick");
        collector.CollectCount.Should().Be(1, "exactly one tick should be in flight at this point");

        // Dispose must wait for that in-flight tick to return, then drain once more.
        var stopwatch = Stopwatch.StartNew();
        collector.Dispose();
        stopwatch.Stop();

        collector.CollectCount.Should().Be(
            2,
            "Dispose must wait for the running tick and then perform the final drain; " +
            "with the parameterless Timer.Dispose() the collecting guard swallowed this flush");

        // Guard on the setup rather than the behaviour. The assertion above only proves anything
        // while Dispose is genuinely racing a tick, and that depends on the block outlasting the
        // handoff between WaitForFirstTick and Dispose. If the block is ever shortened past that
        // point the test would still pass while quietly testing nothing, so pin it here: Dispose
        // cannot have returned faster than most of the remaining block.
        stopwatch.Elapsed.Should().BeGreaterThan(
            TimeSpan.FromMilliseconds(75),
            "Dispose should have blocked waiting for the in-flight tick; returning sooner means the " +
            "tick had already finished and this test is no longer exercising the wait");
    }

    /// <summary>
    /// Regression test for a process-killing bug on the timeout path.
    /// <para>
    /// <c>Timer.Dispose(WaitHandle)</c> is a promise from the runtime to signal the handle once the
    /// in-flight callback returns, and the runtime holds the handle in order to do so. The dispose
    /// path used to own that handle with a <c>using</c> and <c>return</c> on timeout, which disposed
    /// it while the promise was still outstanding. The eventual signal then threw
    /// <c>ObjectDisposedException</c> on a thread-pool thread — unhandled, so it took the whole
    /// process down. The timeout branch that exists to protect shutdown was the one that crashed it.
    /// </para>
    /// <para>
    /// The wedged tick below outlasts the drain wait, so the timeout path is taken and the runtime
    /// signals the handle after <c>Dispose</c> has already returned. The test asserting anything at
    /// all is secondary: against the old implementation the test host dies, taking the run with it.
    /// </para>
    /// </summary>
    [Fact]
    public void Dispose_WhenATickOutlastsTheDrainWait_SkipsTheDrainAndSurvivesTheLateSignal()
    {
        // Comfortably longer than CollectorBase's drain wait, so the wait is guaranteed to time out.
        const int WedgedTickMs = 1500;

        var collector = new BlockingCollector(flushIntervalMs: 50, firstTickBlockMs: WedgedTickMs);

        collector.Start();
        collector.WaitForFirstTick(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the timer should have fired at least one tick");

        var stopwatch = Stopwatch.StartNew();
        collector.Dispose();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromMilliseconds(WedgedTickMs),
            "Dispose must give up on a wedged tick rather than block shutdown until it finishes");

        collector.CollectCount.Should().Be(
            1,
            "the final drain must be skipped when the wait timed out, rather than emitting " +
            "concurrently with the tick that is still running");

        // Let the wedged tick return so the runtime delivers the signal that used to crash here,
        // and give it a moment to actually do so. Surviving this is the assertion.
        collector.WaitForFirstTickCompleted(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the wedged tick should have completed on its own");
        Thread.Sleep(250);

        collector.CollectCount.Should().Be(1, "reaching this line at all means the process survived");
    }

    /// <summary>
    /// The shutdown waits share one deadline instead of each getting its own bound.
    /// <para>
    /// Teardown is sequential — every collector waits out its in-flight tick before the exporters get
    /// to flush — so per-wait bounds add up while the process-exit window they share does not.
    /// Overrunning it is worse than waiting less: the runtime kills the process mid-flush and the
    /// telemetry the final drain existed to save is lost anyway.
    /// </para>
    /// <para>
    /// Four wedged collectors would spend four independent drain waits back to back. Sharing a single
    /// budget must keep the total inside that budget instead.
    /// </para>
    /// </summary>
    [Fact]
    public void Dispose_WithASharedBudget_BoundsTotalShutdownTimeAcrossCollectors()
    {
        // Five rather than the two or three a real teardown uses, purely to widen the gap between the
        // shared and unshared totals: five independent drain waits would be 2.5s against a 1s budget,
        // so the bound below has roughly a second of margin in each direction and does not need the
        // machine to be idle to land on the right side of it.
        var collectors = Enumerable.Range(0, 5)
            .Select(_ => new BlockingCollector(flushIntervalMs: 50, firstTickBlockMs: 3000))
            .ToList();

        foreach (var collector in collectors)
        {
            collector.Start();
            collector.WaitForFirstTick(TimeSpan.FromSeconds(10))
                .Should().BeTrue("every collector should have a tick in flight before shutdown");
        }

        var budget = ShutdownBudget.FromNow(ShutdownBudget.Default);

        var stopwatch = Stopwatch.StartNew();
        foreach (var collector in collectors)
        {
            collector.Dispose(budget);
        }

        stopwatch.Stop();

        // Every tick is wedged well past the budget, so each wait runs to its limit and the total is
        // decided purely by whether that limit is shared. Shared: the whole pass fits in the budget,
        // leaving the slack below for scheduling noise. Per-collector: five drain waits back to back,
        // 2.5s, which overshoots the bound by about a second rather than by a hair.
        stopwatch.Elapsed.Should().BeLessThan(
            ShutdownBudget.Default + TimeSpan.FromMilliseconds(500),
            "the collectors must draw their waits from one shared deadline; exceeding this means each " +
            "is bounded only by its own cap and the total grows with the number of collectors");

        foreach (var collector in collectors)
        {
            collector.WaitForFirstTickCompleted(TimeSpan.FromSeconds(10))
                .Should().BeTrue("wedged ticks should complete rather than leak into later tests");
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var collector = new BlockingCollector(flushIntervalMs: 50, firstTickBlockMs: 0);

        collector.Start();
        collector.WaitForFirstTick(TimeSpan.FromSeconds(10)).Should().BeTrue();

        collector.Dispose();
        var afterFirstDispose = collector.CollectCount;

        collector.Dispose();

        collector.CollectCount.Should().Be(
            afterFirstDispose,
            "a second Dispose must not emit another window");
    }

    /// <summary>
    /// A collector whose first <see cref="Collect" /> blocks, so a test can deterministically
    /// arrange for <c>Dispose</c> to arrive while a tick is still running. Later ticks return
    /// immediately so the final drain itself stays fast.
    /// </summary>
    private sealed class BlockingCollector : CollectorBase
    {
        private readonly ManualResetEventSlim firstTick = new(false);
        private readonly ManualResetEventSlim firstTickCompleted = new(false);
        private readonly int firstTickBlockMs;
        private int collectCount;

        public BlockingCollector(int flushIntervalMs, int firstTickBlockMs)
            : base(flushIntervalMs, "BlockingCollector")
        {
            this.firstTickBlockMs = firstTickBlockMs;
        }

        public int CollectCount => Volatile.Read(ref this.collectCount);

        public bool WaitForFirstTick(TimeSpan timeout) => this.firstTick.Wait(timeout);

        /// <summary>Waits for the blocking first tick to return, so a test can observe what the
        /// runtime does once the callback the dispose path gave up on finally completes.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>Whether the first tick completed within <paramref name="timeout" />.</returns>
        public bool WaitForFirstTickCompleted(TimeSpan timeout) => this.firstTickCompleted.Wait(timeout);

        protected override void Collect()
        {
            var invocation = Interlocked.Increment(ref this.collectCount);
            this.firstTick.Set();

            if (invocation == 1)
            {
                if (this.firstTickBlockMs > 0)
                {
                    Thread.Sleep(this.firstTickBlockMs);
                }

                this.firstTickCompleted.Set();
            }
        }
    }
}
