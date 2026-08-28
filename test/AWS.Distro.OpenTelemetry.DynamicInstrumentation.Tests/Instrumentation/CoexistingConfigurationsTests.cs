// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

/// <summary>
/// Several configurations on ONE method: a PROBE and a method-level BREAKPOINT, which the backend issues as
/// two independent configurations with their own LocationHash, capture policy and MaxHits budget.
/// </summary>
// WHY THIS EXISTS. Both carry LineNumber 0, so a key of type+method alone made them collide and the
// registry's AddOrUpdate silently discarded whichever registered first: one snapshot stream for two
// configurations, no error anywhere, and the discarded config never reporting status. Found by the DI
// contract tests (DotnetDynamicInstrumentationProbeAndBreakpointTest, 6 failures), which the method-level
// demo could not find because its two configs sit on different methods.
[Collection("SerialProcessState")]
public class CoexistingConfigurationsTests : IDisposable
{
    private const string CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation";
    private const string ClassName = "CaptureTarget";

    public CoexistingConfigurationsTests() => DIDataStore.Clear();

    public void Dispose() => DIDataStore.Clear();

    [Fact]
    public void ProbeAndBreakpointOnOneMethod_AreTwoDistinctRegistrations()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(Config(InstrumentationType.PROBE, "loc-probe"));
        registry.Register(Config(InstrumentationType.BREAKPOINT, "loc-breakpoint"));

        registry.Count.Should().Be(2, "a PROBE and a BREAKPOINT on one method are two configurations");
        registry.GetAll().Select(r => r.Config.LocationHash)
            .Should().BeEquivalentTo(new[] { "loc-probe", "loc-breakpoint" });
    }

    [Fact]
    public void OneCall_ProducesOneSnapshotPerConfiguration_EachWithItsOwnIdentity()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(Config(InstrumentationType.PROBE, "loc-probe"));
        registry.Register(Config(InstrumentationType.BREAKPOINT, "loc-breakpoint"));
        DiIntegrationHelper.Configure(registry);

        var target = new CaptureTarget();
        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "ORD-1", 5 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "result", null, in state);

        var drained = DIDataStore.Drain();
        drained.Should().HaveCount(2, "one invocation owes a snapshot to each configuration watching it");

        // The identities an operator tells the two apart by in the console.
        drained.Select(c => c.LocationHash).Should().BeEquivalentTo(new[] { "loc-probe", "loc-breakpoint" });

        // Both must carry the real capture, not just exist: a fan-out that emitted an empty second snapshot
        // would satisfy a count-only assertion.
        // CaptureConfiguration.Default carries an empty argument filter, which captures every argument as
        // arg0..argN rather than by name.
        foreach (var capture in drained)
        {
            capture.Arguments.Should().ContainKey("arg0");
            capture.Arguments!["arg0"].Value.Should().Be("ORD-1");
            capture.ReturnValue!.Value.Should().Be("result");
        }
    }

    [Fact]
    public void OneCall_FansOutOnTheARITYPath_TheOneProductionUses()
    {
        // THE PATH THAT ACTUALLY RUNS IN PRODUCTION. Resolution is (type, arity) first and falls back to
        // type-only only for a type the profiler has never woven. A test that skips IndexArities therefore
        // exercises the fallback, and would stay green while the arity path still served one config —
        // measured: reverting ResolveKeysByTypeAndArity to "first key wins" left every other test here
        // passing.
        var registry = new InstrumentationRegistry();
        var probe = Config(InstrumentationType.PROBE, "loc-probe");
        var breakpoint = Config(InstrumentationType.BREAKPOINT, "loc-breakpoint");
        registry.Register(probe);
        registry.Register(breakpoint);
        registry.IndexArities(probe.TypeName, probe.InstrumentationKey, new[] { 2 });
        registry.IndexArities(breakpoint.TypeName, breakpoint.InstrumentationKey, new[] { 2 });
        DiIntegrationHelper.Configure(registry);

        registry.HasArityIndex(probe.TypeName).Should().BeTrue("otherwise this is the fallback path again");

        var target = new CaptureTarget();
        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "ORD-1", 5 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "result", null, in state);

        DIDataStore.Drain().Select(c => c.LocationHash)
            .Should().BeEquivalentTo(new[] { "loc-probe", "loc-breakpoint" });
    }

    [Fact]
    public void EachConfigurationSpendsItsOwnHitBudget()
    {
        // MaxHits is per configuration. A BREAKPOINT that has exhausted its budget must stop capturing while
        // the PROBE on the same method keeps going — otherwise the budgets were never really separate.
        var registry = new InstrumentationRegistry();
        registry.Register(Config(InstrumentationType.PROBE, "loc-probe"));
        registry.Register(Config(
            InstrumentationType.BREAKPOINT,
            "loc-breakpoint",
            CaptureConfiguration.Default with { MaxHits = 1 }));
        DiIntegrationHelper.Configure(registry);

        var target = new CaptureTarget();
        for (var i = 0; i < 3; i++)
        {
            var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "ORD-1", 5 });
            DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "result", null, in state);
        }

        var byHash = DIDataStore.Drain().GroupBy(c => c.LocationHash).ToDictionary(g => g.Key, g => g.Count());
        byHash["loc-probe"].Should().Be(3, "a PROBE has no hit budget");
        byHash["loc-breakpoint"].Should().Be(1, "the BREAKPOINT's MaxHits=1 must bind only itself");
    }

    [Fact]
    public void SameMethodConfigurations_AreNotReportedAsAnOverloadCollision()
    {
        // IndexArities reports OVERLOADED_METHODS for keys it cannot separate by args.Length. Configurations
        // on the SAME method share an arity bucket too, but they are not ambiguous — reporting them would
        // fail the very configurations that now work.
        var registry = new InstrumentationRegistry();
        var probe = Config(InstrumentationType.PROBE, "loc-probe");
        var breakpoint = Config(InstrumentationType.BREAKPOINT, "loc-breakpoint");
        registry.Register(probe);
        registry.Register(breakpoint);

        registry.IndexArities(probe.TypeName, probe.InstrumentationKey, new[] { 2 })
            .Should().BeEmpty();
        registry.IndexArities(breakpoint.TypeName, breakpoint.InstrumentationKey, new[] { 2 })
            .Should().BeEmpty("both configurations target one method, so arity resolution is not ambiguous");
    }

    [Fact]
    public void DifferentMethodsSharingAnArity_AreStillReportedAsACollision()
    {
        // The guard that keeps the fix narrow: two different methods at the same arity remain genuinely
        // indistinguishable in the callback, and both must still be reported.
        var registry = new InstrumentationRegistry();
        var process = Config(InstrumentationType.PROBE, "loc-process", method: "Process");
        var validate = Config(InstrumentationType.PROBE, "loc-validate", method: "Validate");
        registry.Register(process);
        registry.Register(validate);

        registry.IndexArities(process.TypeName, process.InstrumentationKey, new[] { 1 })
            .Should().BeEmpty();
        registry.IndexArities(validate.TypeName, validate.InstrumentationKey, new[] { 1 })
            .Should().BeEquivalentTo(new[] { process.InstrumentationKey, validate.InstrumentationKey });
    }

    [Fact]
    public void RemovingOneConfiguration_LeavesTheOtherCapturing()
    {
        var registry = new InstrumentationRegistry();
        var probe = Config(InstrumentationType.PROBE, "loc-probe");
        var breakpoint = Config(InstrumentationType.BREAKPOINT, "loc-breakpoint");
        registry.Register(probe);
        registry.Register(breakpoint);
        DiIntegrationHelper.Configure(registry);

        registry.RemoveStale(new HashSet<string> { probe.InstrumentationKey })
            .Select(c => c.LocationHash).Should().BeEquivalentTo(new[] { "loc-breakpoint" });

        var target = new CaptureTarget();
        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "ORD-1", 5 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "result", null, in state);

        DIDataStore.Drain().Select(c => c.LocationHash)
            .Should().BeEquivalentTo(new[] { "loc-probe" }, "removing the BREAKPOINT must leave the PROBE capturing");
    }

    private static InstrumentationConfiguration Config(
        InstrumentationType type,
        string locationHash,
        CaptureConfiguration? capture = null,
        string method = "Process") =>
        new()
        {
            Type = type,
            CodeUnit = CodeUnit,
            ClassName = ClassName,
            MethodName = method,
            LocationHash = locationHash,
            Capture = capture ?? CaptureConfiguration.Default,
        };
}
