// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// Phase-2 PoC harness. Clones poc/ProfilerE2E: exit code == number of FAILED assertions.
// Registers ONE hardcoded line probe via the forked AddLineProbes export, runs SpikeTarget.Compute,
// and asserts (1) it survives, (2) the plain-static callback fired exactly once per call, (3) the
// result is unchanged.

using System.Runtime.InteropServices;
using LineProbeSinkNs;

// Touch the sink assembly so it is loaded into the AppDomain before ReJIT, and so the linker
// cannot trim LineProbeSink.Probe (the native rewrite resolves it by name at ReJIT time).
_ = LineProbeSink.FireCount;
GC.KeepAlive(typeof(LineProbeSink));

Console.WriteLine("=== DI Line-Probe E2E (Phase 2 PoC — interior insertion + runtime fire) ===\n");

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] Native profiler not loaded. Run via run.sh.");
    return 99;
}

// ---- The one hardcoded offset (see run.sh / RESULTS doc for how it was obtained by IL inspection).
// Statement boundary B in SpikeTarget.Compute, i.e. just before the `ldloc.0; ret`.
uint ilOffset = uint.Parse(Environment.GetEnvironmentVariable("LINEPROBE_IL_OFFSET") ?? "0");
int probeId = 4242;

// ---- Register the line probe with the forked native profiler (names-based ABI, Q3). ----
var def = new NativeLineProbeDefinition(
    targetAssembly: "LineProbeE2E",
    targetType: "SpikeTarget",
    targetMethod: "Compute",
    targetSignatureTypes: new[] { "System.Int32", "System.Int32" }, // ret, arg0 — count disambiguates
    ilOffset: ilOffset,
    probeId: probeId,
    callbackAssembly: "LineProbeSink",
    callbackType: "LineProbeSinkNs.LineProbeSink",
    callbackMethod: "Probe");

try
{
    var arr = new[] { def };
    NativeMethods.AddLineProbes("lineprobe-poc", arr, arr.Length);
    Console.WriteLine($"[reg] AddLineProbes submitted: SpikeTarget.Compute @ IL offset {ilOffset} -> LineProbeSink.Probe(probeId={probeId})");
}
finally
{
    def.Dispose();
}

Console.WriteLine("[rejit] Waiting 700ms for ReJIT of SpikeTarget.Compute...\n");
Thread.Sleep(700);

int failures = 0;
void Assert(int num, string name, Func<bool> check, string detail)
{
    bool ok;
    try { ok = check(); }
    catch (Exception ex) { ok = false; detail = ex.GetType().Name + ": " + ex.Message; }
    if (ok) { Console.WriteLine($"[PASS] Assertion {num}: {name}"); }
    else { failures++; Console.WriteLine($"[FAIL] Assertion {num}: {name} -- {detail}"); }
}

// ---- Assertion 1: process runs Compute to completion (no CLR crash / InvalidProgramException). ----
// If the ReJIT'd body were invalid, the JIT throws InvalidProgramException at first call here.
int result = 0;
Exception? computeEx = null;
LineProbeSink.FireCount = 0;
try
{
    result = SpikeTarget.Compute(41);
}
catch (Exception ex)
{
    computeEx = ex;
}
Assert(1, "Compute runs to completion (no InvalidProgramException / CLR crash)",
    () => computeEx == null,
    computeEx == null ? "" : computeEx.GetType().FullName + ": " + computeEx.Message);

// ---- Assertion 3: original result unchanged (injected sequence is net-zero on the stack). ----
Assert(3, "Compute(41) == 42 (result unchanged; injected seq is stack-neutral)",
    () => result == 42,
    $"got {result}");

// ---- Assertion 2: callback fired exactly once per call. Run N>1 so a one-shot fluke can't pass. ----
LineProbeSink.FireCount = 0;
LineProbeSink.LastProbeId = -1;
const int N = 5;
for (int i = 0; i < N; i++)
{
    _ = SpikeTarget.Compute(i);
}
Assert(2, $"LineProbeSink.FireCount == {N} after {N} calls (fires exactly once per call)",
    () => LineProbeSink.FireCount == N,
    $"FireCount={LineProbeSink.FireCount}, LastProbeId={LineProbeSink.LastProbeId}");

// Extra evidence (not a gate): the probeId arg actually arrived.
Console.WriteLine($"[info] LastProbeId observed by callback = {LineProbeSink.LastProbeId} (expected {probeId})");

Console.WriteLine($"\n=== Result: {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
return failures;

// ------------------------------------------------------------------
// P/Invoke surface for the forked native export (mirrors NativeMethods.cs discipline).
// ------------------------------------------------------------------
internal static class NativeMethods
{
    private const string NativeLib = "OpenTelemetry.AutoInstrumentation.Native";

    [DllImport(NativeLib, EntryPoint = "AddLineProbes")]
    public static extern void AddLineProbes(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] NativeLineProbeDefinition[] items,
        int size);
}

// Mirror of NativeCallTargetDefinition's AllocHGlobal/Dispose discipline. Names-based (Q3):
// target located by assembly/type/method(+signature count); callback built as a cross-assembly
// MemberRef from callbackAssembly/type/method on the native side. Plus ilOffset + probeId.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeLineProbeDefinition : IDisposable
{
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetType;
    [MarshalAs(UnmanagedType.LPWStr)] public string TargetMethod;
    public IntPtr TargetSignatureTypes;
    public ushort TargetSignatureTypesLength;
    public uint IlOffset;
    public int ProbeId;
    // ASYNC SPIKE: 0 => sync single-call path; nonzero mdFieldDef => read that hoisted local off the
    // async state machine (ldarg.0; ldfld <token>; box; call CaptureLocal(int32, object)).
    public uint HoistedFieldToken;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackType;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackMethod;
    // BOX-GATE SPIKE (DECISION A) trailing fields; 0/null => legacy behavior. Must match the native
    // struct stride (see line_probe.h) — this harness only ever uses legacy (mode 0).
    public int EmissionMode;
    public int BoxValue;
    [MarshalAs(UnmanagedType.LPWStr)] public string GateMethod;

    // ADDED 2026-08-20: the product struct grew these two trailing fields for non-int local capture
    // (88 -> 104 bytes, 16 fields). Without them the native side reads localTypeName as a pointer past the
    // end of this struct and the process dies with SIGBUS inside AddLineProbes. LocalIsValueType must be 1
    // for an int local or the `box` is suppressed and the woven IL is invalid (InvalidProgramException),
    // because the callback is `void CaptureLocal(int, object)`.
    [MarshalAs(UnmanagedType.LPWStr)] public string? LocalTypeName;
    public int LocalIsValueType;

    public NativeLineProbeDefinition(
        string targetAssembly,
        string targetType,
        string targetMethod,
        string[] targetSignatureTypes,
        uint ilOffset,
        int probeId,
        string callbackAssembly,
        string callbackType,
        string callbackMethod,
        uint hoistedFieldToken = 0,
        int emissionMode = 0,
        int boxValue = 0,
        string gateMethod = null)
    {
        this.TargetAssembly = targetAssembly;
        this.TargetType = targetType;
        this.TargetMethod = targetMethod;
        this.IlOffset = ilOffset;
        this.ProbeId = probeId;
        this.HoistedFieldToken = hoistedFieldToken;
        this.CallbackAssembly = callbackAssembly;
        this.CallbackType = callbackType;
        this.CallbackMethod = callbackMethod;
        this.EmissionMode = emissionMode;
        this.BoxValue = boxValue;
        this.GateMethod = gateMethod;
        this.LocalTypeName = null;
        this.LocalIsValueType = 1;

        this.TargetSignatureTypesLength = (ushort)targetSignatureTypes.Length;
        this.TargetSignatureTypes = Marshal.AllocHGlobal(IntPtr.Size * targetSignatureTypes.Length);
        for (int i = 0; i < targetSignatureTypes.Length; i++)
        {
            Marshal.WriteIntPtr(
                this.TargetSignatureTypes,
                i * IntPtr.Size,
                Marshal.StringToHGlobalUni(targetSignatureTypes[i]));
        }
    }

    public void Dispose()
    {
        if (this.TargetSignatureTypes == IntPtr.Zero)
        {
            return;
        }

        for (int i = 0; i < this.TargetSignatureTypesLength; i++)
        {
            var ptr = Marshal.ReadIntPtr(this.TargetSignatureTypes, i * IntPtr.Size);
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        Marshal.FreeHGlobal(this.TargetSignatureTypes);
        this.TargetSignatureTypes = IntPtr.Zero;
    }
}
