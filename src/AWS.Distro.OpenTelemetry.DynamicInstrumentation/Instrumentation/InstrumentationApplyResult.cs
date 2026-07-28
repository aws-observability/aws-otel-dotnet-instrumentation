// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

/// <summary>Typed outcome of <see cref="ProfilerTranslator.ApplyInstrumentation(Model.InstrumentationConfiguration)"/> that distinguishes a retryable transient failure from a permanent one mapping to a backend InstrumentationErrorCause.</summary>
// This enum is the "instrumentation-failed" taxonomy: whether the SDK could weave the target at all. It is
// distinct from a "capture-failed" outcome (Capture.NotCapturedReason), which means the method WAS woven and
// ran but an individual value could not be fully serialized (depth/width/field/timeout limits). The two are
// reported on different channels: an instrumentation failure is an ERROR status on the configuration (see
// MapErrorCause); a capture failure is a per-value NotCapturedReason emitted inside the snapshot, never an
// ERROR on the configuration. The ERROR status itself is emitted by StatusReporter; this enum +
// MapErrorCause is the taxonomy that gets wired to the backend error causes.
internal enum InstrumentationApplyResult
{
    /// <summary>At least one definition was registered with the profiler. Note: a method with mixed arities
    /// (e.g. overloads at 4 and 12 params) is still Applied for the supported overloads; the over-cap
    /// overloads are silently skipped — a partial success, not an error.</summary>
    Applied,

    /// <summary>Intentionally not applied (line-level or unsupported method); not an error and not reported.</summary>
    Skipped,

    /// <summary>Target type not found in any loaded assembly (likely not loaded yet); caller should retry on a later poll and must not report an ERROR.</summary>
    TypeNotLoaded,

    /// <summary>The target type was found but exposes no method with the configured name. Permanent (misconfiguration).</summary>
    MethodNotFound,

    /// <summary>Method resolved but NO overload had a profiler-supported arity (all exceeded the fixed 9-parameter
    /// cap — see <see cref="ProfilerTranslator"/>). Permanent for this config until the target is changed.</summary>
    NoSupportedArity,

    /// <summary>The native AddInstrumentations call threw. Permanent (native/profiler error).</summary>
    RuntimeError,
}
