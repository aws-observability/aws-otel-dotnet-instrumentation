// SPDX-License-Identifier: Apache-2.0
//
// R9 + GAP-2/3 E2E: removal-under-load, teardown-vs-in-flight, >2 probes, and MIXED emission modes
// in one method. Runs on the REAL forked native profiler (no mocks).
//
// THE R9 QUESTION (highest-value open feasibility item): a probe is removed on the config thread
// WHILE its woven `call` is executing on user threads, and the re-ReJIT that excludes it must not
// DROP or DOUBLE-FIRE a co-located survivor.
//
// VERIFICATION DISCIPLINE (Phase-1 scar: code can weave yet silently never fire):
//   * Offsets are read from the REAL portable PDB sequence points (never guessed). A prior spike
//     failed by guessing offsets that landed on operand bytes mid-instruction.
//   * Targets are PRE-WARMED before any probe is applied — RequestReJIT only recompiles methods that
//     have already been JIT'd, otherwise it silently no-ops.
//   * Every positive result is gated on a BASELINE assertion firing first. A test whose baseline
//     never fired proves nothing.
//   * The load threads verify the target's OWN return value on every call — a side effect the
//     harness cannot fake. Body corruption shows up here even if fire counts look fine.
//   * EXACT counts are asserted in a QUIESCENT window (load stopped): a live probe must fire exactly
//     once per call. `== calls` catches drops AND double-fires; `>= 1` would catch neither.
//   * PAIRED NEGATIVE CONTROL: R9_NO_REMOVE=1 flips exactly ONE variable (whether RemoveLineProbe is
//     called at all). The silencing of the removed probe must DISAPPEAR. Fire-in-positive +
//     silence-in-negative = causally real. R9_BAD_OFFSETS=1 is the second control (mid-instruction
//     offsets must be refused, nothing fires).
//
// Exit code = number of failed assertions.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using R9SinkNs;

_ = R9Sink.TotalFires;
GC.KeepAlive(typeof(R9Sink));

Console.WriteLine("=== R9: removal-under-load + mixed-mode multi-probe E2E ===\n");
if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] no profiler — run via ./run.sh");
    return 99;
}

bool negNoRemove = Environment.GetEnvironmentVariable("R9_NO_REMOVE") == "1";
bool negBadOffsets = Environment.GetEnvironmentVariable("R9_BAD_OFFSETS") == "1";
if (negNoRemove)
{
    Console.WriteLine("[NEG-1] R9_NO_REMOVE=1 — RemoveLineProbe will NOT be called. The removed-probe");
    Console.WriteLine("        silencing MUST DISAPPEAR (probe 9002 must still fire at the end).");
}

if (negBadOffsets)
{
    Console.WriteLine("[NEG-2] R9_BAD_OFFSETS=1 — offsets moved to a genuine OPERAND byte (validated");
    Console.WriteLine("        against a reflected-opcode IL walk, not a blind +1). Injection MUST be");
    Console.WriteLine("        refused; NOTHING may fire; the method must stay intact.");
}

int failures = 0;
void Assert(string name, bool ok, string detail)
{
    Console.WriteLine(ok ? $"  [PASS] {name}" : $"  [FAIL] {name} -- {detail}");
    if (!ok)
    {
        failures++;
    }
}

// ---------------------------------------------------------------------------------------------
// STEP 0 — discover REAL statement-boundary IL offsets from the portable PDB's sequence points.
// This is the production mechanism (what PdbReader/P3a will do), not a guess. A sequence point's
// IL offset IS a statement boundary by definition; 0xFEEFEE marks a hidden/compiler point and is
// filtered exactly as the real reader must.
// ---------------------------------------------------------------------------------------------
const int HiddenSequencePoint = 0xFEEFEE;
string asmPath = typeof(R9Targets).Assembly.Location;
string pdbPath = Path.ChangeExtension(asmPath, ".pdb");
Console.WriteLine($"[pdb] {pdbPath} exists={File.Exists(pdbPath)}");
if (!File.Exists(pdbPath))
{
    Console.WriteLine("[FATAL] no portable PDB next to the assembly — cannot discover offsets honestly.");
    return 97;
}

// Mvid cross-check (the R4 trust case): the PDB must belong to THIS module, else its offsets are
// plausible-but-wrong. Portable PDBs carry the module's Mvid in their debug-directory entry; here we
// use the simpler available signal — the PE's CodeView entry path/GUID vs the PDB's id.
Guid moduleMvid;
using (var peStream = File.OpenRead(asmPath))
using (var peReader = new PEReader(peStream))
{
    var mdReader = peReader.GetMetadataReader();
    moduleMvid = mdReader.GetGuid(mdReader.GetModuleDefinition().Mvid);
}

Dictionary<string, List<uint>> boundaries = new();
Dictionary<string, int> localCounts = new();
using (var pdbStream = File.OpenRead(pdbPath))
using (var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream))
{
    var pdb = pdbProvider.GetMetadataReader();

    // The PDB's own id blob must match the PE debug directory; we assert the weaker but still
    // load-bearing property that the PDB parses and resolves the target methods by token.
    foreach (var name in new[] { "Hot", "Mixed", "Untouched" })
    {
        var mi = typeof(R9Targets).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
        var handle = MetadataTokens.MethodDefinitionHandle(mi.MetadataToken);
        var dbgInfo = pdb.GetMethodDebugInformation(handle);
        var offsets = new List<uint>();
        foreach (var sp in dbgInfo.GetSequencePoints())
        {
            if (sp.StartLine == HiddenSequencePoint || sp.IsHidden)
            {
                continue; // 0xFEEFEE filter — a hidden point is NOT a user statement boundary
            }

            offsets.Add((uint)sp.Offset);
        }

        boundaries[name] = offsets;
        localCounts[name] = mi.GetMethodBody()!.LocalVariables.Count;
        Console.WriteLine($"[pdb] {name}: locals={localCounts[name]} sequence-point offsets=[{string.Join(",", offsets)}]");
    }
}

Console.WriteLine($"[pdb] module Mvid={moduleMvid}");

// ---------------------------------------------------------------------------------------------
// OFFSET -> SLOT MAPPING. This is subtle and a first version of this harness got it WRONG, which
// the value assertions caught (every probe read a boxed 0):
//
//   A sequence point's offset is the START of that statement, i.e. BEFORE its assignment executes.
//   So to read the local assigned by statement k, you must inject at the start of statement k+1.
//   Injecting at statement k's own start reads a slot that is ALLOCATED BUT NOT YET ASSIGNED and
//   silently yields 0 — the sync-local twin of the async hoisted-field pre-assignment hazard the
//   plan flags (§0 DECISION B). Worth keeping: it is a live demonstration that "the probe fired"
//   and "the probe read the right value" are genuinely independent claims.
//
// boundaries[]  = [ '{', stmt1, stmt2, ..., 'return', '}' ]
// index         =   0     1      2
// To read the local assigned by stmt N (slot N-1), inject at boundaries[N+1].
// ---------------------------------------------------------------------------------------------
List<uint> hotB = boundaries["Hot"];
List<uint> mixedB = boundaries["Mixed"];
List<uint> untouchedB = boundaries["Untouched"];
if (hotB.Count < 6 || mixedB.Count < 6 || untouchedB.Count < 3)
{
    Console.WriteLine($"[FATAL] not enough boundaries: hot={hotB.Count} mixed={mixedB.Count} untouched={untouchedB.Count}");
    return 96;
}

// ---------------------------------------------------------------------------------------------
// IL INSTRUCTION-BOUNDARY WALKER. Needed for an HONEST negative control.
//
// The first version of NEG-2 used `off + 1` and FAILED — because at these offsets +1 often lands on
// the next real opcode (e.g. IL_0005 is `ldloc.0`, one byte, so +1 is the `ldc.i4.s` at IL_0006, a
// perfectly valid boundary). The probes fired, and a naive reading would have called that "the fork
// accepts bad offsets." It didn't: the offsets weren't bad. A negative control that doesn't actually
// flip the variable proves nothing, so it needs a REAL mid-instruction (operand-byte) offset.
//
// The opcode table is reflected out of System.Reflection.Emit.OpCodes rather than hand-written, so
// it is authoritative instead of a guess. This is also the machinery P3a's offset pre-validation
// needs (statement boundaries AND branch-target avoidance), so it doubles as a scope data point.
// ---------------------------------------------------------------------------------------------
var opcodeByValue = new Dictionary<ushort, System.Reflection.Emit.OpCode>();
foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
{
    if (f.GetValue(null) is System.Reflection.Emit.OpCode oc)
    {
        opcodeByValue[unchecked((ushort)oc.Value)] = oc;
    }
}

static int OperandSize(System.Reflection.Emit.OperandType t) => t switch
{
    System.Reflection.Emit.OperandType.InlineNone => 0,
    System.Reflection.Emit.OperandType.ShortInlineBrTarget => 1,
    System.Reflection.Emit.OperandType.ShortInlineI => 1,
    System.Reflection.Emit.OperandType.ShortInlineVar => 1,
    System.Reflection.Emit.OperandType.InlineVar => 2,
    System.Reflection.Emit.OperandType.InlineI8 => 8,
    System.Reflection.Emit.OperandType.InlineR => 8,
    System.Reflection.Emit.OperandType.InlineSwitch => -1, // variable: 4 + 4*N
    _ => 4,
};

// Returns (validInstructionStartOffsets, branchTargetOffsets) for a method's IL.
(HashSet<uint> Starts, HashSet<uint> BranchTargets) WalkIl(MethodInfo mi)
{
    byte[] il = mi.GetMethodBody()!.GetILAsByteArray()!;
    var starts = new HashSet<uint>();
    var targets = new HashSet<uint>();
    int p = 0;
    while (p < il.Length)
    {
        starts.Add((uint)p);
        int opStart = p;
        ushort val = il[p];
        p++;
        if (val == 0xFE && p < il.Length)
        {
            val = (ushort)(0xFE00 | il[p]);
            p++;
        }

        if (!opcodeByValue.TryGetValue(val, out var oc))
        {
            break; // unknown opcode: stop rather than mis-walk and produce bogus "boundaries"
        }

        var ot = oc.OperandType;
        int sz = OperandSize(ot);
        if (sz == -1)
        {
            int n = BitConverter.ToInt32(il, p);
            int baseAfter = p + 4 + (4 * n);
            for (int k = 0; k < n; k++)
            {
                targets.Add((uint)(baseAfter + BitConverter.ToInt32(il, p + 4 + (4 * k))));
            }

            p = baseAfter;
            continue;
        }

        if (ot == System.Reflection.Emit.OperandType.ShortInlineBrTarget)
        {
            targets.Add((uint)(p + 1 + (sbyte)il[p]));
        }
        else if (ot == System.Reflection.Emit.OperandType.InlineBrTarget)
        {
            targets.Add((uint)(p + 4 + BitConverter.ToInt32(il, p)));
        }

        p += sz;
        _ = opStart;
    }

    return (starts, targets);
}

var hotWalk = WalkIl(typeof(R9Targets).GetMethod("Hot", BindingFlags.Public | BindingFlags.Static)!);
var mixedWalk = WalkIl(typeof(R9Targets).GetMethod("Mixed", BindingFlags.Public | BindingFlags.Static)!);
var untWalk = WalkIl(typeof(R9Targets).GetMethod("Untouched", BindingFlags.Public | BindingFlags.Static)!);
Console.WriteLine($"[il] Hot instruction starts=[{string.Join(",", hotWalk.Starts.OrderBy(x => x))}] branchTargets=[{string.Join(",", hotWalk.BranchTargets.OrderBy(x => x))}]");

// All three targets' walks, so a bad offset can be validated against the RIGHT method.
var walks = new Dictionary<string, (HashSet<uint> Starts, HashSet<uint> BranchTargets)>
{
    ["Hot"] = hotWalk, ["Mixed"] = mixedWalk, ["Untouched"] = untWalk,
};

// Track which offsets NEG-2 actually used, so we can PROVE they were genuinely mid-instruction
// rather than trusting that "+something" made them bad.
var badOffsetsUsed = new List<(string Target, uint Offset, bool ReallyMidInstruction)>();

// In BAD mode, walk forward from the requested offset to the first offset that is NOT an
// instruction start — i.e. a genuine operand byte. Self-checked below.
uint BadFor(string target, uint off)
{
    if (!negBadOffsets)
    {
        return off;
    }

    var starts = walks[target].Starts;
    uint bodyLen = (uint)typeof(R9Targets).GetMethod(target, BindingFlags.Public | BindingFlags.Static)!
        .GetMethodBody()!.GetILAsByteArray()!.Length;
    for (uint cand = off + 1; cand < bodyLen; cand++)
    {
        if (!starts.Contains(cand))
        {
            badOffsetsUsed.Add((target, cand, true));
            return cand;
        }
    }

    // No operand byte exists after `off` (all-single-byte tail). Fail loudly rather than silently
    // running a control that isn't a control.
    Console.WriteLine($"[FATAL] NEG-2 could not find a mid-instruction offset in {target} after {off}.");
    Environment.Exit(95);
    return off;
}

uint Bad(uint off) => negBadOffsets ? BadFor("Hot", off) : off;

// Read the local assigned by stmt N (slot N-1) => inject at boundaries[N+1].
uint ReadSlotAt(string target, List<uint> b, int slot) =>
    negBadOffsets ? BadFor(target, b[slot + 2]) : b[slot + 2];

// Hot(): 4 probes reading slots 0..3 (locals a,b,c,d), each at the boundary AFTER its assignment.
uint hOff1 = ReadSlotAt("Hot", hotB, 0); // reads a
uint hOff2 = ReadSlotAt("Hot", hotB, 1); // reads b  <- the one removed under load
uint hOff3 = ReadSlotAt("Hot", hotB, 2); // reads c
uint hOff4 = ReadSlotAt("Hot", hotB, 3); // reads d

// Deterministic expected values for Hot(1): a=2 b=12 c=112 d=1112 e=11112.
const int HotArg = 1;
const int HotExpectedReturn = 11112;
const int ExpectA = 2, ExpectB = 12, ExpectC = 112;

const int PidLow = 9001;   // survivor #1 (reads a)
const int PidMid = 9002;   // THE ONE WE REMOVE (reads b)
const int PidHigh = 9003;  // survivor #2 (reads c)
const int PidAdded = 9004; // added LATER, to an already-woven method (incremental add)
const int PidUntouched = 9100;

// Emission modes (must match LineProbeEmissionMode in the fork's line_probe.h).
const int ModeLegacy = 0;
const int ModeGatedBox = 1;
const int ModeUngatedBox = 2;
const int ModeLocalCapture = 3;

const string SinkAsm = "R9Sink";
const string SinkType = "R9SinkNs.R9Sink";

// Signature types: [return, arg0] — the count is arity+1, matching the CallTarget convention the
// fork's signature-count match uses.
static string[] Sig() => new[] { "System.Int32", "System.Int32" };

NativeLineProbeDefinition MkLocal(string target, uint off, int id, int slot) => new(
    "R9RemovalUnderLoadE2E", "R9Targets", target, Sig(), off, id,
    SinkAsm, SinkType, "CaptureLocal", 0, ModeLocalCapture, slot, null);

NativeLineProbeDefinition MkLegacy(string target, uint off, int id) => new(
    "R9RemovalUnderLoadE2E", "R9Targets", target, Sig(), off, id,
    SinkAsm, SinkType, "Probe", 0, ModeLegacy, 0, null);

NativeLineProbeDefinition MkGated(string target, uint off, int id, int boxValue) => new(
    "R9RemovalUnderLoadE2E", "R9Targets", target, Sig(), off, id,
    SinkAsm, SinkType, "Capture", 0, ModeGatedBox, boxValue, "ShouldCapture");

NativeLineProbeDefinition MkUngated(string target, uint off, int id, int boxValue) => new(
    "R9RemovalUnderLoadE2E", "R9Targets", target, Sig(), off, id,
    SinkAsm, SinkType, "Capture", 0, ModeUngatedBox, boxValue, null);

void Apply(string id, params NativeLineProbeDefinition[] defs)
{
    NativeMethods.AddLineProbes(id, defs, defs.Length);
    foreach (var d in defs)
    {
        d.Dispose();
    }
}

// ---------------------------------------------------------------------------------------------
// STEP 1 — PRE-WARM. RequestReJIT only recompiles methods that are already JIT-compiled; without
// this the request silently no-ops and every later result is a false negative. (This is exactly
// what invalidated the earlier bulk-apply microbenchmark.)
// ---------------------------------------------------------------------------------------------
int warm = 0;
for (int i = 0; i < 5000; i++)
{
    warm += R9Targets.Hot(HotArg) + R9Targets.Mixed(i) + R9Targets.Untouched(i);
}

Console.WriteLine($"[warm] pre-warmed all 3 targets (checksum {warm})");
Assert("target returns correct value BEFORE any probe (control)", R9Targets.Hot(HotArg) == HotExpectedReturn,
    $"got {R9Targets.Hot(HotArg)}");

// ---------------------------------------------------------------------------------------------
// STEP 2 — BASELINE: 3 co-located probes on Hot() + 1 on Untouched(). Everything downstream is
// gated on this. LOCAL_CAPTURE mode so each probe reads a REAL local, letting us prove the survivor
// still reads the CORRECT slot after a re-ReJIT (a survivor that fires but reads a corrupted slot
// would pass a count-only test).
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n---- STEP 2: baseline, 3 co-located probes on Hot() ----");
R9Sink.Reset();
Apply("r9-hot",
    MkLocal("Hot", hOff1, PidLow, 0),
    MkLocal("Hot", hOff2, PidMid, 1),
    MkLocal("Hot", hOff3, PidHigh, 2));
Apply("r9-untouched", MkLocal("Untouched", ReadSlotAt("Untouched", untouchedB, 0), PidUntouched, 0));
Thread.Sleep(1200);

for (int i = 0; i < 300; i++)
{
    _ = R9Targets.Hot(HotArg);
    _ = R9Targets.Untouched(1);
}

long b1 = R9Sink.Get(PidLow), b2 = R9Sink.Get(PidMid), b3 = R9Sink.Get(PidHigh), bu = R9Sink.Get(PidUntouched);
Console.WriteLine($"[base] fires: {PidLow}={b1} {PidMid}={b2} {PidHigh}={b3} untouched({PidUntouched})={bu}");

if (negBadOffsets)
{
    // SELF-CHECK FIRST: prove the control actually flipped the variable. The first version of this
    // control used `off + 1`, which landed on the NEXT VALID opcode — so the probes fired and the
    // control silently proved nothing. Assert every offset we used is genuinely NOT an instruction
    // start before drawing any conclusion from the silence below.
    Console.WriteLine($"[NEG-2] offsets used: {string.Join(", ", badOffsetsUsed.Select(x => $"{x.Target}@{x.Offset}"))}");
    bool allReallyBad = badOffsetsUsed.Count > 0 &&
        badOffsetsUsed.All(x => !walks[x.Target].Starts.Contains(x.Offset));
    Assert("NEG-2 SELF-CHECK: every offset used is genuinely mid-instruction (control is valid)",
        allReallyBad,
        $"used={badOffsetsUsed.Count}; starts(Hot)=[{string.Join(",", walks["Hot"].Starts.OrderBy(x => x))}]");

    // NEG-2: mid-instruction offsets must be refused by the native fail-safe. Nothing may fire, and
    // critically the method must still WORK (body left intact, no partial rewrite exported).
    Assert("NEG-2: bad (mid-instruction) offsets => NOTHING fires", b1 == 0 && b2 == 0 && b3 == 0,
        $"{PidLow}={b1} {PidMid}={b2} {PidHigh}={b3}");
    Assert("NEG-2: method body still intact after refused injection", R9Targets.Hot(HotArg) == HotExpectedReturn,
        $"got {R9Targets.Hot(HotArg)}");
    Console.WriteLine($"\n=== NEG-2 done: {(failures == 0 ? "AS EXPECTED (silent + intact)" : failures + " FAILED")} ===");
    return failures;
}

bool baselineOk = b1 > 0 && b2 > 0 && b3 > 0;
Assert("BASELINE: all 3 co-located probes fire (>2 probes per method — gap 2)", baselineOk,
    $"{PidLow}={b1} {PidMid}={b2} {PidHigh}={b3}");
Assert("BASELINE: co-located probes read their CORRECT distinct locals",
    Equals(R9Sink.LastValue.GetValueOrDefault(PidLow), ExpectA) &&
    Equals(R9Sink.LastValue.GetValueOrDefault(PidMid), ExpectB) &&
    Equals(R9Sink.LastValue.GetValueOrDefault(PidHigh), ExpectC),
    $"a={R9Sink.LastValue.GetValueOrDefault(PidLow)} b={R9Sink.LastValue.GetValueOrDefault(PidMid)} c={R9Sink.LastValue.GetValueOrDefault(PidHigh)} (want {ExpectA}/{ExpectB}/{ExpectC})");
Assert("BASELINE: method still returns correct value with 3 probes woven",
    R9Targets.Hot(HotArg) == HotExpectedReturn, $"got {R9Targets.Hot(HotArg)}");

if (!baselineOk)
{
    Console.WriteLine("\n[R9] ABORT — baseline did not fire, so nothing about removal can be concluded.");
    Console.WriteLine($"\n=== {failures} FAILED ===");
    return failures;
}

// ---------------------------------------------------------------------------------------------
// STEP 3 — INCREMENTAL ADD: a 4th probe onto an ALREADY-WOVEN method. Distinct from STEP 2 (which
// applied 3 at once). Removal uses revert-then-rejit precisely because RequestReJIT on an
// already-rejitted method fails without a revert; this checks whether the ADD path has the same
// constraint.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n---- STEP 3: incremental add (4th probe onto an already-woven method) ----");
R9Sink.Reset();
Apply("r9-hot-add4", MkLocal("Hot", hOff4, PidAdded, 3));
Thread.Sleep(1200);
for (int i = 0; i < 300; i++)
{
    _ = R9Targets.Hot(HotArg);
}

long added = R9Sink.Get(PidAdded);
long stillLow = R9Sink.Get(PidLow);
Console.WriteLine($"[add4] {PidAdded}={added} (pre-existing {PidLow}={stillLow})");
Assert("INCREMENTAL ADD: 4th probe added to an already-woven method fires", added > 0, $"{PidAdded}={added}");
Assert("INCREMENTAL ADD: pre-existing probes keep firing", stillLow > 0, $"{PidLow}={stillLow}");

// ---------------------------------------------------------------------------------------------
// STEP 4 — THE R9 TEST. Start user threads hammering Hot() in a tight loop, each verifying the
// return value on EVERY call, then churn a probe add/remove on the config thread. The removal is
// therefore issued while the woven `call` is genuinely executing on other threads.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n---- STEP 4: removal under load (config thread vs in-flight user threads) ----");
int threads = int.TryParse(Environment.GetEnvironmentVariable("R9_THREADS"), out var t) ? t : 6;
int churn = int.TryParse(Environment.GetEnvironmentVariable("R9_CHURN"), out var c) ? c : 8;
using var stop = new CancellationTokenSource();
long calls = 0, badReturns = 0;

var workers = new List<Thread>();
for (int w = 0; w < threads; w++)
{
    var th = new Thread(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 1000; i++)
            {
                // The load thread verifies the target's OWN result. Body corruption from a
                // concurrent re-ReJIT shows up HERE even if fire counts look plausible.
                if (R9Targets.Hot(HotArg) != HotExpectedReturn)
                {
                    Interlocked.Increment(ref badReturns);
                }
            }

            Interlocked.Add(ref calls, 1000);
        }
    }) { IsBackground = true, Name = $"r9-load-{w}" };
    workers.Add(th);
    th.Start();
}

Console.WriteLine($"[load] {threads} threads hammering Hot(); churning {churn} remove/re-add cycles");
var sw = Stopwatch.StartNew();
int churnAddOk = 0;
for (int cycle = 0; cycle < churn; cycle++)
{
    if (!negNoRemove)
    {
        NativeMethods.RemoveLineProbe(PidMid);
    }

    Thread.Sleep(120);

    // Re-add with a UNIQUE definitions id: the native side dedups by id (definitions_ids_), so
    // reusing one would make the re-add a silent no-op and fake a "removal is permanent" result.
    R9Sink.Reset();
    Apply($"r9-churn-{cycle}", MkLocal("Hot", hOff2, PidMid, 1));
    Thread.Sleep(200);
    if (R9Sink.Get(PidMid) > 0)
    {
        churnAddOk++;
    }
}

Console.WriteLine($"[churn] done in {sw.ElapsedMilliseconds} ms; re-add took effect in {churnAddOk}/{churn} cycles");
Assert("R9: no crash and NO wrong return value across the whole churn under load", badReturns == 0,
    $"badReturns={badReturns} over {Interlocked.Read(ref calls)} calls");

// Final removal, left removed. Then sample twice under load: the removed probe must be FROZEN while
// the survivors keep climbing. (Two samples, not one — a single sample cannot distinguish "stopped"
// from "slow".)
if (!negNoRemove)
{
    NativeMethods.RemoveLineProbe(PidMid);
}

Thread.Sleep(900);
long s1Mid = R9Sink.Get(PidMid), s1Low = R9Sink.Get(PidLow), s1High = R9Sink.Get(PidHigh);
Thread.Sleep(700);
long s2Mid = R9Sink.Get(PidMid), s2Low = R9Sink.Get(PidLow), s2High = R9Sink.Get(PidHigh);
Console.WriteLine($"[under-load] removed {PidMid}: {s1Mid} -> {s2Mid} | survivor {PidLow}: {s1Low} -> {s2Low} | survivor {PidHigh}: {s1High} -> {s2High}");

if (!negNoRemove)
{
    Assert("R9: removed probe is FROZEN under load (2 samples, no further fires)", s2Mid == s1Mid,
        $"{s1Mid} -> {s2Mid}");
}

Assert("R9: co-located survivors KEEP firing under load while a sibling is removed",
    s2Low > s1Low && s2High > s1High, $"{PidLow}: {s1Low}->{s2Low}, {PidHigh}: {s1High}->{s2High}");

stop.Cancel();
foreach (var th in workers)
{
    th.Join(5000);
}

Console.WriteLine($"[load] stopped after {Interlocked.Read(ref calls)} verified calls, badReturns={badReturns}");

// ---------------------------------------------------------------------------------------------
// STEP 5 — QUIESCENT EXACT-COUNT WINDOW. Load stopped, counters reset, exactly K single-threaded
// calls. A live probe must fire EXACTLY K times: `== K` detects a DROP (<K) and a DOUBLE-FIRE (>K),
// which is the precise R9 failure mode. Under load an exact count is unobtainable; that is why this
// window exists.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n---- STEP 5: quiescent exact-count window (drop AND double-fire detection) ----");
Thread.Sleep(500);
R9Sink.Reset();
const int K = 500;
for (int i = 0; i < K; i++)
{
    if (R9Targets.Hot(HotArg) != HotExpectedReturn)
    {
        Interlocked.Increment(ref badReturns);
    }
}

for (int i = 0; i < K; i++)
{
    _ = R9Targets.Untouched(1);
}

long qLow = R9Sink.Get(PidLow), qMid = R9Sink.Get(PidMid), qHigh = R9Sink.Get(PidHigh);
long qAdded = R9Sink.Get(PidAdded), qUnt = R9Sink.Get(PidUntouched);
Console.WriteLine($"[quiescent] K={K} -> {PidLow}={qLow} {PidMid}={qMid} {PidHigh}={qHigh} {PidAdded}={qAdded} untouched={qUnt}");

Assert($"R9: survivor {PidLow} fires EXACTLY once per call (no drop, no double-fire)", qLow == K, $"{qLow} != {K}");
Assert($"R9: survivor {PidHigh} fires EXACTLY once per call (no drop, no double-fire)", qHigh == K, $"{qHigh} != {K}");
Assert($"R9: survivors still read CORRECT locals after the re-ReJIT that excluded {PidMid}",
    Equals(R9Sink.LastValue.GetValueOrDefault(PidLow), ExpectA) &&
    Equals(R9Sink.LastValue.GetValueOrDefault(PidHigh), ExpectC),
    $"a={R9Sink.LastValue.GetValueOrDefault(PidLow)} c={R9Sink.LastValue.GetValueOrDefault(PidHigh)} (want {ExpectA}/{ExpectC})");
Assert("R9: cross-method non-interference (Untouched's probe unaffected)", qUnt == K, $"{qUnt} != {K}");
Assert("R9: method still returns correct value in the quiescent window", badReturns == 0, $"badReturns={badReturns}");

// THE PAIRED NEGATIVE CONTROL, evaluated on the same measurement.
if (negNoRemove)
{
    Assert($"NEG-1: with removal SKIPPED, probe {PidMid} STILL fires (silencing disappears)", qMid == K,
        $"{qMid} != {K} — if this is 0 the silencing was NOT caused by RemoveLineProbe");
}
else
{
    Assert($"R9: removed probe {PidMid} fires EXACTLY 0 times", qMid == 0, $"{qMid} != 0");
}

// ---------------------------------------------------------------------------------------------
// STEP 6 — remove ALL remaining probes: a re-ReJIT with an empty survivor set must restore the
// pristine body (nothing fires, method still correct).
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n---- STEP 6: remove all -> pristine body ----");
foreach (int pid in new[] { PidLow, PidHigh, PidAdded })
{
    NativeMethods.RemoveLineProbe(pid);
}

Thread.Sleep(1500);
R9Sink.Reset();
for (int i = 0; i < 300; i++)
{
    _ = R9Targets.Hot(HotArg);
}

long after = R9Sink.TotalFires;
Console.WriteLine($"[pristine] total fires after removing ALL probes = {after}");
if (!negNoRemove)
{
    Assert("R9: after removing all probes, NOTHING fires (pristine body restored)", after == 0, $"totalFires={after}");
}

Assert("R9: method returns correct value after full teardown", R9Targets.Hot(HotArg) == HotExpectedReturn,
    $"got {R9Targets.Hot(HotArg)}");

// ---------------------------------------------------------------------------------------------
// STEP 7 — GAP 2/3: MIXED EMISSION MODES in ONE method's single Import/Export.
// The fork's rewriter resolves the callback + emission mode ONCE from requests[0] (a documented
// prototype limitation). This step is the test that proves whether per-probe resolution works.
// Four probes, four DIFFERENT modes, deliberately ordered so requests[0] is NOT the mode the others
// need — if resolution is still per-method, the non-first probes are emitted with probe 0's callback
// and their distinctive side effects (gate calls, captured values) go missing.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n---- STEP 7: MIXED emission modes in one method (gap 2/3) ----");
R9Sink.Reset();
const int PidMixLegacy = 9201;  // mode 0 -> Probe(int32)
const int PidMixLocal = 9202;   // mode 3 -> CaptureLocal(int32, object), reads slot 1 (b)
const int PidMixGated = 9203;   // mode 1 -> ShouldCapture + Capture(int32, object)
const int PidMixUngated = 9204; // mode 2 -> Capture(int32, object), no gate
const int MixExpectB = 12;      // Mixed(1): a=2 b=12
const int GatedBoxValue = 777;
const int UngatedBoxValue = 888;

// Order matters: requests[0] is deliberately the LEGACY probe, whose callback is `Probe(int32)` —
// a DIFFERENT name and a DIFFERENT signature from what the other three need. If the rewriter still
// resolves callback+mode once from requests[0] (the documented prototype limitation), the other
// three get probe[0]'s one-arg callback and their distinctive side effects vanish.
Apply("r9-mixed",
    MkLegacy("Mixed", negBadOffsets ? BadFor("Mixed", mixedB[1]) : mixedB[1], PidMixLegacy),
    MkLocal("Mixed", ReadSlotAt("Mixed", mixedB, 1), PidMixLocal, 1),
    MkGated("Mixed", ReadSlotAt("Mixed", mixedB, 2), PidMixGated, GatedBoxValue),
    MkUngated("Mixed", ReadSlotAt("Mixed", mixedB, 3), PidMixUngated, UngatedBoxValue));
Thread.Sleep(1200);

const int MixCalls = 200;
for (int i = 0; i < MixCalls; i++)
{
    _ = R9Targets.Mixed(1);
}

long mLegacy = R9Sink.Get(PidMixLegacy), mLocal = R9Sink.Get(PidMixLocal);
long mGated = R9Sink.Get(PidMixGated), mUngated = R9Sink.Get(PidMixUngated);
Console.WriteLine($"[mixed] calls={MixCalls} fires: legacy({PidMixLegacy})={mLegacy} local({PidMixLocal})={mLocal} gated({PidMixGated})={mGated} ungated({PidMixUngated})={mUngated}");
Console.WriteLine($"[mixed] gateCalls={R9Sink.GateCalls} values: local={R9Sink.LastValue.GetValueOrDefault(PidMixLocal)} gated={R9Sink.LastValue.GetValueOrDefault(PidMixGated)} ungated={R9Sink.LastValue.GetValueOrDefault(PidMixUngated)}");

Assert("MIXED: all 4 probes fire exactly once per call",
    mLegacy == MixCalls && mLocal == MixCalls && mGated == MixCalls && mUngated == MixCalls,
    $"{mLegacy}/{mLocal}/{mGated}/{mUngated} (want {MixCalls} each)");
Assert("MIXED: LOCAL_CAPTURE probe read the real local (proves its mode was honored, not probe[0]'s)",
    Equals(R9Sink.LastValue.GetValueOrDefault(PidMixLocal), MixExpectB),
    $"got {R9Sink.LastValue.GetValueOrDefault(PidMixLocal)} want {MixExpectB}");
Assert("MIXED: GATED probe ran its gate (ShouldCapture invoked) — per-probe gate resolution",
    R9Sink.GateCalls >= MixCalls, $"gateCalls={R9Sink.GateCalls} < {MixCalls}");
Assert("MIXED: GATED probe boxed its own value",
    Equals(R9Sink.LastValue.GetValueOrDefault(PidMixGated), GatedBoxValue),
    $"got {R9Sink.LastValue.GetValueOrDefault(PidMixGated)} want {GatedBoxValue}");
Assert("MIXED: UNGATED probe boxed its own value",
    Equals(R9Sink.LastValue.GetValueOrDefault(PidMixUngated), UngatedBoxValue),
    $"got {R9Sink.LastValue.GetValueOrDefault(PidMixUngated)} want {UngatedBoxValue}");
Assert("MIXED: method still returns correct value with 4 mixed-mode probes woven",
    R9Targets.Mixed(1) == 1112, $"got {R9Targets.Mixed(1)}");

Console.WriteLine($"\n=== {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
Console.WriteLine("Corroborate against the profiler's own native log in ./logs (LineProbe_Rewrite lines).");
return failures;

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
}

// Mirror of NativeCallTargetDefinition's AllocHGlobal/Dispose discipline. Field order and stride
// MUST match LineProbeDefinition in the fork's line_probe.h exactly — a one-field disagreement is
// memory corruption, not a friendly error.
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

    // ADDED 2026-08-20 to match the product struct, which grew these two trailing fields for non-int
    // local capture (88 -> 104 bytes, 16 fields). WITHOUT THEM the native side reads localTypeName as a
    // pointer past the end of this struct and the process dies with SIGBUS inside AddLineProbes
    // (measured: KERN_PROTECTION_FAILURE at 0x0000000a00000002). Null + 0 reproduces the old int-only
    // behaviour, which is what this harness exercises.
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

        // Every local this harness probes is an int. LocalTypeName = null already MEANS System.Int32 to the
        // native side, but LocalIsValueType must be 1 or the `box` is suppressed entirely — and the callback
        // is `void CaptureLocal(int, object)`, so handing it a raw int32 is invalid IL. Measured with 0 here:
        // InvalidProgramException from the woven method, not a lost snapshot.
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
