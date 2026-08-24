// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

/// <summary>
/// Translates DI configurations into NativeCallTargetDefinitions and registers them with the native profiler via AddInstrumentations P/Invoke.
/// </summary>
// The native profiler binds by matching the signature-array length (arity + 1) against the
// method's parameter count; individual type entries may be the "_" wildcard. So we resolve
// arity via reflection and register one definition per distinct overload arity.
internal sealed class ProfilerTranslator
{
    private const int MaxSupportedParams = 9;

    /// <summary>Assembly and type-name stem the native profiler is told to weave calls to.</summary>
    // DERIVED FROM THE REAL SYMBOLS, not typed out, because these two strings cross into the native
    // profiler and nothing on either side checks them. Renaming the assembly, moving the namespace, or
    // renaming DiIntegrationN would leave a hardcoded literal COMPILING happily and then binding to
    // nothing at runtime: the profiler weaves a call to a type that does not exist, and the failure is a
    // MethodAccessException or a silently-never-fires probe rather than a build error. typeof/nameof make
    // the compiler the thing that notices.
    //
    // The trailing arity digit is stripped off nameof(DiIntegration0). There are exactly ten of these types
    // (0..9) because MaxSupportedParams is 9, so the suffix is always ONE character.
    private static readonly string IntegrationAssembly =
        typeof(FunctionLevel.DiIntegration0).Assembly.GetName().Name!;

    private static readonly string IntegrationTypePrefix =
        $"{typeof(FunctionLevel.DiIntegration0).Namespace}.{nameof(FunctionLevel.DiIntegration0)[..^1]}";

    private readonly Action<string, NativeCallTargetDefinition[], int>? addInstrumentationsOverride;
    private readonly Func<InstrumentationConfiguration, MethodResolution?> resolveMethod;

    public ProfilerTranslator(
        Action<string, NativeCallTargetDefinition[], int>? addInstrumentationsOverride = null,
        Func<InstrumentationConfiguration, MethodResolution?>? methodResolver = null)
    {
        this.addInstrumentationsOverride = addInstrumentationsOverride;
        this.resolveMethod = methodResolver ?? ReflectionResolveMethod;
    }

    /// <summary>
    /// Applies instrumentation for the given config, returning a typed outcome so the caller can distinguish a transient failure (target not yet loaded — retry) from a permanent one (method absent, native throw).
    /// </summary>
    /// <param name="config">The configuration to apply.</param>
    /// <returns>The typed apply outcome.</returns>
    public InstrumentationApplyResult ApplyInstrumentation(InstrumentationConfiguration config) =>
        this.ApplyInstrumentation(config, out _);

    /// <summary>
    /// Applies instrumentation and reports the profiler-supported arities that were woven, so the caller
    /// can index them for arity-aware capture resolution (#3). <paramref name="appliedArities"/> is
    /// non-empty only when the result is <see cref="InstrumentationApplyResult.Applied"/>.
    /// </summary>
    /// <param name="config">The configuration to apply.</param>
    /// <param name="appliedArities">The parameter counts actually registered with the profiler.</param>
    /// <returns>The typed apply outcome.</returns>
    public InstrumentationApplyResult ApplyInstrumentation(
        InstrumentationConfiguration config, out IReadOnlyCollection<int> appliedArities)
    {
        appliedArities = Array.Empty<int>();

        // Line-level is a different weave entirely (interior IL offset, not a method boundary) and is owned
        // by LineProbeTranslator. Reaching here with a line-level config means the manager routed it wrong,
        // so Skipped is the right answer rather than an attempt to weave a boundary for it.
        if (!config.IsMethodLevel)
        {
            return InstrumentationApplyResult.Skipped;
        }

        if (IsUnsupportedTarget(config))
        {
            return InstrumentationApplyResult.Skipped;
        }

        var resolution = this.resolveMethod(config);
        if (resolution == null)
        {
            // Type not in any loaded assembly — likely not loaded yet; caller retries and does not report an ERROR.
            return InstrumentationApplyResult.TypeNotLoaded;
        }

        if (resolution.Arities.Count == 0)
        {
            // Type resolved but has no method by that name — a genuine misconfiguration.
            return InstrumentationApplyResult.MethodNotFound;
        }

        var supportedArities = new List<int>();
        var definitions = new List<NativeCallTargetDefinition>();
        foreach (var arity in resolution.Arities)
        {
            if (arity < 0 || arity > MaxSupportedParams)
            {
                continue;
            }

            supportedArities.Add(arity);

            definitions.Add(new NativeCallTargetDefinition(
                targetAssembly: resolution.AssemblyName,
                targetType: config.TypeName,
                targetMethod: config.MethodName,
                targetSignatureTypes: BuildSignatureTypes(arity),
                integrationAssembly: IntegrationAssembly,
                integrationType: $"{IntegrationTypePrefix}{arity}"));
        }

        if (definitions.Count == 0)
        {
            return InstrumentationApplyResult.NoSupportedArity; // All overloads exceeded MaxSupportedParams
        }

        var array = definitions.ToArray();
        try
        {
            if (this.addInstrumentationsOverride != null)
            {
                this.addInstrumentationsOverride(config.LocationHash, array, array.Length);
            }
            else
            {
                NativeMethods.AddInstrumentations(config.LocationHash, array, array.Length);
            }

            // Surface only on success: on a native throw nothing was woven, so nothing should be indexed.
            appliedArities = supportedArities;
            return InstrumentationApplyResult.Applied;
        }
        catch
        {
            return InstrumentationApplyResult.RuntimeError;
        }
        finally
        {
            foreach (var def in array)
            {
                def.Dispose();
            }
        }
    }

    internal static bool IsUnsupportedTarget(InstrumentationConfiguration config)
    {
        var method = config.MethodName;
        return method == ".ctor" || method == ".cctor";
    }

    /// <summary>
    /// Resolves the target type's assembly name and the distinct parameter counts (arities) across all overloads of the named method by scanning loaded assemblies; returns null if the type is not found.
    /// </summary>
    private static MethodResolution? ReflectionResolveMethod(InstrumentationConfiguration config)
    {
        var fullTypeName = config.TypeName;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullTypeName, throwOnError: false);
            }
            catch
            {
                continue; // Reflection can throw on dynamic/unloadable assemblies
            }

            if (type == null)
            {
                continue;
            }

            var arities = new HashSet<int>();
            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                if (method.Name == config.MethodName)
                {
                    arities.Add(method.GetParameters().Length);
                }
            }

            // Return even when arities is empty so the caller can distinguish MethodNotFound from a not-yet-loaded type (null).
            var assemblyName = assembly.GetName().Name ?? config.CodeUnit;
            return new MethodResolution(assemblyName, arities);
        }

        return null; // Type not found in any loaded assembly
    }

    private static string[] BuildSignatureTypes(int paramCount)
    {
        // [returnType, param1Type, ...]: profiler matches on array length (paramCount + 1); "_" is the type wildcard, so we never resolve individual parameter type names.
        var types = new string[paramCount + 1];
        for (int i = 0; i <= paramCount; i++)
        {
            types[i] = "_";
        }

        return types;
    }
}
