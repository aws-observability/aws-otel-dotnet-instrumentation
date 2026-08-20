// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Tests for <c>LineProbeTranslator</c> — the managed→native seam: PDB resolution in, one marshaled
/// definition and one <c>AddLineProbes</c> call out.
/// </summary>
// Driven through the constructor's addLineProbesOverride seam, so the whole path is exercised WITHOUT the
// forked native binary present. That is what lets this land before the fork is in the build (F2).
// Resolution is against the test assembly's own real PDB via PdbReaderTargets, not a stub, so the offsets
// asserted below are ones the compiler actually emitted.
public class LineProbeTranslatorTests
{
    private const string TargetTypeName =
        "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel.PdbReaderTargets";

    // Splits TargetTypeName the way InstrumentationConfiguration composes TypeName ($"{CodeUnit}.{ClassName}"),
    // so the config round-trips to exactly the type PdbReaderTargets lives in.
    private static InstrumentationConfiguration LineConfig(
        string methodName,
        int lineNumber,
        string? captureLocal = null,
        string[]? captureLocals = null) =>
        new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = TargetTypeName[..TargetTypeName.LastIndexOf('.')],
            ClassName = TargetTypeName[(TargetTypeName.LastIndexOf('.') + 1)..],
            MethodName = methodName,
            LineNumber = lineNumber,
            LocationHash = "aabb000000000001",

            // captureLocals covers the multi-local case (N probes at one offset); captureLocal is the
            // single-local shorthand the other tests use. InstrumentationConfiguration's properties are
            // init-only, so this has to be decided here rather than adjusted afterwards.
            Capture = (captureLocal, captureLocals) switch
            {
                (null, null) => CaptureConfiguration.Default,
                (_, null) => CaptureConfiguration.Default with { CaptureLocals = [captureLocal!] },
                _ => CaptureConfiguration.Default with { CaptureLocals = captureLocals! },
            },
        };

    // Resolves to the real fixture type regardless of what the config says, so these tests do not depend
    // on the ambient set of loaded assemblies.
    private static readonly Func<InstrumentationConfiguration, Type?> ResolvesToFixture =
        _ => typeof(PdbReaderTargets);

    private static readonly Func<InstrumentationConfiguration, Type?> NeverResolves = _ => null;

    private sealed record Captured(string Id, NativeLineProbeDefinition[] Definitions, int Size);

    // Copies the fields off the definition EAGERLY. ApplyLineProbe disposes in a `finally`, so a spy that
    // held the struct and read it afterwards would be reading freed unmanaged memory for the signature
    // array — exactly the use-after-free this seam exists to let us test for.
    private static (Func<Action<string, NativeLineProbeDefinition[], int>> Seam, Func<Captured?> Read) Spy()
    {
        Captured? captured = null;
        return (
            () => (id, defs, size) => captured = new Captured(id, [.. defs], size),
            () => captured);
    }

    [Fact]
    public void ApplyLineProbe_RegistersEveryProbe_BEFORE_TheNativeApply()
    {
        // The injected callback becomes reachable the moment the ReJIT that AddLineProbes triggers completes,
        // which can be before ApplyLineProbe returns. A hit carries only its probeId, so anything registered
        // after the native call races that first hit and loses it — the probe silently misses its first
        // execution. This asserts the ordering directly: the spy stands in for the native call, and by the
        // time it runs, every probe must already have been handed to the register callback.
        var registeredWhenNativeCalled = new List<int>();
        var registered = new List<int>();

        var translator = new LineProbeTranslator(
            (_, _, _) => registeredWhenNativeCalled.AddRange(registered),
            typeResolver: ResolvesToFixture);

        // Two locals, so this covers the multi-probe case: N ids at one offset, all needing registration
        // before the single batched apply.
        var config = LineConfig(
            nameof(PdbReaderTargets.MixedLocalTypes),
            PdbReaderTargets.LineOf("mixedItems"),
            captureLocals: ["number", "text"]);

        var nextId = 100;
        var result = translator.ApplyLineProbe(
            config,
            probeId: 42,
            allocateProbeId: () => ++nextId,
            registerBeforeApply: applied => registered.Add(applied.ProbeId));

        result.IsResolved.Should().BeTrue($"expected resolution, got {result.Status}: {result.Detail}");
        registered.Should().NotBeEmpty("the register callback must be invoked for each resolved probe");
        registeredWhenNativeCalled.Should().BeEquivalentTo(
            registered,
            "every probe must be registered BEFORE AddLineProbes weaves it — a probeId that arrives at the "
            + "sink unregistered resolves to nothing and its capture is dropped");
    }

    [Fact]
    public void ApplyLineProbe_ResolvableLine_CallsAddLineProbesWithOneDefinition()
    {
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 42);

        result.IsResolved.Should().BeTrue($"expected resolution, got {result.Status}: {result.Detail}");

        var captured = read();
        captured.Should().NotBeNull("the native call must actually be made, not merely resolved");
        captured!.Size.Should().Be(1, "one captured local means exactly one definition");
        captured.Definitions.Should().HaveCount(1);
        captured.Definitions[0].ProbeId.Should().Be(42);
        captured.Definitions[0].TargetMethod.Should().Be(nameof(PdbReaderTargets.ThreeStatements));
    }

    [Fact]
    public void ApplyLineProbe_DefinitionsIdIncludesTheProbeId_SoAReAddIsNotDedupedAway()
    {
        // The native side dedups by this id (cor_profiler.cpp "Id already processed"). A bare LocationHash
        // would make re-adding the same location after a removal a SILENT no-op: the managed side reports
        // success, nothing is woven, and no error is raised anywhere. Hence probeId in the key.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);
        var config = LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB"));

        translator.ApplyLineProbe(config, probeId: 99);

        read()!.Id.Should().Be(
            $"{config.LocationHash}:99",
            "the id must vary with probeId, or a re-add after removal is silently discarded natively");
    }

    [Fact]
    public void ApplyLineProbe_TwoProbesAtTheSameLocation_GetDistinctDefinitionsIds()
    {
        // The mutation-visible form of the assertion above: same location, different probe, different id.
        // A translator that keyed on LocationHash alone passes the single-call test and fails this one.
        var ids = new List<string>();
        var translator = new LineProbeTranslator(
            (id, _, _) => ids.Add(id), typeResolver: ResolvesToFixture);
        var config = LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB"));

        translator.ApplyLineProbe(config, probeId: 1);
        translator.ApplyLineProbe(config, probeId: 2);

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ApplyLineProbe_WithRequestedLocal_UsesLocalCaptureModeAndTheTwoArgCallback()
    {
        // MODE↔CALLBACK ARITY LOCK. The native side derives the callback SIGNATURE from emissionMode
        // (line_probe.cpp: needsTwoArgCallback), while the NAME comes from this definition. Pairing
        // LocalCapture with the one-arg `Probe` name does not fail cleanly — DefineMemberRef succeeds
        // against a signature no managed method has, and the call then binds to nothing at runtime. So
        // mode and callback name must be asserted TOGETHER; either one alone permits the broken pairing.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB"), captureLocal: "b"),
            probeId: 5);

        result.IsResolved.Should().BeTrue($"expected resolution, got {result.Status}: {result.Detail}");

        var definition = read()!.Definitions[0];
        definition.EmissionMode.Should().Be((int)LineProbeEmissionMode.LocalCapture);
        definition.CallbackMethod.Should().Be(
            LineProbeTranslator.CaptureMethod, "LocalCapture emits a two-arg (int32, object) call");
        definition.BoxValue.Should().BeGreaterThanOrEqualTo(
            0, "BoxValue carries the local SLOT INDEX in LocalCapture mode");
    }

    [Fact]
    public void ApplyLineProbe_WithoutRequestedLocal_UsesLegacyModeAndTheOneArgCallback()
    {
        // The other half of the arity lock. A translator that hardcoded either callback name passes one of
        // these two tests and fails the other.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 6);

        result.IsResolved.Should().BeTrue();

        var definition = read()!.Definitions[0];
        definition.EmissionMode.Should().Be((int)LineProbeEmissionMode.Legacy);
        definition.CallbackMethod.Should().Be(
            LineProbeTranslator.ProbeMethod, "Legacy emits a one-arg (int32) call");
        definition.GateMethod.Should().BeNull("v1 ships ungated; GatedBox is not wired yet");
    }

    [Fact]
    public void ApplyLineProbe_OffsetHandedToNativeIsASafeInjectionPoint()
    {
        // End-to-end form of P3a's central guarantee, asserted on the value that ACTUALLY crosses the
        // boundary rather than on PdbReader's return value. Catches a translator that resolved correctly
        // and then marshaled the wrong field into ilOffset.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);
        var scan = IlBoundaryScanner.Scan(
            typeof(PdbReaderTargets).GetMethod(nameof(PdbReaderTargets.ThreeStatements))!
                .GetMethodBody()!.GetILAsByteArray()!);

        translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 7);

        var ilOffset = read()!.Definitions[0].IlOffset;
        scan.IsSafeInjectionPoint(ilOffset).Should().BeTrue(
            "offset {0} reached the native side; it must be an instruction start and not a branch target",
            ilOffset);
    }

    [Fact]
    public void ApplyLineProbe_SignatureArrayLengthIsArityPlusOne()
    {
        // The native side matches on LENGTH (return type + parameters), so an off-by-one here means the
        // target method is never matched and the probe silently never weaves.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);

        translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 8);

        // ThreeStatements(int x) — one parameter, so returnType + 1 parameter = 2.
        read()!.Definitions[0].TargetSignatureTypesLength.Should().Be(2);
    }

    [Fact]
    public void ApplyLineProbe_DisposesTheDefinitionEvenOnTheSuccessPath()
    {
        // The leak guard. The unmanaged signature array is invisible to the GC, so a missing Dispose grows
        // the process on every successful apply — the common path, not an error path.
        NativeLineProbeDefinition[]? held = null;
        var translator = new LineProbeTranslator(
            (_, defs, _) => held = defs, typeResolver: ResolvesToFixture);

        translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 9);

        // The spy holds the SAME array instance the translator disposes, so a nulled pointer is direct
        // evidence Dispose ran.
        held.Should().NotBeNull();
        held![0].TargetSignatureTypes.Should().Be(
            IntPtr.Zero, "ApplyLineProbe must dispose the definition in its finally block");
    }

    [Fact]
    public void ApplyLineProbe_DisposesTheDefinitionWhenTheNativeCallThrows()
    {
        // Same guarantee on the failure path: an exception out of AddLineProbes must not leak the array.
        NativeLineProbeDefinition[]? held = null;
        var translator = new LineProbeTranslator(
            (_, defs, _) =>
            {
                held = defs;
                throw new EntryPointNotFoundException("simulated stock profiler");
            },
            typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 10);

        result.Status.Should().Be(LineProbeResolutionStatus.ProfilerMissingLineProbeSupport);
        held.Should().NotBeNull();
        held![0].TargetSignatureTypes.Should().Be(IntPtr.Zero, "the finally must run on the throwing path too");
    }

    [Fact]
    public void ApplyLineProbe_StockProfiler_MapsToProfilerMissingLineProbeSupport()
    {
        // Running against the STOCK upstream binary is a DEPLOYMENT condition, not a bad config: the
        // exports simply are not there. It must not surface as a generic runtime error, because the
        // operator action is completely different (ship the right binary, do not touch the probe).
        var translator = new LineProbeTranslator(
            (_, _, _) => throw new EntryPointNotFoundException(),
            typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 11);

        result.Status.Should().Be(LineProbeResolutionStatus.ProfilerMissingLineProbeSupport);
        result.Detail.Should().NotBeNullOrEmpty("the operator needs to be told which binary is loaded");
    }

    [Fact]
    public void ApplyLineProbe_NoProfilerAtAll_MapsToProfilerMissingLineProbeSupport()
    {
        // DllNotFoundException rather than EntryPointNotFound: no profiler in the process at all (unit
        // tests, or an app started without the profiler env vars). Same operator-facing conclusion.
        var translator = new LineProbeTranslator(
            (_, _, _) => throw new DllNotFoundException(),
            typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 12);

        result.Status.Should().Be(LineProbeResolutionStatus.ProfilerMissingLineProbeSupport);
    }

    [Fact]
    public void ApplyLineProbe_TypeNotLoadedYet_IsRetryableAndDoesNotCallNative()
    {
        // Must stay RETRYABLE and must not be reported as an ERROR: the customer's assembly may simply
        // not have been JITted yet. Reporting an error here would mark a valid probe permanently failed.
        var called = false;
        var translator = new LineProbeTranslator((_, _, _) => called = true, typeResolver: NeverResolves);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 13);

        result.Status.Should().Be(LineProbeResolutionStatus.TypeNotLoaded);
        called.Should().BeFalse("nothing should be handed to the profiler when the target is not loaded");
    }

    [Fact]
    public void ApplyLineProbe_MethodLevelConfig_IsRejectedBeforeAnyResolutionWork()
    {
        // REJECT SITE 1. A LineNumber of 0 is a METHOD-level config; it must never reach AddLineProbes,
        // which has no method-boundary emission path at all.
        //
        // MUTATION NOTE — the obvious form of this test is vacuous. Asserting only "native was not called"
        // still passes when the `!config.IsLineLevel` guard is DELETED, because PdbReader independently
        // refuses line 0 and the definition is never built. Verified by deliberately removing the guard:
        // the suite stayed green. So the assertion that actually discriminates is that the guard fires
        // FIRST — before any type resolution or PDB read is attempted. A resolver that throws when touched
        // is the cheapest way to prove that ordering.
        var called = false;
        var translator = new LineProbeTranslator(
            (_, _, _) => called = true,
            typeResolver: _ => throw new InvalidOperationException(
                "a non-line-level config must be rejected before resolution is attempted"));

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), lineNumber: 0), probeId: 14);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
        called.Should().BeFalse("a method-level config must not reach the line-probe export");
    }

    [Fact]
    public void ApplyLineProbe_ProbeTypeConfigIsMethodLevelByConstruction_AndIsRejected()
    {
        // REJECT SITE 2, and it guards a real landmine (D2): InstrumentationConfiguration discards the
        // line number when Type == PROBE. A line fixture served as PROBE therefore becomes method-level
        // SILENTLY, and a contract test written against it would pass for the wrong reason. Asserted here
        // so the constraint is pinned in code rather than living only in a plan document.
        var called = false;
        var translator = new LineProbeTranslator(
            (_, _, _) => called = true,
            typeResolver: _ => throw new InvalidOperationException(
                "a PROBE config must be rejected before resolution is attempted"));

        // LineNumber is left at its default 0 to model what the parser produces for a PROBE: it discards
        // the requested line at parse time (InstrumentationConfiguration.cs, `LineNumber = type == PROBE ? 0 : lineNumber`).
        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = TargetTypeName[..TargetTypeName.LastIndexOf('.')],
            ClassName = TargetTypeName[(TargetTypeName.LastIndexOf('.') + 1)..],
            MethodName = nameof(PdbReaderTargets.ThreeStatements),
            LocationHash = "aabb000000000002",
            Capture = CaptureConfiguration.Default,
        };

        var result = translator.ApplyLineProbe(config, probeId: 15);

        config.IsLineLevel.Should().BeFalse("a PROBE cannot be line-level; the parser zeroes LineNumber");
        result.IsResolved.Should().BeFalse();
        called.Should().BeFalse();
    }

    [Fact]
    public void ApplyLineProbe_NullConfig_FailsClosedWithoutDereferencingIt()
    {
        // The null check must precede everything: a resolver that touches the config would NullReference
        // out of a poll loop rather than returning a typed failure.
        var called = false;
        var translator = new LineProbeTranslator(
            (_, _, _) => called = true,
            typeResolver: _ => throw new InvalidOperationException("null must be rejected first"));

        var apply = () => translator.ApplyLineProbe(null!, probeId: 16);

        apply.Should().NotThrow("a null config is a caller bug we absorb, not one we propagate");
        apply().IsResolved.Should().BeFalse();
        called.Should().BeFalse();
    }

    [Fact]
    public void ApplyLineProbe_UnresolvableLine_DoesNotCallNative()
    {
        // A line with no executable statement is permanent misconfiguration. Weaving nothing is correct;
        // the point of the assertion is that we do not hand the native side a bogus offset to reject.
        var called = false;
        var translator = new LineProbeTranslator((_, _, _) => called = true, typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            LineConfig(nameof(PdbReaderTargets.ThreeStatements), lineNumber: 999_999), probeId: 17);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
        called.Should().BeFalse();
    }

    [Fact]
    public void RemoveLineProbe_ForwardsTheProbeIdToNative()
    {
        int? removed = null;
        var translator = new LineProbeTranslator(
            (_, _, _) => { }, removeLineProbeOverride: id => removed = id, typeResolver: ResolvesToFixture);

        var ok = translator.RemoveLineProbe(77);

        ok.Should().BeTrue();
        removed.Should().Be(77, "the native side reverts by probeId");
    }

    [Fact]
    public void RemoveLineProbe_StockProfiler_ReportsFailureRatherThanThrowing()
    {
        // Nothing was ever woven if the export is missing, so a failed removal is consistent rather than
        // alarming — but it must not throw out of a teardown path.
        var translator = new LineProbeTranslator(
            (_, _, _) => { },
            removeLineProbeOverride: _ => throw new EntryPointNotFoundException(),
            typeResolver: ResolvesToFixture);

        translator.RemoveLineProbe(78).Should().BeFalse();
    }

    // ── MULTI-LOCAL CAPTURE ──────────────────────────────────────────────────────
    // N probes at ONE offset, one per captured local — not one probe carrying an object[]. The native side
    // dedups requests by (offset, probeId), so distinct ids at the same offset are accepted, and every emit
    // is InsertBefore(targetInstr), so the sequences chain in order. All N ship in ONE AddLineProbes call,
    // which is also what closes the batching gap: one Import/Export per method rather than N.

    private static InstrumentationConfiguration MultiLocalConfig(
        string methodName, int lineNumber, params string[] locals) =>
        new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = TargetTypeName[..TargetTypeName.LastIndexOf('.')],
            ClassName = TargetTypeName[(TargetTypeName.LastIndexOf('.') + 1)..],
            MethodName = methodName,
            LineNumber = lineNumber,
            LocationHash = "aabb000000000002",
            Capture = CaptureConfiguration.Default with { CaptureLocals = locals },
        };

    [Fact]
    public void ApplyLineProbe_WithSeveralLocals_EmitsOneDefinitionPerLocalInASingleCall()
    {
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);
        var nextId = 500;

        var result = translator.ApplyLineProbe(
            MultiLocalConfig(
                nameof(PdbReaderTargets.MixedLocalTypes),
                PdbReaderTargets.LineOf("mixedItems"),
                "number", "text", "ratio"),
            probeId: 42,
            allocateProbeId: () => nextId++);

        result.IsResolved.Should().BeTrue($"expected resolution, got {result.Status}: {result.Detail}");

        var captured = read();
        captured.Should().NotBeNull();

        // ONE native call carrying THREE definitions — batching, not three separate P/Invokes.
        captured!.Size.Should().Be(3);
        captured.Definitions.Should().HaveCount(3);

        // Every probe targets the SAME offset; only the local slot and box type differ.
        captured.Definitions.Select(d => d.IlOffset).Distinct().Should().HaveCount(
            1, "all locals are captured at the same line, so all probes share one IL offset");

        // Distinct ids: the callback receives only the probeId, so two probes sharing one id would be
        // indistinguishable and one local's value would be attributed to the other's name.
        captured.Definitions.Select(d => d.ProbeId).Should().OnlyHaveUniqueItems();
        captured.Definitions[0].ProbeId.Should().Be(42, "the first probe uses the caller-supplied id");

        // Each local keeps its OWN declared type — which is the point of N probes over one object[].
        captured.Definitions.Select(d => d.LocalTypeName).Should()
            .BeEquivalentTo(["System.Int32", "System.String", "System.Double"]);

        // And the resolution reports every applied probe so the caller can register each id to its local.
        result.Locations.Should().HaveCount(3);
        result.Locations.Select(l => l.Location.LocalName).Should()
            .BeEquivalentTo(["number", "text", "ratio"]);
        result.Locations.Select(l => l.ProbeId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ApplyLineProbe_WithNoAllocator_CapturesOnlyTheFirstLocal()
    {
        // Back-compat: a caller that supplies no id allocator cannot be given extra ids, so it gets the
        // historical CaptureLocals[0] behavior instead of N probes sharing one id.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            MultiLocalConfig(
                nameof(PdbReaderTargets.MixedLocalTypes),
                PdbReaderTargets.LineOf("mixedItems"),
                "number", "text"),
            probeId: 7);

        result.IsResolved.Should().BeTrue();
        read()!.Size.Should().Be(1);
        result.Locations.Should().HaveCount(1);
        result.Locations[0].Location.LocalName.Should().Be("number");
    }

    [Fact]
    public void ApplyLineProbe_WithOneUnresolvableLocal_StillAppliesTheOthers()
    {
        // PARTIAL SUCCESS. One bad name among several must not discard the locals that DID resolve — the
        // operator gets what is capturable rather than nothing at all.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);
        var nextId = 600;

        var result = translator.ApplyLineProbe(
            MultiLocalConfig(
                nameof(PdbReaderTargets.MixedLocalTypes),
                PdbReaderTargets.LineOf("mixedItems"),
                "number", "noSuchLocal", "text"),
            probeId: 11,
            allocateProbeId: () => nextId++);

        result.IsResolved.Should().BeTrue("the resolvable locals must still be applied");
        read()!.Size.Should().Be(2);
        result.Locations.Select(l => l.Location.LocalName).Should().BeEquivalentTo(["number", "text"]);
    }

    [Fact]
    public void ApplyLineProbe_WhenNoLocalResolves_FailsWithTheRealCause()
    {
        // All names bad → report the underlying resolution status, not a generic one, so the operator's
        // ErrorCause names the actual problem.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);
        var nextId = 700;

        var result = translator.ApplyLineProbe(
            MultiLocalConfig(
                nameof(PdbReaderTargets.MixedLocalTypes),
                PdbReaderTargets.LineOf("mixedItems"),
                "nope", "alsoNope"),
            probeId: 12,
            allocateProbeId: () => nextId++);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LocalOutOfScope);
        read().Should().BeNull("nothing may be woven when no local resolved");
    }

    [Fact]
    public void ApplyLineProbe_CapsTheNumberOfLocalsPerLine()
    {
        // Each captured local is an extra `call` on the customer's line, so a long list is a self-inflicted
        // performance problem. Extra names are DROPPED rather than refused — the first N are more useful to
        // an operator than an error.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);
        var nextId = 800;

        // Six names against a cap of five; all six exist in the fixture.
        var result = translator.ApplyLineProbe(
            MultiLocalConfig(
                nameof(PdbReaderTargets.MixedLocalTypes),
                PdbReaderTargets.LineOf("mixedItems"),
                "number", "text", "ratio", "stamp", "boxed", "items"),
            probeId: 13,
            allocateProbeId: () => nextId++);

        result.IsResolved.Should().BeTrue();
        read()!.Size.Should().Be(
            LineProbeTranslator.MaxLocalsPerLine,
            "the cap bounds the per-hit cost on the customer's line");
    }

    [Fact]
    public void ApplyLineProbe_WithNoLocalsRequested_StillAppliesABareLineProbe()
    {
        // A probe with no capture still records that the line was REACHED, which is a legitimate use.
        var (seam, read) = Spy();
        var translator = new LineProbeTranslator(seam(), typeResolver: ResolvesToFixture);

        var result = translator.ApplyLineProbe(
            MultiLocalConfig(nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB")),
            probeId: 14,
            allocateProbeId: () => 900);

        result.IsResolved.Should().BeTrue();
        read()!.Size.Should().Be(1);
        result.Locations.Should().HaveCount(1);
        result.Locations[0].Location.LocalName.Should().BeNull();
        result.Locations[0].Location.LocalSlot.Should().Be(-1);
    }

    [Fact]
    public void BuildSignatureTypes_IsAllWildcards_MatchingTheFunctionLevelConvention()
    {
        // Individual entries are never resolved by the native side (it matches on length), so wildcards
        // avoid having to render type names that would be ignored. Same convention as ProfilerTranslator.
        LineProbeTranslator.BuildSignatureTypes(0).Should().Equal("_");
        LineProbeTranslator.BuildSignatureTypes(3).Should().Equal("_", "_", "_", "_");
    }
}
