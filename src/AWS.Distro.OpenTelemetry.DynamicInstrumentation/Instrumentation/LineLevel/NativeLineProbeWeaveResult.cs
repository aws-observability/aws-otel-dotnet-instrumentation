// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// One (probe id, weave outcome) pair read back from the native profiler's
/// <c>GetLineProbeWeaveResults</c>.
/// </summary>
// MUST MATCH `_LineProbeWeaveResult` in the forked profiler's line_probe.h: two INT32s, in this order.
// The same raw-memory-contract warning as NativeLineProbeDefinition applies — nothing checks it.
//
// Blittable, unlike NativeLineProbeDefinition: no strings, no handles, nothing to allocate or free, so this
// carries no Dispose. That is what lets the caller hand the runtime a plain array and have it pinned rather
// than copied, and it is why the buffer can be reused across calls.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeLineProbeWeaveResult
{
    /// <summary>The probe id the managed side allocated and baked into the injected IL.</summary>
    public int ProbeId;

    /// <summary>The outcome, as a <see cref="LineProbeWeaveOutcome"/> value.</summary>
    // Kept as a raw int rather than the enum type so an outcome added to a NEWER native profiler than this
    // assembly knows about arrives intact instead of becoming an undefined enum value mid-marshal. The reader
    // range-checks it and treats anything unrecognised as a generic failure.
    public int Outcome;
}
