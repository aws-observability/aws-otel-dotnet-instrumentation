// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

public class ProfilerTranslatorTests
{
    private static InstrumentationConfiguration CreateConfig(
        string methodName = "Process",
        int lineNumber = 0,
        InstrumentationType type = InstrumentationType.PROBE) =>
        new()
        {
            Type = type,
            CodeUnit = "MyApp.Services",
            ClassName = "OrderService",
            MethodName = methodName,
            LineNumber = lineNumber,
            LocationHash = "aabb000000000001",
            Capture = CaptureConfiguration.Default
        };

    // A method resolver stub that reports a fixed set of overload arities and assembly name.
    private static Func<InstrumentationConfiguration, MethodResolution?> Resolver(
        string assembly, params int[] arities) =>
        _ => new MethodResolution(assembly, arities.ToHashSet());

    private static readonly Func<InstrumentationConfiguration, MethodResolution?> Unresolvable = _ => null;

    [Fact]
    public void ApplyInstrumentation_MethodLevel_CallsAddInstrumentations()
    {
        string? capturedId = null;
        NativeCallTargetDefinition[]? capturedDefs = null;

        var translator = new ProfilerTranslator(
            (id, defs, size) => { capturedId = id; capturedDefs = defs; },
            Resolver("MyApp", 2));

        var result = translator.ApplyInstrumentation(CreateConfig());

        result.Should().Be(InstrumentationApplyResult.Applied);
        capturedId.Should().Be("aabb000000000001");
        capturedDefs.Should().NotBeNull();
        capturedDefs!.Length.Should().Be(1);
        capturedDefs[0].TargetType.Should().Be("MyApp.Services.OrderService");
        capturedDefs[0].TargetMethod.Should().Be("Process");
        capturedDefs[0].TargetAssembly.Should().Be("MyApp");
    }

    [Fact]
    public void ApplyInstrumentation_ResolvesArity_PicksMatchingIntegration()
    {
        string? integrationType = null;
        var translator = new ProfilerTranslator(
            (_, defs, _) => integrationType = defs[0].IntegrationType,
            Resolver("MyApp", 2));

        translator.ApplyInstrumentation(CreateConfig());

        integrationType.Should().EndWith("DiIntegration2");
    }

    [Fact]
    public void ApplyInstrumentation_ZeroArity_UsesDiIntegration0()
    {
        string? integrationType = null;
        var translator = new ProfilerTranslator(
            (_, defs, _) => integrationType = defs[0].IntegrationType,
            Resolver("MyApp", 0));

        translator.ApplyInstrumentation(CreateConfig());

        integrationType.Should().EndWith("DiIntegration0");
    }

    [Fact]
    public void ApplyInstrumentation_SignatureArrayLength_MatchesArityPlusOne()
    {
        // Verified against the real profiler: it selects by signature-array length
        // (arity + 1), with "_" wildcards for the individual types.
        NativeCallTargetDefinition[]? defs = null;
        var translator = new ProfilerTranslator((_, d, _) => defs = d, Resolver("MyApp", 3));

        translator.ApplyInstrumentation(CreateConfig());

        defs![0].TargetSignatureTypesLength.Should().Be(4); // return + 3 params
    }

    [Fact]
    public void ApplyInstrumentation_Overloads_RegistersOneDefinitionPerArity()
    {
        NativeCallTargetDefinition[]? defs = null;
        var translator = new ProfilerTranslator((_, d, _) => defs = d, Resolver("MyApp", 1, 2, 3));

        var result = translator.ApplyInstrumentation(CreateConfig());

        result.Should().Be(InstrumentationApplyResult.Applied);
        defs!.Length.Should().Be(3);
        defs.Select(d => (int)d.TargetSignatureTypesLength).Should().BeEquivalentTo(new[] { 2, 3, 4 });
    }

    [Fact]
    public void ApplyInstrumentation_UnresolvableTarget_ReportsTypeNotLoaded()
    {
        bool registered = false;
        var translator = new ProfilerTranslator((_, _, _) => registered = true, Unresolvable);

        var result = translator.ApplyInstrumentation(CreateConfig());

        // A null resolution means the type isn't in any loaded assembly — transient, retry-worthy.
        result.Should().Be(InstrumentationApplyResult.TypeNotLoaded);
        registered.Should().BeFalse(); // never registers when arity can't be resolved
    }

    [Fact]
    public void ApplyInstrumentation_MethodMissingOnLoadedType_ReportsMethodNotFound()
    {
        // Type resolved (non-null) but exposes no method by that name (empty arities) — a genuine
        // misconfiguration, distinct from a not-yet-loaded type.
        bool registered = false;
        var translator = new ProfilerTranslator(
            (_, _, _) => registered = true,
            _ => new MethodResolution("MyApp", new HashSet<int>()));

        var result = translator.ApplyInstrumentation(CreateConfig());

        result.Should().Be(InstrumentationApplyResult.MethodNotFound);
        registered.Should().BeFalse();
    }

    [Fact]
    public void ApplyInstrumentation_ArityExceedsMax_ReportsNoSupportedArity()
    {
        bool registered = false;
        var translator = new ProfilerTranslator((_, _, _) => registered = true, Resolver("MyApp", 10));

        var result = translator.ApplyInstrumentation(CreateConfig());

        result.Should().Be(InstrumentationApplyResult.NoSupportedArity);
        registered.Should().BeFalse();
    }

    [Fact]
    public void ApplyInstrumentation_MixedArities_SkipsOverMaxKeepsValid()
    {
        NativeCallTargetDefinition[]? defs = null;
        var translator = new ProfilerTranslator((_, d, _) => defs = d, Resolver("MyApp", 2, 15));

        var result = translator.ApplyInstrumentation(CreateConfig());

        result.Should().Be(InstrumentationApplyResult.Applied);
        defs!.Length.Should().Be(1); // arity 15 dropped, arity 2 kept
        defs[0].TargetSignatureTypesLength.Should().Be(3);
    }

    [Fact]
    public void ApplyInstrumentation_LineLevelConfig_ReportsSkipped()
    {
        var translator = new ProfilerTranslator((_, _, _) => { }, Resolver("MyApp", 2));
        var result = translator.ApplyInstrumentation(CreateConfig(lineNumber: 42));
        result.Should().Be(InstrumentationApplyResult.Skipped);
    }

    [Fact]
    public void ApplyInstrumentation_Constructor_ReportsSkipped()
    {
        var translator = new ProfilerTranslator((_, _, _) => { }, Resolver("MyApp", 0));
        var result = translator.ApplyInstrumentation(CreateConfig(methodName: ".ctor"));
        result.Should().Be(InstrumentationApplyResult.Skipped);
    }

    [Fact]
    public void ApplyInstrumentation_StaticConstructor_ReportsSkipped()
    {
        var translator = new ProfilerTranslator((_, _, _) => { }, Resolver("MyApp", 0));
        var result = translator.ApplyInstrumentation(CreateConfig(methodName: ".cctor"));
        result.Should().Be(InstrumentationApplyResult.Skipped);
    }

    [Fact]
    public void ApplyInstrumentation_ExceptionInNativeCall_ReportsRuntimeError()
    {
        var translator = new ProfilerTranslator(
            (_, _, _) => throw new DllNotFoundException("profiler not loaded"),
            Resolver("MyApp", 2));

        var result = translator.ApplyInstrumentation(CreateConfig());

        result.Should().Be(InstrumentationApplyResult.RuntimeError);
    }

    [Fact]
    public void IsUnsupportedTarget_Constructor_ReturnsTrue()
    {
        ProfilerTranslator.IsUnsupportedTarget(CreateConfig(methodName: ".ctor")).Should().BeTrue();
    }

    [Fact]
    public void IsUnsupportedTarget_StaticConstructor_ReturnsTrue()
    {
        ProfilerTranslator.IsUnsupportedTarget(CreateConfig(methodName: ".cctor")).Should().BeTrue();
    }

    [Fact]
    public void IsUnsupportedTarget_NormalMethod_ReturnsFalse()
    {
        ProfilerTranslator.IsUnsupportedTarget(CreateConfig(methodName: "ProcessOrder")).Should().BeFalse();
    }

    [Fact]
    public void ReflectionResolver_ResolvesRealType_ByArity()
    {
        // Exercises the real reflection path (no resolver override) against a type in
        // this test assembly. Proves end-to-end arity resolution without the profiler.
        NativeCallTargetDefinition[]? defs = null;
        var translator = new ProfilerTranslator((_, d, _) => defs = d);

        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation",
            ClassName = "ReflectionTarget",
            MethodName = "TwoArgs",
            LocationHash = "hash",
            Capture = CaptureConfiguration.Default
        };

        var result = translator.ApplyInstrumentation(config);

        result.Should().Be(InstrumentationApplyResult.Applied);
        defs!.Length.Should().Be(1);
        defs[0].TargetSignatureTypesLength.Should().Be(3); // return + 2 params
    }

    [Fact]
    public void ReflectionResolver_UnknownType_ReportsTypeNotLoaded()
    {
        var translator = new ProfilerTranslator((_, _, _) => { });
        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "No.Such.Namespace",
            ClassName = "Nope",
            MethodName = "X",
            LocationHash = "hash",
            Capture = CaptureConfiguration.Default
        };

        translator.ApplyInstrumentation(config).Should().Be(InstrumentationApplyResult.TypeNotLoaded);
    }

    [Fact]
    public void ReflectionResolver_KnownTypeMissingMethod_ReportsMethodNotFound()
    {
        // ReflectionTarget exists in this assembly but has no method "DoesNotExist" — the resolver
        // returns a non-null resolution with empty arities, which maps to MethodNotFound (not the
        // transient TypeNotLoaded).
        var translator = new ProfilerTranslator((_, _, _) => { });
        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation",
            ClassName = "ReflectionTarget",
            MethodName = "DoesNotExist",
            LocationHash = "hash",
            Capture = CaptureConfiguration.Default
        };

        translator.ApplyInstrumentation(config).Should().Be(InstrumentationApplyResult.MethodNotFound);
    }
}

// Target type for the real-reflection test above.
public class ReflectionTarget
{
    public string TwoArgs(string a, int b) => $"{a}{b}";
}
