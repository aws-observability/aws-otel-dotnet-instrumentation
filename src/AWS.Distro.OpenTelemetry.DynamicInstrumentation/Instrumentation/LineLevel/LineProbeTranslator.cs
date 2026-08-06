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
internal sealed class LineProbeTranslator
{
    /// <summary>Assembly hosting the managed line-probe callback.</summary>
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

    private readonly Action<string, NativeLineProbeDefinition[], int>? addLineProbesOverride;
    private readonly Action<int>? removeLineProbeOverride;
    private readonly Func<InstrumentationConfiguration, Type?> resolveType;
    private readonly PdbReader pdbReader;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineProbeTranslator"/> class.
    /// </summary>
    /// <param name="addLineProbesOverride">Test seam replacing the <c>AddLineProbes</c> P/Invoke.</param>
    /// <param name="removeLineProbeOverride">Test seam replacing the <c>RemoveLineProbe</c> P/Invoke.</param>
    /// <param name="typeResolver">Test seam replacing loaded-assembly type resolution.</param>
    /// <param name="pdbReader">Reader used to resolve line→offset; a fresh one is created if omitted.</param>
    public LineProbeTranslator(
        Action<string, NativeLineProbeDefinition[], int>? addLineProbesOverride = null,
        Action<int>? removeLineProbeOverride = null,
        Func<InstrumentationConfiguration, Type?>? typeResolver = null,
        PdbReader? pdbReader = null)
    {
        this.addLineProbesOverride = addLineProbesOverride;
        this.removeLineProbeOverride = removeLineProbeOverride;
        this.resolveType = typeResolver ?? ReflectionResolveType;
        this.pdbReader = pdbReader ?? new PdbReader();
    }

    /// <summary>
    /// Resolves and applies a line probe for the given configuration.
    /// </summary>
    /// <param name="config">A line-level configuration (<c>LineNumber &gt; 0</c>).</param>
    /// <param name="probeId">Opaque id to bake into the injected IL.</param>
    /// <returns>The outcome; <see cref="LineProbeResolutionStatus.Resolved"/> on success.</returns>
    public LineProbeResolution ApplyLineProbe(InstrumentationConfiguration config, int probeId)
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

        // v1 captures ONE local (D7). CaptureLocals is a list on the wire, but the proven native
        // emission boxes a single System.Int32, so honoring the whole list would need fork work that is
        // not in scope. Taking [0] is the documented v1 behavior, not an oversight.
        var localName = config.Capture?.CaptureLocals is { Length: > 0 } locals ? locals[0] : null;

        var resolution = this.pdbReader.Resolve(type, config.MethodName, config.LineNumber, localName);
        if (!resolution.IsResolved)
        {
            return resolution;
        }

        var location = resolution.Location!;

        // A local was requested but no slot resolved: capture nothing rather than emit a probe that
        // reads slot -1. PdbReader already refuses out-of-scope locals, so this is a belt-and-braces
        // guard against a future resolution path returning Resolved with no slot.
        if (localName != null && location.LocalSlot < 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LocalOutOfScope,
                $"local '{localName}' resolved to no slot at IL offset {location.IlOffset}");
        }

        var mode = location.LocalSlot >= 0
            ? LineProbeEmissionMode.LocalCapture
            : LineProbeEmissionMode.Legacy;

        // The callback name MUST match the arity the native side derives from emissionMode: LocalCapture
        // builds a two-arg `(int32, object)` MemberRef, Legacy a one-arg `(int32)` one (line_probe.cpp:
        // `needsTwoArgCallback = isAsyncHoistedCapture || isBoxGate || isLocalCapture`). A name/arity
        // mismatch does not fail cleanly — DefineMemberRef succeeds against a signature no managed method
        // has, and the call then binds to nothing at runtime. So the two modes get two distinct callbacks,
        // not one shared name.
        var callbackMethod = mode == LineProbeEmissionMode.LocalCapture ? CaptureMethod : ProbeMethod;

        var definition = new NativeLineProbeDefinition(
            targetAssembly: location.AssemblyName,
            targetType: location.TypeName,
            targetMethod: location.MethodName,
            targetSignatureTypes: BuildSignatureTypes(location.ParameterCount),
            ilOffset: location.IlOffset,
            probeId: probeId,
            callbackAssembly: CallbackAssembly,
            callbackType: CallbackType,
            callbackMethod: callbackMethod,
            emissionMode: mode,
            boxValue: location.LocalSlot >= 0 ? location.LocalSlot : 0,
            gateMethod: null);

        var array = new[] { definition };
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

            return resolution;
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
}
