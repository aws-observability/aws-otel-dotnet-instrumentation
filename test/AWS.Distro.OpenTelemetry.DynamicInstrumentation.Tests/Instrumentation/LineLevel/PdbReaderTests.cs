// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
        // the discriminating values: correct returns line 48's boundary, broken returns line 47's own start,
        // where b is still 0. An earlier version of this test asserted only `> 1` and STILL PASSED under the
        // mutation — vacuously — so it is pinned to an exact offset rather than a range. Do not widen it.
        //
        // The two offsets are READ FROM THE PDB rather than hardcoded. They were originally the literals 5
        // and 10, measured in net8.0 Debug — and that made the test pass in Debug while FAILING in Release,
        // where the same statements start at different offsets. A test that only holds in one configuration
        // silently stops guarding the other. Reading the sequence-point table keeps the assertion exact (still
        // one specific offset, still distinguishable from the broken value) without baking in a codegen
        // detail. The offsets come from System.Reflection.Metadata directly, NOT from PdbReader, so this is
        // not circular.
        using var reader = new PdbReader();
        var offsetIfRuleIsBroken = SequencePointOffsetOf(
            nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsB"));
        var offsetIfRuleHolds = SequencePointOffsetOf(
            nameof(PdbReaderTargets.ThreeStatements), PdbReaderTargets.LineOf("assignsC"));

        offsetIfRuleHolds.Should().NotBe(
            offsetIfRuleIsBroken, "the two offsets must differ or this test cannot detect the broken rule");

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

    // ── Local TYPE resolution (non-int local capture) ────────────────────────────
    // The native rewriter needs the local's declared type to pick a `box` token, and needs to know whether
    // to box at ALL. Getting either wrong is not a clean failure: boxing a DateTime against a System.Int32
    // token, or boxing a reference type, produces a method body the runtime rejects — verified by mutation,
    // where forcing an Int32 token for every local crashed the app with
    // `TypeLoadException: Could not load type 'Invalid_Token.0x01000000'`.

    [Fact]
    public void Resolve_IntLocal_ReportsInt32AsAValueType()
    {
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedLocalTypes),
            PdbReaderTargets.LineOf("mixedText"), "number");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.LocalTypeName.Should().Be("System.Int32");
        result.Location.LocalIsValueType.Should().BeTrue();
    }

    [Fact]
    public void Resolve_StringLocal_ReportsAReferenceType()
    {
        // The case that was impossible before: a reference-type local must be reported as NOT a value type
        // so the native side emits no `box` at all.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedLocalTypes),
            PdbReaderTargets.LineOf("mixedRatio"), "text");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.LocalTypeName.Should().Be("System.String");
        result.Location.LocalIsValueType.Should().BeFalse(
            "a reference-type local is already an object reference; boxing one is invalid IL");
    }

    [Fact]
    public void Resolve_NonInt32ValueTypeLocals_ReportTheirOwnTypes()
    {
        // Two distinct, differently-sized value types, so a pass cannot come from one of them happening to
        // match a hardcoded assumption.
        using var reader = new PdbReader();

        var ratio = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedLocalTypes),
            PdbReaderTargets.LineOf("mixedStamp"), "ratio");
        ratio.IsResolved.Should().BeTrue($"ratio: {ratio.Status} {ratio.Detail}");
        ratio.Location!.LocalTypeName.Should().Be("System.Double");
        ratio.Location.LocalIsValueType.Should().BeTrue();

        var stamp = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedLocalTypes),
            PdbReaderTargets.LineOf("mixedBoxed"), "stamp");
        stamp.IsResolved.Should().BeTrue($"stamp: {stamp.Status} {stamp.Detail}");
        stamp.Location!.LocalTypeName.Should().Be("System.DateTime");
        stamp.Location.LocalIsValueType.Should().BeTrue();
    }

    [Theory]
    [InlineData("structLocal", "level", "the enum lives in the test assembly, not corlib")]
    [InlineData("nullableLocal", "point", "the struct lives in the test assembly, not corlib")]
    [InlineData("corlibLocal", "maybe", "Nullable<int> is generic and needs a TypeSpec, not a TypeRef by name")]
    public void Resolve_ValueTypeTheNativeSideCannotName_IsRefused(string marker, string localName, string why)
    {
        // THE CRASH THIS PREVENTS IS IN THE CUSTOMER'S METHOD, NOT IN OURS. A value type has to be boxed to
        // reach the object-typed callback, and the native rewriter names that box token with
        // DefineTypeRefByName against the CORLIB AssemblyRef — an API that validates nothing and happily
        // appends a TypeRef row for a type corlib does not contain. The emitted `box [corlib]<name>` then
        // fails to resolve when the JIT compiles the rewritten body: TypeLoadException for every caller of
        // that method, for as long as the process lives. Refusing during resolution is the only outcome that
        // leaves the customer's code working.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.UncapturableValueTypes),
            PdbReaderTargets.LineOf(marker), localName);

        result.IsResolved.Should().BeFalse(
            $"capturing '{localName}' must be refused: {why} — otherwise the rewritten method fails to JIT");
        result.Status.Should().Be(LineProbeResolutionStatus.LocalNotCapturable);
        result.Detail.Should().Contain(localName);
    }

    [Fact]
    public void Resolve_LastStatementInsideAConditional_NeverSharesAnInjectionPointWithTheLineAfterTheBlock()
    {
        // A probe reports "this line ran, and here is a local". If the injection point sits past the block's
        // merge point, the probe ALSO fires on the path that skipped the line — a snapshot for code that never
        // executed, which is worse than no snapshot.
        //
        // Asserted as an invariant rather than as an offset, because the offsets differ by configuration and
        // the bug only existed in one of them: in Release, line 76's next boundary was the `brfalse` target, so
        // resolution skipped past it and landed on the same offset as line 79 — the line AFTER the block.
        // Two lines in different control-flow regions sharing one injection point is the defect, in any
        // configuration, so that is what this pins.
        using var reader = new PdbReader();

        var insideBlock = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.HasInnerScope),
            PdbReaderTargets.LineOf("insideInnerScope"), "outer");

        var afterBlock = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.HasInnerScope),
            PdbReaderTargets.LineOf("outsideInnerScope"), "outer");

        afterBlock.IsResolved.Should().BeTrue(
            $"the line after the block is ordinary straight-line code: {afterBlock.Status} {afterBlock.Detail}");

        // Either outcome is acceptable for the in-block line — resolve to a point still inside the block, or
        // refuse — but it must never borrow the post-block line's injection point.
        if (insideBlock.IsResolved)
        {
            insideBlock.Location!.IlOffset.Should().NotBe(
                afterBlock.Location!.IlOffset,
                "a probe on the last statement inside the `if` must not be injected at the same offset as one "
                + "on the line after it; that offset is reached even when the `if` body is skipped");
        }
        else
        {
            insideBlock.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
            insideBlock.Detail.Should().NotBeNullOrEmpty("a refusal has to tell the operator why");
        }
    }

    [Fact]
    public void Resolve_PlainCorlibValueType_IsStillCapturable()
    {
        // The guard above must not become "refuse all value types": a plain System.* value type is exactly
        // what the native side CAN name, and it is the common case.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.UncapturableValueTypes),
            PdbReaderTargets.LineOf("corlibLocal"), "plain");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.LocalTypeName.Should().Be("System.Int64");
        result.Location.LocalIsValueType.Should().BeTrue();
    }

    [Fact]
    public void Resolve_ArrayLocal_IsAReferenceType()
    {
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedLocalTypes),
            PdbReaderTargets.LineOf("mixedItems"), "boxed");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.LocalTypeName.Should().Be("System.Object");
        result.Location.LocalIsValueType.Should().BeFalse();
    }

    [Fact]
    public void Resolve_NoLocalRequested_LeavesTypeInfoUnset()
    {
        // A bare line probe (no capture) must not carry a type name, or the native side would take the
        // LocalCapture path and emit a two-arg callback for a probe that has nothing to pass.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.ThreeStatements),
            PdbReaderTargets.LineOf("assignsC"), null);

        result.IsResolved.Should().BeTrue();
        result.Location!.LocalSlot.Should().Be(-1);
        result.Location.LocalTypeName.Should().BeNull();
        result.Location.LocalIsValueType.Should().BeFalse();
    }

    [Fact]
    public void Resolve_AsyncMethod_RetargetsTheStateMachineMoveNext()
    {
        // The operator names `ReserveAsync`, but that method body is only a state-machine launcher: it holds
        // no sequence point for any interior line. Resolution must silently follow AsyncStateMachineAttribute
        // into `<ReserveAsync>d__N.MoveNext`, because weaving the launcher would place the probe on a line
        // that never executes the user's statement.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.ReserveAsync),
            PdbReaderTargets.LineOf("asyncTotalAssigned"),
            "total");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");

        var location = result.Location!;
        location.MethodName.Should().Be("MoveNext", "the user's lines only exist in the state machine");
        location.TypeName.Should().Contain("<ReserveAsync>d__");
        location.ParameterCount.Should().Be(0, "MoveNext takes no arguments");

        // The variable is a FIELD, not a slot — so the native side must emit `ldarg.0; ldfld`, and a
        // non-zero token is the only thing that selects that emission.
        location.HoistedFieldToken.Should().NotBe(0u);
        location.LocalSlot.Should().Be(-1);
        location.LocalName.Should().Be("total", "the snapshot must read as the operator wrote it");
        location.LocalTypeName.Should().Be("System.Int32");
        location.LocalIsValueType.Should().BeTrue();
    }

    [Fact]
    public void Resolve_AsyncMethod_ReportsTheStateMachineTypeAsSingleLevelNested()
    {
        // The native target lookup splits the type name on '+' and supports exactly one nesting level. A
        // state machine is nested once, so the emitted name must contain exactly one '+' — more would be
        // silently unresolvable on the native side.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.ReserveAsync),
            PdbReaderTargets.LineOf("asyncTotalAssigned"),
            "total");

        result.IsResolved.Should().BeTrue($"{result.Status}: {result.Detail}");
        result.Location!.TypeName.Count(c => c == '+').Should().Be(1);
    }

    [Fact]
    public void Resolve_AsyncHoistedLocals_CarryTheirOwnDeclaredTypes()
    {
        // A hoisted field is a relocated local, so it keeps its own type. Boxing all of them as System.Int32
        // — which the native async path did before this — is undefined behavior inside the CUSTOMER'S method,
        // not a lost snapshot. Three different type families, so a hardcoded token cannot pass.
        using var reader = new PdbReader();
        var line = PdbReaderTargets.LineOf("asyncMixedAfterAwait");

        var note = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedAsync), line, "note");
        note.IsResolved.Should().BeTrue($"note: {note.Status} {note.Detail}");
        note.Location!.LocalTypeName.Should().Be("System.String");
        note.Location.LocalIsValueType.Should().BeFalse("a reference-type field must get NO box");
        note.Location.HoistedFieldToken.Should().NotBe(0u);

        var stamp = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedAsync), line, "stamp");
        stamp.IsResolved.Should().BeTrue($"stamp: {stamp.Status} {stamp.Detail}");
        stamp.Location!.LocalTypeName.Should().Be("System.DateTime");
        stamp.Location.LocalIsValueType.Should().BeTrue();

        var ratio = reader.Resolve(
            typeof(PdbReaderTargets), nameof(PdbReaderTargets.MixedAsync), line, "ratio");
        ratio.IsResolved.Should().BeTrue($"ratio: {ratio.Status} {ratio.Detail}");
        ratio.Location!.LocalTypeName.Should().Be("System.Double");
        ratio.Location.LocalIsValueType.Should().BeTrue();

        // Each local must resolve to a DISTINCT field. One shared token would mean every probe read the same
        // variable while reporting three different names — a wrong answer that looks completely plausible.
        var tokens = new[]
        {
            note.Location.HoistedFieldToken, stamp.Location.HoistedFieldToken, ratio.Location.HoistedFieldToken,
        };
        tokens.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void Resolve_SameNamedHoistedLocalsInDisjointScopes_PicksTheOneLiveAtTheOffset()
    {
        // THE CASE THAT MAKES NAME MATCHING UNSAFE. Two locals both called `y`, of different types, in
        // disjoint scopes — measured to produce TWO hoisted fields. Resolving at the first scope's line and
        // the second's must yield DIFFERENT fields with DIFFERENT types; a name-only resolver would return
        // the same field for both and box a string as an int (or vice versa).
        using var reader = new PdbReader();

        var first = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.SameNameDifferentTypesAsync),
            PdbReaderTargets.LineOf("asyncFirstY"),
            "y");
        var second = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.SameNameDifferentTypesAsync),
            PdbReaderTargets.LineOf("asyncSecondY"),
            "y");

        first.IsResolved.Should().BeTrue($"first y: {first.Status} {first.Detail}");
        second.IsResolved.Should().BeTrue($"second y: {second.Status} {second.Detail}");

        first.Location!.LocalTypeName.Should().Be("System.Int32");
        second.Location!.LocalTypeName.Should().Be("System.String");
        second.Location.LocalIsValueType.Should().BeFalse();

        first.Location.HoistedFieldToken.Should().NotBe(
            second.Location.HoistedFieldToken,
            "the two same-named locals are different variables and must resolve to different fields");
    }

    [Fact]
    public void Resolve_IteratorBlock_UsesTheSameStateMachinePath()
    {
        // Iterators carry IteratorStateMachineAttribute, not AsyncStateMachineAttribute. Resolution keys on
        // their shared base StateMachineAttribute, so this asserts the path really is shared rather than
        // async-only — otherwise an iterator would report "line not executable" for a line that plainly is.
        using var reader = new PdbReader();

        // An ordinary statement in the loop body. NOT the `yield return` line — see the test below: a
        // suspension point is where the resumed call re-enters, so that line is unobservable by construction.
        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.CountUp),
            PdbReaderTargets.LineOf("iteratorAccumulate"),
            "running");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.MethodName.Should().Be("MoveNext");
        result.Location.TypeName.Should().Contain("<CountUp>d__");
        result.Location.LocalTypeName.Should().Be("System.Int32");
    }

    [Fact]
    public void Resolve_TheSuspensionLineItself_IsRefused_BecauseAResumedCallReEntersAfterIt()
    {
        // `yield return running;` is where the generator suspends, and the compiler puts the resume label
        // immediately after it. So the next statement boundary — where R-A would inject — is exactly where the
        // NEXT MoveNext() call re-enters, without having executed the yield statement in that call. A probe
        // there reports "this line ran" on every resumption. Refusing is the only sound answer; probing any
        // ordinary line in the same iterator still works (the test above).
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.CountUp),
            PdbReaderTargets.LineOf("iteratorYield"),
            "running");

        result.IsResolved.Should().BeFalse("the boundary after a suspension point is reachable by resumption");
        result.Status.Should().Be(LineProbeResolutionStatus.LineNotExecutable);
        result.Detail.Should().NotBeNullOrEmpty("the operator needs to know why the line was refused");
    }

    [Fact]
    public void Resolve_AsyncLocalThatIsNotHoisted_StillResolves()
    {
        // `unitCost` never crosses the await. MEASURED: in Release it stays an ordinary MoveNext local slot,
        // in Debug it is hoisted to a field — the SAME source line resolves through a different mechanism
        // depending on build configuration. So this must pass either way, which is why resolution tries the
        // slot path before the field path instead of assuming async implies hoisted.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.ReserveAsync),
            PdbReaderTargets.LineOf("asyncTotalAssigned"),
            "unitCost");

        result.IsResolved.Should().BeTrue($"expected a resolution, got {result.Status}: {result.Detail}");
        result.Location!.LocalTypeName.Should().Be("System.Int32");

        // Exactly one of the two mechanisms must be in play — never both, because the native side would then
        // emit `ldloc` while the variable lived in a field.
        var viaSlot = result.Location.LocalSlot >= 0;
        var viaField = result.Location.HoistedFieldToken != 0;
        (viaSlot ^ viaField).Should().BeTrue(
            $"expected exactly one read mechanism, got slot={result.Location.LocalSlot} " +
            $"token={result.Location.HoistedFieldToken}");
    }

    [Fact]
    public void Resolve_UndeclaredAsyncLocal_IsRefused()
    {
        // A name that exists nowhere must fail closed, not fall back to some other field.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.SameNameDifferentTypesAsync),
            PdbReaderTargets.LineOf("asyncFirstY"),
            "notADeclaredLocal");

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(LineProbeResolutionStatus.LocalOutOfScope);
        result.Detail.Should().NotBeNullOrEmpty("the operator needs to be told which local was rejected");
    }

    [Fact]
    public void Resolve_AsyncLocalDeadAtTheNextBoundary_IsRefusedRatherThanSubstituted()
    {
        // THE BUG THIS PINS, found by running the suite in RELEASE after it passed in Debug.
        //
        // `inner` is the last statement of its block, so R-A's next-boundary read lands in the FOLLOWING
        // block — where a different `inner` of a different type is live. The first implementation matched
        // "name + live at the injection offset" and happily returned that second variable: a String reported
        // under the operator's name for the Int32 they asked about, boxed against the wrong token.
        //
        // Refusing is the only correct answer: the value the operator asked for no longer exists anywhere by
        // the time we are able to read it.
        using var reader = new PdbReader();

        var result = reader.Resolve(
            typeof(PdbReaderTargets),
            nameof(PdbReaderTargets.EndOfScopeAsync),
            PdbReaderTargets.LineOf("asyncEndOfScope"),
            "inner");

        // WHETHER it resolves is codegen-dependent and NOT the point: measured, Debug emits a sequence point
        // for the block's closing brace — still inside the scope — so the read is legitimately possible there,
        // while Release's next boundary is already in the following block. Asserting "must refuse" would pin
        // an incidental codegen detail and fail in Debug.
        //
        // The invariant that must hold in EVERY configuration is that we never substitute the OTHER `inner`.
        if (result.IsResolved)
        {
            result.Location!.LocalTypeName.Should().Be(
                "System.Int32",
                "the operator asked about the Int32 'inner'; resolving the String one from the following " +
                "block would be a wrong value reported under the right name");
        }
        else
        {
            result.Status.Should().Be(LineProbeResolutionStatus.LocalOutOfScope);
            result.Detail.Should().NotBeNullOrEmpty();
        }
    }

    /// <summary>
    /// Reads the IL offset of the sequence point for a given source line, straight from the test assembly's
    /// own PDB.
    /// </summary>
    /// <param name="methodName">Method on <see cref="PdbReaderTargets"/> to look inside.</param>
    /// <param name="sourceLine">The 1-based source line wanted.</param>
    /// <returns>The IL offset of that line's sequence point.</returns>
    // Deliberately a SEPARATE implementation from PdbReader's: this exists so the R-A test can state its
    // expected offsets without hardcoding a configuration's codegen, and reusing PdbReader here would make
    // the assertion agree with the code under test by construction.
    private static uint SequencePointOffsetOf(string methodName, int sourceLine)
    {
        var method = typeof(PdbReaderTargets).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"no method {methodName} on PdbReaderTargets");

        var location = typeof(PdbReaderTargets).Assembly.Location;
        using var peStream = File.OpenRead(location);
        using var peReader = new PEReader(peStream);

        MetadataReaderProvider? provider = null;
        MetadataReader pdb;
        var embedded = peReader.ReadDebugDirectory()
            .Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            .Select(e => (DebugDirectoryEntry?)e)
            .FirstOrDefault();

        if (embedded != null)
        {
            provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embedded.Value);
            pdb = provider.GetMetadataReader();
        }
        else
        {
            provider = MetadataReaderProvider.FromPortablePdbStream(
                File.OpenRead(Path.ChangeExtension(location, ".pdb")));
            pdb = provider.GetMetadataReader();
        }

        try
        {
            var debugInfo = pdb.GetMethodDebugInformation(
                MetadataTokens.MethodDefinitionHandle(method.MetadataToken));

            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (!sp.IsHidden && sp.StartLine == sourceLine)
                {
                    return (uint)sp.Offset;
                }
            }
        }
        finally
        {
            provider.Dispose();
        }

        throw new InvalidOperationException(
            $"no sequence point for line {sourceLine} in {methodName}; the marker may have moved onto a " +
            "line the compiler emits no sequence point for");
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
