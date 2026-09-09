// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Verifies that a line-probe hit becomes a snapshot on the capture queue, and that an unattributable
/// hit does not.
/// </summary>
// This is the class that was missing while the whole line-level stack sat inert: the callbacks fired and
// nothing consumed them. These tests assert the VALUE that lands on the queue, not merely that something was
// enqueued — a fire count would pass even if the local, line number, or key were wrong.
[Collection("SerialProcessState")]
public class LineProbeSinkTests : IDisposable
{
    public LineProbeSinkTests() => DIDataStore.Clear();

    public void Dispose() => DIDataStore.Clear();

    [Fact]
    public void OnLineProbeHit_WithRegisteredProbe_EnqueuesLineSnapshotWithNamedLocal()
    {
        var (sink, config) = CreateSinkWithProbe(localName: "total", probeId: out var probeId);

        sink.OnLineProbeHit(probeId, hasValue: true, value: 42);

        var captures = DIDataStore.Drain();
        captures.Should().HaveCount(1);

        var capture = captures[0];
        capture.Type.Should().Be(CaptureType.LINE);
        capture.InstrumentationKey.Should().Be(config.InstrumentationKey);
        capture.LocationHash.Should().Be(config.LocationHash);
        capture.LineNumber.Should().Be(config.LineNumber);

        // The local must arrive under its SOURCE name; the injected IL carries only the probe id, so a wrong
        // name here means the registration lost the mapping.
        capture.Locals.Should().ContainKey("total");
        capture.Locals!["total"].Value.Should().Be("42");
        capture.Locals["total"].Type.Should().Be("System.Int32");
    }

    [Fact]
    public void OnLineProbeHit_WithUnknownProbeId_EnqueuesNothing()
    {
        // The woven `call` outlives its registration — there is no un-weave — so a hit on a removed probe is
        // the expected steady state after a removal, and it must be a silent no-op rather than a
        // misattributed snapshot.
        var (sink, _) = CreateSinkWithProbe(localName: "x", probeId: out _);

        sink.OnLineProbeHit(probeId: 9999, hasValue: true, value: 1);

        DIDataStore.Drain().Should().BeEmpty();
    }

    [Fact]
    public void OnLineProbeHit_AfterUnregister_EnqueuesNothing()
    {
        var (sink, config) = CreateSinkWithProbe(localName: "x", probeId: out var probeId);

        sink.Unregister(config.InstrumentationKey, out var removedProbeIds).Should().BeTrue();
        removedProbeIds.Should().Equal(probeId);

        sink.OnLineProbeHit(probeId, hasValue: true, value: 1);

        DIDataStore.Drain().Should().BeEmpty();
    }

    [Fact]
    public void OnLineProbeHit_WithNoValue_EnqueuesSnapshotWithoutLocals()
    {
        // A bare Probe(int) hit — the Legacy emission, used when no local was requested. It still records
        // that the line was REACHED, which is the whole point of a probe with no capture.
        var (sink, _) = CreateSinkWithProbe(localName: null, probeId: out var probeId);

        sink.OnLineProbeHit(probeId, hasValue: false, value: null);

        var captures = DIDataStore.Drain();
        captures.Should().HaveCount(1);
        captures[0].Locals.Should().BeNull();
    }

    [Fact]
    public void OnLineProbeHit_WithNullLocal_StillRecordsTheLocal()
    {
        // hasValue=true with value=null means the local ITSELF was null. That must stay distinguishable from
        // "captured nothing" — which is why the callback takes two parameters instead of a null check.
        var (sink, _) = CreateSinkWithProbe(localName: "maybe", probeId: out var probeId);

        sink.OnLineProbeHit(probeId, hasValue: true, value: null);

        var captures = DIDataStore.Drain();
        captures.Should().HaveCount(1);
        captures[0].Locals.Should().ContainKey("maybe");
    }

    [Fact]
    public void OnLineProbeHit_BeyondMaxHits_StopsEnqueuing()
    {
        // Rate/hit limiting is enforced through the SAME registry HitState as function-level, so a line probe
        // cannot outlive its MaxHits budget.
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName: "i", maxHits: 2);
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        var probeId = sink.AllocateProbeId();
        sink.Register(probeId, config, CreateLocation("i"), gated: false);

        for (int i = 0; i < 5; i++)
        {
            sink.OnLineProbeHit(probeId, hasValue: true, value: i);
        }

        DIDataStore.Drain().Should().HaveCount(2);
    }

    [Fact]
    public void ShouldCapture_WithUnknownProbeId_ReturnsFalse()
    {
        // FAILS CLOSED. Returning true for a probe we cannot attribute would run the capture path — and its
        // allocation — for a hit that can never become a valid snapshot.
        var (sink, _) = CreateSinkWithProbe(localName: "x", probeId: out _);

        sink.ShouldCapture(9999).Should().BeFalse();
    }

    [Fact]
    public void ShouldCapture_ForGatedProbe_DoesNotDoubleChargeMaxHits()
    {
        // The gate consumes the hit, so a gated probe's OnLineProbeHit must NOT charge again. If it did, a
        // MaxHits of 2 would yield only 1 capture — the bug this asymmetry exists to prevent.
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName: "i", maxHits: 2);
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        var probeId = sink.AllocateProbeId();
        sink.Register(probeId, config, CreateLocation("i"), gated: true);

        // Mirror the emitted IL: gate, then capture only when the gate allows.
        for (int i = 0; i < 5; i++)
        {
            if (sink.ShouldCapture(probeId))
            {
                sink.OnLineProbeHit(probeId, hasValue: true, value: i);
            }
        }

        DIDataStore.Drain().Should().HaveCount(2);
    }

    [Fact]
    public void MultiLocal_EachProbeCapturesItsOwnNamedLocal()
    {
        // Multi-local capture registers N probes under ONE config. Each must resolve to its own local name,
        // because the woven callback carries nothing but the probeId.
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName: "a");
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        var idA = sink.AllocateProbeId();
        var idB = sink.AllocateProbeId();
        sink.Register(idA, config, CreateLocation("a"), gated: false);
        sink.Register(idB, config, CreateLocation("b"), gated: false);

        sink.OnLineProbeHit(idA, hasValue: true, value: 1);
        sink.OnLineProbeHit(idB, hasValue: true, value: "two");

        var captures = DIDataStore.Drain();
        captures.Should().HaveCount(2);
        captures.SelectMany(c => c.Locals!.Keys).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Unregister_RemovesEVERYProbeTheConfigOwns()
    {
        // THE MULTI-LOCAL REMOVAL BUG THIS GUARDS: probeIdsByKey used to hold a single id, so registering a
        // second local overwrote the first. Unregister then dropped only the last one and every earlier
        // probe stayed registered — still firing and still enqueuing snapshots after the operator deleted
        // the configuration.
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName: "a");
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        var ids = new[] { sink.AllocateProbeId(), sink.AllocateProbeId(), sink.AllocateProbeId() };
        foreach (var id in ids)
        {
            sink.Register(id, config, CreateLocation("a"), gated: false);
        }

        sink.Count.Should().Be(3);

        sink.Unregister(config.InstrumentationKey, out var removed).Should().BeTrue();
        removed.Should().BeEquivalentTo(ids);
        sink.Count.Should().Be(0, "no probe may survive removal of its configuration");

        // And every one is now inert.
        foreach (var id in ids)
        {
            sink.OnLineProbeHit(id, hasValue: true, value: 1);
        }

        DIDataStore.Drain().Should().BeEmpty();
    }

    [Fact]
    public void MultiLocal_SharesOneHitBudgetAcrossItsProbes()
    {
        // MaxHits limits how many times the LINE is captured: one line hit that captures 2 locals is ONE
        // observation, not two. The budget was charged per PROBE instead, which both spent it N times too fast
        // and TORE the result — with MaxHits=1 and two locals, the line's only snapshot held the first local
        // and the second was never captured at all, with nothing marking the omission.
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName: "a", maxHits: 2);
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        var idA = sink.AllocateProbeId();
        var idB = sink.AllocateProbeId();
        sink.Register(idA, config, CreateLocation("a"), gated: false);
        sink.Register(idB, config, CreateLocation("b"), gated: false);

        // Three executions of the line, each invoking both probes back to back, exactly as the woven IL does.
        for (int i = 0; i < 3; i++)
        {
            sink.OnLineProbeHit(idA, hasValue: true, value: i);
            sink.OnLineProbeHit(idB, hasValue: true, value: i);
        }

        var captures = DIDataStore.Drain().ToList();

        // Two line hits fit in the budget, and each one produced BOTH locals — 2 hits x 2 locals.
        captures.Should().HaveCount(
            4,
            "MaxHits=2 permits two LINE hits, and each captures every requested local");

        var localNames = captures
            .SelectMany(c => c.Locals?.Keys ?? Enumerable.Empty<string>())
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        localNames.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["a"] = 2, ["b"] = 2 },
            "every permitted line hit must report the SAME set of locals; a partial set is a torn observation");
    }

    [Fact]
    public void MultiLocal_WithASingleHitAllowed_StillCapturesEveryLocal()
    {
        // The tightest case, and the one that used to lose data outright: budget of one, two locals. Charging
        // per probe let the first local consume the whole budget and dropped the second forever.
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName: "a", maxHits: 1);
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        var idA = sink.AllocateProbeId();
        var idB = sink.AllocateProbeId();
        sink.Register(idA, config, CreateLocation("a"), gated: false);
        sink.Register(idB, config, CreateLocation("b"), gated: false);

        sink.OnLineProbeHit(idA, hasValue: true, value: 1);
        sink.OnLineProbeHit(idB, hasValue: true, value: 2);

        var locals = DIDataStore.Drain()
            .SelectMany(c => c.Locals?.Keys ?? Enumerable.Empty<string>())
            .ToList();

        locals.Should().BeEquivalentTo(
            new[] { "a", "b" },
            "one line hit is one observation, so its whole local set is captured or none of it is");
    }

    [Fact]
    public void AllocateProbeId_NeverReturnsTheSameIdTwice()
    {
        // Ids are baked into customer IL that survives removal, so a recycled id would let a stale woven
        // probe report as a live one.
        var sink = new LineProbeSink(new InstrumentationRegistry());

        var ids = new HashSet<int>();
        for (int i = 0; i < 100; i++)
        {
            ids.Add(sink.AllocateProbeId()).Should().BeTrue();
        }
    }

    private static InstrumentationConfiguration CreateLineConfig(string? localName, int? maxHits = null) =>
        new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "MyApp",
            ClassName = "OrderService",
            MethodName = "Process",
            LineNumber = 42,
            LocationHash = "line-hash",
            Capture = CaptureConfiguration.Default with
            {
                CaptureLocals = localName == null ? [] : [localName],
                CaptureStackTrace = false,
                MaxHits = maxHits,
            },
        };

    private static LineProbeLocation CreateLocation(string? localName) =>
        new(
            MethodToken: 0x06000001,
            AssemblyName: "MyApp",
            TypeName: "MyApp.OrderService",
            MethodName: "Process",
            ParameterCount: 0,
            IlOffset: 12,
            LocalSlot: localName == null ? -1 : 0,
            LocalName: localName);

    [Fact]
    public void TryGetInstrumentationKey_ResolvesARegisteredProbeAndRefusesEverythingElse()
    {
        // The reverse direction of the hit path, and the only way a weave verdict read back from the native
        // profiler — which carries nothing but the opaque probe id — can be attributed to a configuration.
        var (sink, config) = CreateSinkWithProbe(localName: "total", probeId: out var probeId);

        sink.TryGetInstrumentationKey(probeId, out var key).Should().BeTrue();
        key.Should().Be(config.InstrumentationKey);

        // An id from another lifetime, or one whose config was removed. FALSE, not a stale key: attributing a
        // dead probe to whatever configuration next occupied that key would report a failure against the wrong
        // probe entirely.
        sink.TryGetInstrumentationKey(probeId + 1000, out var missing).Should().BeFalse();
        missing.Should().BeEmpty();

        sink.Unregister(config.InstrumentationKey, out _).Should().BeTrue();
        sink.TryGetInstrumentationKey(probeId, out _).Should().BeFalse(
            "removal must make the id unattributable, exactly as it makes hits undeliverable");
    }

    private static (LineProbeSink Sink, InstrumentationConfiguration Config) CreateSinkWithProbe(
        string? localName, out int probeId)
    {
        var registry = new InstrumentationRegistry();
        var config = CreateLineConfig(localName);
        registry.Register(config);

        var sink = new LineProbeSink(registry);
        probeId = sink.AllocateProbeId();
        sink.Register(probeId, config, CreateLocation(localName), gated: false);

        return (sink, config);
    }
}
