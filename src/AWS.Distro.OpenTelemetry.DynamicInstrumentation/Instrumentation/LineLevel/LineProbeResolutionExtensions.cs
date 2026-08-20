// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Maps line-probe resolution outcomes onto the retry/report policy and the backend error causes.
/// </summary>
internal static class LineProbeResolutionExtensions
{
    /// <summary>
    /// True when the caller should forget applied-state and retry on a later poll instead of reporting.
    /// </summary>
    /// <param name="status">The resolution status.</param>
    /// <returns>True when the failure is transient.</returns>
    // ONLY TypeNotLoaded is retryable. Debug info is a deploy-time property of the assembly and a missing
    // PDB will still be missing on the next poll, so retrying those would re-resolve the same failure every
    // poll interval forever. ProfilerMissingLineProbeSupport is likewise permanent for the process: the
    // native profiler cannot be swapped without a restart.
    public static bool IsRetryable(this LineProbeResolutionStatus status) =>
        status == LineProbeResolutionStatus.TypeNotLoaded;

    /// <summary>
    /// Maps a failed resolution to a backend InstrumentationErrorCause, or null when nothing should be
    /// reported.
    /// </summary>
    /// <param name="status">The resolution status.</param>
    /// <returns>The error cause, or null.</returns>
    // The backend enum is fixed (FILE_NOT_FOUND, METHOD_NOT_FOUND, OVERLOADED_METHODS, LANGUAGE_MISMATCH,
    // LINE_NOT_EXECUTABLE, RUNTIME_ERROR), so several distinct line-level failures collapse onto
    // RUNTIME_ERROR. That is a deliberate lossy mapping, not an oversight: the operator-facing precision
    // lives in the Detail string, while the enum stays inside the contract all four SDKs share.
    public static string? MapErrorCause(this LineProbeResolutionStatus status) => status switch
    {
        LineProbeResolutionStatus.Resolved => null,

        // Retryable: the target may simply not be loaded yet. Reporting would spam an ERROR on every poll
        // for an app that is still warming up.
        LineProbeResolutionStatus.TypeNotLoaded => null,

        LineProbeResolutionStatus.LineNotExecutable => "LINE_NOT_EXECUTABLE",

        // A local that is out of scope at the resolved offset is a property of the requested LINE, so it
        // maps to LINE_NOT_EXECUTABLE rather than RUNTIME_ERROR: the operator's fix is to move the probe,
        // not to retry or file a bug.
        LineProbeResolutionStatus.LocalOutOfScope => "LINE_NOT_EXECUTABLE",

        // A by-ref/pointer local is a bad CONFIG (that variable is uncapturable anywhere), not a bad line,
        // so it maps to RUNTIME_ERROR and the Detail names the type.
        LineProbeResolutionStatus.LocalNotCapturable => "RUNTIME_ERROR",

        // No PDB, or a PDB from a different build. Both are deployment conditions rather than bad configs,
        // but the backend has no cause for "debug info missing" — so RUNTIME_ERROR carries them, and the
        // Detail string is what tells the operator to ship PDBs. This is the single most likely line-level
        // failure in a containerized app, which is why it must be reported at all rather than dropped.
        LineProbeResolutionStatus.DebugInfoUnavailable => "RUNTIME_ERROR",
        LineProbeResolutionStatus.DebugInfoMismatch => "RUNTIME_ERROR",

        // Running against the stock upstream profiler instead of our fork.
        LineProbeResolutionStatus.ProfilerMissingLineProbeSupport => "RUNTIME_ERROR",

        _ => "RUNTIME_ERROR",
    };
}
