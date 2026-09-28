// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

/// <summary>Maps an <see cref="InstrumentationApplyResult"/> to the backend's InstrumentationErrorCause wire
/// value, and classifies whether a result is a permanent failure worth reporting as an ERROR status.</summary>
// The instrumentation-failed taxonomy (this type) is deliberately separate from the capture-failed taxonomy
// (Capture.NotCapturedReason): a value that couldn't be fully serialized is a per-snapshot NotCapturedReason,
// NOT an ERROR on the configuration. Only weave failures reach here; the ERROR status is emitted by StatusReporter.
internal static class InstrumentationApplyResultExtensions
{
    /// <summary>
    /// True when the result is a permanent instrumentation failure that should be reported to the backend as
    /// an ERROR exactly once. Applied/Skipped are not failures; TypeNotLoaded is transient (retried, never
    /// reported — reporting it would spam an ERROR on every poll until the assembly loads).
    /// </summary>
    public static bool IsReportableFailure(this InstrumentationApplyResult result) => result switch
    {
        InstrumentationApplyResult.MethodNotFound => true,
        InstrumentationApplyResult.NoSupportedArity => true,
        InstrumentationApplyResult.RuntimeError => true,
        _ => false,
    };

    /// <summary>
    /// Maps a result to the backend InstrumentationErrorCause enum value (spec: FILE_NOT_FOUND,
    /// METHOD_NOT_FOUND, OVERLOADED_METHODS, LANGUAGE_MISMATCH, LINE_NOT_EXECUTABLE, RUNTIME_ERROR).
    /// Returns null for non-failure results (Applied/Skipped/TypeNotLoaded), which are never reported as ERROR.
    /// </summary>
    // NoSupportedArity maps to RUNTIME_ERROR, not a bespoke "arity" cause: the backend enum has no
    // arity-specific member, and the operator-facing detail (">9 parameters is not supported") belongs in the
    // human-readable message, not the coarse cause enum. Revisit if the backend adds a dedicated cause.
    public static string? MapErrorCause(this InstrumentationApplyResult result) => result switch
    {
        InstrumentationApplyResult.MethodNotFound => "METHOD_NOT_FOUND",
        InstrumentationApplyResult.NoSupportedArity => "RUNTIME_ERROR",
        InstrumentationApplyResult.RuntimeError => "RUNTIME_ERROR",
        _ => null,
    };
}
