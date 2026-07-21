// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.AutoInstrumentation.CallTarget;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;

/// <summary>
/// CallTarget integration for methods with 6 parameter(s).
/// </summary>
internal static class DiIntegration6
{
    internal static CallTargetState OnMethodBegin<TTarget, TArg1, TArg2, TArg3, TArg4, TArg5, TArg6>(TTarget instance, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5, TArg6 arg6)
    {
        return DiIntegrationHelper.OnMethodBegin(instance, new object?[] { arg1, arg2, arg3, arg4, arg5, arg6 });
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
