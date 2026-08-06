// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection.Emit;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Walks a method's raw IL to determine which offsets are real instruction boundaries and which are
/// branch targets. Both are required before an offset can be handed to the native rewriter.
/// </summary>
// WHY THIS EXISTS — two failure modes proven live in the Phase-2/3 spikes, both of which produce
// SILENT wrong behavior rather than an error:
//
//  1. MID-INSTRUCTION OFFSETS. The native rewriter resolves an offset through a sparse offset->instr
//     map and refuses anything that is not an instruction start (COR_E_INVALIDPROGRAM). Refusal is
//     safe, but it costs a wasted ReJIT and reports an opaque failure, so we reject managed-side
//     first with a precise cause. Note an offset that merely LOOKS wrong may be fine: in one spike a
//     "bad" offset of +1 from a 1-byte `ldloc.0` landed on the next VALID opcode and the probe fired
//     normally. Only a real walk can tell the difference.
//
//  2. BRANCH TARGETS. The rewriter inserts the probe sequence BEFORE the instruction at the offset.
//     If that instruction is a branch target, control arriving via the branch jumps straight to the
//     original instruction and skips the injected code entirely. The weave reports success, the body
//     stays valid, and the probe NEVER FIRES — verified in the async spike (FireCount=0 at a
//     MoveNext branch target). This is exactly the Phase-1 silent-no-op class, so branch targets are
//     rejected and the caller advances to the next safe boundary.
//
// The opcode table is reflected out of System.Reflection.Emit.OpCodes rather than hand-written, so it
// is authoritative for the runtime we are on instead of a transcription that can drift.
internal static class IlBoundaryScanner
{
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = BuildOpCodeTable();

    /// <summary>
    /// Scans a method body's IL and returns the set of valid instruction-start offsets and the set of
    /// offsets that are the target of at least one branch or switch case.
    /// </summary>
    /// <param name="il">The raw IL byte array (see <c>MethodBody.GetILAsByteArray</c>).</param>
    /// <returns>The instruction starts and branch targets found.</returns>
    public static IlScanResult Scan(byte[] il)
    {
        var starts = new HashSet<uint>();
        var branchTargets = new HashSet<uint>();
        if (il == null || il.Length == 0)
        {
            return new IlScanResult(starts, branchTargets, true);
        }

        int position = 0;
        bool complete = true;

        while (position < il.Length)
        {
            starts.Add((uint)position);

            ushort code = il[position];
            position++;

            // Two-byte opcodes are prefixed with 0xFE.
            if (code == 0xFE)
            {
                if (position >= il.Length)
                {
                    complete = false;
                    break;
                }

                code = (ushort)(0xFE00 | il[position]);
                position++;
            }

            if (!OpCodesByValue.TryGetValue(code, out var opCode))
            {
                // Unknown opcode: stop rather than mis-walk. A mis-walk would emit CONFIDENTLY WRONG
                // boundaries, which is worse than admitting we could not scan the whole body.
                complete = false;
                break;
            }

            var operandType = opCode.OperandType;

            if (operandType == OperandType.InlineSwitch)
            {
                if (position + 4 > il.Length)
                {
                    complete = false;
                    break;
                }

                int caseCount = BitConverter.ToInt32(il, position);
                int caseTableEnd = position + 4 + (4 * caseCount);
                if (caseCount < 0 || caseTableEnd > il.Length)
                {
                    complete = false;
                    break;
                }

                for (int i = 0; i < caseCount; i++)
                {
                    int target = caseTableEnd + BitConverter.ToInt32(il, position + 4 + (4 * i));
                    if (target >= 0 && target <= il.Length)
                    {
                        branchTargets.Add((uint)target);
                    }
                }

                position = caseTableEnd;
                continue;
            }

            int operandSize = GetOperandSize(operandType);
            if (position + operandSize > il.Length)
            {
                complete = false;
                break;
            }

            // Branch operands are relative to the instruction FOLLOWING the branch.
            if (operandType == OperandType.ShortInlineBrTarget)
            {
                int target = position + 1 + (sbyte)il[position];
                if (target >= 0 && target <= il.Length)
                {
                    branchTargets.Add((uint)target);
                }
            }
            else if (operandType == OperandType.InlineBrTarget)
            {
                int target = position + 4 + BitConverter.ToInt32(il, position);
                if (target >= 0 && target <= il.Length)
                {
                    branchTargets.Add((uint)target);
                }
            }

            position += operandSize;
        }

        return new IlScanResult(starts, branchTargets, complete);
    }

    private static int GetOperandSize(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        _ => 4,
    };

    private static Dictionary<ushort, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<ushort, OpCode>();
        var fields = typeof(OpCodes).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var field in fields)
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                table[unchecked((ushort)opCode.Value)] = opCode;
            }
        }

        return table;
    }
}
