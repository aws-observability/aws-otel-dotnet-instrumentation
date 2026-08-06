// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// The result of an IL scan.
/// </summary>
/// <param name="InstructionStarts">Offsets that begin an instruction.</param>
/// <param name="BranchTargets">Offsets that are the target of a branch or switch case.</param>
/// <param name="Complete">False when the walk aborted early (truncated or unknown opcode). When false,
/// the sets cover only the prefix that was successfully decoded and MUST NOT be treated as
/// authoritative for the rest of the body.</param>
internal sealed record IlScanResult(
    IReadOnlySet<uint> InstructionStarts,
    IReadOnlySet<uint> BranchTargets,
    bool Complete)
{
    /// <summary>Determines whether an offset is safe to inject a probe before.</summary>
    /// <param name="offset">The candidate IL offset.</param>
    /// <returns>True when the offset begins an instruction and is not a branch target.</returns>
    public bool IsSafeInjectionPoint(uint offset) =>
        this.InstructionStarts.Contains(offset) && !this.BranchTargets.Contains(offset);
}
