// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Typed outcome of resolving a line-level configuration's source line to an IL offset.
/// </summary>
// Mirrors the InstrumentationApplyResult discipline: distinguishes a RETRYABLE transient state (the
// target assembly is not loaded yet) from PERMANENT misconfiguration (no such line, no debug info),
// so the manager knows whether to retry on the next poll or report an ERROR once. These map to
// backend InstrumentationErrorCause values in P6 — see LineProbeResolutionExtensions.
internal enum LineProbeResolutionStatus
{
    /// <summary>The line resolved to a usable interior IL offset.</summary>
    Resolved,

    /// <summary>Target type not found in any loaded assembly (likely not loaded yet); the caller should
    /// retry on a later poll and must NOT report an ERROR.</summary>
    TypeNotLoaded,

    /// <summary>No PDB could be located for the target assembly, or it could not be read. Permanent for
    /// this process: debug info is a deploy-time property. NOTE: this is the COMMON case for
    /// containerized apps that ship without PDBs, not an edge case.</summary>
    DebugInfoUnavailable,

    /// <summary>A PDB was found but does not belong to the loaded module (Mvid mismatch). Rejected
    /// deliberately: a mismatched PDB yields plausible-but-wrong offsets, which is worse than no
    /// capture at all because the resulting data looks valid.</summary>
    DebugInfoMismatch,

    /// <summary>The type/method was found and debug info was readable, but the requested line has no
    /// executable statement (blank line, comment, declaration, or outside the method).</summary>
    LineNotExecutable,

    /// <summary>The line resolved, but the requested local is not in scope at that offset — or its slot
    /// is reused by a different variable there. Refused rather than captured, to avoid emitting a
    /// confidently-wrong value.</summary>
    LocalOutOfScope,

    /// <summary>
    /// The loaded native profiler does not export the line-probe API, i.e. we are running against the
    /// STOCK upstream profiler rather than our fork. Everything managed-side resolved correctly.
    /// </summary>
    // Deliberately its own status rather than folded into RuntimeError: this is a DEPLOYMENT condition
    // (wrong binary present), not a bad config and not a code defect, and the operator action is
    // completely different — nothing about the instrumentation request needs changing. Treated as
    // permanent for the process lifetime, because the profiler cannot be swapped without a restart, so
    // retrying on every poll would spam identical failures forever.
    ProfilerMissingLineProbeSupport,
}
