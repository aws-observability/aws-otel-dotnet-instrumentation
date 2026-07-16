// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

/// <summary>Typed outcome of <see cref="ProfilerTranslator.ApplyInstrumentation(Model.InstrumentationConfiguration)"/> that distinguishes a retryable transient failure from a permanent one mapping to a backend InstrumentationErrorCause.</summary>
internal enum InstrumentationApplyResult
{
    /// <summary>At least one definition was registered with the profiler.</summary>
    Applied,

    /// <summary>Intentionally not applied (line-level or unsupported method); not an error and not reported.</summary>
    Skipped,

    /// <summary>Target type not found in any loaded assembly (likely not loaded yet); caller should retry on a later poll and must not report an ERROR.</summary>
    TypeNotLoaded,

    /// <summary>The target type was found but exposes no method with the configured name.</summary>
    MethodNotFound,

    /// <summary>Method resolved but no overload had a profiler-supported arity (0..9 parameters).</summary>
    NoSupportedArity,

    /// <summary>The native AddInstrumentations call threw.</summary>
    RuntimeError,
}
