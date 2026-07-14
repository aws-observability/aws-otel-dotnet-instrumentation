// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using OpenTelemetry.AutoInstrumentation.CallTarget;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

// End-to-end test of the capture pipeline: OnMethodBegin -> DIDataStore -> OnMethodEnd
// -> drain. Exercises DiIntegrationHelper against a real InstrumentationRegistry with no
// profiler. Static state (_registry, DIDataStore queue/AsyncLocal) is mutated, so these
// run serially and clear state per test.
[Collection("SerialProcessState")]
public class CapturePipelineTests : IDisposable
{
    // The target type: its FullName must equal CodeUnit + "." + ClassName so
    // ResolveInstrumentationKey<TTarget> matches the registered config.
    // Namespace here is the test namespace; class is CaptureTarget.
    private const string CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation";
    private const string ClassName = "CaptureTarget";

    public CapturePipelineTests() => DIDataStore.Clear();

    public void Dispose() => DIDataStore.Clear();

    private static InstrumentationRegistry RegistryWith(CaptureConfiguration capture, string method = "Process")
    {
        var registry = new InstrumentationRegistry();
        registry.Register(new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = CodeUnit,
            ClassName = ClassName,
            MethodName = method,
            LocationHash = "loc-hash-1",
            Capture = capture
        });
        DiIntegrationHelper.Configure(registry);
        return registry;
    }

    [Fact]
    public void FullCycle_CapturesArguments_ReturnValue_Duration()
    {
        RegistryWith(CaptureConfiguration.Default);
        var target = new CaptureTarget();

        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "ORD-1", 5 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "result", null, in state);

        var drained = DIDataStore.Drain();
        drained.Should().HaveCount(1);
        var cap = drained[0];
        cap.Type.Should().Be(CaptureType.METHOD);
        cap.LocationHash.Should().Be("loc-hash-1");
        cap.Arguments.Should().NotBeNull();
        cap.Arguments!["arg0"].Value.Should().Be("ORD-1");
        cap.Arguments["arg1"].Value.Should().Be("5");
        cap.ReturnValue.Should().NotBeNull();
        cap.ReturnValue!.Value.Should().Be("result");
        cap.Exception.Should().BeNull();
        cap.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        cap.ThreadId.Should().Be(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void FullCycle_CapturesException()
    {
        RegistryWith(CaptureConfiguration.Default);
        var target = new CaptureTarget();
        var ex = new InvalidOperationException("boom");

        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "x", 1 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, null!, ex, in state);

        var cap = DIDataStore.Drain().Single();
        cap.Exception.Should().NotBeNull();
        cap.Exception!.Type.Should().Be("System.InvalidOperationException");
        cap.Exception.Value.Should().Be("boom");

        // Exception stack is now stored as structured frames (not a raw string). This exception was
        // never thrown, so its frame list may be empty — the filtering guarantee is asserted in
        // FullCycle_Exception_TruncatesMessage_AndCapsFrames, which throws for real.
        cap.Exception.StackFrames.Should().NotBeNull();
    }

    [Fact]
    public void FullCycle_Exception_TruncatesMessage_AndCapsFrames()
    {
        var capture = CaptureConfiguration.Default with { MaxStringLength = 10, MaxStackFrames = 2 };
        RegistryWith(capture);
        var target = new CaptureTarget();

        // Build a genuinely-thrown exception so it has a real (deep) stack trace to filter+cap.
        Exception ex;
        try
        {
            throw new InvalidOperationException(new string('x', 500));
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "x", 1 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, null!, ex, in state);

        var cap = DIDataStore.Drain().Single();
        cap.Exception!.Value.Should().HaveLength(10, "message is truncated to MaxStringLength");
        cap.Exception.Truncated.Should().BeTrue();
        cap.Exception.StackFrames.Should().NotBeNull();
        cap.Exception.StackFrames!.Length.Should().BeLessThanOrEqualTo(2, "frames capped at MaxStackFrames");

        // Whatever frames survive have all passed the internal-frame filter shared with the entry
        // stack (BuildFrames) — none are agent/runtime-internal frames.
        cap.Exception.StackFrames.Should()
            .NotContain(f => f.MethodName!.StartsWith("AWS.Distro.OpenTelemetry.DynamicInstrumentation")
                          || f.MethodName.StartsWith("System.Runtime")
                          || f.MethodName.StartsWith("System.Threading"));
    }

    [Fact]
    public void NamedArguments_CaptureOnlyNamedSubset()
    {
        var capture = CaptureConfiguration.Default with { CaptureArguments = new[] { "orderId", "quantity" } };
        RegistryWith(capture);
        var target = new CaptureTarget();

        // Three args passed, but only two named — third must be dropped.
        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "ORD", 5, "extra" });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "r", null, in state);

        var cap = DIDataStore.Drain().Single();
        cap.Arguments!.Keys.Should().BeEquivalentTo(new[] { "orderId", "quantity" });
        cap.Arguments["orderId"].Value.Should().Be("ORD");
        cap.Arguments["quantity"].Value.Should().Be("5");
    }

    [Fact]
    public void CaptureReturnFalse_OmitsReturnValue()
    {
        var capture = CaptureConfiguration.Default with { CaptureReturn = false };
        RegistryWith(capture);
        var target = new CaptureTarget();

        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "x", 1 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "secret", null, in state);

        DIDataStore.Drain().Single().ReturnValue.Should().BeNull();
    }

    [Fact]
    public void UnregisteredType_DoesNotCapture()
    {
        RegistryWith(CaptureConfiguration.Default);
        var stranger = new UnrelatedTarget();

        var state = DiIntegrationHelper.OnMethodBegin<UnrelatedTarget>(stranger, new object?[] { 1 });
        DiIntegrationHelper.OnMethodEnd<UnrelatedTarget, int>(stranger, 0, null, in state);

        DIDataStore.Drain().Should().BeEmpty();
    }

    [Fact]
    public void MaxHitsExceeded_StopsCapturing()
    {
        var capture = new CaptureConfiguration(
            CaptureArguments: Array.Empty<string>(), CaptureLocals: null,
            CaptureReturn: true, CaptureStackTrace: false,
            MaxStringLength: 255, MaxCollectionWidth: 20, MaxCollectionDepth: 3,
            MaxObjectDepth: 3, MaxFieldsPerObject: 20, MaxStackFrames: 20, MaxHits: 2);
        RegistryWith(capture);
        var target = new CaptureTarget();

        for (int i = 0; i < 5; i++)
        {
            var s = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "x", i });
            DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "r", null, in s);
        }

        // maxHits=2 → only 2 captures should have proceeded.
        DIDataStore.Drain().Should().HaveCount(2);
    }

    [Fact]
    public void CapturesTraceContext_WhenActivityPresent()
    {
        RegistryWith(CaptureConfiguration.Default);
        var target = new CaptureTarget();

        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("op");
        var state = DiIntegrationHelper.OnMethodBegin<CaptureTarget>(target, new object?[] { "x", 1 });
        DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(target, "r", null, in state);

        var cap = DIDataStore.Drain().Single();
        cap.TraceId.Should().NotBeNullOrEmpty();
        cap.SpanId.Should().NotBeNullOrEmpty();
        cap.TraceId.Should().Be(activity!.TraceId.ToHexString());
    }

    [Fact]
    public void OnMethodEnd_WithoutMatchingBegin_DoesNotThrowOrEnqueue()
    {
        RegistryWith(CaptureConfiguration.Default);
        var target = new CaptureTarget();

        // Default state (no begin recorded) → end should be a no-op.
        var act = () => { _ = DiIntegrationHelper.OnMethodEnd<CaptureTarget, string>(
            target, "r", null, in CallTargetStateHolder.Default); };
        act.Should().NotThrow();
        DIDataStore.Drain().Should().BeEmpty();
    }
}

// Target types for the pipeline tests. FullName of CaptureTarget must equal
// CodeUnit + ".CaptureTarget".
public class CaptureTarget
{
    public string Process(string orderId, int quantity) => $"{orderId}:{quantity}";
}

public class UnrelatedTarget
{
    public int Compute(int x) => x;
}

// Helper to obtain a default CallTargetState for the "no matching begin" test.
internal static class CallTargetStateHolder
{
    public static readonly CallTargetState Default = CallTargetState.GetDefault();
}
