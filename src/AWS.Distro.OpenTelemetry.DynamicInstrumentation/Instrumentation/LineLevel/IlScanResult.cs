// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// The result of an IL scan.
/// </summary>
/// <param name="InstructionStarts">Offsets that begin an instruction.</param>
/// <param name="BranchTargets">Offsets that are the target of a branch or switch case.</param>
/// <param name="Branches">Every (source, target) pair, so a caller can tell WHERE a jump came from.</param>
/// <param name="Complete">False when the walk aborted early (truncated or unknown opcode). When false,
/// the sets cover only the prefix that was successfully decoded and MUST NOT be treated as
/// authoritative for the rest of the body.</param>
internal sealed record IlScanResult(
    IReadOnlySet<uint> InstructionStarts,
    IReadOnlySet<uint> BranchTargets,
    IReadOnlyList<IlBranch> Branches,
    bool Complete)
{
    /// <summary>Determines whether an offset is safe to inject a probe before.</summary>
    /// <param name="offset">The candidate IL offset.</param>
    /// <returns>True when the offset begins an instruction and is not a branch target.</returns>
    public bool IsSafeInjectionPoint(uint offset) =>
        this.InstructionStarts.Contains(offset) && !this.BranchTargets.Contains(offset);

    /// <summary>
    /// Whether some path can reach <paramref name="candidate"/> WITHOUT executing the statement at
    /// <paramref name="lineOffset"/>.
    /// </summary>
    /// <param name="lineOffset">IL offset of the probed statement.</param>
    /// <param name="candidate">IL offset a probe would be injected before.</param>
    /// <returns>True when a jump from before the statement lands after it, at or before the candidate.</returns>
    // THE MERGE-POINT TEST. A probe reports "this line ran", so it must be injected somewhere only reachable
    // after the line actually executed. Injecting past a merge point breaks that: the `if (flag)` case measured
    // on HasInnerScope compiled Release had the last statement inside the block resolve to the SAME offset as
    // the line AFTER the block, so `HasInnerScope(false)` — which never ran the probed line — still fired.
    //
    // The SOURCE is what makes this precise, and why BranchTargets alone was not enough:
    //   - a jump from BEFORE the statement to AFTER it bypasses the statement  -> unsafe
    //   - a jump within the statement's own span (a ternary, `&&`, `??`) rejoins after both operands ran, and
    //     the statement executes either way                                    -> safe
    // Refusing on targets alone rejected every ternary and every line in an iterator/async MoveNext, whose
    // state-dispatch jumps sit between most statements. Both were measured as test failures.
    public bool IsReachableWithoutExecuting(uint lineOffset, uint candidate)
    {
        foreach (var branch in this.Branches)
        {
            if (branch.Source < lineOffset && branch.Target > lineOffset && branch.Target <= candidate)
            {
                return true;
            }
        }

        return false;
    }
}
