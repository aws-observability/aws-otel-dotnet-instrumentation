// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Translates a line-level configuration into a <see cref="NativeLineProbeDefinition"/> and registers it
/// with the native profiler via the <c>AddLineProbes</c> P/Invoke.
/// </summary>
// The line-level sibling of ProfilerTranslator. Kept as a SEPARATE class rather than another branch
// inside ProfilerTranslator, because the two share almost nothing beyond the marshaling discipline:
// function-level registers one definition per overload ARITY and needs no source information, while
// line-level registers exactly one definition at one resolved IL OFFSET for one specific method.
// Merging them would mean a class where half the fields are meaningless on each path.
//
// Constructor injection mirrors ProfilerTranslator's `addInstrumentationsOverride` seam so the whole
// path is unit-testable WITHOUT the forked native binary present — which matters right now, because the
// fork is not yet in the build (F2). The seam is the only reason P3b can be written and verified before
// the fork lands.
internal sealed class LineProbeTranslator : IDisposable
{
    /// <summary>Simple name of the assembly hosting the managed line-probe callback.</summary>
    internal const string CallbackAssembly = "AWS.Distro.OpenTelemetry.DynamicInstrumentation";

    /// <summary>Fully-qualified type hosting the managed line-probe callback.</summary>
    internal const string CallbackType =
        "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel.DiLineIntegration";

    /// <summary>Capture callback for <see cref="LineProbeEmissionMode.LocalCapture"/>: <c>void CaptureLocal(int32, object)</c>.</summary>
    internal const string CaptureMethod = "CaptureLocal";

    /// <summary>Callback for <see cref="LineProbeEmissionMode.Legacy"/> (no local): <c>void Probe(int32)</c>.</summary>
    internal const string ProbeMethod = "Probe";

    /// <summary>Rate-limit gate name: <c>bool ShouldCapture(int32)</c>.</summary>
    internal const string GateMethod = "ShouldCapture";

    /// <summary>
    /// Maximum locals captured at one line. Extra names are dropped, not refused.
    /// </summary>
    // Each captured local adds a `call` (and, for a value type, a `box`) to the customer's line. On a line
    // inside a hot loop that cost is paid on every iteration, so an operator pasting a long name list would
    // silently slow their own service. 5 matches the practical ceiling of what fits in a readable snapshot;
    // Datadog's comparable limit is per-object field count (20), not per-line locals, so there is no vendor
    // number to mirror here.
    internal const int MaxLocalsPerLine = 5;

    /// <summary>
    /// Initial size of the weave-result buffer, grown on demand.
    /// </summary>
    // Comfortably above any realistic live-probe count (each config contributes at most MaxLocalsPerLine),
    // so the steady state is one P/Invoke per poll with no reallocation. The grow path below is what keeps
    // this a performance choice rather than a correctness limit.
    internal const int InitialWeaveResultCapacity = 64;

    /// <summary>
    /// Full display name of the callback assembly — what actually crosses the ABI.
    /// </summary>
    // A DISPLAY NAME, NOT THE SIMPLE NAME, because the native side may have to DEFINE the AssemblyRef rather
    // than find one. A customer assembly has no compile-time reference to this one, so when a module carries
    // only line-level probes there is no existing ref to reuse, and emitting one requires the version, culture
    // and public key token. `AssemblyReference` on the native side parses exactly this format, so the whole
    // identity fits in the string field the ABI already had — no struct change, and every harness that still
    // passes a bare simple name keeps working.
    //
    // Read off the loaded assembly rather than hardcoded: this assembly is strong-named, and a hardcoded
    // token would silently rot the moment the signing key or version changed. A ref carrying the WRONG token
    // does not fail loudly — it binds to nothing, and the woven call resolves to no method at runtime.
    //
    // Declared BELOW the constants, not next to CallbackAssembly which it derives from, purely to satisfy
    // SA1203 (constants before non-constant fields).
    internal static readonly string CallbackAssemblyFullName =
        typeof(DiLineIntegration).Assembly.FullName ?? CallbackAssembly;

    private readonly Action<string, NativeLineProbeDefinition[], int>? addLineProbesOverride;
    private readonly Action<int>? removeLineProbeOverride;
    private readonly Func<NativeLineProbeWeaveResult[], int, int>? getWeaveResultsOverride;
    private readonly Func<InstrumentationConfiguration, Type?> resolveType;
    private readonly PdbReader pdbReader;

    // Reused across polls, and only ever touched by GetWeaveResults. Not thread-safe on purpose — see the
    // no-concurrent-callers note there.
    private NativeLineProbeWeaveResult[] weaveResultBuffer = new NativeLineProbeWeaveResult[InitialWeaveResultCapacity];

    /// <summary>
    /// Initializes a new instance of the <see cref="LineProbeTranslator"/> class.
    /// </summary>
    /// <param name="addLineProbesOverride">Test seam replacing the <c>AddLineProbes</c> P/Invoke.</param>
    /// <param name="removeLineProbeOverride">Test seam replacing the <c>RemoveLineProbe</c> P/Invoke.</param>
    /// <param name="typeResolver">Test seam replacing loaded-assembly type resolution.</param>
    /// <param name="pdbReader">Reader used to resolve line→offset; a fresh one is created if omitted.</param>
    /// <param name="getWeaveResultsOverride">
    /// Test seam replacing the <c>GetLineProbeWeaveResults</c> P/Invoke. Takes the buffer and its capacity,
    /// and returns the TOTAL result count exactly as the native export does — including the case where that
    /// total exceeds the capacity, so the grow-and-retry path is testable without a profiler.
    /// </param>
    public LineProbeTranslator(
        Action<string, NativeLineProbeDefinition[], int>? addLineProbesOverride = null,
        Action<int>? removeLineProbeOverride = null,
        Func<InstrumentationConfiguration, Type?>? typeResolver = null,
        PdbReader? pdbReader = null,
        Func<NativeLineProbeWeaveResult[], int, int>? getWeaveResultsOverride = null)
    {
        this.addLineProbesOverride = addLineProbesOverride;
        this.removeLineProbeOverride = removeLineProbeOverride;
        this.getWeaveResultsOverride = getWeaveResultsOverride;
        this.resolveType = typeResolver ?? ReflectionResolveType;
        this.pdbReader = pdbReader ?? new PdbReader();
    }

    /// <summary>
    /// Releases the PDB reader's open file handles.
    /// </summary>
    // The reader caches an AssemblyDebugInfo per resolved assembly, each holding an open FileStream over the
    // assembly's PE image plus one over its sidecar .pdb. Nothing released them: Cleanup only nulled this
    // field, so every Initialize cycle leaked a fresh set for the process lifetime. On Windows those handles
    // also block in-place replacement of the customer's DLLs during a rolling deploy.
    public void Dispose() => this.pdbReader.Dispose();

    /// <summary>
    /// Resolves and applies a line probe for the given configuration, one probe per requested local.
    /// </summary>
    /// <param name="config">A line-level configuration (<c>LineNumber &gt; 0</c>).</param>
    /// <param name="probeId">Opaque id to bake into the injected IL for the FIRST probe.</param>
    /// <param name="allocateProbeId">
    /// Supplies additional ids when more than one local is captured. Called once per extra local; may be
    /// null, in which case only the first local is captured (the historical single-local behavior).
    /// </param>
    /// <param name="registerBeforeApply">
    /// Invoked once per resolved probe IMMEDIATELY BEFORE the native apply, so a hit arriving the instant the
    /// ReJIT completes already resolves. Callers that route hits by probeId must supply this rather than
    /// registering from the returned resolution, which is too late. May be null for callers that do not
    /// dispatch hits (tests).
    /// </param>
    /// <returns>
    /// The outcome. On success, <see cref="LineProbeResolution.Locations"/> holds one entry per applied
    /// probe, each paired with its id.
    /// </returns>
    // MULTI-LOCAL IS N PROBES AT ONE OFFSET, not one probe carrying N values.
    //
    // The alternative — an `object[]` built with newarr/stelem and a `CaptureLocals(int, object[])`
    // callback — needs new native emission, allocates an array on every hit, and would have to be
    // mutation-proven from scratch. N probes reuse emission that is already proven end to end, and the
    // native side already supports it: requests are deduped by (offset, probeId), so distinct ids at the
    // SAME offset are accepted, and every emit is InsertBefore(targetInstr), so N sequences chain in order
    // ahead of the original instruction. Verified in line_probe.cpp (AddLineProbeRequest dedup) and
    // il_rewriter.cpp (m_pOffsetToInstr maps to instruction POINTERS, which insertion does not invalidate).
    //
    // Cost: one extra `call` per local on the hot line. Benefit: no new IL shape, no per-hit allocation
    // beyond the boxes already required, and each local keeps its own type/slot — which is what makes
    // mixed-type capture (string + DateTime + int at one line) work without an object[] of boxed values.
    public LineProbeResolution ApplyLineProbe(
        InstrumentationConfiguration config,
        int probeId,
        Func<int>? allocateProbeId = null,
        Action<LineProbeProbeLocation>? registerBeforeApply = null)
    {
        if (config == null || !config.IsLineLevel)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable, "configuration is not line-level");
        }

        var type = this.resolveType(config);
        if (type == null)
        {
            // Not loaded YET — the caller must retry on a later poll and must not report an ERROR.
            return LineProbeResolution.Fail(LineProbeResolutionStatus.TypeNotLoaded);
        }

        // Every requested local, not just [0] (lifts D7). An empty/absent list is a bare line probe: it
        // records that the line was REACHED and captures nothing, which is still a useful probe.
        var requested = config.Capture?.CaptureLocals ?? Array.Empty<string>();
        var localNames = requested.Length == 0
            ? new string?[] { null }
            : requested.Where(n => !string.IsNullOrEmpty(n)).Cast<string?>().ToArray();

        if (localNames.Length == 0)
        {
            // The list existed but held only null/empty entries — treat as a bare probe rather than
            // resolving nothing and reporting a confusing failure.
            localNames = new string?[] { null };
        }

        // Cap the number of locals per line. Each one is an extra `call` inlined into the customer's hot
        // path, so an unbounded list is a self-inflicted performance problem on a line that might run
        // millions of times. Extra names are dropped rather than refused: capturing the first N is more
        // useful to an operator than refusing the whole probe.
        if (localNames.Length > MaxLocalsPerLine)
        {
            localNames = localNames.Take(MaxLocalsPerLine).ToArray();
        }

        var definitions = new List<NativeLineProbeDefinition>(localNames.Length);
        var applied = new List<LineProbeProbeLocation>(localNames.Length);
        LineProbeResolution? firstFailure = null;

        // Names that did not resolve, so a PARTIAL success can say WHICH ones were dropped. The first
        // failure alone is not enough: it carries a cause but not the operator's spelling, and with
        // several names dropped only the first would ever be mentioned.
        List<string>? unresolvedLocals = null;
        var nextId = probeId;

        foreach (var localName in localNames)
        {
            var resolution = this.pdbReader.Resolve(type, config.MethodName, config.LineNumber, localName);
            if (!resolution.IsResolved)
            {
                // PARTIAL SUCCESS IS THE RIGHT BEHAVIOR HERE. One out-of-scope name among several must not
                // discard the locals that DID resolve — the operator gets what is capturable plus an error
                // naming what was not. The first failure is remembered so a run where NOTHING resolves
                // still reports a real cause instead of a generic one.
                firstFailure ??= resolution;
                if (localName != null)
                {
                    (unresolvedLocals ??= new List<string>()).Add(localName);
                }

                continue;
            }

            var location = resolution.Location!;

            // A captured variable reaches the native side one of exactly two ways: a local SLOT (`ldloc`) or,
            // for an async/iterator method, a hoisted state-machine FIELD (`ldarg.0; ldfld`).
            var isHoisted = location.HoistedFieldToken != 0;
            var capturesLocal = isHoisted || location.LocalSlot >= 0;

            // A local was requested but neither a slot nor a field resolved: skip it rather than emit a probe
            // that reads slot -1. PdbReader already refuses out-of-scope locals, so this guards a future
            // resolution path returning Resolved with nothing to read.
            if (localName != null && !capturesLocal)
            {
                var detail =
                    $"local '{localName}' resolved to neither a slot nor a hoisted field at IL offset " +
                    $"{location.IlOffset}";
                firstFailure ??= LineProbeResolution.Fail(LineProbeResolutionStatus.LocalOutOfScope, detail);
                (unresolvedLocals ??= new List<string>()).Add(localName);
                continue;
            }

            // ASYNC RIDES `Legacy` + A NON-ZERO HOISTED TOKEN, NOT ITS OWN MODE. The native side derives the
            // async path from `hoisted_field_token != nil` alone (line_probe.cpp), and its branch order puts
            // isLocalCapture FIRST — so sending LocalCapture together with a token would emit `ldloc` against
            // a slot that does not hold the variable. Legacy + token is the combination that selects the
            // `ldarg.0; ldfld` emission.
            var mode = !isHoisted && location.LocalSlot >= 0
                ? LineProbeEmissionMode.LocalCapture
                : LineProbeEmissionMode.Legacy;

            // The callback name MUST match the arity the native side derives, which is
            // `needsTwoArgCallback = isAsyncHoistedCapture || isBoxGate || isLocalCapture` — so a hoisted
            // capture needs the TWO-arg callback even though its mode is Legacy. Keyed off capturesLocal
            // rather than off `mode` for exactly that reason. A name/arity mismatch does not fail cleanly:
            // DefineMemberRef succeeds against a signature no managed method has, and the call then binds to
            // nothing at runtime.
            var callbackMethod = capturesLocal ? CaptureMethod : ProbeMethod;

            // localTypeName/localIsValueType are what lift the int-only limit: the native side boxes a
            // value-type local against ITS OWN type token, and emits NO box for a reference type (boxing an
            // object reference is invalid IL). A null type name means System.Int32, preserving the original
            // behavior for the pre-existing spike harnesses.
            definitions.Add(new NativeLineProbeDefinition(
                targetAssembly: location.AssemblyName,
                targetType: location.TypeName,
                targetMethod: location.MethodName,
                targetSignatureTypes: BuildSignatureTypes(location.ParameterCount),
                ilOffset: location.IlOffset,
                probeId: nextId,
                callbackAssembly: CallbackAssemblyFullName,
                callbackType: CallbackType,
                callbackMethod: callbackMethod,
                emissionMode: mode,
                boxValue: location.LocalSlot >= 0 ? location.LocalSlot : 0,
                gateMethod: null,
                hoistedFieldToken: location.HoistedFieldToken,
                localTypeName: location.LocalTypeName,
                localIsValueType: location.LocalIsValueType));

            applied.Add(new LineProbeProbeLocation(nextId, location));

            // Allocate the NEXT id only when another local still needs one. Without an allocator we stop
            // after the first, which is exactly the previous single-local behavior.
            if (applied.Count < localNames.Length)
            {
                if (allocateProbeId == null)
                {
                    break;
                }

                nextId = allocateProbeId();
            }
        }

        if (definitions.Count == 0)
        {
            // Nothing resolved. Report the first real cause rather than a synthesized one.
            return firstFailure ?? LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"no requested local resolved at line {config.LineNumber}");
        }

        // REGISTER EVERY PROBE BEFORE THE P/INVOKE. AddLineProbes triggers a ReJIT, after which the injected
        // callback can fire on a customer thread immediately — potentially before this method returns. A hit
        // that arrives with its probeId unregistered resolves to nothing and is silently dropped, so the
        // registration cannot wait until the caller inspects the returned resolution.
        //
        // This is why the ids and locations are collected during resolution above and handed over here rather
        // than being read off the result: at this point nothing is woven yet, so no callback can exist, and
        // every id a hit could carry is already resolvable.
        //
        // Registering an id whose apply then FAILS is the strictly safer error of the two: nothing weaves, so
        // the entry is simply unreachable, and the caller drops it on the failure path.
        if (registerBeforeApply != null)
        {
            foreach (var appliedProbe in applied)
            {
                registerBeforeApply(appliedProbe);
            }
        }

        // ONE P/Invoke for all N probes (closes the N1 batching gap). The native side receives them as a
        // single definitions batch and performs ONE Import/Export per method — "N edits, 1 ReJIT" — rather
        // than N separate rewrites of the same body.
        var array = definitions.ToArray();
        try
        {
            // The definitions id must be UNIQUE per apply: the native side dedups by it
            // (cor_profiler.cpp "Id already processed"), so reusing a bare LocationHash would make a
            // re-add after a removal a silent no-op. Including the probeId keeps re-adds effective.
            var definitionsId = $"{config.LocationHash}:{probeId}";

            if (this.addLineProbesOverride != null)
            {
                this.addLineProbesOverride(definitionsId, array, array.Length);
            }
            else
            {
                NativeMethods.AddLineProbes(definitionsId, array, array.Length);
            }

            // Carries EVERY applied probe so the caller can register each id against its own local. The
            // single Location stays populated (the first probe) so existing single-local callers and tests
            // read unchanged.
            var success = LineProbeResolution.Success(applied[0].Location, applied);

            // PARTIAL SUCCESS MUST NOT LOOK LIKE A CLEAN ONE. Previously `firstFailure` was discarded here, so
            // a misspelled name among several was dropped with no signal anywhere — indistinguishable from a
            // probe that captured everything asked of it, which is precisely the case an operator needs told.
            //
            // Carried as Detail rather than as a failure Status ON PURPOSE. The config IS live: probes are
            // woven and capturing. Returning a failure would make the manager skip MarkApplied, and
            // StatusReporter suppresses READY for anything it has reported an error against — so the operator
            // would be told nothing was instrumented while snapshots were arriving. Detail keeps both facts
            // true at once.
            return unresolvedLocals == null
                ? success
                : success with
                {
                    Detail =
                        $"captured {applied.Count} of {localNames.Length} requested locals at line " +
                        $"{config.LineNumber}; not in scope: {string.Join(", ", unresolvedLocals)}",
                };
        }
        catch (EntryPointNotFoundException)
        {
            // Running on the STOCK profiler: the fork's exports are absent. A deployment condition, not
            // a config error — distinct status so the operator is told the right thing.
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.ProfilerMissingLineProbeSupport,
                "the loaded native profiler does not export AddLineProbes (stock upstream binary)");
        }
        catch (DllNotFoundException)
        {
            // No profiler at all (e.g. unit tests, or the app launched without the profiler env vars).
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.ProfilerMissingLineProbeSupport,
                "the native profiler library could not be loaded");
        }
        finally
        {
            // INDEXED, NOT foreach. `foreach (var def in array)` binds `def` as a COPY of the struct, so
            // Dispose frees the unmanaged block but nulls TargetSignatureTypes only on the copy — the array
            // element keeps a dangling pointer, and a second disposal of the same element double-frees it
            // (heap corruption, not an exception). Indexing mutates the element in place, which is what
            // makes NativeLineProbeDefinition.Dispose's idempotence guard actually hold.
            //
            // ProfilerTranslator has the same foreach shape; it is safe there only because nothing disposes
            // its array twice. Not relied on here.
            for (int i = 0; i < array.Length; i++)
            {
                array[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Removes a previously applied line probe, re-weaving the method without it.
    /// </summary>
    /// <param name="probeId">The id used when the probe was applied.</param>
    /// <returns>True when the removal call reached the profiler.</returns>
    public bool RemoveLineProbe(int probeId)
    {
        try
        {
            if (this.removeLineProbeOverride != null)
            {
                this.removeLineProbeOverride(probeId);
            }
            else
            {
                NativeMethods.RemoveLineProbe(probeId);
            }

            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Nothing was ever woven if the export is missing, so failing to remove is consistent.
            return false;
        }
    }

    /// <summary>
    /// Builds the signature array the native side matches on by LENGTH (parameter count + 1).
    /// </summary>
    /// <param name="parameterCount">The target method's parameter count.</param>
    /// <returns>An array of <c>"_"</c> wildcards of length <paramref name="parameterCount"/> + 1.</returns>
    // Identical convention to ProfilerTranslator.BuildSignatureTypes: individual entries are never
    // resolved, so the wildcard avoids having to render parameter type names the native side ignores.
    internal static string[] BuildSignatureTypes(int parameterCount)
    {
        var types = new string[parameterCount + 1];
        for (int i = 0; i <= parameterCount; i++)
        {
            types[i] = "_";
        }

        return types;
    }

    /// <summary>
    /// Reads back what the native rewriter actually did with every probe it has a verdict for.
    /// </summary>
    /// <returns>
    /// One entry per probe the profiler holds a verdict for. Empty when the profiler has no verdicts yet, or
    /// when it predates this export.
    /// </returns>
    // WHY THIS IS A POLL AND NOT A CALLBACK. The rewrite happens on a CLR ReJIT thread at an arbitrary moment
    // — the first time the target method runs, which may be hours after the apply. Calling managed code from
    // there would mean a native->managed transition inside the rewriter, on a thread the CLR owns mid-JIT.
    // Polling moves all of that onto a thread we already own, at a cadence we choose, at the cost of latency
    // that does not matter for a status report.
    //
    // NOT THREAD-SAFE: `weaveResultBuffer` is reused, so two concurrent callers would write the same array.
    // The single caller is the status-reporting timer. Documented rather than locked, because adding a lock
    // would imply concurrent use is supported when the reuse of the buffer is the point.
    internal IReadOnlyList<(int ProbeId, LineProbeWeaveOutcome Outcome)> GetWeaveResults()
    {
        int total;
        try
        {
            total = this.QueryWeaveResults();

            // GROW AND RETRY ONCE. The native side returns the TOTAL it holds, not the number it wrote, so a
            // total above capacity means the view was truncated. Truncation is not benign here: the entries
            // are ordered by probe id, so a short buffer would permanently hide the failures of the
            // most-recently-applied probes — precisely the ones an operator just created and is watching.
            if (total > this.weaveResultBuffer.Length)
            {
                this.weaveResultBuffer = new NativeLineProbeWeaveResult[total];
                total = this.QueryWeaveResults();
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Stock upstream profiler, or no profiler at all. Nothing was woven either, so there is no verdict
            // to miss. Deliberately silent: LineProbeTranslator.ApplyLineProbe already reports
            // ProfilerMissingLineProbeSupport once per config, and this runs on a timer.
            return Array.Empty<(int, LineProbeWeaveOutcome)>();
        }

        // Still short after the retry means the profiler recorded more verdicts between the two calls (a
        // method got ReJIT-ed in between). Take what fits; the rest arrive on the next poll.
        var count = Math.Min(total, this.weaveResultBuffer.Length);
        if (count <= 0)
        {
            return Array.Empty<(int, LineProbeWeaveOutcome)>();
        }

        var results = new List<(int ProbeId, LineProbeWeaveOutcome Outcome)>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = this.weaveResultBuffer[i];

            // A value this assembly does not know maps to ExportFailed rather than being dropped or thrown on:
            // a newer profiler could add a reason code, and "some failure we cannot name" is far closer to the
            // truth than silently treating it as woven. Pending/Woven are inside the range, so a genuinely
            // successful probe can never land here.
            var outcome = Enum.IsDefined(typeof(LineProbeWeaveOutcome), entry.Outcome)
                ? (LineProbeWeaveOutcome)entry.Outcome
                : LineProbeWeaveOutcome.ExportFailed;

            results.Add((entry.ProbeId, outcome));
        }

        return results;
    }

    private static Type? ReflectionResolveType(InstrumentationConfiguration config)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(config.TypeName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // Reflection can throw on dynamic or partially-loaded assemblies; skip and keep looking.
            }
        }

        return null;
    }

    private int QueryWeaveResults() =>
        this.getWeaveResultsOverride != null
            ? this.getWeaveResultsOverride(this.weaveResultBuffer, this.weaveResultBuffer.Length)
            : NativeMethods.GetLineProbeWeaveResults(this.weaveResultBuffer, this.weaveResultBuffer.Length);
}
