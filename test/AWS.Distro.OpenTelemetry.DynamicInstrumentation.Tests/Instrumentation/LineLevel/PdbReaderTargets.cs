// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Target methods for <see cref="PdbReaderTests"/>, plus a way to look up the source line number of a
/// specific statement inside them.
/// </summary>
// LINE NUMBERS ARE LOOKED UP BY MARKER, NOT HARDCODED OR COMPUTED.
//
// A test that hardcodes "line 42" keeps passing after someone adds a using directive above — it just
// silently targets a different statement. Offset arithmetic ("first statement + 2") has the same flaw
// with extra steps. Both are the "passes for the wrong reason" hazard that the line-level spikes hit
// repeatedly, so the fixture avoids the class entirely: each interesting statement carries a
// `// @marker:NAME` comment, and LineOf(NAME) finds it by reading THIS FILE at test time (path from
// [CallerFilePath]). Rearranging this file cannot invalidate the tests; deleting a marker fails loudly.
internal static class PdbReaderTargets
{
    // The factory closes over LoadMarkers() so the [CallerFilePath] default is captured at THIS call
    // site — i.e. the path of this file — rather than wherever a test happens to invoke LineOf from.
    private static readonly Lazy<Dictionary<string, int>> Markers = new(() => LoadMarkers());

    /// <summary>
    /// Gets the 1-based source line number of the statement tagged with the given marker.
    /// </summary>
    /// <param name="marker">The marker name, without the <c>@marker:</c> prefix.</param>
    /// <returns>The line number of the tagged statement.</returns>
    public static int LineOf(string marker) =>
        Markers.Value.TryGetValue(marker, out var line)
            ? line
            : throw new InvalidOperationException(
                $"marker '{marker}' not found in PdbReaderTargets.cs. Markers present: " +
                string.Join(", ", Markers.Value.Keys.OrderBy(k => k)));

    /// <summary>
    /// Three sequential assignments then a return: four statement boundaries, so a probe on any of the
    /// first three statements has a following boundary to be read at.
    /// </summary>
    /// <param name="x">Seed value.</param>
    /// <returns>x + 111.</returns>
    public static int ThreeStatements(int x)
    {
        int a = x + 1; // @marker:assignsA
        int b = a + 10; // @marker:assignsB
        int c = b + 100; // @marker:assignsC

        // @marker:blankish  (a comment-only line: no sequence point is emitted for it)
        return c; // @marker:returnsC
    }

    /// <summary>
    /// A method whose body is a single statement, so that statement has no successor boundary.
    /// </summary>
    /// <param name="x">Seed value.</param>
    /// <returns>x + 1.</returns>
    public static int SingleStatement(int x)
    {
        return x + 1; // @marker:onlyStatement
    }

    /// <summary>
    /// Declares a local inside a nested block so its scope does not cover the whole method body.
    /// </summary>
    /// <param name="flag">Whether to enter the inner block.</param>
    /// <returns>A value depending on <paramref name="flag"/>.</returns>
    public static int HasInnerScope(bool flag)
    {
        int outer = 1; // @marker:outerAssigned

        if (flag)
        {
            int inner = outer + 5; // @marker:innerAssigned
            outer = inner * 2; // @marker:insideInnerScope
        }

        outer = outer + 1; // @marker:outsideInnerScope
        return outer;
    }

    /// <summary>
    /// Declares locals of several type families so resolution can be asserted to report the right declared
    /// type and value-ness per slot.
    /// </summary>
    /// <param name="x">Seed value.</param>
    /// <returns>A string built from every local.</returns>
    // Type resolution decides whether the native side emits a `box` and against WHICH token. Reference
    // types must get no box at all; value types must get their own. So the fixture needs at least one of
    // each, plus a non-Int32 value type — otherwise a resolver that always answered "System.Int32,
    // isValueType=true" would pass.
    public static string MixedLocalTypes(int x)
    {
        int number = x + 1; // @marker:mixedNumber
        string text = $"v{number}"; // @marker:mixedText
        double ratio = number * 1.5; // @marker:mixedRatio
        DateTime stamp = new DateTime(2026, 1, 1).AddDays(number); // @marker:mixedStamp
        object boxed = text; // @marker:mixedBoxed
        int[] items = new[] { number }; // @marker:mixedItems
        return $"{number}{text}{ratio}{stamp:O}{boxed}{items.Length}";
    }

    /// <summary>
    /// Declares a by-ref local, which cannot be boxed and therefore cannot be captured.
    /// </summary>
    /// <param name="value">A value to alias.</param>
    /// <returns>The aliased value.</returns>
    // `box` on a managed pointer is invalid IL — the verifier would reject the ENTIRE rewritten method
    // body, taking the customer's method down rather than merely losing a snapshot. So this must be
    // refused during resolution, before anything is emitted.
    public static int HasByRefLocal(int value)
    {
        ref int alias = ref value;
        alias = alias + 1; // @marker:byRefAssigned
        return alias;
    }

    /// <summary>
    /// Declares value-type locals that live OUTSIDE corlib or need more than a name to denote, which the
    /// native rewriter cannot express as a box token.
    /// </summary>
    /// <param name="seed">Seed value.</param>
    /// <returns>A label built from the locals.</returns>
    // The native side names a box token with DefineTypeRefByName against the CORLIB AssemblyRef, and that API
    // validates nothing — it appends a TypeRef row for any name. So a customer enum emitted
    // `box [corlib]...+Severity` and the JIT killed the customer's method with TypeLoadException when it
    // compiled the rewritten body. Each local here is one shape that has to be refused during resolution:
    // a customer enum, a customer struct, and a generic value type (Nullable<int>, which needs a TypeSpec).
    public static string UncapturableValueTypes(int seed)
    {
        Severity level = seed > 0 ? Severity.High : Severity.Low; // @marker:enumLocal
        Point point = new Point(seed, seed + 1); // @marker:structLocal
        int? maybe = seed > 0 ? seed : null; // @marker:nullableLocal
        long plain = seed + 3L; // @marker:corlibLocal
        return $"{level}{point.X}{maybe}{plain}";
    }

    /// <summary>
    /// An async method whose captured local crosses an <c>await</c>, so the compiler hoists it into a field
    /// of the generated state machine.
    /// </summary>
    /// <param name="quantity">Seed value.</param>
    /// <returns>A label built from the hoisted local.</returns>
    // The lines an operator names in an async method DO NOT EXIST in this method's body — it compiles to a
    // state-machine launcher. They exist in `<ReserveAsync>d__N.MoveNext()`, and `total` is not a local there
    // but a field `<total>5__N`, so `ldloc` cannot reach it. Both facts have to hold for resolution to be
    // right, and neither is visible from the source.
    public static async Task<string> ReserveAsync(int quantity)
    {
        var unitCost = 7; // @marker:asyncUnitCost
        var total = quantity * unitCost; // @marker:asyncTotalAssigned
        await Task.Yield();
        var label = $"reserved:{total}"; // @marker:asyncAfterAwait
        return label;
    }

    /// <summary>
    /// An async method hoisting locals of three different type families, so a hardcoded box token cannot pass.
    /// </summary>
    /// <param name="id">Seed value.</param>
    /// <returns>A summary of every hoisted local.</returns>
    public static async Task<string> MixedAsync(int id)
    {
        var note = $"item-{id}";
        var stamp = new DateTime(2026, 1, 1).AddDays(id);
        var ratio = id * 1.5; // @marker:asyncMixedRatioAssigned
        await Task.Yield();
        var summary = $"{note}/{stamp:yyyy-MM-dd}/{ratio}"; // @marker:asyncMixedAfterAwait
        return summary;
    }

    /// <summary>
    /// An async method declaring TWO locals that share a source name but differ in type, in disjoint scopes.
    /// </summary>
    /// <param name="seed">Seed value.</param>
    /// <returns>Both values concatenated.</returns>
    // THE AMBIGUITY CASE. Measured on net8.0, this produces two hoisted fields — `<y>5__3` (Int32) and
    // `<y>5__4` (String) in Release — so choosing by NAME alone picks a variable at random, of the wrong
    // type, and boxes it against the wrong token. Only the StateMachineHoistedLocalScopes IL ranges tell the
    // two apart, which is what these markers let the tests assert per-scope.
    // Each block has a statement AFTER the probed one, deliberately: R-A reads the local at the NEXT
    // statement boundary, so a marker on a block's LAST statement would put the read past the block's scope.
    // The end-of-scope case is covered separately by EndOfScopeAsync, where it is the point of the test.
    public static async Task<string> SameNameDifferentTypesAsync(int seed)
    {
        string acc = string.Empty;
        {
            var y = seed + 1;
            await Task.Yield();
            acc += y; // @marker:asyncFirstY
            acc += "|";
        }

        {
            var y = $"s{seed}";
            await Task.Yield();
            acc += y; // @marker:asyncSecondY
            acc += "|";
        }

        return acc;
    }

    /// <summary>
    /// An async method where the probed local goes out of scope at the very next statement boundary.
    /// </summary>
    /// <param name="seed">Seed value.</param>
    /// <returns>The accumulated string.</returns>
    // THE SUBSTITUTION HAZARD. `inner` is the last statement of its block, so R-A's next boundary lies in the
    // FOLLOWING block, where a different variable of the same name is live. Resolution must refuse rather
    // than read that one — the operator asked about the first `inner`, and answering with the second is a
    // wrong value that looks entirely plausible.
    public static async Task<string> EndOfScopeAsync(int seed)
    {
        var acc = string.Empty;
        {
            var inner = seed + 1;
            await Task.Yield();
            acc += inner; // @marker:asyncEndOfScope
        }

        {
            var inner = $"s{seed}";
            await Task.Yield();
            acc += inner;
        }

        return acc;
    }

    /// <summary>
    /// An iterator block, which compiles to a state machine exactly as an async method does.
    /// </summary>
    /// <param name="count">How many values to yield.</param>
    /// <returns>A sequence of running totals.</returns>
    // Iterators carry IteratorStateMachineAttribute rather than AsyncStateMachineAttribute. Both derive from
    // StateMachineAttribute, which is what resolution keys on — so this asserts the shared path really is
    // shared, rather than async-only.
    public static IEnumerable<int> CountUp(int count)
    {
        var running = 0;
        for (int i = 0; i < count; i++)
        {
            running += i; // @marker:iteratorAccumulate
            yield return running; // @marker:iteratorYield
        }
    }

    private static Dictionary<string, int> LoadMarkers([CallerFilePath] string? path = null)
    {
        if (path == null || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"PdbReaderTargets source not found at '{path}'. The marker-based fixture needs the " +
                "source file at test time; if this ever runs from a packaged location, switch to " +
                "committing a generated line map instead.");
        }

        var markers = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(path);

        for (int i = 0; i < lines.Length; i++)
        {
            const string token = "// @marker:";
            int at = lines[i].IndexOf(token, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            // Take the first whitespace-delimited word after the token as the marker name, so a
            // marker may carry a trailing explanatory comment.
            var rest = lines[i][(at + token.Length)..].Trim();
            var name = rest.Split(' ', '\t')[0];
            if (name.Length > 0)
            {
                markers[name] = i + 1; // file lines are 1-based
            }
        }

        return markers;
    }

    /// <summary>A customer-defined enum: a value type that is NOT in corlib.</summary>
    // Nested here so the fixture stays one file; its FullName therefore also carries a '+', which is a
    // second reason a corlib TypeRef-by-name cannot denote it. A top-level customer enum fails on the
    // assembly alone, which IsNameableThroughCorlib checks first.
    internal enum Severity
    {
        /// <summary>Low.</summary>
        Low = 0,

        /// <summary>High.</summary>
        High = 1,
    }

    /// <summary>A customer-defined struct: a value type that is NOT in corlib.</summary>
    /// <param name="X">First component.</param>
    /// <param name="Y">Second component.</param>
    internal readonly record struct Point(int X, int Y);
}
