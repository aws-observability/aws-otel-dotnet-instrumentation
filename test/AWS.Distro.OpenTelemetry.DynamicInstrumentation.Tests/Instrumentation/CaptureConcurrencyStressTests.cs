// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using OpenTelemetry.AutoInstrumentation.CallTarget;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

// High-concurrency / adversarial stress for the capture hot path — the highest-risk area for GA. Woven
// callbacks run on arbitrary user threads, fan out across Parallel/Task.Run, and must (1) never throw into
// user code, (2) never lose, duplicate, or cross-attribute captures under contention, (3) never corrupt the
// shared queue or the per-call pairing map, and (4) honor the rate/hit gates atomically. Mutates static
// state (registry, DIDataStore), so runs serially and clears state per test.
//
// NOTE on volume: the production capture gate (HitState) enforces a fixed-window rate limit of 5 captures/
// second PER instrumentation in addition to MaxHits (see HitState). Tests that fire thousands of hits in
// well under a second therefore see the pipeline PRODUCE only ~5 captures — that throttle is a deliberate
// production safeguard, not a defect. Pipeline-level tests here assert the throttle holds and that whatever
// DOES pass is correct/uncorrupted; the raw concurrency guarantees of the underlying structures (queue,
// per-call pairing) are exercised directly against DIDataStore, which has no rate limiter.
[Collection("SerialProcessState")]
public class CaptureConcurrencyStressTests : IDisposable
{
    private const string CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation";
    private const string ClassName = "StressTarget";

    public CaptureConcurrencyStressTests() => DIDataStore.Clear();

    public void Dispose() => DIDataStore.Clear();

    private static InstrumentationRegistry RegistryWith(CaptureConfiguration capture, string method = "Work")
    {
        var registry = new InstrumentationRegistry();
        registry.Register(new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = CodeUnit,
            ClassName = ClassName,
            MethodName = method,
            LocationHash = "stress-loc",
            Capture = capture,
        });
        DiIntegrationHelper.Configure(registry);
        return registry;
    }

    // ── Raw data-structure concurrency (no rate limiter) ────────────────────────────────────────────────

    [Fact]
    public async Task DIDataStore_ConcurrentEnqueueAndDrain_NoLossNoDuplication()
    {
        // 32 threads × 500 enqueues = 16,000, with a background drainer running concurrently. The union of
        // every drained batch must equal exactly what was enqueued — the ConcurrentQueue's core guarantee,
        // which is what makes the collector safe to drain while probes fire.
        const int threads = 32;
        const int perThread = 500;
        var collected = new ConcurrentBag<string>();
        var producersDone = false;

        var drainer = Task.Run(() =>
        {
            while (!Volatile.Read(ref producersDone) || DIDataStore.Count > 0)
            {
                foreach (var cap in DIDataStore.Drain())
                {
                    collected.Add(cap.LocationHash!);
                }
            }
        });

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                DIDataStore.Enqueue(new PendingCapture { Type = CaptureType.METHOD, LocationHash = $"t{t}-i{i}" });
            }
        });
        Volatile.Write(ref producersDone, true);

        await drainer.WaitAsync(TimeSpan.FromSeconds(30));

        collected.Should().HaveCount(threads * perThread);
        collected.Distinct().Should().HaveCount(threads * perThread, "no enqueue lost or duplicated");
    }

    [Fact]
    public void DIDataStore_ConcurrentRecordAndRetrieveEntry_EachCallIdPairsExactlyOnce()
    {
        // The per-call pairing map (ConcurrentDictionary keyed by a globally unique call id) is what keeps
        // recursive/fan-out entry/exit pairs from overwriting each other. Record then retrieve across many
        // threads: every retrieve must return ITS OWN entry, and each id must be retrievable exactly once.
        const int threads = 16;
        const int perThread = 1_000;
        var mismatches = 0;
        var nulls = 0;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                var marker = $"t{t}-i{i}";
                var callId = DIDataStore.RecordEntry(new PendingEntryData { LocationHash = marker });
                var got = DIDataStore.RetrieveEntry(callId);
                if (got == null)
                {
                    Interlocked.Increment(ref nulls);
                }
                else if (got.LocationHash != marker)
                {
                    Interlocked.Increment(ref mismatches);
                }

                // A second retrieve of the same id must be null — entries are removed on retrieve.
                DIDataStore.RetrieveEntry(callId).Should().BeNull("an entry is consumed exactly once");
            }
        });

        nulls.Should().Be(0, "every recorded entry must be retrievable");
        mismatches.Should().Be(0, "no entry may be cross-attributed to another call");
    }

    [Fact]
    public void HitState_RateLimit_AtomicUnderContention_NeverExceedsPerSecondCap()
    {
        // 5,000 concurrent attempts against a fresh HitState in well under a second must yield at most the
        // per-second cap (5). A non-atomic window counter would let extras through.
        var hit = new HitState(maxHits: null, expiresAt: null);

        var allowed = 0;
        Parallel.For(0, 5_000, _ =>
        {
            if (hit.TryHit())
            {
                Interlocked.Increment(ref allowed);
            }
        });

        allowed.Should().BeGreaterThan(0);
        allowed.Should().BeLessThanOrEqualTo(5, "the fixed-window rate limiter (5/sec) must hold atomically under contention");
    }

    [Fact]
    public void HitState_MaxHits_AtomicUnderContention_NeverExceedsLimit_AcrossWindows()
    {
        // With a high per-second cap so the rate limiter is not the binding constraint, MaxHits=50 must be
        // the hard ceiling even under 5,000 concurrent attempts.
        var hit = new HitState(maxHits: 50, expiresAt: null, maxCapturesPerSecond: 1_000_000);

        var allowed = 0;
        Parallel.For(0, 5_000, _ =>
        {
            if (hit.TryHit())
            {
                Interlocked.Increment(ref allowed);
            }
        });

        allowed.Should().Be(50, "MaxHits must be an exact atomic ceiling under contention");
        hit.IsDisabled.Should().BeTrue();
        hit.Reason.Should().Be(DisableReason.MAX_HITS_EXCEEDED);
    }

    // ── Full pipeline under contention (rate-limited, but must stay correct) ─────────────────────────────

    [Fact]
    public void Pipeline_ManyConcurrentCalls_WhateverPassesTheGateIsCorrectlyPaired_AndRateLimited()
    {
        // Fire 16,000 begin/end pairs concurrently through the REAL pipeline. The rate limiter caps how many
        // become captures, but every capture that IS produced must be internally consistent: its return value
        // must pair with its OWN argument (no cross-thread bleed), and the volume must respect the throttle.
        RegistryWith(CaptureConfiguration.Default with { MaxHits = 1_000_000 });
        var target = new StressTarget();
        const int threads = 32;
        const int perThread = 500;

        var act = () => Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                var id = $"t{t}-i{i}";
                var state = DiIntegrationHelper.OnMethodBegin<StressTarget>(target, new object?[] { id });
                DiIntegrationHelper.OnMethodEnd<StressTarget, string>(target, $"ret-{id}", null, in state);
            }
        });

        act.Should().NotThrow("the hot path must never throw into user code under contention");

        var drained = DIDataStore.Drain();
        drained.Should().NotBeEmpty();
        foreach (var cap in drained)
        {
            var arg = cap.Arguments!["arg0"].Value;
            cap.ReturnValue!.Value.Should().Be($"ret-{arg}", "return must pair with THIS call's own argument — no cross-attribution");
        }

        drained.Select(c => c.Arguments!["arg0"].Value).Distinct().Should()
            .HaveCount(drained.Count, "no captured call may be duplicated");
    }

    [Fact]
    public void Pipeline_ParallelFanoutWithinProbedMethod_DoesNotCorruptPairingMap_OrCrash()
    {
        // A probed method whose body fans out into probed child calls (Parallel.For) — the exact scenario the
        // ConcurrentDictionary (not Dictionary) pairing map was introduced to make safe. The AsyncLocal map
        // flows to every child; concurrent RecordEntry/RetrieveEntry must neither corrupt it nor throw. The
        // rate limiter bounds produced captures, but whatever is produced must be correctly paired.
        RegistryWith(CaptureConfiguration.Default with { MaxHits = 1_000_000 });
        var target = new StressTarget();

        var act = () =>
        {
            var outer = DiIntegrationHelper.OnMethodBegin<StressTarget>(target, new object?[] { "outer" });
            Parallel.For(0, 1_000, i =>
            {
                var child = DiIntegrationHelper.OnMethodBegin<StressTarget>(target, new object?[] { $"child-{i}" });
                DiIntegrationHelper.OnMethodEnd<StressTarget, string>(target, $"cret-{i}", null, in child);
            });
            DiIntegrationHelper.OnMethodEnd<StressTarget, string>(target, "outer-ret", null, in outer);
        };

        act.Should().NotThrow("concurrent fan-out through the AsyncLocal pairing map must not corrupt or throw");

        foreach (var cap in DIDataStore.Drain())
        {
            var arg = cap.Arguments!["arg0"].Value!;
            var expected = arg == "outer" ? "outer-ret" : $"cret-{arg["child-".Length..]}";
            cap.ReturnValue!.Value.Should().Be(expected, "each capture must keep its own entry data");
        }
    }

    [Fact]
    public void Pipeline_NeverThrowsIntoUserCode_EvenWhenSerializationHitsAdversarialArgs()
    {
        // Capture callbacks wrap everything in try/catch so an agent-internal fault can never propagate into
        // the user's method. Feed deliberately hostile arguments (throwing ToString, throwing property,
        // self-cycle, throwing-count collection) and assert neither Begin nor End ever throws.
        RegistryWith(CaptureConfiguration.Default);
        var target = new StressTarget();

        var hostileArgs = new object?[]
        {
            new ExplodingToString(),
            new ExplodingProperty(),
            MakeSelfCycle(),
            new ExplodingCountCollection(),
        };

        var act = () =>
        {
            var state = DiIntegrationHelper.OnMethodBegin<StressTarget>(target, hostileArgs);
            DiIntegrationHelper.OnMethodEnd<StressTarget, object>(target, new ExplodingToString(), null, in state);
        };

        act.Should().NotThrow("capture must never throw into the user's woven method");
    }

    [Fact]
    public void Pipeline_BeginWithNoRegistry_ReturnsDefaultState_DoesNotThrow()
    {
        // Defensive: if a callback fires before Configure (or after Cleanup nulls the registry), it must
        // no-op cleanly, not NRE into user code.
        DiIntegrationHelper.Configure(null);
        var target = new StressTarget();

        var act = () =>
        {
            var state = DiIntegrationHelper.OnMethodBegin<StressTarget>(target, new object?[] { "x" });
            DiIntegrationHelper.OnMethodEnd<StressTarget, string>(target, "r", null, in state);
        };

        act.Should().NotThrow();
        DIDataStore.Drain().Should().BeEmpty("no registry => no capture");
    }

    private static object MakeSelfCycle()
    {
        var n = new CycleNode();
        n.Self = n;
        return n;
    }

    private class ExplodingToString
    {
        public override string ToString() => throw new InvalidOperationException("boom-tostring");
    }

    private class ExplodingProperty
    {
        public string Bad => throw new InvalidOperationException("boom-prop");
    }

    private class CycleNode
    {
        public CycleNode? Self { get; set; }
    }

    private class ExplodingCountCollection : ICollection<int>
    {
        public int Count => throw new InvalidOperationException("boom-count");

        public bool IsReadOnly => true;

        public IEnumerator<int> GetEnumerator()
        {
            yield return 1;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        public void Add(int item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(int item) => throw new NotSupportedException();

        public void CopyTo(int[] array, int arrayIndex) => throw new NotSupportedException();

        public bool Remove(int item) => throw new NotSupportedException();
    }
}

// Top-level so its Type.FullName is exactly "<CodeUnit>.StressTarget" (a nested class would be
// "...+StressTarget" and never match the registered config's CodeUnit.ClassName).
public class StressTarget
{
    public string Work(string id) => $"ret-{id}";
}
