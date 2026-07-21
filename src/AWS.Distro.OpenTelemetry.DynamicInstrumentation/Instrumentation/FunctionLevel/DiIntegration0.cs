// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.AutoInstrumentation.CallTarget;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;

/// <summary>
/// CallTarget integration for methods with 0 parameter(s).
/// </summary>
internal static class DiIntegration0
{
    internal static CallTargetState OnMethodBegin<TTarget>(TTarget instance)
    {
        return DiIntegrationHelper.OnMethodBegin(instance, Array.Empty<object?>());
    }

    internal static CallTargetReturn<TReturn> OnMethodEnd<TTarget, TReturn>(
        TTarget instance, TReturn returnValue, Exception? exception, in CallTargetState state)
    {
        return DiIntegrationHelper.OnMethodEnd(instance, returnValue, exception, in state);
    }

    // Void-returning target methods: profiler requires the non-generic CallTargetReturn.
    internal static CallTargetReturn OnMethodEnd<TTarget>(
        TTarget instance, Exception? exception, in CallTargetState state)
    {
        return DiIntegrationHelper.OnMethodEnd(instance, exception, in state);
    }
}
