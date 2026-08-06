// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

public class IlBoundaryScannerTests
{
    // Targets compiled into THIS test assembly, so the IL scanned is real compiler output rather than
    // a hand-assembled byte array. Hand-assembled IL can encode an offset layout the compiler would
    // never emit, which is exactly the kind of test that passes while the production path breaks.
    private static int StraightLine(int x)
    {
        int a = x + 1;
        int b = a + 10;
        return b;
    }

    private static int WithLoop(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += i;
        }

        return total;
    }

    private static int WithSwitch(int n)
    {
        switch (n)
        {
            case 0: return 10;
            case 1: return 20;
            case 2: return 30;
            default: return -1;
        }
    }

    private static byte[] IlOf(string methodName) =>
        typeof(IlBoundaryScannerTests)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetMethodBody()!
            .GetILAsByteArray()!;

    [Fact]
    public void Scan_EmptyOrNullIl_ReturnsEmptyCompleteResult()
    {
        var fromNull = IlBoundaryScanner.Scan(null!);
        fromNull.Complete.Should().BeTrue();
        fromNull.InstructionStarts.Should().BeEmpty();

        var fromEmpty = IlBoundaryScanner.Scan(Array.Empty<byte>());
        fromEmpty.Complete.Should().BeTrue();
        fromEmpty.InstructionStarts.Should().BeEmpty();
    }

    [Fact]
    public void Scan_StraightLineMethod_DecodesEntireBodyAndFindsTheDebugReturnBranch()
    {
        var il = IlOf(nameof(StraightLine));

        var result = IlBoundaryScanner.Scan(il);

        result.Complete.Should().BeTrue("a simple method must decode end-to-end");
        result.InstructionStarts.Should().Contain(0u);

        // NOT empty — and this surprised the first version of this test, which asserted no branch
        // targets on the grounds that the method has no control flow. Verified IL (Debug, net8.0):
        //   0:nop 1:ldarg.0 2:ldc.i4.1 3:add 4:stloc.0 5:ldloc.0 6:ldc.i4.s 7:(10) 8:add 9:stloc.1
        //   10:ldloc.1 11:stloc.2 12:br.s 13:(+0) 14:ldloc.2 15:ret
        // The Debug compiler emits `br.s +0` for `return b;`, making offset 14 a branch target.
        // CONSEQUENCE FOR P3a: even a method with no user-visible control flow has an unsafe
        // injection point, and it sits exactly at the `return` — a very likely place to set a probe.
        // Branch-target rejection is therefore load-bearing for ordinary code, not just loops.
        result.BranchTargets.Should().BeEquivalentTo(new[] { 14u });
    }

    [Fact]
    public void Scan_OffsetZeroIsAlwaysAnInstructionStart()
    {
        IlBoundaryScanner.Scan(IlOf(nameof(StraightLine))).InstructionStarts.Should().Contain(0u);
        IlBoundaryScanner.Scan(IlOf(nameof(WithLoop))).InstructionStarts.Should().Contain(0u);
    }

    [Fact]
    public void Scan_LoopMethod_FindsAtLeastOneBranchTarget()
    {
        var result = IlBoundaryScanner.Scan(IlOf(nameof(WithLoop)));

        result.Complete.Should().BeTrue();
        result.BranchTargets.Should().NotBeEmpty("a for-loop compiles to at least one branch");
    }

    [Fact]
    public void Scan_LoopMethod_EveryBranchTargetIsAlsoAnInstructionStart()
    {
        // A branch target that is not an instruction start would mean the walker mis-decoded operand
        // bytes as opcodes — the mis-walk failure mode. This invariant catches it.
        var result = IlBoundaryScanner.Scan(IlOf(nameof(WithLoop)));

        foreach (var target in result.BranchTargets)
        {
            result.InstructionStarts.Should().Contain(
                target, "branch target {0} must land on a decoded instruction start", target);
        }
    }

    [Fact]
    public void Scan_SwitchMethod_DecodesTheJumpTableWithoutMisWalking()
    {
        // InlineSwitch has a variable-length operand (4 + 4*N). Getting its size wrong desynchronizes
        // every subsequent offset, so this is the highest-risk opcode in the walker.
        var result = IlBoundaryScanner.Scan(IlOf(nameof(WithSwitch)));

        result.Complete.Should().BeTrue("the switch jump table must be decoded, not skipped");
        foreach (var target in result.BranchTargets)
        {
            result.InstructionStarts.Should().Contain(target);
        }
    }

    [Fact]
    public void IsSafeInjectionPoint_RejectsMidInstructionOffsets()
    {
        var il = IlOf(nameof(StraightLine));
        var result = IlBoundaryScanner.Scan(il);

        // Any offset inside the body that is NOT a decoded start is an operand byte.
        var operandBytes = Enumerable.Range(0, il.Length)
            .Select(i => (uint)i)
            .Where(o => !result.InstructionStarts.Contains(o))
            .ToArray();

        operandBytes.Should().NotBeEmpty("this method has multi-byte instructions with operands");
        foreach (var offset in operandBytes)
        {
            result.IsSafeInjectionPoint(offset).Should().BeFalse(
                "offset {0} is an operand byte, not an instruction boundary", offset);
        }
    }

    [Fact]
    public void IsSafeInjectionPoint_RejectsBranchTargetsEvenThoughTheyAreInstructionStarts()
    {
        // THE FINDING-B2 REGRESSION LOCK. A branch target IS a valid instruction start, so a naive
        // boundary check accepts it — and the woven probe then never fires, silently, because control
        // arriving via the branch jumps past the injected code. Proven live (async spike: FireCount=0).
        var result = IlBoundaryScanner.Scan(IlOf(nameof(WithLoop)));

        result.BranchTargets.Should().NotBeEmpty();
        foreach (var target in result.BranchTargets)
        {
            result.InstructionStarts.Should().Contain(target);
            result.IsSafeInjectionPoint(target).Should().BeFalse(
                "offset {0} is a branch target: injecting there weaves fine but never fires", target);
        }
    }

    [Fact]
    public void Scan_TruncatedIl_ReportsIncompleteRatherThanGuessing()
    {
        // A truncated body must be reported as incomplete. Silently returning a partial set as if it
        // were authoritative is the mis-walk hazard: we would hand the native side a "verified"
        // offset derived from bytes we never decoded.
        var full = IlOf(nameof(StraightLine));
        var truncated = full.Take(full.Length - 1).ToArray();

        // Nudge the last byte to a two-byte-opcode prefix so the walk must run off the end.
        truncated[^1] = 0xFE;

        var result = IlBoundaryScanner.Scan(truncated);

        result.Complete.Should().BeFalse("the walker must admit it could not decode the whole body");
    }

    [Fact]
    public void Scan_UnknownOpcode_StopsAndReportsIncomplete()
    {
        // 0xF0 is not a defined single-byte opcode. The walker must stop rather than treat the
        // following bytes as instructions.
        var result = IlBoundaryScanner.Scan(new byte[] { 0x00, 0xF0, 0x00, 0x00 });

        result.Complete.Should().BeFalse();
        result.InstructionStarts.Should().Contain(0u, "the leading nop decoded fine before the failure");
    }
}
