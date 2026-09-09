// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// BOX-GATE spike harness (DECISION A). Exit code == number of FAILED assertions.
//
// Registers TWO line probes via the forked AddLineProbes export at the same interior offset (5,
// `return y;`) of two structurally-identical hot methods:
//   * GatedSpikeTarget.HotGated   -> GATED emission:
//         ldc.i4 probeId; call ShouldCapture; brfalse SKIP; ldc.i4 probeId; ldc.i4 <v>; box; call Capture; SKIP:
//   * GatedSpikeTarget.HotUngated -> UNGATED (always-box) emission:
//         ldc.i4 probeId; ldc.i4 <v>; box; call Capture
//
// ShouldCapture mimics MaxHits: true for the first LineProbeSink.MaxHits calls, false afterwards.
// The spike proves that when ShouldCapture returns FALSE the injected brfalse branches PAST the box,
// so NO value-type -> heap allocation happens on the discarded path — the whole point of DECISION A.

using System.Runtime.InteropServices;
using LineProbeSinkNs;

_ = LineProbeSink.CaptureCount;
GC.KeepAlive(typeof(LineProbeSink));

Console.WriteLine("=== DI Line-Probe BOX-GATE Spike (DECISION A — gate-before-box) ===\n");

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] Native profiler not loaded. Run via run.sh.");
    return 99;
}

// Interior statement boundary B (`return y;`), same offset as the proven Phase-2 SpikeTarget.
uint ilOffset = uint.Parse(Environment.GetEnvironmentVariable("LINEPROBE_IL_OFFSET") ?? "5");
const int GatedProbeId = 5150;
const int UngatedProbeId = 6160;
const int GatedBoxValue = 111;   // the constant the GATED box materializes (distinguishing value)
const int UngatedBoxValue = 222; // the constant the UNGATED box materializes

// MaxHits gate budget: ShouldCapture returns true this many times, then false.
LineProbeSink.MaxHits = 3;

const int EMIT_GATED = 1;
const int EMIT_UNGATED = 2;

var gatedDef = new NativeLineProbeDefinition(
    targetAssembly: "LineProbeGatedE2E",
    targetType: "GatedSpikeTarget",
    targetMethod: "HotGated",
    targetSignatureTypes: new[] { "System.Int32", "System.Int32" },
    ilOffset: ilOffset,
    probeId: GatedProbeId,
    callbackAssembly: "LineProbeSink",
    callbackType: "LineProbeSinkNs.LineProbeSink",
    callbackMethod: "Capture",
    emissionMode: EMIT_GATED,
    boxValue: GatedBoxValue,
    gateMethod: "ShouldCapture");

var ungatedDef = new NativeLineProbeDefinition(
    targetAssembly: "LineProbeGatedE2E",
    targetType: "GatedSpikeTarget",
    targetMethod: "HotUngated",
    targetSignatureTypes: new[] { "System.Int32", "System.Int32" },
    ilOffset: ilOffset,
    probeId: UngatedProbeId,
    callbackAssembly: "LineProbeSink",
    callbackType: "LineProbeSinkNs.LineProbeSink",
    callbackMethod: "Capture",
    emissionMode: EMIT_UNGATED,
    boxValue: UngatedBoxValue,
    gateMethod: null);

try
{
    var arr = new[] { gatedDef, ungatedDef };
    NativeMethods.AddLineProbes("lineprobe-boxgate", arr, arr.Length);
    Console.WriteLine($"[reg] GATED   probe: HotGated   @ IL {ilOffset} -> ShouldCapture->Capture(probeId={GatedProbeId}, box={GatedBoxValue}), MaxHits={LineProbeSink.MaxHits}");
    Console.WriteLine($"[reg] UNGATED probe: HotUngated @ IL {ilOffset} -> Capture(probeId={UngatedProbeId}, box={UngatedBoxValue}) [always boxes]");
}
finally
{
    gatedDef.Dispose();
    ungatedDef.Dispose();
}

Console.WriteLine("[rejit] Waiting 700ms for ReJIT of both methods...\n");
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

// ============================================================================================
// PHASE 1 — Correctness of the GATED body (no crash, gate fires, capture on the allowed hits).
// ============================================================================================
LineProbeSink.ResetGate();

const int N = 10; // >> MaxHits(3): 3 captures allowed, 7 discarded.
int gatedResult = 0;
Exception? gatedEx = null;
try
{
    for (int i = 0; i < N; i++)
    {
        gatedResult = GatedSpikeTarget.HotGated(40 + i);
    }
}
catch (Exception ex) { gatedEx = ex; }

// ---- Assertion 1: the GATED body (interior brfalse + skip label) is runtime-valid. ----
// An invalid conditional-branch body throws InvalidProgramException at first JIT.
Assert(1, "HotGated runs to completion (interior brfalse+skip produced a valid body; no InvalidProgramException)",
    () => gatedEx == null,
    gatedEx == null ? "" : gatedEx.GetType().FullName + ": " + gatedEx.Message);

// ---- Assertion 4: original method result unchanged. HotGated(40 + (N-1)) = 40+9+1 = 50. ----
Assert(4, $"HotGated(49) == 50 (result unchanged; gated seq is stack-neutral around the branch)",
    () => gatedResult == 50,
    $"got {gatedResult}");

// The gate ran on EVERY iteration (cheap, no alloc); Capture fired only on the first MaxHits.
// ---- Assertion 2: when ShouldCapture returns TRUE, Capture fires and gets the boxed value. ----
Assert(2, $"Capture fired exactly MaxHits={LineProbeSink.MaxHits} times, boxed value=={GatedBoxValue} (TRUE path boxes + captures)",
    () => LineProbeSink.CaptureCount == LineProbeSink.MaxHits
          && LineProbeSink.LastGatedValue is int gv && gv == GatedBoxValue
          && LineProbeSink.LastCaptureProbeId == GatedProbeId,
    $"CaptureCount={LineProbeSink.CaptureCount} (want {LineProbeSink.MaxHits}), "
        + $"LastGatedValue={LineProbeSink.LastGatedValue ?? "(null)"}, LastCaptureProbeId={LineProbeSink.LastCaptureProbeId}");

// ---- Assertion 3: when ShouldCapture returns FALSE, the gate ran but Capture did NOT fire. ----
// ShouldCapture was invoked once per iteration (N times); Capture only MaxHits times => the
// remaining (N - MaxHits) discarded iterations branched PAST the box + Capture.
Assert(3, $"Gate ran on all {N} iterations but Capture skipped on the {N - LineProbeSink.MaxHits} discarded hits (brfalse skipped the box+call)",
    () => LineProbeSink.ShouldCaptureCalls == N && LineProbeSink.CaptureCount == LineProbeSink.MaxHits,
    $"ShouldCaptureCalls={LineProbeSink.ShouldCaptureCalls} (want {N}), CaptureCount={LineProbeSink.CaptureCount} (want {LineProbeSink.MaxHits})");

// ============================================================================================
// PHASE 2 — ALLOCATION EVIDENCE. Run a DISCARD-ONLY loop on each target (gate budget already
// exhausted above, so every HotGated iteration here takes the FALSE/skip path) and measure
// per-call heap allocation with GC.GetAllocatedBytesForCurrentThread(). The gated discard path
// must allocate ~0/iteration; the ungated path must allocate one box (~24 bytes) per iteration.
// ============================================================================================
Console.WriteLine();

// Gate budget is already exhausted (ShouldCaptureCalls == N=10 > MaxHits=3), so from here on
// HotGated always takes the skip path. Warm up once to force JIT + any first-touch allocation.
_ = GatedSpikeTarget.HotGated(1);
_ = GatedSpikeTarget.HotUngated(1);

const int M = 100_000;

// Snapshot Capture count immediately BEFORE the gated discard loop. NOTE: Capture is SHARED by both
// HotGated and HotUngated, so we measure the DELTA across only the gated loop (must be 0 — every
// iteration takes the FALSE/skip branch past the box+call).
int captureBeforeGatedLoop = LineProbeSink.CaptureCount;

long beforeGated = GC.GetAllocatedBytesForCurrentThread();
int sinkG = 0;
for (int i = 0; i < M; i++) { sinkG += GatedSpikeTarget.HotGated(i); }
long afterGated = GC.GetAllocatedBytesForCurrentThread();
long gatedAlloc = afterGated - beforeGated;

int gatedLoopCaptureDelta = LineProbeSink.CaptureCount - captureBeforeGatedLoop; // must be 0

// HotUngated does NOT call ShouldCapture at all — it boxes + Captures unconditionally.
long beforeUngated = GC.GetAllocatedBytesForCurrentThread();
int sinkU = 0;
for (int i = 0; i < M; i++) { sinkU += GatedSpikeTarget.HotUngated(i); }
long afterUngated = GC.GetAllocatedBytesForCurrentThread();
long ungatedAlloc = afterUngated - beforeUngated;

GC.KeepAlive(sinkG);
GC.KeepAlive(sinkU);

double gatedPerCall = (double)gatedAlloc / M;
double ungatedPerCall = (double)ungatedAlloc / M;

Console.WriteLine($"[alloc] GATED   discard-only loop: {gatedAlloc,10} bytes over {M} calls = {gatedPerCall:F3} bytes/call");
Console.WriteLine($"[alloc] UNGATED always-box  loop: {ungatedAlloc,10} bytes over {M} calls = {ungatedPerCall:F3} bytes/call");

// ---- Assertion 5: the GATED discard path allocates ~0/call (the box was skipped). ----
// Allow a tiny slack for any incidental thread-local bookkeeping; the box of an int is ~24 bytes,
// so anything under ~4 bytes/call means the per-iteration box did NOT happen.
Assert(5, "GATED discard path allocates ~0 bytes/call (the box was branched past)",
    () => gatedPerCall < 4.0,
    $"gatedPerCall={gatedPerCall:F3} bytes/call (expected < 4.0)");

// ---- Assertion 6: the UNGATED path allocates one box per call (the hazard DECISION A describes). ----
Assert(6, "UNGATED path allocates a box every call (>= 16 bytes/call — the per-hit boxing hazard)",
    () => ungatedPerCall >= 16.0,
    $"ungatedPerCall={ungatedPerCall:F3} bytes/call (expected >= 16.0)");

// ---- Assertion 7: Capture did not fire at all during the gated discard-only loop. ----
Assert(7, $"Capture did NOT fire across {M} discarded gated calls (delta == 0; every call took the skip branch)",
    () => gatedLoopCaptureDelta == 0,
    $"Capture fired {gatedLoopCaptureDelta} times during the {M}-iteration gated discard loop (want 0)");

Console.WriteLine($"\n[info] ShouldCapture invoked {LineProbeSink.ShouldCaptureCalls} times total (gate ran every gated iteration, cheaply).");
Console.WriteLine($"[info] Captures on the gated discard loop: {gatedLoopCaptureDelta} (0 => box + call fully skipped).");
Console.WriteLine($"[info] Allocation: gated discard = {gatedPerCall:F3} B/call, ungated = {ungatedPerCall:F3} B/call "
    + $"({(gatedAlloc <= 0 ? "∞" : (ungatedPerCall / gatedPerCall).ToString("F1"))}x more on the ungated path).");

Console.WriteLine($"\n=== Result: {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
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

// Mirror of the shared NativeLineProbeDefinition (must match the native struct stride exactly —
// line_probe.h). Adds the BOX-GATE trailing fields: EmissionMode, BoxValue, GateMethod.
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
    public uint HoistedFieldToken; // async spike field; 0 here
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackAssembly;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackType;
    [MarshalAs(UnmanagedType.LPWStr)] public string CallbackMethod;
    // BOX-GATE SPIKE (DECISION A): 0 => legacy; 1 => gated two-call; 2 => ungated always-box.
    public int EmissionMode;
    public int BoxValue;
    [MarshalAs(UnmanagedType.LPWStr)] public string? GateMethod;

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
        int emissionMode,
        int boxValue,
        string? gateMethod,
        uint hoistedFieldToken = 0)
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
