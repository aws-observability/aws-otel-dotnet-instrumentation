// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Resolves a line-level configuration's (file, line, local name) into the interior IL offset and
/// local slot the native rewriter needs, using the target assembly's portable or embedded PDB.
/// </summary>
// This is the line-level analogue of ProfilerTranslator.ReflectionResolveMethod: function-level only
// needs a type + arity, but line-level additionally needs to know WHERE inside the method body to
// inject and WHICH local slot to read. Everything before this class existed used hardcoded offsets.
//
// Uses only System.Reflection.Metadata, which ships in the shared framework — no new package
// dependency (verified: net8.0 resolves it without a PackageReference).
//
// THREE CORRECTNESS RULES THIS ENCODES, each learned from a spike that silently produced wrong data:
//
//  R-A (offset -> statement+1). A sequence point's IL offset is the START of its statement, i.e.
//      BEFORE that statement's assignment has executed. To read the local assigned by statement k you
//      must inject at the start of statement k+1. Injecting at statement k's own start reads a slot
//      that is allocated but NOT YET ASSIGNED and silently yields 0/null. Proven live: the first R9
//      run captured a boxed 0 for every probe with no error anywhere.
//
//  R-B (instruction boundary + not a branch target). See IlBoundaryScanner. A branch-target offset
//      weaves successfully and NEVER FIRES.
//
//  R-C (Mvid must match). A PDB from a different build yields plausible-but-wrong offsets. Wrong-but-
//      plausible is worse than absent, because the resulting snapshot looks valid. Reject on mismatch.
//
//  R-D (async: the user's lines are in MoveNext, and the locals are FIELDS). For an async or iterator
//      method the operator names `FooAsync`, but that method body is only a state-machine launcher: it has
//      no sequence point for any interior source line. The user's statements live in the compiler-generated
//      `<FooAsync>d__N.MoveNext()`, and any local whose lifetime crosses an `await` is rewritten into a
//      FIELD `<name>5__N` on the state machine, so `ldloc` cannot reach it. See ResolveHoistedField for why
//      the field cannot be chosen by name alone.
internal sealed class PdbReader : IDisposable
{
    /// <summary>The hidden-sequence-point line marker (0xFEEFEE). Not a user statement.</summary>
    internal const int HiddenSequencePointLine = 0xFEEFEE;

    /// <summary>
    /// Portable-PDB CustomDebugInformation kind id for <c>StateMachineHoistedLocalScopes</c>.
    /// </summary>
    // From the portable PDB spec. The blob is a packed array of (StartOffset, Length) UINT32 pairs, one per
    // hoisted local ORDINAL — the only machine-readable record of which source scope each `<name>5__N` field
    // belongs to. MEASURED to be emitted by Roslyn for both Debug and Release on net8.0.
    private static readonly Guid StateMachineHoistedLocalScopesKind =
        new("6DA9A61E-F8C7-4874-BE62-68BC5630DF71");

    private readonly Dictionary<string, AssemblyDebugInfo?> cache = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>
    /// Resolves the injection location for a line-level target.
    /// </summary>
    /// <param name="type">The resolved target type.</param>
    /// <param name="methodName">The target method name.</param>
    /// <param name="lineNumber">The 1-based source line to instrument.</param>
    /// <param name="localName">Optional local variable name to capture.</param>
    /// <returns>The resolution outcome.</returns>
    public LineProbeResolution Resolve(Type type, string methodName, int lineNumber, string? localName)
    {
        if (type == null)
        {
            return LineProbeResolution.Fail(LineProbeResolutionStatus.TypeNotLoaded);
        }

        var assembly = type.Assembly;
        var debugInfo = this.GetDebugInfo(assembly);
        if (debugInfo == null)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoUnavailable,
                $"no readable PDB for {assembly.GetName().Name}");
        }

        if (!debugInfo.MvidMatches)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoMismatch,
                $"PDB does not belong to the loaded module {assembly.GetName().Name}");
        }

        // A line belongs to exactly one method body, but the configured method name disambiguates
        // overloads that share a file. Try every same-named overload and take the first whose debug
        // info actually contains the requested line.
        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        if (candidates.Length == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"no method named {methodName} on {type.FullName}");
        }

        LineProbeResolution? lastFailure = null;

        foreach (var method in candidates)
        {
            var result = this.ResolveInMethod(debugInfo, type, method, lineNumber, localName);
            if (result.IsResolved)
            {
                return result;
            }

            // Prefer the most specific failure: a local-scope failure means we DID find the line.
            if (lastFailure == null || result.Status == LineProbeResolutionStatus.LocalOutOfScope)
            {
                lastFailure = result;
            }

            // R-D: the requested line was not in this body. If the method is a state machine (async,
            // iterator, or async iterator), the user's statements are in the generated MoveNext, so retry
            // there. Tried AFTER the direct attempt rather than instead of it, because an async method's
            // OWN body does own a couple of real lines (the signature line) and a sync method must not pay
            // for an attribute lookup on the happy path.
            var moveNext = TryGetStateMachineMoveNext(method);
            if (moveNext?.DeclaringType == null)
            {
                continue;
            }

            var asyncResult = this.ResolveInMethod(
                debugInfo, moveNext.DeclaringType, moveNext, lineNumber, localName, isStateMachine: true);
            if (asyncResult.IsResolved)
            {
                return asyncResult;
            }

            // A state-machine failure is strictly more informative than the launcher's "line not found":
            // the launcher NEVER contains an interior line, so its failure says nothing.
            if (asyncResult.Status != LineProbeResolutionStatus.LineNotExecutable ||
                lastFailure?.Status == LineProbeResolutionStatus.LineNotExecutable)
            {
                lastFailure = asyncResult;
            }
        }

        return lastFailure ?? LineProbeResolution.Fail(LineProbeResolutionStatus.LineNotExecutable);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        foreach (var info in this.cache.Values)
        {
            info?.Dispose();
        }

        this.cache.Clear();
        this.disposed = true;
    }

    /// <summary>
    /// Finds the local variable slot for a source name that is in scope at the given IL offset.
    /// </summary>
    // Scope intersection is load-bearing, not a nicety: the compiler REUSES slots across disjoint
    // scopes, so the same slot index can hold a different variable earlier or later in the body.
    // Matching on name alone would return a slot that holds an unrelated value at our offset — a
    // confidently-wrong capture. Only names whose declaring scope CONTAINS the offset are eligible.
    private static int? ResolveLocalSlot(
        MetadataReader reader, MethodDefinitionHandle methodHandle, string localName, uint ilOffset)
    {
        var localScopes = reader.GetLocalScopes(methodHandle);

        foreach (var scopeHandle in localScopes)
        {
            var scope = reader.GetLocalScope(scopeHandle);
            uint start = (uint)scope.StartOffset;
            uint end = (uint)scope.EndOffset;

            // Half-open [start, end): a scope ending exactly at our offset no longer covers it.
            if (ilOffset < start || ilOffset >= end)
            {
                continue;
            }

            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = reader.GetLocalVariable(localHandle);
                if (local.Attributes.HasFlag(LocalVariableAttributes.DebuggerHidden))
                {
                    continue;
                }

                if (string.Equals(reader.GetString(local.Name), localName, StringComparison.Ordinal))
                {
                    return local.Index;
                }
            }
        }

        return null;
    }

    // Mirrors what the native side can express for a `box` token: a TypeRef defined by NAME against the corlib
    // AssemblyRef. Generic types need a TypeSpec, nested types resolve through their enclosing TypeRef as
    // scope, and anything outside corlib needs a different AssemblyRef — none of which a name can carry.
    // Keep in step with isCorlibNameableType in line_probe.cpp, which is the last line of defense.
    private static bool IsNameableThroughCorlib(Type type) =>
        type.Assembly == typeof(object).Assembly &&
        !type.IsGenericType &&
        !type.IsNested;

    /// <summary>
    /// Resolves the declared type of a local slot from the method body's local signature.
    /// </summary>
    /// <param name="method">The method declaring the local.</param>
    /// <param name="slot">The local slot index.</param>
    /// <returns>The local's declared type, or null when it cannot be determined.</returns>
    // Reflection, not the PDB: the PDB carries local NAMES and scopes but no signatures. LocalVariables
    // comes from the method's LocalVarSig blob — the same thing the JIT uses to type `ldloc` — so it is the
    // authoritative answer to "what does reading this slot push". GetMethodBody() returns null for
    // abstract/extern methods with no IL, hence the null tolerance rather than assuming a body exists.
    private static Type? ResolveLocalType(MethodInfo method, int slot)
    {
        try
        {
            var locals = method.GetMethodBody()?.LocalVariables;
            if (locals == null)
            {
                return null;
            }

            // Matched on LocalIndex rather than taken positionally: the collection's order is not
            // guaranteed to match slot numbering, and reading the wrong entry yields a
            // plausible-but-wrong box token — the exact class of silent error R-C exists to prevent.
            foreach (var local in locals)
            {
                if (local.LocalIndex == slot)
                {
                    return local.LocalType;
                }
            }

            return null;
        }
        catch (Exception)
        {
            // Reflecting over a body can throw for trimmed/dynamic/R2R methods. Treated as "type unknown",
            // which the caller converts into a refusal rather than a guessed box.
            return null;
        }
    }

    /// <summary>
    /// Returns the compiler-generated <c>MoveNext</c> for a state-machine method, or null when the method is
    /// an ordinary one.
    /// </summary>
    /// <param name="method">The method the operator named.</param>
    /// <returns>The state machine's <c>MoveNext</c>, or null.</returns>
    // Keyed on StateMachineAttribute, the BASE of AsyncStateMachineAttribute, IteratorStateMachineAttribute
    // and AsyncIteratorStateMachineAttribute — all three compile to a nested type with a MoveNext holding the
    // user's sequence points and `<name>5__N` hoisted fields, so one code path covers async methods, iterator
    // blocks and async streams. The attribute is the compiler's own record of the mapping; deriving the type
    // name from the method name (`<Foo>d__N`) would be guessing at an implementation detail that has changed
    // between Roslyn versions.
    private static MethodInfo? TryGetStateMachineMoveNext(MethodInfo method)
    {
        try
        {
            var attribute = method.GetCustomAttribute<StateMachineAttribute>(inherit: false);
            var stateMachineType = attribute?.StateMachineType;
            if (stateMachineType == null)
            {
                return null;
            }

            // MoveNext is private and explicitly implements IAsyncStateMachine/IEnumerator, so NonPublic is
            // required. DeclaredOnly keeps a base-class MoveNext from being picked up.
            return stateMachineType.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception)
        {
            // Attribute or member lookup can throw for trimmed/partially-loaded types. No state machine we
            // can prove, so resolution falls back to a plain LineNotExecutable rather than guessing.
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>StateMachineHoistedLocalScopes</c> table for a <c>MoveNext</c> body.
    /// </summary>
    /// <param name="reader">The PDB metadata reader.</param>
    /// <param name="moveNextHandle">Handle of the <c>MoveNext</c> method.</param>
    /// <returns>
    /// IL ranges indexed by hoisted-local ordinal, or null when the compiler emitted no such record.
    /// </returns>
    private static List<(uint Start, uint End)>? ReadHoistedLocalScopes(
        MetadataReader reader, MethodDefinitionHandle moveNextHandle)
    {
        try
        {
            foreach (var handle in reader.GetCustomDebugInformation(moveNextHandle))
            {
                var info = reader.GetCustomDebugInformation(handle);
                if (reader.GetGuid(info.Kind) != StateMachineHoistedLocalScopesKind)
                {
                    continue;
                }

                var blob = reader.GetBlobReader(info.Value);
                var scopes = new List<(uint Start, uint End)>();
                while (blob.RemainingBytes >= 8)
                {
                    uint start = blob.ReadUInt32();
                    uint length = blob.ReadUInt32();
                    scopes.Add((start, start + length));
                }

                return scopes;
            }

            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the state-machine field holding a hoisted source local that is live at the given IL offset.
    /// </summary>
    /// <param name="reader">The PDB metadata reader.</param>
    /// <param name="moveNextHandle">Handle of the <c>MoveNext</c> being woven.</param>
    /// <param name="stateMachineType">The compiler-generated state-machine type.</param>
    /// <param name="localName">The SOURCE name of the local, as the operator wrote it.</param>
    /// <param name="lineOffset">IL offset of the statement the operator actually named.</param>
    /// <param name="injectionOffset">The chosen injection offset — the NEXT statement boundary (R-A).</param>
    /// <param name="failureDetail">On failure, an operator-readable reason; null on success.</param>
    /// <returns>The matching field, or null when none is unambiguously live there.</returns>
    // NAME ALONE IS NOT ENOUGH, and this is the correctness crux of async capture.
    //
    // Roslyn renames a hoisted local to `<name>5__N`. Two locals that share a source NAME in disjoint scopes
    // produce TWO fields when their types differ — MEASURED on net8.0: `{var y = 1;} {var y = "s";}` yields
    // `<y>5__3` (Int32) and `<y>5__4` (String) in Release. Picking the first name match would capture the
    // wrong variable, of the wrong type, and then box it against the wrong token — the confidently-wrong
    // outcome R-C exists to prevent, and here it would also corrupt the customer's method body.
    //
    // The disambiguator is the StateMachineHoistedLocalScopes table: entry ORDINAL i covers the field whose
    // mangled suffix is i + 1. MEASURED on net8.0 in BOTH configurations — Release `<total>5__2` -> entry #1,
    // Debug `<unitCost>5__1` -> entry #0 — and the two `y` fields above land on disjoint ranges
    // [0x1C,0xA3) and [0xA3,0x14D), which is exactly what makes the choice decidable.
    //
    // TWO OFFSETS, NOT ONE — and this is the subtle part. R-A reads the local at the NEXT statement boundary,
    // which may lie PAST the end of the requested variable's scope. When a different variable of the same
    // name is live at that boundary, matching on "live at the injection offset" alone silently captures the
    // WRONG variable: MEASURED on net8.0 Release, probing the last statement of the first `{ var y = int; }`
    // block resolved to the String `y` of the following block. So the variable the operator MEANT is chosen
    // by the requested line, and the injection offset only decides whether it is still readable there.
    //
    // FAILS CLOSED on ambiguity. Returning nothing costs the operator a snapshot; returning the wrong field
    // costs them a wrong answer they cannot detect.
    private static FieldInfo? ResolveHoistedField(
        MetadataReader reader,
        MethodDefinitionHandle moveNextHandle,
        Type stateMachineType,
        string localName,
        uint lineOffset,
        uint injectionOffset,
        out string? failureDetail)
    {
        failureDetail = null;

        FieldInfo[] fields;
        try
        {
            fields = stateMachineType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (Exception)
        {
            failureDetail = $"could not enumerate fields of state machine {stateMachineType.Name}";
            return null;
        }

        // Exact prefix `<name>5__`, so `<y>5__3` matches `y` but never `<yy>5__3` or the compiler's own
        // `<>1__state` / `<>t__builder` / `<>u__1` bookkeeping fields.
        var prefix = $"<{localName}>5__";
        var candidates = new List<(FieldInfo Field, int Ordinal)>();
        foreach (var field in fields)
        {
            if (!field.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(field.Name.AsSpan(prefix.Length), out var suffix) || suffix < 1)
            {
                continue;
            }

            candidates.Add((field, suffix - 1));
        }

        if (candidates.Count == 0)
        {
            failureDetail = $"no hoisted field for local '{localName}' on {stateMachineType.Name}";
            return null;
        }

        var scopes = ReadHoistedLocalScopes(reader, moveNextHandle);
        if (scopes == null)
        {
            // No scope table to decide with. A SINGLE candidate is still unambiguous — there is no other
            // variable it could be — so accept it; more than one is a genuine coin flip, so refuse.
            if (candidates.Count == 1)
            {
                return candidates[0].Field;
            }

            failureDetail =
                $"local '{localName}' has {candidates.Count} hoisted fields and the PDB carries no " +
                "StateMachineHoistedLocalScopes to choose between them";
            return null;
        }

        // Half-open [start, end), matching ResolveLocalSlot: a scope ending exactly at our offset no longer
        // covers it.
        var atLine = new List<FieldInfo>();
        var atInjection = new List<FieldInfo>();
        foreach (var (field, ordinal) in candidates)
        {
            if (ordinal >= scopes.Count)
            {
                continue;
            }

            var (start, end) = scopes[ordinal];
            if (lineOffset >= start && lineOffset < end)
            {
                atLine.Add(field);
            }

            if (injectionOffset >= start && injectionOffset < end)
            {
                atInjection.Add(field);
            }
        }

        if (atLine.Count == 1)
        {
            // The operator's line unambiguously identifies WHICH variable they meant. It is usable only if
            // that same variable is still live where we can actually read it.
            if (atInjection.Contains(atLine[0]))
            {
                return atLine[0];
            }

            failureDetail =
                $"local '{localName}' is out of scope by IL offset {injectionOffset}, the next statement " +
                $"boundary after the requested line; refusing to read a different variable of the same name";
            return null;
        }

        if (atLine.Count > 1)
        {
            failureDetail = $"local '{localName}' matches {atLine.Count} hoisted fields at the requested line";
            return null;
        }

        // No candidate covers the requested line. Normal for the line that DECLARES the local: a hoisted
        // scope opens after the assigning store, so the declaring statement's own offset precedes it. Fall
        // back to whatever is unambiguously live where the probe will actually run.
        if (atInjection.Count == 1)
        {
            return atInjection[0];
        }

        failureDetail = atInjection.Count == 0
            ? $"local '{localName}' is hoisted but not live at IL offset {injectionOffset} in " +
              $"{stateMachineType.Name}.MoveNext"
            : $"local '{localName}' matches {atInjection.Count} hoisted fields live at IL offset " +
              $"{injectionOffset}; refusing to guess";
        return null;
    }

    private LineProbeResolution ResolveInMethod(
        AssemblyDebugInfo debugInfo,
        Type type,
        MethodInfo method,
        int lineNumber,
        string? localName,
        bool isStateMachine = false)
    {
        var reader = debugInfo.Reader;
        var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);

        MethodDebugInformation methodDebugInfo;
        try
        {
            methodDebugInfo = reader.GetMethodDebugInformation(handle);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoUnavailable, $"unreadable debug info: {ex.Message}");
        }

        // Collect non-hidden sequence points in IL order. 0xFEEFEE marks compiler-generated code with
        // no user statement; injecting there is meaningless and its "line" is a sentinel, not a line.
        var points = new List<(int Line, uint Offset)>();
        try
        {
            foreach (var sp in methodDebugInfo.GetSequencePoints())
            {
                if (sp.IsHidden || sp.StartLine == HiddenSequencePointLine)
                {
                    continue;
                }

                points.Add((sp.StartLine, (uint)sp.Offset));
            }
        }
        catch (BadImageFormatException ex)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoUnavailable, $"corrupt sequence points: {ex.Message}");
        }

        if (points.Count == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable, $"{method.Name} has no sequence points");
        }

        int matchIndex = points.FindIndex(p => p.Line == lineNumber);
        if (matchIndex < 0)
        {
            var nearest = points.OrderBy(p => Math.Abs(p.Line - lineNumber)).First().Line;
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"line {lineNumber} is not an executable statement in {method.Name}; nearest is {nearest}");
        }

        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null || il.Length == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable, $"{method.Name} has no IL body");
        }

        var scan = IlBoundaryScanner.Scan(il);

        // R-A: to observe the effect of the requested line, inject at the START OF THE NEXT statement.
        // Without a next statement in this method the line's own effect is never observable at a
        // boundary we control, so refuse rather than capture a pre-assignment value.
        var candidateOffsets = new List<uint>();
        for (int i = matchIndex + 1; i < points.Count; i++)
        {
            candidateOffsets.Add(points[i].Offset);
        }

        if (candidateOffsets.Count == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"line {lineNumber} is the last statement in {method.Name}; no following boundary to read it at");
        }

        // R-B: take the first candidate that is a real instruction start and not a branch target.
        //
        // R-E: and the candidate must not be reachable WITHOUT the probed line having executed. A probe asserts
        // "this line ran"; injecting it past a control-flow merge breaks that assertion. Measured on
        // HasInnerScope compiled Release: line 76 (the last statement inside the `if`) sits at IL 0x9, its next
        // boundary 0xD is the `brfalse` target, so the old rule skipped to 0x11 — which is where line 79, AFTER
        // the block, also resolves. HasInnerScope(false) never runs line 76 and fired the probe anyway.
        //
        // Debug hid it entirely: the block's closing brace gets its own sequence point inside the block, so the
        // first candidate was already safe and nothing skipped. Release is what ships.
        var lineOffset = points[matchIndex].Offset;
        uint? chosenOffset = null;
        string? rejectedForMerge = null;
        foreach (var offset in candidateOffsets)
        {
            if (offset >= il.Length)
            {
                continue;
            }

            if (!scan.IsSafeInjectionPoint(offset))
            {
                continue;
            }

            if (scan.IsReachableWithoutExecuting(lineOffset, offset))
            {
                // Remembered rather than returned immediately: a LATER candidate can still be safe (a loop
                // body's back-edge lands early, and the statement after it is only reachable through the
                // body), so keep scanning and only report this if nothing else qualifies.
                rejectedForMerge ??=
                    $"line {lineNumber} in {method.Name} cannot be observed at IL 0x{offset:X}: a branch from " +
                    "before the line lands there, so a probe would also fire on paths that skip the line";
                continue;
            }

            chosenOffset = offset;
            break;
        }

        if (chosenOffset == null && rejectedForMerge != null)
        {
            return LineProbeResolution.Fail(LineProbeResolutionStatus.LineNotExecutable, rejectedForMerge);
        }

        if (chosenOffset == null)
        {
            var detail = $"no safe injection boundary after line {lineNumber} in {method.Name} " +
                $"(all candidates were branch targets or mid-instruction; scanComplete={scan.Complete})";
            return LineProbeResolution.Fail(LineProbeResolutionStatus.LineNotExecutable, detail);
        }

        int slot = -1;
        Type? capturedType = null;
        uint hoistedFieldToken = 0;

        if (!string.IsNullOrEmpty(localName))
        {
            var slotResult = ResolveLocalSlot(reader, handle, localName!, chosenOffset.Value);
            if (slotResult != null)
            {
                slot = slotResult.Value;

                // The PDB gives the slot INDEX but not the slot's TYPE — sequence points and local scopes
                // carry names, not signatures. The type comes from the method body's signature via
                // reflection, which is the authoritative source for what `ldloc <slot>` actually pushes.
                capturedType = ResolveLocalType(method, slot);
                if (capturedType == null)
                {
                    // Slot resolved from the PDB but absent from the method body's local signature — the two
                    // disagree, so refuse rather than guess a box token. Same fail-closed rule as a mismatched
                    // PDB (R-C): a wrong box is undefined behavior, not a clean error.
                    var detail =
                        $"local '{localName}' resolved to slot {slot} but that slot has no type in " +
                        $"{method.Name}'s body";
                    return LineProbeResolution.Fail(LineProbeResolutionStatus.LocalOutOfScope, detail);
                }
            }
            else if (isStateMachine)
            {
                // R-D: not a slot, so it was hoisted onto the state machine. Both outcomes are normal and
                // depend on the BUILD CONFIGURATION, not the source: measured on net8.0, a local that never
                // crosses an `await` stays a real slot in Release but is hoisted to a field in Debug. So both
                // paths must work for the same source line.
                var field = ResolveHoistedField(
                    reader,
                    handle,
                    method.DeclaringType!,
                    localName!,
                    lineOffset: points[matchIndex].Offset,
                    injectionOffset: chosenOffset.Value,
                    out var detail);
                if (field == null)
                {
                    return LineProbeResolution.Fail(LineProbeResolutionStatus.LocalOutOfScope, detail);
                }

                // The native side needs the token in the TARGET's module, which is where this field is
                // declared — the state machine lives in the customer's own assembly alongside its method.
                hoistedFieldToken = (uint)field.MetadataToken;
                capturedType = field.FieldType;
            }
            else
            {
                return LineProbeResolution.Fail(
                    LineProbeResolutionStatus.LocalOutOfScope,
                    $"local '{localName}' is not in scope at IL offset {chosenOffset.Value} in {method.Name}");
            }
        }

        string? localTypeName = null;
        var localIsValueType = false;
        if (capturedType != null)
        {
            localTypeName = capturedType.FullName ?? capturedType.Name;
            localIsValueType = capturedType.IsValueType;

            // A by-ref local (`ref int x`) is a managed pointer, not a value. `box` on one is invalid IL and
            // the verifier would reject the rewritten body, so refuse before emitting anything.
            if (capturedType.IsByRef || capturedType.IsPointer)
            {
                return LineProbeResolution.Fail(
                    LineProbeResolutionStatus.LocalNotCapturable,
                    $"local '{localName}' is a by-ref or pointer type ({localTypeName}) and cannot be captured");
            }

            // A VALUE type has to be boxed to reach the object-typed callback, and the native rewriter names
            // the box token with DefineTypeRefByName against the CORLIB AssemblyRef — so it can only express a
            // plain, non-generic, non-nested corlib type. Any other value type (a customer enum or struct,
            // Nullable<int>, a nested value type) would be emitted as `box [corlib]That.Name`, which the JIT
            // cannot resolve when it compiles the rewritten body: TypeLoadException in the CUSTOMER'S method,
            // for every caller, not merely a lost snapshot. Refuse here so the operator gets a reason.
            //
            // Reference types are unaffected — they need no box at all.
            if (localIsValueType && !IsNameableThroughCorlib(capturedType))
            {
                var boxDetail =
                    $"local '{localName}' is a value type ({localTypeName}) that the native rewriter cannot " +
                    "name as a box token; only plain non-generic, non-nested System.* value types are supported";
                return LineProbeResolution.Fail(LineProbeResolutionStatus.LocalNotCapturable, boxDetail);
            }
        }

        var typeName = type.FullName ?? type.Name;

        // The native target lookup (clr_helpers.cpp FindTypeDefByName) splits the type name on '+' and
        // supports EXACTLY ONE level of nesting. A state machine is nested once inside its declaring type,
        // which fits — but an async method on an ALREADY-NESTED type produces two levels and the native side
        // would silently find no TypeDef and skip the probe. Refuse here so the operator gets a reason.
        if (typeName.IndexOf('+') != typeName.LastIndexOf('+'))
        {
            var nestingDetail =
                $"target type '{typeName}' is nested more than one level deep, which the native rewriter " +
                "cannot resolve";
            return LineProbeResolution.Fail(LineProbeResolutionStatus.LocalNotCapturable, nestingDetail);
        }

        return LineProbeResolution.Success(new LineProbeLocation(
            MethodToken: method.MetadataToken,
            AssemblyName: type.Assembly.GetName().Name ?? string.Empty,
            TypeName: typeName,
            MethodName: method.Name,
            ParameterCount: method.GetParameters().Length,
            IlOffset: chosenOffset.Value,
            LocalSlot: slot,
            LocalName: localName,
            LocalTypeName: localTypeName,
            LocalIsValueType: localIsValueType,
            HoistedFieldToken: hoistedFieldToken));
    }

    private AssemblyDebugInfo? GetDebugInfo(Assembly assembly)
    {
        var key = assembly.FullName ?? assembly.GetName().Name ?? assembly.Location;
        if (this.cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = AssemblyDebugInfo.TryOpen(assembly);
        this.cache[key] = info;
        return info;
    }

    /// <summary>
    /// An opened PDB for one assembly, plus whether it actually belongs to that assembly's module.
    /// </summary>
    private sealed class AssemblyDebugInfo : IDisposable
    {
        private readonly IDisposable?[] owned;

        private AssemblyDebugInfo(MetadataReader reader, bool mvidMatches, params IDisposable?[] owned)
        {
            this.Reader = reader;
            this.MvidMatches = mvidMatches;
            this.owned = owned;
        }

        public MetadataReader Reader { get; }

        public bool MvidMatches { get; }

        /// <summary>
        /// Opens the embedded or sidecar portable PDB for an assembly, verifying it matches the module.
        /// </summary>
        /// <param name="assembly">The assembly whose debug info is wanted.</param>
        /// <returns>The opened debug info, or null when none is available.</returns>
        public static AssemblyDebugInfo? TryOpen(Assembly assembly)
        {
            // Dynamic/in-memory assemblies have no on-disk image to read debug info from.
            string location;
            try
            {
                location = assembly.Location;
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return null;
            }

            try
            {
                var peStream = File.OpenRead(location);
                var peReader = new PEReader(peStream);
                var moduleMvid = ReadMvid(peReader);

                // Prefer an EMBEDDED PDB: it cannot be stale by construction, since it ships inside
                // the same file as the IL.
                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        continue;
                    }

                    var embedded = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    return new AssemblyDebugInfo(
                        embedded.GetMetadataReader(), true, embedded, peReader, peStream);
                }

                // Fall back to a sidecar .pdb next to the assembly.
                var pdbPath = Path.ChangeExtension(location, ".pdb");
                if (!File.Exists(pdbPath))
                {
                    peReader.Dispose();
                    peStream.Dispose();
                    return null;
                }

                var pdbStream = File.OpenRead(pdbPath);
                MetadataReaderProvider provider;
                try
                {
                    provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                }
                catch (BadImageFormatException)
                {
                    // Almost certainly a Windows (native) PDB rather than a portable one. v1 is
                    // portable + embedded only, so treat it as unavailable rather than guessing.
                    pdbStream.Dispose();
                    peReader.Dispose();
                    peStream.Dispose();
                    return null;
                }

                var pdbReader = provider.GetMetadataReader();
                bool matches = MvidMatchesModule(peReader, pdbReader, moduleMvid);
                return new AssemblyDebugInfo(pdbReader, matches, provider, pdbStream, peReader, peStream);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            // Reverse order: readers before the streams they read from.
            for (int i = this.owned.Length - 1; i >= 0; i--)
            {
                try
                {
                    this.owned[i]?.Dispose();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Disposal of debug-info handles must never propagate: resolution already
                    // succeeded or failed, and throwing here would surface as an unrelated error.
                }
            }
        }

        private static Guid ReadMvid(PEReader peReader)
        {
            var mdReader = peReader.GetMetadataReader();
            return mdReader.GetGuid(mdReader.GetModuleDefinition().Mvid);
        }

        /// <summary>
        /// Verifies a sidecar PDB belongs to this module (R-C).
        /// </summary>
        // The PE's CodeView debug-directory entry carries the PDB's identity (GUID + age); the
        // portable PDB carries the same identity in its DebugMetadataHeader.Id blob (first 16 bytes =
        // the GUID). Equal GUIDs means same build. If we cannot read either side we FAIL CLOSED
        // (report mismatch) rather than assume a match — an unverifiable PDB is exactly the case R-C
        // exists to reject.
        private static bool MvidMatchesModule(PEReader peReader, MetadataReader pdbReader, Guid moduleMvid)
        {
            try
            {
                var pdbId = pdbReader.DebugMetadataHeader?.Id ?? default;
                if (pdbId.IsDefaultOrEmpty || pdbId.Length < 16)
                {
                    return false;
                }

                var pdbGuid = new Guid(pdbId.AsSpan(0, 16).ToArray());

                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.CodeView)
                    {
                        continue;
                    }

                    var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                    return codeView.Guid == pdbGuid;
                }

                // No CodeView entry to compare against: fall back to the module Mvid, which the
                // deterministic-build toolchain also uses as the PDB id.
                return pdbGuid == moduleMvid;
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
            {
                return false;
            }
        }
    }
}
