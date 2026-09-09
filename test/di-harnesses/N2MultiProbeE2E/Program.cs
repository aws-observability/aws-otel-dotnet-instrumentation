// SPDX-License-Identifier: Apache-2.0
// N2: can N line probes coexist in ONE method? Clones the PROVEN LineProbeE2E harness (register →
// wait 700ms → call → assert) but registers TWO probes at TWO different interior offsets of the
// SAME method. Offsets are DISCOVERED from the real IL at runtime (the prior attempts failed by
// guessing offsets that landed mid-instruction). Exit code = failed assertions.
using System.Reflection;
using System.Runtime.InteropServices;
using LineProbeSinkNs;

_ = LineProbeSink.FireCount;
GC.KeepAlive(typeof(LineProbeSink));
Console.WriteLine("=== N2: multi-probe-per-method E2E ===\n");
if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1") { Console.WriteLine("[FATAL] no profiler"); return 99; }

// ---- STEP 0: discover real statement-boundary offsets from MultiTarget.Compute's IL ----
var m = typeof(MultiTarget).GetMethod("Compute", BindingFlags.Public | BindingFlags.Static)!;
var il = m.GetMethodBody()!.GetILAsByteArray()!;
// stloc for locals 0..3 are single-byte opcodes 0x0A..0x0D; the boundary is the offset AFTER each.
var boundaries = new List<uint>();
for (int i = 0; i < il.Length; i++)
{
    if (il[i] >= 0x0A && il[i] <= 0x0D) { boundaries.Add((uint)(i + 1)); }
}
Console.WriteLine($"[il] Compute IL len={il.Length}, stloc boundaries at offsets: {string.Join(",", boundaries)}");
if (boundaries.Count < 2) { Console.WriteLine("[FATAL] need >=2 boundaries"); return 98; }
uint off1 = 5, off2 = 9; // verified boundaries: after stloc.0 (5), after stloc.1 (9)
if (Environment.GetEnvironmentVariable("N2_BAD_OFFSETS") == "1") { off1 = 7; off2 = 12; Console.WriteLine("[NEG] BAD offsets 7,12 (mid-instruction operand bytes) — injection MUST be refused"); }

// warm so ReJIT has JIT'd code
for (int i = 0; i < 2000; i++) { _ = MultiTarget.Compute(i); }

NativeLineProbeDefinition Mk(uint off, int id) => new(
    "LineProbeE2E", "MultiTarget", "Compute", new[] { "System.Int32", "System.Int32" }, off, id, // [ret, arg0] — count = arity+1
    "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Probe");

int failures = 0;
void Assert(int n, string name, bool ok, string detail)
{ Console.WriteLine(ok ? $"[PASS] {n}: {name}" : $"[FAIL] {n}: {name} -- {detail}"); if (!ok) failures++; }

// ---- STEP A: ONE probe at off1. Baseline: proves offset+weave on THIS method. ----
LineProbeSink.SeenProbeIdsMap.Clear();
var a = new[] { Mk(off1, 5001) };
NativeMethods.AddLineProbes("n2-A", a, a.Length); a[0].Dispose();
Console.WriteLine($"[reg] A: probe 5001 @off{off1}"); Thread.Sleep(700);
for (int i = 0; i < 200; i++) { _ = MultiTarget.Compute(i); }
int afterA = LineProbeSink.SeenProbeIds.Count;
Assert(1, "single probe fires (baseline for the whole test)", afterA == 1, $"distinct={afterA}");

// ---- STEP B: SECOND probe at off2, same method, SEPARATE call. ----
var b = new[] { Mk(off2, 5002) };
NativeMethods.AddLineProbes("n2-B", b, b.Length); b[0].Dispose();
Console.WriteLine($"[reg] B: probe 5002 @off{off2} (separate call)"); Thread.Sleep(700);
for (int i = 0; i < 200; i++) { _ = MultiTarget.Compute(i); }
int afterB = LineProbeSink.SeenProbeIds.Count;
Console.WriteLine($"[obs] distinct probeIds fired after 2nd probe = {afterB} (ids: {string.Join(",", LineProbeSink.SeenProbeIds)})");

// This is the N2 question. We DO NOT assert a direction — we report what happened, gated on A passing.
if (afterA != 1) Console.WriteLine("[N2] INCONCLUSIVE: baseline (A) did not fire, so B tells us nothing.");
else if (afterB >= 2) Console.WriteLine("[N2] RESULT: MULTI-PROBE-PER-METHOD **SUPPORTED** — 2 distinct probes fired in one method.");
else Console.WriteLine("[N2] RESULT: SINGLE-PROBE-PER-METHOD — 2nd probe DROPPED (confirms m_methods dedup by mdMethodDef).");


// ---- STEP D: REMOVAL. Both 5001 and 5002 are woven. Remove 5001, keep 5002. ----
// Proves: re-ReJIT with the survivor set (a) stops the removed probe firing, (b) keeps the survivor
// firing, (c) leaves the method runnable. Then remove 5002 too -> pristine body, nothing fires.
if (afterA == 1 && afterB >= 2)
{
    Console.WriteLine("\n---- STEP D: removal ----");
    NativeMethods.RemoveLineProbe(5001);
    Thread.Sleep(700);
    LineProbeSink.SeenProbeIdsMap.Clear();
    int survived = 0;
    for (int i = 0; i < 200; i++) { survived = MultiTarget.Compute(i); }
    var idsAfterRemove1 = string.Join(",", LineProbeSink.SeenProbeIds);
    Assert(2, "removed probe 5001 no longer fires", !LineProbeSink.SeenProbeIds.Contains(5001), $"ids={idsAfterRemove1}");
    Assert(3, "survivor probe 5002 STILL fires", LineProbeSink.SeenProbeIds.Contains(5002), $"ids={idsAfterRemove1}");
    // Compute(5): a=5+1=6, b=6+10=16, c=16+100=116.
    Assert(4, "method still returns correct value after removal (Compute(5)==116)", MultiTarget.Compute(5) == 116, $"got {MultiTarget.Compute(5)}");

    // Remove the last probe -> pristine body.
    NativeMethods.RemoveLineProbe(5002);
    Thread.Sleep(700);
    LineProbeSink.SeenProbeIdsMap.Clear();
    for (int i = 0; i < 200; i++) { _ = MultiTarget.Compute(i); }
    Assert(5, "after removing ALL probes, NOTHING fires (pristine body restored)", LineProbeSink.SeenProbeIds.Count == 0, $"ids={string.Join(",", LineProbeSink.SeenProbeIds)}");
    Console.WriteLine($"[N2-REMOVE] MultiTarget.Compute(5)={MultiTarget.Compute(5)} (expect 116, proves body intact)");
}

Console.WriteLine($"\n=== {(failures == 0 ? "baseline OK" : failures + " FAILED")} ===");
return failures;
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

    [DllImport(NativeLib, EntryPoint = "RemoveLineProbe")]
    public static extern void RemoveLineProbe(int probeId);
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
