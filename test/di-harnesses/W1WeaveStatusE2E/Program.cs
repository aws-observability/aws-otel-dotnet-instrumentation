// SPDX-License-Identifier: Apache-2.0
//
// W1: does the native rewriter's PER-PROBE WEAVE VERDICT actually reflect what it did?
//
// WHY THIS HARNESS EXISTS. Applying a line probe reports READY as soon as the MANAGED resolution
// succeeds, because that is all that is knowable then: the rewrite happens later, on a CLR ReJIT
// thread, the first time the target method runs. Anything the rewriter then declines used to be
// reported live and never corrected — measured, before the callback-AssemblyRef fix, as eleven
// probes silently skipped while every one of them reported READY.
//
// The fix records a verdict per probe and exposes it through GetLineProbeWeaveResults. A managed unit
// test can only exercise the seam in front of that export; ONLY a real profiler can prove the
// verdicts are real. So this harness asserts, against a live rewrite:
//
//   1. a probe at a VALID offset comes back WOVEN(1)                      -> no false alarms
//   2. a probe at a MID-INSTRUCTION offset comes back OFFSET_NOT_INSTR(8) -> the refusal is visible
//   3. a LOCAL_CAPTURE probe with an out-of-range slot comes back SLOT(7) -> reason codes are distinct
//   4. no verdict exists BEFORE the method is first called                -> PENDING is not a failure
//   5. RemoveLineProbe drops the verdict                                  -> the log tracks live probes
//   6. a short buffer returns the TOTAL, not the written count            -> truncation is detectable
//
// Checks 2 and 3 are the load-bearing ones: without them a verdict log that reported WOVEN for
// everything would pass check 1 and be worthless. Check 4 is the other direction — a log that
// reported a failure for every not-yet-run probe would turn every idle method into an error.
//
// Exit code = number of failed assertions, matching every other harness here.
using System.Reflection;
using System.Runtime.InteropServices;
using LineProbeSinkNs;

_ = LineProbeSink.FireCount;
GC.KeepAlive(typeof(LineProbeSink));
Console.WriteLine("=== W1: per-probe weave status E2E ===\n");
if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] no profiler");
    return 99;
}

// Weave outcome codes, mirroring LineProbeWeaveOutcome in line_probe.h. Hardcoded here ON PURPOSE:
// the point of this harness is to check the NATIVE values, so deriving them from the managed enum
// would make the two agree by construction.
const int WOVEN = 1;
const int FAILED_LOCAL_SLOT_RANGE = 7;
const int FAILED_OFFSET_NOT_INSTR = 8;

int failures = 0;
void Assert(int n, string name, bool ok, string detail)
{
    Console.WriteLine(ok ? $"[PASS] {n}: {name}" : $"[FAIL] {n}: {name} -- {detail}");
    if (!ok)
    {
        failures++;
    }
}

// ---- STEP 0: real statement-boundary offsets from the target's IL ----
// DISCOVERED, not guessed. Every earlier attempt in this repo that hardcoded an offset landed
// mid-instruction and proved nothing.
var m = typeof(WeaveTarget).GetMethod("Compute", BindingFlags.Public | BindingFlags.Static)!;
var il = m.GetMethodBody()!.GetILAsByteArray()!;
var boundaries = new List<uint>();
for (int i = 0; i < il.Length; i++)
{
    if (il[i] >= 0x0A && il[i] <= 0x0D)
    {
        boundaries.Add((uint)(i + 1));
    }
}

Console.WriteLine($"[il] Compute IL len={il.Length}, stloc boundaries at: {string.Join(",", boundaries)}");
if (boundaries.Count < 2)
{
    Console.WriteLine("[FATAL] need >=2 boundaries");
    return 98;
}

uint goodOffset = boundaries[0];   // a real instruction start
uint badOffset = boundaries[0] + 2; // inside the NEXT instruction's operand bytes

NativeLineProbeDefinition Mk(uint off, int id, int emissionMode = 0, int boxValue = 0) => new(
    "LineProbeE2E", "WeaveTarget", "Compute", new[] { "System.Int32", "System.Int32" }, off, id,
    "LineProbeSink", "LineProbeSinkNs.LineProbeSink", emissionMode == 3 ? "CaptureLocal" : "Probe",
    emissionMode: emissionMode, boxValue: boxValue);

// Warm so the method has JIT'd code for ReJIT to replace.
for (int i = 0; i < 2000; i++)
{
    _ = WeaveTarget.Compute(i);
}

// ---- CHECK 4 FIRST: register, but do NOT let the method run. ----
// ORDER MATTERS. This has to happen before any other probe is registered, because the assertion is
// about the ABSENCE of a verdict and a later probe's verdict would make it ambiguous. The registered
// probe targets Compute, which is not called between the registration and the read.
var pending = new[] { Mk(goodOffset, 7101) };
NativeMethods.AddLineProbes("w1-pending", pending, pending.Length);
pending[0].Dispose();
var beforeAnyCall = ReadVerdicts();
Assert(
    4,
    "no verdict before the target method runs (PENDING is the absence of a record, not a failure)",
    !beforeAnyCall.ContainsKey(7101),
    $"got {Describe(beforeAnyCall)}");

// ---- CHECK 1: a valid offset weaves, and the verdict says so. ----
Thread.Sleep(700);
LineProbeSink.SeenProbeIdsMap.Clear();
for (int i = 0; i < 200; i++)
{
    _ = WeaveTarget.Compute(i);
}

var afterGood = ReadVerdicts();
Assert(
    1,
    "a probe at a valid offset is recorded WOVEN",
    afterGood.TryGetValue(7101, out var goodOutcome) && goodOutcome == WOVEN,
    $"got {Describe(afterGood)}");

// CORROBORATION, not decoration. A verdict log could report WOVEN without anything being woven; the
// probe actually firing is what makes WOVEN mean something.
Assert(
    1_1,
    "and that probe really does fire (WOVEN is not just a claim)",
    LineProbeSink.SeenProbeIds.Contains(7101),
    $"fired ids={string.Join(",", LineProbeSink.SeenProbeIds)}");

// ---- CHECK 2: a mid-instruction offset is REFUSED, with the offset-specific reason. ----
var bad = new[] { Mk(badOffset, 7102) };
NativeMethods.AddLineProbes("w1-badoffset", bad, bad.Length);
bad[0].Dispose();
Console.WriteLine($"[reg] bad offset {badOffset} (inside an instruction's operand bytes) as probe 7102");
Thread.Sleep(700);
for (int i = 0; i < 200; i++)
{
    _ = WeaveTarget.Compute(i);
}

var afterBad = ReadVerdicts();
Assert(
    2,
    "a probe at a mid-instruction offset is recorded OFFSET_NOT_INSTRUCTION_BOUNDARY",
    afterBad.TryGetValue(7102, out var badOutcome) && badOutcome == FAILED_OFFSET_NOT_INSTR,
    $"got {Describe(afterBad)}");
Assert(
    2_1,
    "the refused probe never fires",
    !LineProbeSink.SeenProbeIds.Contains(7102),
    $"fired ids={string.Join(",", LineProbeSink.SeenProbeIds)}");
Assert(
    2_2,
    "and its VALID sibling on the same method is still WOVEN (one refusal does not poison the method)",
    afterBad.TryGetValue(7101, out var stillGood) && stillGood == WOVEN,
    $"got {Describe(afterBad)}");

// ---- CHECK 3: a DIFFERENT refusal reports a DIFFERENT code. ----
// A log that collapsed every failure onto one code would pass check 2 and tell an operator nothing.
// LOCAL_CAPTURE with a slot past the ldloc operand range is refused by its own guard.
var badSlot = new[] { Mk(boundaries[1], 7103, emissionMode: 3, boxValue: 70000) };
NativeMethods.AddLineProbes("w1-badslot", badSlot, badSlot.Length);
badSlot[0].Dispose();
Console.WriteLine("[reg] LOCAL_CAPTURE probe 7103 with slot 70000 (out of ldloc range)");
Thread.Sleep(700);
for (int i = 0; i < 200; i++)
{
    _ = WeaveTarget.Compute(i);
}

var afterSlot = ReadVerdicts();
Assert(
    3,
    "an out-of-range local slot is recorded LOCAL_SLOT_OUT_OF_RANGE, a DISTINCT code from the bad offset",
    afterSlot.TryGetValue(7103, out var slotOutcome) && slotOutcome == FAILED_LOCAL_SLOT_RANGE,
    $"got {Describe(afterSlot)}");
Assert(
    3_1,
    "the two refusals are distinguishable (the log is not collapsing every failure onto one reason)",
    afterSlot.TryGetValue(7102, out var stillBadOffset) && stillBadOffset != slotOutcome,
    $"got {Describe(afterSlot)}");

// ---- CHECK 6: the short-buffer contract. Do this while three verdicts exist. ----
// The export returns the TOTAL it holds, not the number it wrote. A caller that trusted the written
// count would read a truncated view as complete and permanently miss the highest probe ids — i.e.
// the most recently created probes, which is exactly what an operator is watching.
var oneSlot = new NativeWeaveResult[1];
int totalFromShortBuffer = NativeMethods.GetLineProbeWeaveResults(oneSlot, 1);
Assert(
    6,
    "a capacity-1 read returns the TOTAL (>1), not the number written",
    totalFromShortBuffer >= 3,
    $"returned {totalFromShortBuffer} with 3+ verdicts held");

// ---- CHECK 5: removal drops the verdict. ----
// Otherwise the log grows for the process lifetime, and a stale failure would be attributed to
// whatever configuration next occupied that key.
NativeMethods.RemoveLineProbe(7102);
var afterRemove = ReadVerdicts();
Assert(
    5,
    "RemoveLineProbe forgets that probe's verdict",
    !afterRemove.ContainsKey(7102),
    $"got {Describe(afterRemove)}");
Assert(
    5_1,
    "and leaves the other probes' verdicts alone",
    afterRemove.ContainsKey(7101) && afterRemove.ContainsKey(7103),
    $"got {Describe(afterRemove)}");

// The method must still work after all of this — a refused probe must never corrupt the body.
// Compute(5): a=6, b=16, c=116.
Assert(
    7,
    "the target method still returns the correct value after two refusals and a removal",
    WeaveTarget.Compute(5) == 116,
    $"got {WeaveTarget.Compute(5)}");

Console.WriteLine($"\n=== {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
Console.WriteLine("Corroborate against the profiler's own native log in ./logs (LineProbe_Rewrite lines).");
return failures;

// Reads every verdict the profiler currently holds, growing the buffer the way the product does.
static Dictionary<int, int> ReadVerdicts()
{
    var buffer = new NativeWeaveResult[64];
    int total = NativeMethods.GetLineProbeWeaveResults(buffer, buffer.Length);
    if (total > buffer.Length)
    {
        buffer = new NativeWeaveResult[total];
        total = NativeMethods.GetLineProbeWeaveResults(buffer, buffer.Length);
    }

    var map = new Dictionary<int, int>();
    for (int i = 0; i < Math.Min(total, buffer.Length); i++)
    {
        map[buffer[i].ProbeId] = buffer[i].Outcome;
    }

    return map;
}

static string Describe(Dictionary<int, int> verdicts) =>
    verdicts.Count == 0 ? "{}" : "{" + string.Join(", ", verdicts.Select(kv => $"{kv.Key}=>{kv.Value}")) + "}";

// ------------------------------------------------------------------
// P/Invoke surface for the forked native exports (mirrors NativeMethods.cs discipline).
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

    [DllImport(NativeLib, EntryPoint = "GetLineProbeWeaveResults")]
    public static extern int GetLineProbeWeaveResults([Out] NativeWeaveResult[] buffer, int capacity);
}

// Must match _LineProbeWeaveResult in line_probe.h: two INT32s, in this order.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeWeaveResult
{
    public int ProbeId;
    public int Outcome;
}

// Mirror of NativeCallTargetDefinition's AllocHGlobal/Dispose discipline. 16 fields / 104 bytes —
// see the note on LocalTypeName/LocalIsValueType below; a stale copy of this struct is a SIGBUS.
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
    public int BoxValue;
    [MarshalAs(UnmanagedType.LPWStr)] public string? GateMethod;

    // The product struct grew these two trailing fields for non-int local capture (88 -> 104 bytes,
    // 16 fields). Without them the native side reads localTypeName as a pointer past the end of this
    // struct and the process dies with SIGBUS inside AddLineProbes. LocalIsValueType must be 1 for an
    // int local or the `box` is suppressed and the woven IL is invalid (InvalidProgramException),
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
        string? gateMethod = null)
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
