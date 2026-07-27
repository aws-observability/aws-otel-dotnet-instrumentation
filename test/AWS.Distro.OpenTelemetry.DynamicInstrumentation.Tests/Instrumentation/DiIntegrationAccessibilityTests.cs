// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

/// <summary>
/// Regression guard for a GA-blocking MethodAccessException. The native profiler bakes the
/// DiIntegrationN type as a generic type ARGUMENT into the target (customer) assembly's rewritten IL
/// (CallTargetInvoker.LogException&lt;DiIntegrationN, TTarget&gt;). Calling a generic method requires
/// every type argument to be accessible from the call site — so if DiIntegrationN is internal, the
/// customer assembly cannot reference it and the JIT throws MethodAccessException at the first woven
/// call. These must therefore be public. Verified end to end by poc/DeployedAppDemo/run-e2e-linux.sh;
/// this test fails fast in CI if a refactor silently reverts the accessibility.
/// (Asserted via reflection, not compile-time access — the test project has an InternalsVisibleTo
/// grant, so a direct reference would compile even if the types were internal.)
/// </summary>
public class DiIntegrationAccessibilityTests
{
    [Fact]
    public void AllDiIntegrationTypes_MustBePublic()
    {
        var assembly = typeof(DiIntegration0).Assembly;

        for (int arity = 0; arity <= 9; arity++)
        {
            var typeName = $"AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel.DiIntegration{arity}";
            var type = assembly.GetType(typeName);

            type.Should().NotBeNull($"{typeName} must exist (profiler binds by this exact type name)");
            type!.IsPublic.Should().BeTrue(
                $"{typeName} is baked as a generic type argument into the customer assembly's rewritten IL; " +
                "an internal type throws MethodAccessException at the first woven call");
        }
    }
}
