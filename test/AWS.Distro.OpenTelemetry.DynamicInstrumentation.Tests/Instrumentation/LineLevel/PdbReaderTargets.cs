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
}
