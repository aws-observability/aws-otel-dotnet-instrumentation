// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Selects the IL sequence the native rewriter splices at the interior offset.
/// </summary>
// MUST STAY IN LOCKSTEP WITH `LineProbeEmissionMode` in the forked profiler's line_probe.h. These are
// raw integers crossing the managed/native boundary with no type checking on either side: if the two
// enums disagree, the rewriter emits a DIFFERENT IL sequence than the one intended and the failure is
// silent — the wrong callback gets called, or a value arrives unboxed. There is no compiler or
// marshaler that can catch this, so the values are pinned explicitly rather than left implicit.
internal enum LineProbeEmissionMode
{
    /// <summary>
    /// `ldc.i4 probeId; call Probe(int32)` — or, when a hoisted-field token is supplied, the async
    /// `ldarg.0; ldfld &lt;hoisted&gt;; box; call CaptureLocal(int32, object)` path.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Two-call gated emission (DECISION A, the shipping choice for hot lines):
    /// `ldc.i4 probeId; call ShouldCapture; brfalse SKIP; ldc.i4 probeId; ldc.i4 value; box; call Capture; SKIP:`
    /// The box lives PAST the branch, so a declined hit costs no allocation (measured 0 B/call vs 24 B/call).
    /// </summary>
    GatedBox = 1,

    /// <summary>
    /// Unconditional box + call, no gate. Allocates on EVERY execution — retained as the measured
    /// contrast case that proves the gate is what avoids the allocation. Not for production use.
    /// </summary>
    UngatedBox = 2,

    /// <summary>
    /// Sync local capture: `ldc.i4 probeId; ldloc &lt;slot&gt;; box; call CaptureLocal(int32, object)`.
    /// The local slot index travels in the definition's <c>BoxValue</c> field (the native side reuses
    /// that field as the slot index rather than adding an ABI field — see line_probe.h).
    /// </summary>
    LocalCapture = 3,
}
