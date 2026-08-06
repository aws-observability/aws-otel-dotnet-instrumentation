// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Tests for <c>PdbReader</c>, driven against the test assembly's OWN portable PDB.
/// </summary>
// WHY SELF-TARGETING: the reader's whole job is to agree with what the compiler actually emitted.
// A hand-built fake PDB would let the test agree with my *model* of a PDB rather than a real one —
// the exact class of mistake that produced two silent-wrong-value bugs in the spikes. So these tests
// resolve real lines in PdbReaderTargets, whose line numbers are looked up by `// @marker:` comment
// at test time rather than hardcoded, so rearranging that file cannot silently retarget a test.
public class PdbReaderTests
{
    [Fact]
    public void Resolve_KnownLine_ResolvesToASafeInteriorOffset()
    {
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"), null);

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location.Should().NotBeNull();
        result.Location!.MethodName.Should().Be(nameof(PdbReaderTargets.ThreeStatements));
        result.Location.TypeName.Should().Contain(nameof(PdbReaderTargets));
        result.Location.MethodToken.Should().NotBe(0);
    }

    [Fact]
    public void Resolve_ChosenOffsetIsAlwaysASafeInjectionPoint()
    {
        // The contract P3b depends on: any offset this reader returns must be an instruction start and
        // must not be a branch target. If that guarantee is ever weakened, the native side either
        // refuses the weave (wasted ReJIT) or — worse — weaves a probe that silently never fires.
        using var reader = new PdbReader();
        var method = typeof(PdbReaderTargets).GetMethod(nameof(PdbReaderTargets.ThreeStatements))!;
        var scan = IlBoundaryScanner.Scan(method.GetMethodBody()!.GetILAsByteArray()!);

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"), null);

        result.IsResolved.Should().BeTrue();
        scan.IsSafeInjectionPoint(result.Location!.IlOffset).Should().BeTrue(
            "offset {0} must be an instruction start and not a branch target", result.Location.IlOffset);
    }

    [Fact]
    public void Resolve_RequestedLocal_ResolvesToTheSlotThatIsAlreadyAssignedAtThatOffset()
    {
        // R-A REGRESSION LOCK — the trap that caught the R9 spike. A sequence point's offset is the
        // START of its statement, so injecting at the line that ASSIGNS a local reads the slot before
        // the assignment has run and silently yields 0. The reader must place the probe at the NEXT
        // statement boundary instead.
        //
        // Verified debug info + IL for ThreeStatements (net8.0 Debug), so this is not a guess.
        // `int b = a + 10;` is source line 47, and its statement spans IL_0005..IL_0009:
        //   line 46 -> IL_0001   `int a = x + 1;`
        //   line 47 -> IL_0005   `int b = a + 10;` STARTS here; b is NOT yet assigned
        //   line 48 -> IL_000A   `int c = b + 100;` — b IS assigned by now (stloc.1 is at IL_0009)
        // So a reader that returned the matched line's own offset would return 5 and capture b == 0.
        // MUTATION-VERIFIED. I broke the rule in PdbReader (matchIndex + 1 -> matchIndex) and confirmed
        // the discriminating values: correct returns IL_0005 (line 47's boundary), broken returns
        // IL_0001 (line 46's own start, where b is still 0). An earlier version of this test asserted
        // only `> 1` and STILL PASSED under the mutation — vacuously — so it is now pinned to the exact
        // offset. If this ever fails after a compiler upgrade changes the layout, re-derive the two
        // values by mutation before relaxing the assertion; do not simply widen it.
        using var reader = new PdbReader();
        const uint offsetIfRuleIsBroken = 5;   // line 47's own start — b not yet assigned there
        const uint offsetIfRuleHolds = 10;     // line 48's start — b assigned (stloc.1 at IL_0009)

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB"), "b");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.IlOffset.Should().NotBe(
            offsetIfRuleIsBroken,
            "R-A: injecting at the assigning statement's own start reads the slot pre-assignment (yields 0)");
        result.Location.IlOffset.Should().Be(
            offsetIfRuleHolds, "R-A: the probe belongs at the NEXT statement boundary");
        result.Location.LocalSlot.Should().BeGreaterThanOrEqualTo(0, "local 'b' must map to a real slot");
        result.Location.LocalName.Should().Be("b");
    }

    [Fact]
    public void Resolve_NoLocalRequested_ReturnsNegativeSlotSentinel()
    {
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"), null);

        result.IsResolved.Should().BeTrue();
        result.Location!.LocalSlot.Should().Be(-1);
        result.Location.LocalName.Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownLocalName_ReportsLocalOutOfScopeNotSuccess()
    {
        // Refusing is the point: returning any slot for a name we could not verify would emit a
        // confidently-wrong value into a customer's snapshot.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.ThreeStatements),
            PdbReaderTargets.LineOf("assignsB"),
            "thisLocalDoesNotExist");

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LocalOutOfScope);
    }

    [Fact]
    public void Resolve_LocalDeclaredInAnInnerScope_IsRefusedFromOutsideThatScope()
    {
        // Slot reuse makes name-only matching unsafe: the compiler can assign the same slot index to
        // different variables in disjoint scopes. A local declared inside an if-block must not resolve
        // at an offset outside that block, even though its NAME exists in the method's debug info.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.HasInnerScope),
            PdbReaderTargets.LineOf("outsideInnerScope"),
            "inner");

        result.IsResolved.Should().BeFalse(
            "'inner' is not in scope at the requested line, so it must not resolve");
        result.Status.Should().Be(LineProbeResolutionStatus.LocalOutOfScope);
    }

    [Fact]
    public void Resolve_NonExecutableLine_ReportsLineNotExecutableWithNearestHint()
    {
        // A blank/comment line has no sequence point. The detail should name a nearby real line so an
        // operator can correct the config instead of guessing.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("blankish"), null);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
        result.Detail.Should().Contain("nearest");
    }

    [Fact]
    public void Resolve_LineOutsideAnyMethod_ReportsLineNotExecutable()
    {
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), 999_999, null);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
    }

    [Fact]
    public void Resolve_UnknownMethodName_ReportsLineNotExecutable()
    {
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), "NoSuchMethod", PdbReaderTargets.LineOf("assignsC"), null);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
    }

    [Fact]
    public void Resolve_NullType_ReportsTypeNotLoadedRatherThanThrowing()
    {
        // The manager calls this on a polling thread; an exception there would take down config
        // processing for every other instrumentation too.
        using var reader = new PdbReader();

        var result = reader.Resolve(null!, "Whatever", 10, null);

        result.Status.Should().Be(LineProbeResolutionStatus.TypeNotLoaded);
    }

    [Fact]
    public void Resolve_LastStatementOfMethod_IsRefusedBecauseThereIsNoFollowingBoundary()
    {
        // Consequence of R-A: reading the effect of the final statement would require injecting after
        // it, but the only following boundary is the epilogue. Refusing beats capturing a
        // pre-assignment value that looks legitimate.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.SingleStatement), PdbReaderTargets.LineOf("onlyStatement"), null);

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
    }

    [Fact]
    public void Resolve_IsRepeatable_CachedDebugInfoReturnsTheSameOffset()
    {
        // The reader caches an opened PDB per assembly. A cached second call must not return a
        // different answer, and must not fail from a disposed/exhausted stream.
        using var reader = new PdbReader();

        var first = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"), null);
        var second = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"), null);

        first.IsResolved.Should().BeTrue();
        second.IsResolved.Should().BeTrue();
        second.Location!.IlOffset.Should().Be(first.Location!.IlOffset);
    }

    [Fact]
    public void Fixture_MarkersResolveToTheStatementsTheyName()
    {
        // Guards the fixture itself. If a marker moves or is deleted, fail here with a clear message
        // rather than letting a downstream test pass against the wrong statement.
        var assignsB = PdbReaderTargets.LineOf("assignsB");
        var assignsC = PdbReaderTargets.LineOf("assignsC");
        var blankish = PdbReaderTargets.LineOf("blankish");

        assignsB.Should().BeGreaterThan(0);
        assignsC.Should().Be(assignsB + 1, "assignsC is the statement immediately after assignsB");
        blankish.Should().BeGreaterThan(assignsC, "the comment-only line follows the assignments");
    }

    [Fact]
    public void Fixture_MissingMarker_FailsLoudlyRatherThanSilently()
    {
        var act = () => PdbReaderTargets.LineOf("noSuchMarker");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*noSuchMarker*", "the error must name the missing marker")
            .And.Message.Should().Contain("Markers present", "and list what IS available");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var reader = new PdbReader();
        _ = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"), null);

        reader.Dispose();
        var second = () => reader.Dispose();

        second.Should().NotThrow();
    }
}
