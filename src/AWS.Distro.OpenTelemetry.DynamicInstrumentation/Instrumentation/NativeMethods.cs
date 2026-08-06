// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

internal static class NativeMethods
{
    private const string NativeLib = "OpenTelemetry.AutoInstrumentation.Native";

    [DllImport(NativeLib, EntryPoint = "AddInstrumentations")]
    public static extern void AddInstrumentations(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeCallTargetDefinition[] methodArrays,
        int size);

    [DllImport(NativeLib, EntryPoint = "AddDerivedInstrumentations")]
    public static extern void AddDerivedInstrumentations(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeCallTargetDefinition[] methodArrays,
        int size);

    // ---------------------------------------------------------------------------------------------
    // LINE-LEVEL (fork-only). The two exports below exist ONLY in our forked profiler; the stock
    // upstream binary does not define them (verified: `AddLineProbes` symbol count in the shipped
    // OpenTelemetryDistribution profiler is 0).
    //
    // CONSEQUENCE: calling either against a stock profiler throws EntryPointNotFoundException at the
    // first invocation — not at load time, because .NET resolves P/Invoke targets lazily. So merely
    // declaring them here is harmless and cannot affect function-level instrumentation. Callers MUST
    // treat the throw as an expected outcome and map it to a typed failure rather than letting it
    // escape (see LineProbeTranslator, which catches it and returns ProfilerMissingLineProbeSupport).
    // ---------------------------------------------------------------------------------------------
    [DllImport(NativeLib, EntryPoint = "AddLineProbes")]
    public static extern void AddLineProbes(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] LineLevel.NativeLineProbeDefinition[] items,
        int size);

    [DllImport(NativeLib, EntryPoint = "RemoveLineProbe")]
    public static extern void RemoveLineProbe(int probeId);
}
