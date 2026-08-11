// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
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
    /// <c>Dispose</c> is guaranteed to be called while a tick is in flight. Against the old
    /// implementation the observed collect count stays at 1; the fix must produce 2.
    /// </para>
    /// </summary>
    [Fact]
    public void Dispose_WhenATickIsInFlight_StillPerformsTheFinalDrain()
    {
        var collector = new BlockingCollector(flushIntervalMs: 50, firstTickBlockMs: 800);

        collector.Start();

        collector.WaitForFirstTick(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the timer should have fired at least one tick");
        collector.CollectCount.Should().Be(1, "exactly one tick should be in flight at this point");

        // Dispose must wait for that in-flight tick to return, then drain once more.
        collector.Dispose();

        collector.CollectCount.Should().Be(
            2,
            "Dispose must wait for the running tick and then perform the final drain; " +
            "with the parameterless Timer.Dispose() the collecting guard swallowed this flush");
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
        private readonly int firstTickBlockMs;
        private int collectCount;

        public BlockingCollector(int flushIntervalMs, int firstTickBlockMs)
            : base(flushIntervalMs, "BlockingCollector")
        {
            this.firstTickBlockMs = firstTickBlockMs;
        }

        public int CollectCount => Volatile.Read(ref this.collectCount);

        public bool WaitForFirstTick(TimeSpan timeout) => this.firstTick.Wait(timeout);

        protected override void Collect()
        {
            var invocation = Interlocked.Increment(ref this.collectCount);
            this.firstTick.Set();

            if (invocation == 1 && this.firstTickBlockMs > 0)
            {
                Thread.Sleep(this.firstTickBlockMs);
            }
        }
    }
}
