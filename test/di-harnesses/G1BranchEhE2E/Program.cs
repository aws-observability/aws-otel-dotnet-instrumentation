// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// G1 gate harness (risk R2: branch + EH relocation on interior IL injection). Exit code == number
// of FAILED assertions. ONE probe is registered per process (ReJIT is driven by AddLineProbes), and
// the CASE to run is selected by the G1_CASE env var so run.sh can orchestrate all cases and
// aggregate. Each case injects at a REAL statement boundary (offsets obtained by ilspycmd -il +
// portable-PDB sequence points — see G1-SPIKE-RESULTS.md) and asserts the ReJIT'd body stays
// runtime-valid: branches still land, EH still catches, and unsafe (try-entry) offsets are refused.

using System.Runtime.InteropServices;
using LineProbeSinkNs;

_ = LineProbeSink.FireCount;
GC.KeepAlive(typeof(LineProbeSink));

string @case = Environment.GetEnvironmentVariable("G1_CASE") ?? "sum";
Console.WriteLine($"=== DI G1 Line-Probe Gate (branch + EH relocation) — case '{@case}' ===\n");

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] Native profiler not loaded. Run via run.sh.");
    return 99;
}

// --- Ground-truth expected values (from the source logic). ---
const int SumN = 6;                 // Sum(6) = 0-1+2-3+4-5 = -3
const int SumExpected = -3;
const int GuardedFiveExpected = (100 / 5) + 1;   // try succeeds: r=20, finally +1 => 21
const int GuardedZeroExpected = -1 + 1;          // DivideByZero -> catch r=-1, finally +1 => 0

// LOCAL_CAPTURE emission mode (native line_probe.h LINE_EMIT_LOCAL_CAPTURE).
const int EMIT_LOCAL_CAPTURE = 3;

int probeId = 9100;

// ---- Case table: (targetType, targetMethod, sig, ilOffset, localSlot, emissionMode, expectRefuse) ----
(string type, string method, string[] sig, uint offset, int slot, int emit, bool expectRefuse) cfg = @case switch
{
    // Sum: inject at IL_0013 (L21 `total += i;`, inside the even-branch loop body). Capture loop var
    // `i` = local slot 1. Forces Export() to recompute the two forward branches jumping PAST 0x13
    // (IL_0005->IL_0025, IL_0010->IL_001a) AND the backward loop branch (IL_002b->IL_0007).
    "sum" => ("G1Target", "Sum", new[] { "System.Int32", "System.Int32" }, 0x13u, 1, EMIT_LOCAL_CAPTURE, false),

    // Guarded try: inject at IL_0004 (L37 `r = 100/x;`, INSIDE the try). Capture `r` = slot 0. Grows
    // the try region, so Export() must recompute TryLength and shift the catch/finally offsets and the
    // `leave` targets.
    "guarded_try" => ("G1Target", "Guarded", new[] { "System.Int32", "System.Int32" }, 0x04u, 0, EMIT_LOCAL_CAPTURE, false),

    // Guarded catch: inject at IL_000E (L41 `r = -1;`, INSIDE the catch handler). Capture `r` = slot 0.
    // Grows the catch handler, so Export() must recompute HandlerLength.
    "guarded_catch" => ("G1Target", "Guarded", new[] { "System.Int32", "System.Int32" }, 0x0Eu, 0, EMIT_LOCAL_CAPTURE, false),

    // Try-entry refusal: inject at IL_0003 (the FIRST instruction of the try == TryOffset). The
    // profiler MUST refuse (body intact, no fire), not emit an invalid method.
    "try_entry" => ("G1Target", "Guarded", new[] { "System.Int32", "System.Int32" }, 0x03u, 0, EMIT_LOCAL_CAPTURE, true),

    _ => throw new ArgumentException($"unknown G1_CASE '{@case}'"),
};

var def = new NativeLineProbeDefinition(
    targetAssembly: "G1BranchEhE2E",
    targetType: cfg.type,
    targetMethod: cfg.method,
    targetSignatureTypes: cfg.sig,
    ilOffset: cfg.offset,
    probeId: probeId,
    callbackAssembly: "LineProbeSink",
    callbackType: "LineProbeSinkNs.LineProbeSink",
    callbackMethod: "CaptureLocal",
    emissionMode: cfg.emit,
    boxValue: cfg.slot);           // box_value carries the local SLOT index for LOCAL_CAPTURE

try
{
    var arr = new[] { def };
    NativeMethods.AddLineProbes($"g1-{@case}", arr, arr.Length);
    Console.WriteLine($"[reg] AddLineProbes: {cfg.type}.{cfg.method} @ IL offset 0x{cfg.offset:X} "
        + $"(local slot {cfg.slot}, emit={cfg.emit}) -> LineProbeSink.CaptureLocal(probeId={probeId})");
}
finally
{
    def.Dispose();
}

Console.WriteLine("[rejit] Waiting 700ms for ReJIT...\n");
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

LineProbeSink.FireCount = 0;
LineProbeSink.LastProbeId = -1;
LineProbeSink.LastCapturedValue = null;

if (@case == "sum")
{
    // --- SUM: forward + backward branch relocation ---
    int result = 0;
    Exception? ex = null;
    try { result = G1Target.Sum(SumN); }
    catch (Exception e) { ex = e; }

    // Assertion 1: runs to completion (invalid body => InvalidProgramException at first JIT).
    Assert(1, "Sum runs to completion (no InvalidProgramException / verification failure)",
        () => ex == null, ex == null ? "" : ex.GetType().FullName + ": " + ex.Message);

    // Assertion 2: same value with probe as without (branches still land right).
    Assert(2, $"Sum({SumN}) == {SumExpected} with probe (forward + backward branches land correctly)",
        () => result == SumExpected, $"got {result}");

    // Assertion 3: the probe at IL_0013 sits inside the `if (i%2==0)` TRUE branch, so it fires once
    // per EVEN iteration. For Sum(6), even i in {0,2,4} => exactly 3 fires (last captured i == 4).
    const int SumExpectedFires = 3;
    Assert(3, $"probe fired once per even iteration (FireCount == {SumExpectedFires}; probe is in the if-true branch)",
        () => LineProbeSink.FireCount == SumExpectedFires,
        $"FireCount={LineProbeSink.FireCount}");

    Console.WriteLine($"[info] LastProbeId={LineProbeSink.LastProbeId} (expected {probeId}); "
        + $"LastCaptured(loop var i, last even)={LineProbeSink.LastCapturedValue ?? "(null)"} (expected 4); FireCount={LineProbeSink.FireCount}");
}
else if (@case == "guarded_try")
{
    // --- GUARDED try: EH-region relocation, happy path ---
    int r = 0; Exception? ex = null;
    try { r = G1Target.Guarded(5); }
    catch (Exception e) { ex = e; }

    Assert(1, "Guarded(5) runs to completion (no InvalidProgramException / verification failure)",
        () => ex == null, ex == null ? "" : ex.GetType().FullName + ": " + ex.Message);
    Assert(4, $"Guarded(5) == {GuardedFiveExpected} with probe in try (EH intact, try/finally correct)",
        () => r == GuardedFiveExpected, $"got {r}");
    Assert(3, "probe in try fired (FireCount > 0)",
        () => LineProbeSink.FireCount > 0, $"FireCount={LineProbeSink.FireCount}");
    Console.WriteLine($"[info] captured r (post 100/5) = {LineProbeSink.LastCapturedValue ?? "(null)"}; FireCount={LineProbeSink.FireCount}");
}
else if (@case == "guarded_catch")
{
    // --- GUARDED catch: prove the CATCH region survived the offset shift (DivideByZero still caught) ---
    int r = 0; Exception? ex = null;
    try { r = G1Target.Guarded(0); }   // DivideByZero -> must be CAUGHT, r=-1, finally +1 => 0
    catch (Exception e) { ex = e; }

    Assert(1, "Guarded(0) runs to completion (no InvalidProgramException / verification failure)",
        () => ex == null, ex == null ? "" : ex.GetType().FullName + ": " + ex.Message);
    // Assertion 5 (make-or-break for EH): DivideByZero still CAUGHT with probe injected in the catch.
    Assert(5, $"Guarded(0) == {GuardedZeroExpected} with probe in catch (DivideByZero STILL CAUGHT; catch region survived offset shift)",
        () => r == GuardedZeroExpected, $"got {r} (if this threw/!= {GuardedZeroExpected}, EH stopped catching)");
    Assert(3, "probe in catch fired (FireCount > 0)",
        () => LineProbeSink.FireCount > 0, $"FireCount={LineProbeSink.FireCount}");
    Console.WriteLine($"[info] captured r in catch = {LineProbeSink.LastCapturedValue ?? "(null)"}; FireCount={LineProbeSink.FireCount}");
}
else if (@case == "try_entry")
{
    // --- SAFETY: inject at the try-entry offset. Profiler MUST refuse; body intact; probe never fires. ---
    int r = 0; Exception? ex = null;
    try { r = G1Target.Guarded(0); } catch (Exception e) { ex = e; }
    int r2 = 0; Exception? ex2 = null;
    try { r2 = G1Target.Guarded(5); } catch (Exception e) { ex2 = e; }

    // Assertion 6: process survives (no crash) AND the probe did NOT fire (rewrite was refused) AND
    // the original body is intact (both code paths return their normal values, EH still works).
    Assert(6, "inject at try-entry is REFUSED (process survives, body intact, probe never fires)",
        () => ex == null && ex2 == null && LineProbeSink.FireCount == 0
              && r == GuardedZeroExpected && r2 == GuardedFiveExpected,
        $"ex={ex?.GetType().Name ?? "null"}, ex2={ex2?.GetType().Name ?? "null"}, "
            + $"FireCount={LineProbeSink.FireCount}, r(0)={r} (exp {GuardedZeroExpected}), r2(5)={r2} (exp {GuardedFiveExpected})");
    Console.WriteLine($"[info] FireCount={LineProbeSink.FireCount} (expected 0 = refused); r(0)={r}; r2(5)={r2}");
}

Console.WriteLine($"\n=== Result [{@case}]: {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
return failures;

// ------------------------------------------------------------------
// P/Invoke surface for the forked native export (mirrors LineProbeE2E discipline).
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
    public uint HoistedFieldToken;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackType;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackMethod;
    public int EmissionMode;
    public int BoxValue;   // G1: carries the LOCAL SLOT index for LINE_EMIT_LOCAL_CAPTURE
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
