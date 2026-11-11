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
    public void MatchKeyByType_ExactMatch_ReturnsKey()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(Config("MyApp.Services", "OrderService"));

        var key = DiIntegrationHelper.MatchKeyByType("MyApp.Services.OrderService", registry);

        key.Should().Be("MyApp.Services.OrderService.Process");
    }

    [Fact]
    public void MatchKeyByType_SameClassNameDifferentNamespace_DoesNotCollide()
    {
        // Regression for C4: two classes named "Svc" in different namespaces must not
        // collide. Only the exact fully-qualified match should win.
        var registry = new InstrumentationRegistry();
        registry.Register(Config("A", "Svc"));
        registry.Register(Config("B", "Svc"));

        DiIntegrationHelper.MatchKeyByType("A.Svc", registry).Should().Be("A.Svc.Process");
        DiIntegrationHelper.MatchKeyByType("B.Svc", registry).Should().Be("B.Svc.Process");
    }

    [Fact]
    public void MatchKeyByType_SuffixButNotExact_ReturnsNull()
    {
        // "Other.OrderService" must NOT match a registered "MyApp.Services.OrderService"
        // just because the class name suffix lines up.
        var registry = new InstrumentationRegistry();
        registry.Register(Config("MyApp.Services", "OrderService"));

        DiIntegrationHelper.MatchKeyByType("Other.OrderService", registry).Should().BeNull();
    }

    [Fact]
    public void MatchKeyByType_NoMatch_ReturnsNull()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(Config("MyApp", "A"));

        DiIntegrationHelper.MatchKeyByType("MyApp.B", registry).Should().BeNull();
    }
}
