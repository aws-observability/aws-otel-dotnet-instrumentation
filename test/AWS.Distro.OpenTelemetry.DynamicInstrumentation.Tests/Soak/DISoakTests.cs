// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Soak;

/// <summary>
/// Sustained-load soak for the DI capture path: accumulation, not throughput.
/// </summary>
// WHY A SOAK IS NOT THE SAME AS THE STRESS TESTS. CaptureConcurrencyStressTests fires 16,000 pairs from 32
// threads and finishes in well under a second. That shape proves the concurrency primitives hold under
// contention, and it CANNOT prove anything about accumulation, because nothing has had time to accumulate:
//
//   * A fixed-window rate limiter that never resets its window looks IDENTICAL to a working one inside a
//     single burst — both admit ~5 captures. Only crossing many windows separates them.
//   * A leak of one entry per probe hit, or one registration per apply/remove cycle, is invisible at 16,000
//     iterations and fatal over a day of polling.
//   * A queue that drains slower than it fills is bounded-looking in a burst and unbounded in production.
//
// So every assertion here is about a quantity that must STAY BOUNDED, or a rate that must hold ACROSS
// windows, measured against wall-clock time rather than an iteration count.
//
// DURATION. `DI_SOAK_SECONDS` (default 5) sets the per-test load window. The default is deliberately short
// but NON-ZERO and the tests are NOT [Skip]-attributed: a soak that only runs when someone remembers to set
// a variable is a soak nobody runs, and would have caught nothing. At 5s each this adds roughly 15s to the
// suite while still crossing several rate-limit windows. Dial it up for a real soak:
//
//   DI_SOAK_SECONDS=1800 dotnet test --filter FullyQualifiedName~DISoakTests
//
// Mutates process-global state (the registry and DIDataStore are static), so it joins the existing serial
// collection rather than racing the other suites.
[Collection("SerialProcessState")]
public class DISoakTests : IDisposable
{
    private const string CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Soak";
    private const string ClassName = "SoakTarget";

    /// <summary>The production fixed-window limit, mirrored here so the arithmetic below is explicit.</summary>
    // Not read from HitState: it is a private const there, and a test that derived its expectation from the
    // implementation would pass even if that implementation changed to something wrong.
    private const int CapturesPerSecondPerProbe = 5;

    private static TimeSpan LoadWindow
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DI_SOAK_SECONDS");
            return int.TryParse(configured, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(5);
        }
    }

    public DISoakTests() => DIDataStore.Clear();

    public void Dispose()
    {
        DIDataStore.Clear();
        DiIntegrationHelper.Configure(null);
        DiLineIntegrationHelper.Configure(null);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Soak_SustainedFunctionLevelLoad_PairsExactlyOnce_AndTheQueueStaysBounded()
    {
        // A probed method called continuously while a drainer empties the queue on an interval, which is what
        // DISnapshotCollector does in production. Two properties have to survive the whole run rather than one
        // burst of it: every capture pairs with ITS OWN call, and the queue never trends upward.
        var registry = RegistryWith(CaptureConfiguration.Default with { MaxHits = int.MaxValue });
        var target = new SoakTarget();

        var drained = new ConcurrentBag<PendingCapture>();
        var depthSamples = new ConcurrentBag<int>();
        var stop = new CancellationTokenSource();

        var drainer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                depthSamples.Add(DIDataStore.Count);
                foreach (var capture in DIDataStore.Drain())
                {
                    drained.Add(capture);
                }

                Thread.Sleep(25);
            }
        });

        var deadline = Stopwatch.StartNew();
        var calls = 0L;
        var act = () => Parallel.For(0, Math.Max(4, Environment.ProcessorCount), _ =>
        {
            while (deadline.Elapsed < LoadWindow)
            {
                var n = Interlocked.Increment(ref calls);
                var id = $"call-{n}";
                var state = DiIntegrationHelper.OnMethodBegin<SoakTarget>(target, new object?[] { id });
                DiIntegrationHelper.OnMethodEnd<SoakTarget, string>(target, $"ret-{id}", null, in state);
            }
        });

        act.Should().NotThrow("the hot path must never throw into user code, however long it runs");

        stop.Cancel();
        drainer.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the drainer must not wedge");
        foreach (var capture in DIDataStore.Drain())
        {
            drained.Add(capture);
        }

        calls.Should().BeGreaterThan(0, "the load generator must actually have run");
        drained.Should().NotBeEmpty("the rate limiter throttles, it does not silence");

        // Cross-attribution across the WHOLE run, not just one batch: a capture whose return value belongs to
        // a different call is the failure mode the per-call pairing map exists to prevent, and it is far more
        // likely to appear once the map has been churned for a while.
        foreach (var capture in drained)
        {
            var argument = capture.Arguments!["arg0"].Value;
            capture.ReturnValue!.Value.Should().Be(
                $"ret-{argument}", "every return must pair with its OWN call's argument");
        }

        drained.Select(c => c.Arguments!["arg0"].Value).Should().OnlyHaveUniqueItems(
            "a capture delivered twice would double-count in the backend");

        // THE ACCUMULATION ASSERTION. A drainer that keeps up leaves the queue shallow; one that falls behind
        // leaves it growing without bound. Asserted on the observed maximum rather than the final value,
        // because the final drain would hide a queue that had been deep throughout.
        depthSamples.Should().NotBeEmpty();
        depthSamples.Max().Should().BeLessThan(
            10_000,
            "queue depth must stay bounded while a drainer runs; unbounded growth here is the production "
            + "memory leak, and the rate limiter alone should keep this in the low hundreds");

        DIDataStore.Count.Should().Be(0, "the final drain must leave nothing behind");
    }

    [Fact]
    public void Soak_RateLimitHoldsAcrossEveryWindow_NotJustTheFirst()
    {
        // HitState is a FIXED-WINDOW limiter: it admits N per second, then resets when the window rolls. A
        // limiter whose window never rolls admits 5 in total, forever, and the operator sees a probe that
        // silently stops capturing after its first second.
        //
        // HONEST SCOPE: `HitStateTests.TryHit_RateWindowElapses_WindowCounterResets_AllowsAgain` already
        // catches a window that never resets — measured, by mutating the reset condition to `false` and
        // watching both that test and this one go red. What this adds is SUSTAINED operation: many windows
        // back-to-back under a tight loop, so drift, counter mishandling across dozens of rolls, or CAS
        // contention on windowStartTicks has room to show up where a single sleep-past-the-window test has
        // none. It is duration coverage over the same invariant, not a different invariant.
        //
        // Driven directly against HitState rather than through the pipeline, so the count is the limiter's
        // decision and nothing else — no serializer, no queue, no registry lookup in the way.
        var hitState = new HitState(maxHits: null, expiresAt: null);
        var admitted = 0;
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < LoadWindow)
        {
            if (hitState.TryHit())
            {
                admitted++;
            }
        }

        elapsed.Stop();
        var seconds = elapsed.Elapsed.TotalSeconds;
        var expected = CapturesPerSecondPerProbe * seconds;

        // A generous band, because window boundaries do not align with the loop and the last window is
        // partial. The point is the ORDER OF MAGNITUDE: a limiter stuck in its first window scores ~5 no
        // matter how long this runs, which is nowhere near the lower bound once the window exceeds ~2s.
        admitted.Should().BeGreaterThan(
            (int)(expected * 0.5),
            $"over {seconds:F1}s at {CapturesPerSecondPerProbe}/s the limiter should admit roughly "
            + $"{expected:F0}; a window that never resets would admit only {CapturesPerSecondPerProbe}");

        admitted.Should().BeLessThan(
            (int)(expected * 1.5) + CapturesPerSecondPerProbe,
            "and it must not admit substantially MORE than the cap — an over-admitting limiter is a "
            + "self-inflicted flood of snapshots from a hot line");
    }

    [Fact]
    public void Soak_RepeatedLineProbeApplyAndRemoveCycles_LeaveNoRegistrationsBehind()
    {
        // THE LINE-LEVEL LEAK SURFACE. Every config poll can add and later remove probes, and removal is a
        // LOGICAL uninstrument — the IL cannot be un-woven, so dropping the sink registration is the only
        // thing that stops a hit being attributed. If a cycle leaks even one registration, the sink grows for
        // as long as the process lives, and a stale id can still resolve to a config the operator deleted.
        //
        // Multi-local on purpose: a config owning THREE probes is where a single-id removal used to drop only
        // the last one and leave the earlier two live. That was mutation-proven once; this keeps it proven
        // over many cycles rather than one.
        var registry = new InstrumentationRegistry();
        var sink = new LineProbeSink(registry);
        DiLineIntegrationHelper.Configure(sink);

        sink.Count.Should().Be(0, "baseline");

        var cycles = 0;
        var peakCount = 0;
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < LoadWindow)
        {
            cycles++;

            // A NEW LocationHash each cycle, as an edited probe would have. Reusing one would let a stale
            // registration hide behind a matching key instead of accumulating visibly.
            var config = LineConfig($"soak-hash-{cycles:x8}");
            registry.Register(config);

            var ids = new List<int>();
            foreach (var localName in new[] { "alpha", "beta", "gamma" })
            {
                var probeId = sink.AllocateProbeId();
                ids.Add(probeId);
                sink.Register(probeId, config, LocationFor(localName), gated: false);
            }

            peakCount = Math.Max(peakCount, sink.Count);

            // Fire each probe so the cycle exercises the hit path, not just registration bookkeeping.
            foreach (var probeId in ids)
            {
                sink.OnLineProbeHit(probeId, hasValue: true, value: cycles);
            }

            sink.Unregister(config.InstrumentationKey, out var removed).Should().BeTrue(
                "a registered config must be removable");
            removed.Should().HaveCount(3, "removal must return EVERY probe the config owned, not just one");

            registry.RemoveStale(new HashSet<string>());
            DIDataStore.Drain();

            sink.Count.Should().Be(
                0,
                $"after cycle {cycles} the sink must be empty again; a non-zero count here is a registration "
                + "leaked per poll, which grows for the life of the process");
        }

        cycles.Should().BeGreaterThan(1, "the soak must complete more than one cycle to mean anything");
        peakCount.Should().Be(3, "three locals means three concurrent registrations, never more");
        sink.Count.Should().Be(0, "and nothing may survive the final cycle");
    }

    private static InstrumentationRegistry RegistryWith(CaptureConfiguration capture)
    {
        var registry = new InstrumentationRegistry();
        registry.Register(new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = CodeUnit,
            ClassName = ClassName,
            MethodName = "Work",
            LocationHash = "soak-loc",
            Capture = capture,
        });
        DiIntegrationHelper.Configure(registry);
        return registry;
    }

    private static InstrumentationConfiguration LineConfig(string locationHash) =>
        new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = CodeUnit,
            ClassName = ClassName,
            MethodName = "Work",
            LineNumber = 42,
            LocationHash = locationHash,
            Capture = CaptureConfiguration.Default with
            {
                CaptureLocals = ["alpha", "beta", "gamma"],
                CaptureStackTrace = false,
                MaxHits = int.MaxValue,
            },
        };

    private static LineProbeLocation LocationFor(string localName) =>
        new(
            MethodToken: 0x06000001,
            AssemblyName: CodeUnit,
            TypeName: $"{CodeUnit}.{ClassName}",
            MethodName: "Work",
            ParameterCount: 0,
            IlOffset: 12,
            LocalSlot: 0,
            LocalName: localName);

}

/// <summary>Probe target for the soak. Top-level on purpose.</summary>
// MUST NOT BE NESTED. The capture path resolves a registration by the target's type name, and a nested type
// is `DISoakTests+SoakTarget`, which never matches the ClassName a configuration carries. Nesting it produced
// a soak that ran the full load and captured NOTHING — a green-looking run asserting on an empty set. Mirrors
// how StressTarget is declared for the same reason.
public class SoakTarget
{
    /// <summary>The probed method.</summary>
    /// <param name="id">Call identifier, echoed into the return value so pairing is checkable.</param>
    /// <returns>The identifier prefixed with "ret-".</returns>
    public string Work(string id) => $"ret-{id}";
}
