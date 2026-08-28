// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

public class DiIntegrationHelperTests
{
    private static InstrumentationConfiguration Config(string codeUnit, string className, string method = "Process") =>
        new()
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = codeUnit,
            ClassName = className,
            MethodName = method,
            LocationHash = $"{codeUnit}.{className}",
            Capture = CaptureConfiguration.Default
        };

    [Fact]
    public void MatchKeysByType_ExactMatch_ReturnsKey()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(Config("MyApp.Services", "OrderService"));

        var keys = DiIntegrationHelper.MatchKeysByType("MyApp.Services.OrderService", registry);

        keys.Should().BeEquivalentTo(new[] { "MyApp.Services.OrderService.Process:PROBE" });
    }

    [Fact]
    public void MatchKeysByType_SameClassNameDifferentNamespace_DoesNotCollide()
    {
        // Regression guard: two classes named "Svc" in different namespaces must not
        // collide. Only the exact fully-qualified match should win.
        var registry = new InstrumentationRegistry();
        registry.Register(Config("A", "Svc"));
        registry.Register(Config("B", "Svc"));

        DiIntegrationHelper.MatchKeysByType("A.Svc", registry).Should().BeEquivalentTo(new[] { "A.Svc.Process:PROBE" });
        DiIntegrationHelper.MatchKeysByType("B.Svc", registry).Should().BeEquivalentTo(new[] { "B.Svc.Process:PROBE" });
    }

    [Fact]
    public void MatchKeysByType_SuffixButNotExact_ReturnsNull()
    {
        // "Other.OrderService" must NOT match a registered "MyApp.Services.OrderService"
        // just because the class name suffix lines up.
        var registry = new InstrumentationRegistry();
        registry.Register(Config("MyApp.Services", "OrderService"));

        DiIntegrationHelper.MatchKeysByType("Other.OrderService", registry).Should().BeNull();
    }

    [Fact]
    public void MatchKeysByType_NoMatch_ReturnsNull()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(Config("MyApp", "A"));

        DiIntegrationHelper.MatchKeysByType("MyApp.B", registry).Should().BeNull();
    }
}
