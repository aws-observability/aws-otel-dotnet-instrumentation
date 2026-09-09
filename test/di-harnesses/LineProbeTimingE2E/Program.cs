// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// ============================================================================================
// LINE-PROBE TIMING HARNESS — measures the COST of Approach A (forked profiler + ReJIT).
//
// Answers the reviewer challenge against the "no pause" claim in
// poc/di-parity/LINE-LEVEL-ALTERNATIVES-ANALYSIS.md:114.
//
// Four phases:
//   P0  Instrument calibration        — timer overhead + heartbeat noise floor + POSITIVE CONTROL
//                                       (blocking GC.Collect, a known EE suspension) so we can prove
//                                       the heartbeat detector CAN see a real stop-the-world.
//   P1  Apply cost                    — N independent probe applies. Per apply we record:
//                                         (a) AddLineProbes P/Invoke duration (calling thread block)
//                                         (b) end-to-end apply latency (call -> probe first observed firing)
//                                         (c) max coincident heartbeat gap inside the apply window
//                                             (the SUSPENSION PROXY)
//   P2  First-hit de-opt              — latency of the individual call that first takes the rewritten
//                                       body (ReJIT recompile) vs the following steady-state calls.
//   P3  Steady-state per-hit overhead — ns/call of a woven probe vs an uninstrumented call, for
//                                       legacy sync / gated-discard / ungated-box, in a tight hot loop.
//
// SUSPENSION PROXY — what it is and what it is NOT:
//   Managed code cannot observe ThreadSuspend::SuspendEE directly. Instead TWO dedicated heartbeat
//   threads spin recording Stopwatch timestamps and log every delta above a threshold into a
//   PRE-ALLOCATED buffer (no allocation on the measuring path). A COINCIDENT gap — both threads
//   stalled over an overlapping interval — is the signature of a runtime-wide stop, because OS
//   descheduling of one spinning thread does not stall the other. A single-thread gap is OS noise.
//   The number therefore UPPER-BOUNDS the suspension: it includes any OS jitter that happens to
//   overlap. It does NOT prove causation, which is why P0 runs a blocking GC as a positive control.
// ============================================================================================

using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LineProbeSinkNs;

_ = LineProbeSink.CaptureCount;
GC.KeepAlive(typeof(LineProbeSink));

Console.WriteLine("=== DI Line-Probe TIMING harness (Approach A cost: ReJIT apply + per-hit) ===\n");

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] Native profiler not loaded. Run via run.sh.");
    return 99;
}

uint ilOffset = uint.Parse(Environment.GetEnvironmentVariable("LINEPROBE_IL_OFFSET") ?? "5");
int applyCount = int.Parse(Environment.GetEnvironmentVariable("LINEPROBE_APPLY_COUNT") ?? "40");
if (applyCount > Timing.MaxApplyTargets) { applyCount = Timing.MaxApplyTargets; }

const int EMIT_LEGACY = 0;
const int EMIT_GATED = 1;
const int EMIT_UNGATED = 2;

double tickNs = 1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
Console.WriteLine($"[env] Stopwatch.Frequency={System.Diagnostics.Stopwatch.Frequency:N0} Hz "
    + $"({tickNs:F3} ns/tick), IsHighResolution={System.Diagnostics.Stopwatch.IsHighResolution}, "
    + $"ProcessorCount={Environment.ProcessorCount}, Server GC={System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine($"[env] Runtime={RuntimeInformation.FrameworkDescription}, "
    + $"Arch={RuntimeInformation.ProcessArchitecture}, OS={RuntimeInformation.OSDescription}");
Console.WriteLine($"[env] IL offset={ilOffset}, apply samples={applyCount}\n");

// ============================================================================================
// P0 — CALIBRATION
// ============================================================================================
Console.WriteLine("---------- P0: calibration ----------");

// --- P0a: cost of the timestamp pair itself (subtracted from single-call measurements). ---
const int TimerCalibIters = 200_000;
var timerOverheadTicks = new double[TimerCalibIters];
for (int i = 0; i < TimerCalibIters; i++)
{
    long a = System.Diagnostics.Stopwatch.GetTimestamp();
    long b = System.Diagnostics.Stopwatch.GetTimestamp();
    timerOverheadTicks[i] = b - a;
}

double timerOverheadNs = Timing.Median(timerOverheadTicks) * tickNs;

// The SMALLEST NON-ZERO delta is the real granularity of the underlying clock. On Apple Silicon
// Stopwatch.Frequency reports 1 GHz but mach_absolute_time ticks at ~24 MHz, so many consecutive
// reads return the SAME value and any single measurement is quantised to this step. Every
// single-call latency below this step is unmeasurable and must be reported as "< granularity".
double granularityNs = double.MaxValue;
foreach (double t in timerOverheadTicks)
{
    if (t > 0 && (t * tickNs) < granularityNs) { granularityNs = t * tickNs; }
}

if (granularityNs == double.MaxValue) { granularityNs = 0; }

Console.WriteLine($"[P0a] GetTimestamp() pair overhead: median={timerOverheadNs:F1} ns "
    + $"(p99={Timing.Percentile(timerOverheadTicks, 99) * tickNs:F1} ns) over {TimerCalibIters:N0} pairs");
Console.WriteLine($"[P0a] EFFECTIVE CLOCK GRANULARITY (smallest non-zero delta) = {granularityNs:F2} ns "
    + $"-- single-call latencies below this are NOT measurable; use the P3 batch numbers instead.");
Timing.GranularityNs = granularityNs;

// --- P0b: start the two heartbeat threads (the suspension detector). ---
// Threshold: record any inter-sample gap above this. Low enough to see structure, high enough
// that the buffers do not fill with ordinary cache-miss noise.
double gapThresholdUs = double.Parse(Environment.GetEnvironmentVariable("LINEPROBE_GAP_US") ?? "20");
var hb1 = new Heartbeat("HB1", gapThresholdUs, tickNs);
var hb2 = new Heartbeat("HB2", gapThresholdUs, tickNs);
hb1.Start();
hb2.Start();
Thread.Sleep(200); // let both threads get scheduled and warm

// --- P0c: POSITIVE CONTROL — a blocking gen2 GC genuinely suspends the EE. If the heartbeat
//          detector cannot see THIS, it cannot see any stop-the-world and the P1 proxy is void.
//          A gen2 collect on a TINY heap finishes in microseconds, so first build a large LIVE
//          object graph: that forces real mark/relocate work and a suspension long enough to be
//          unambiguous. This calibrates the detector's real sensitivity. ---
var live = new List<object>(400_000);
for (int k = 0; k < 400_000; k++) { live.Add(new int[8]); }
GC.KeepAlive(live);

// The WORKER is the measurement that most directly answers the reviewer: a real application thread
// executing the very method being rewritten, in a tight loop, timing every batch. Whatever an app
// thread would actually feel at apply time shows up here.
var worker = new Worker(gapThresholdUs, tickNs);
worker.Start();
Thread.Sleep(200);

var gcGapsUs = new List<double>();
var gcWallUs = new List<double>();
var gcHb1Us = new List<double>();
var gcHb2Us = new List<double>();
var gcTruePauseUs = new List<double>();  // GROUND TRUTH from the runtime itself
var gcWorkerUs = new List<double>();     // what the worker (app thread) actually felt
const int GcControlReps = 20;
for (int r = 0; r < GcControlReps; r++)
{
    TimeSpan p0 = GC.GetTotalPauseDuration();
    long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    long g1 = System.Diagnostics.Stopwatch.GetTimestamp();
    TimeSpan p1 = GC.GetTotalPauseDuration();

    // GROUND TRUTH: the runtime's own accounting of how long it had the EE suspended.
    gcTruePauseUs.Add((p1 - p0).TotalMilliseconds * 1000.0);

    var g = Heartbeat.CoincidentGaps(hb1, hb2, g0, g1);
    gcGapsUs.Add(g.Count == 0 ? 0 : g.Max());
    var s1 = Heartbeat.SingleGaps(hb1, g0, g1);
    var s2 = Heartbeat.SingleGaps(hb2, g0, g1);
    gcHb1Us.Add(s1.Count == 0 ? 0 : s1.Max());
    gcHb2Us.Add(s2.Count == 0 ? 0 : s2.Max());
    var w = worker.StallsIn(g0, g1);
    gcWorkerUs.Add(w.Count == 0 ? 0 : w.Max());
    gcWallUs.Add((g1 - g0) * tickNs / 1000.0);
    Thread.Sleep(30);
}

Console.WriteLine($"[P0b] GROUND TRUTH (GC.GetTotalPauseDuration) per blocking gen2 GC: "
    + $"median={Timing.Median(gcTruePauseUs.ToArray()):F1} us, p99={Timing.Percentile(gcTruePauseUs.ToArray(), 99):F1} us, "
    + $"max={gcTruePauseUs.Max():F1} us");

Console.WriteLine($"[P0c] POSITIVE CONTROL — blocking gen2 GC.Collect x{GcControlReps} over a "
    + $"{live.Count:N0}-object live heap (a REAL EE suspension):");
Console.WriteLine($"      GC wall time (calling thread): median={Timing.Median(gcWallUs.ToArray()):F1} us, "
    + $"max={gcWallUs.Max():F1} us");
Console.WriteLine($"      max coincident heartbeat gap per GC: median={Timing.Median(gcGapsUs.ToArray()):F1} us, "
    + $"p99={Timing.Percentile(gcGapsUs.ToArray(), 99):F1} us, max={gcGapsUs.Max():F1} us, "
    + $"detected in {gcGapsUs.Count(x => x > 0)}/{GcControlReps} collections");
Console.WriteLine($"      SINGLE-thread gap per GC: HB1 median={Timing.Median(gcHb1Us.ToArray()):F1} us "
    + $"(detected {gcHb1Us.Count(x => x > 0)}/{GcControlReps}), "
    + $"HB2 median={Timing.Median(gcHb2Us.ToArray()):F1} us (detected {gcHb2Us.Count(x => x > 0)}/{GcControlReps})");
bool detectorWorks = gcGapsUs.Count(x => x > 0) >= GcControlReps / 2;
bool singleWorks = gcHb1Us.Count(x => x > 0) >= GcControlReps / 2
    && gcHb2Us.Count(x => x > 0) >= GcControlReps / 2;
Console.WriteLine($"      WORKER-thread stall per GC (app thread calling the target): "
    + $"median={Timing.Median(gcWorkerUs.ToArray()):F1} us, max={gcWorkerUs.Max():F1} us "
    + $"(detected {gcWorkerUs.Count(x => x > 0)}/{GcControlReps})");
bool workerWorks = gcWorkerUs.Count(x => x > 0) >= GcControlReps / 2;
Console.WriteLine($"      => COINCIDENT detector {(detectorWorks ? "CAN" : "CANNOT")} observe a known "
    + $"stop-the-world at the {gapThresholdUs:F0}us threshold.");
Console.WriteLine($"      => SINGLE-thread detector {(singleWorks ? "CAN" : "CANNOT")} observe it.");
Console.WriteLine($"      => WORKER-thread detector {(workerWorks ? "CAN" : "CANNOT")} observe it. "
    + "<== this is the detector whose null result would be meaningful for P1");

live.Clear();
live = null!;
GC.Collect(2, GCCollectionMode.Forced, blocking: true);
GC.WaitForPendingFinalizers();

// --- P0d: NOISE FLOOR — PAIRED control windows. Each is the same duration as a typical apply
//          window AND has the main thread doing the same spin-calling work, but with NO probe
//          applied. This is the apples-to-apples comparison for P1c: any coincident gap seen here
//          is pure OS/runtime noise, not probe application. ---
var ctrlWindowGapUs = new List<double>();
var ctrlWindowSingleUs = new List<double>();
for (int r = 0; r < 40; r++)
{
    long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
    for (int k = 0; k < 200_000; k++) { Timing.Sink += TimingTargets.SteadyBaseline(k); }
    long c1 = System.Diagnostics.Stopwatch.GetTimestamp();
    var g = Heartbeat.CoincidentGaps(hb1, hb2, c0, c1);
    ctrlWindowGapUs.Add(g.Count == 0 ? 0 : g.Max());
    var s1 = Heartbeat.SingleGaps(hb1, c0, c1);
    var s2 = Heartbeat.SingleGaps(hb2, c0, c1);
    double sMax = Math.Max(s1.Count == 0 ? 0 : s1.Max(), s2.Count == 0 ? 0 : s2.Max());
    ctrlWindowSingleUs.Add(sMax);
    Thread.Sleep(60);
}

Timing.Report("[P0d] NOISE FLOOR: max coincident gap in a NO-PROBE control window", ctrlWindowGapUs, "us");
Timing.Report("[P0d'] NOISE FLOOR: max SINGLE-thread gap in a NO-PROBE control window", ctrlWindowSingleUs, "us");
double ctrlMaxUs = ctrlWindowGapUs.Max();
Console.WriteLine($"      control windows showing ANY coincident gap >{gapThresholdUs:F0}us: "
    + $"{ctrlWindowGapUs.Count(x => x > 0)}/{ctrlWindowGapUs.Count}\n");

// ============================================================================================
// P1 — APPLY COST (the crux: does applying a line probe pause the app, and for how long?)
// ============================================================================================
Console.WriteLine("---------- P1: probe apply cost ----------");
LineProbeSink.MaxHits = int.MaxValue; // irrelevant for legacy mode, but keep the gate permissive

var pinvokeUs = new List<double>();       // (a) how long the CALLING thread is blocked in AddLineProbes
var endToEndUs = new List<double>();      // (b) AddLineProbes entry -> probe observed firing
var applyWindowGapUs = new List<double>(); // (c) max coincident heartbeat gap in the apply window
var applyWindowSingleUs = new List<double>(); // (c') max single-thread gap (more sensitive diagnostic)
var spinCallsToEffect = new List<double>();
var firstHitNs = new List<double>();       // P2: the individual call that first took the new body
var steadyAfterNs = new List<double>();    // P2: median of calls 2..51 after the switch
var preApplyNs = new List<double>();       // P2: same method, same measurement, BEFORE the probe
var baselineBatchNs = new List<double>(); // P2: BATCH per-call latency before apply (above granularity)
var steadyBatchNs = new List<double>();   // P2: BATCH per-call latency after apply (above granularity)

int applied = 0;
int failedToFire = 0;

for (int i = 0; i < applyCount; i++)
{
    int probeId = 9000 + i;

    // Warm the target so it is already JIT-compiled (ReJIT of a never-jitted method would
    // conflate first-JIT with re-JIT), and capture a BEFORE per-call latency baseline for P2.
    for (int w = 0; w < 20_000; w++) { Timing.Sink += TimingTargets.ApplyTargetByIndex(i, w); }
    var before = new double[200];
    for (int s = 0; s < before.Length; s++)
    {
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        Timing.Sink += TimingTargets.ApplyTargetByIndex(i, s);
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        before[s] = ((b - a) * tickNs) - timerOverheadNs;
    }

    preApplyNs.Add(Timing.Median(before));

    // BATCH baseline: 2,000 calls timed as one interval, so the total is far above clock
    // granularity and the per-call figure is real (unlike the quantised single-call number).
    {
        long ba = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int s = 0; s < 2_000; s++) { Timing.Sink += TimingTargets.ApplyTargetByIndex(i, s); }
        long bb = System.Diagnostics.Stopwatch.GetTimestamp();
        baselineBatchNs.Add((bb - ba) * tickNs / 2_000.0);
    }

    int fireBefore = LineProbeSink.FireCount;

    var def = new NativeLineProbeDefinition(
        targetAssembly: "LineProbeTimingE2E",
        targetType: "TimingTargets",
        targetMethod: "ApplyTarget" + i,
        targetSignatureTypes: new[] { "System.Int32", "System.Int32" },
        ilOffset: ilOffset,
        probeId: probeId,
        callbackAssembly: "LineProbeSink",
        callbackType: "LineProbeSinkNs.LineProbeSink",
        callbackMethod: "Probe",
        emissionMode: EMIT_LEGACY,
        boxValue: 0,
        gateMethod: null);

    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
        var arr = new[] { def };
        NativeMethods.AddLineProbes("timing-apply-" + i, arr, arr.Length);
    }
    finally { def.Dispose(); }

    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

    // Spin-call the target until the probe is observed firing. Each individual call is timed so
    // the call that FIRST takes the rewritten body (which is where a ReJIT recompile would land)
    // is captured for P2. Bounded so a non-firing probe cannot hang the run.
    const int MaxSpin = 400_000;
    var spinNs = new double[512];
    int spin = 0;
    long tFire = 0;
    int fireIdx = -1;
    for (; spin < MaxSpin; spin++)
    {
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        Timing.Sink += TimingTargets.ApplyTargetByIndex(i, spin);
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        if (spin < spinNs.Length) { spinNs[spin] = ((b - a) * tickNs) - timerOverheadNs; }
        if (LineProbeSink.FireCount != fireBefore) { tFire = b; fireIdx = spin; break; }
    }

    if (fireIdx < 0)
    {
        failedToFire++;
        Console.WriteLine($"[P1] apply #{i}: probe NEVER fired within {MaxSpin:N0} calls — sample discarded");
        continue;
    }

    applied++;
    double pinvoke = (t1 - t0) * tickNs / 1000.0;
    double e2e = (tFire - t0) * tickNs / 1000.0;
    pinvokeUs.Add(pinvoke);
    endToEndUs.Add(e2e);
    spinCallsToEffect.Add(fireIdx + 1);

    var gaps = Heartbeat.CoincidentGaps(hb1, hb2, t0, tFire);
    applyWindowGapUs.Add(gaps.Count == 0 ? 0 : gaps.Max());
    var as1 = Heartbeat.SingleGaps(hb1, t0, tFire);
    var as2 = Heartbeat.SingleGaps(hb2, t0, tFire);
    applyWindowSingleUs.Add(Math.Max(as1.Count == 0 ? 0 : as1.Max(), as2.Count == 0 ? 0 : as2.Max()));

    // --- P2: first-hit de-opt. fireIdx is the call that first executed the rewritten body. ---
    if (fireIdx < spinNs.Length) { firstHitNs.Add(spinNs[fireIdx]); }

    var after = new double[200];
    for (int s = 0; s < after.Length; s++)
    {
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        Timing.Sink += TimingTargets.ApplyTargetByIndex(i, s);
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        after[s] = ((b - a) * tickNs) - timerOverheadNs;
    }

    steadyAfterNs.Add(Timing.Median(after));

    // BATCH steady state AFTER the rewrite — the honest comparison target for the first-hit cost.
    {
        long sa = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int s = 0; s < 2_000; s++) { Timing.Sink += TimingTargets.ApplyTargetByIndex(i, s); }
        long sb = System.Diagnostics.Stopwatch.GetTimestamp();
        steadyBatchNs.Add((sb - sa) * tickNs / 2_000.0);
    }

    Thread.Sleep(60); // idle gap so consecutive apply windows do not bleed into each other
}

Console.WriteLine($"[P1] applies measured: {applied}/{applyCount} (failed to fire: {failedToFire})");
if (applied == 0)
{
    Console.WriteLine("[FATAL] no probe ever fired — the fork is not weaving. Aborting.");
    hb1.Stop(); hb2.Stop();
    return 98;
}

Timing.Report("[P1a] AddLineProbes() P/Invoke duration (CALLING thread blocked)", pinvokeUs, "us");
Timing.Report("[P1b] end-to-end apply latency (AddLineProbes entry -> probe observed firing)", endToEndUs, "us");
Timing.Report("[P1c] SUSPENSION PROXY: max COINCIDENT heartbeat gap inside apply window", applyWindowGapUs, "us");
Timing.Report("[P1c'] more-sensitive diagnostic: max SINGLE-thread gap inside apply window", applyWindowSingleUs, "us");
Timing.Report("[P1d] target calls needed before the rewritten body took effect", spinCallsToEffect, "calls");

int windowsWithAnyGap = applyWindowGapUs.Count(x => x > 0);
Console.WriteLine($"[P1c] apply windows showing ANY coincident gap >{gapThresholdUs:F0}us: "
    + $"{windowsWithAnyGap}/{applied}");
Console.WriteLine($"[P1c] noise-floor comparison: control-window max was {ctrlMaxUs:F1} us; "
    + $"apply-window max was {applyWindowGapUs.Max():F1} us");
// --- P1e: THE DIRECT TEST. Apply a probe to SteadyWorker while the Worker thread is executing it
//          in a tight loop, and report what that thread actually felt. This is the closest thing to
//          the production question: "if I turn on a line probe on a hot method, does my app stall?" ---
Console.WriteLine();
Console.WriteLine("---------- P1e: apply a probe to a method an app thread is ACTIVELY executing ----------");
Console.WriteLine($"[P1e] worker batches completed before apply: {worker.Batches:N0} "
    + $"({Worker.BatchSizeConst} calls/batch), stalls>{gapThresholdUs:F0}us so far: {worker.StallCount:N0}");

var wkPinvokeUs = new List<double>();
var wkToEffectUs = new List<double>();
var wkMaxStallUs = new List<double>();
var wkAllStallsUs = new List<double>();
int wkFired = 0;

// PAIRED CONTROL: the worker records stalls all the time from ordinary OS scheduling (it logged
// stalls before this phase even began). So for every rep we FIRST watch an equal-length window with
// NO apply in it, then do the apply. Only the DIFFERENCE between the two is attributable to the
// probe. Without this the P1e number would just be measuring macOS scheduling noise.
var wkCtrlMaxStallUs = new List<double>();
var wkCtrlWindowUs = new List<double>();
double ctrlWindowTargetUs = 1500; // seeded; re-estimated from the previous rep's real apply window

// Leave the last BulkReserved SteadyWorker methods UNPROBED so P4 has genuinely fresh live
// targets. Without this P1e consumes all 12 and P4's "live" method is already woven, making its
// apply a no-op that reports a 0 us stall for the wrong reason.
const int BulkReserved = 5;
for (int t = 0; t < TimingTargets.WorkerTargetCount - BulkReserved; t++)
{
    worker.Target = t;
    Thread.Sleep(150); // let the worker settle onto this target (it is now hot and unprobed)

    // --- control window: same duration, same worker, same target, NO apply ---
    long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
    var swCtrl = System.Diagnostics.Stopwatch.StartNew();
    while (swCtrl.Elapsed.TotalMicroseconds < ctrlWindowTargetUs) { Thread.Sleep(1); }
    long c1 = System.Diagnostics.Stopwatch.GetTimestamp();
    var ctrlStalls = worker.StallsIn(c0, c1);
    wkCtrlMaxStallUs.Add(ctrlStalls.Count == 0 ? 0 : ctrlStalls.Max());
    wkCtrlWindowUs.Add((c1 - c0) * tickNs / 1000.0);
    Thread.Sleep(50);

    int fireBefore = LineProbeSink.FireCount;
    var wdef = new NativeLineProbeDefinition(
        "LineProbeTimingE2E", "TimingTargets", "SteadyWorker" + t,
        new[] { "System.Int32", "System.Int32" }, ilOffset, 7100 + t,
        "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Probe", EMIT_LEGACY, 0, null);

    long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
        var arr = new[] { wdef };
        NativeMethods.AddLineProbes("timing-worker-" + t, arr, arr.Length);
    }
    finally { wdef.Dispose(); }

    long w1 = System.Diagnostics.Stopwatch.GetTimestamp();

    // Wait until the WORKER's own calls are observed firing the probe. The main thread does not
    // touch the target at all, so the rewrite must land on the worker's hot path.
    var swFire = System.Diagnostics.Stopwatch.StartNew();
    while (LineProbeSink.FireCount == fireBefore && swFire.ElapsedMilliseconds < 5000)
    {
        Thread.Sleep(1);
    }

    long w2 = System.Diagnostics.Stopwatch.GetTimestamp();
    if (LineProbeSink.FireCount != fireBefore) { wkFired++; }
    Thread.Sleep(100); // let the worker record any stall that straddles the rewrite

    var stalls = worker.StallsIn(w0, w2);
    wkPinvokeUs.Add((w1 - w0) * tickNs / 1000.0);
    wkToEffectUs.Add((w2 - w0) * tickNs / 1000.0);
    wkMaxStallUs.Add(stalls.Count == 0 ? 0 : stalls.Max());
    wkAllStallsUs.AddRange(stalls);

    // Match the NEXT rep's control window to this rep's real apply window.
    ctrlWindowTargetUs = (w2 - w0) * tickNs / 1000.0;
}

Console.WriteLine($"[P1e] reps={TimingTargets.WorkerTargetCount}, probe observed firing VIA THE WORKER "
    + $"thread in {wkFired}/{TimingTargets.WorkerTargetCount} reps");
Timing.Report("[P1e-a] AddLineProbes P/Invoke (calling thread)", wkPinvokeUs, "us");
Timing.Report("[P1e-b] apply -> worker observed executing rewritten body", wkToEffectUs, "us");
Timing.Report("[P1e-c] MAX WORKER-THREAD STALL in the APPLY window (the headline number)", wkMaxStallUs, "us");
Timing.Report("[P1e-d] MAX WORKER-THREAD STALL in a matched NO-APPLY CONTROL window", wkCtrlMaxStallUs, "us");
Timing.Report("[P1e-d] control window durations (should match P1e-b)", wkCtrlWindowUs, "us");
Console.WriteLine($"[P1e-c] reps with ANY worker stall >{gapThresholdUs:F0}us: "
    + $"APPLY {wkMaxStallUs.Count(x => x > 0)}/{wkMaxStallUs.Count}, "
    + $"CONTROL {wkCtrlMaxStallUs.Count(x => x > 0)}/{wkCtrlMaxStallUs.Count}; "
    + $"total stall events across all apply windows: {wkAllStallsUs.Count}");
Console.WriteLine($"[P1e-e] ATTRIBUTABLE TO APPLY = apply-window median MINUS control-window median = "
    + $"{Timing.Median(wkMaxStallUs.ToArray()) - Timing.Median(wkCtrlMaxStallUs.ToArray()):F1} us");
Console.WriteLine($"[P1e-c] for scale: the SAME worker thread recorded "
    + $"{Timing.Median(gcWorkerUs.ToArray()):F0} us median stalls for a blocking gen2 GC (P0c).");

Console.WriteLine();

// ============================================================================================
// P2 — FIRST-HIT DE-OPT
// ============================================================================================
Console.WriteLine("---------- P2: first-hit de-opt (ReJIT recompile on first call of new body) ----------");
Console.WriteLine($"[P2!] NOTE: a single call takes ~{Timing.Median(baselineBatchNs.ToArray()):F1} ns, well BELOW the "
    + $"{granularityNs:F1} ns clock granularity, so P2a/P2c single-call medians quantise to 0. They are "
    + "reported only to establish that the ordinary path is UNMEASURABLY fast; the P2b first-hit "
    + "value is hundreds of granularity steps wide and IS meaningful.");
Timing.Report("[P2a] pre-apply single-call latency (same method, no probe)", preApplyNs, "ns");
Timing.Report("[P2a'] pre-apply BATCH per-call latency (2,000-call batch, above granularity)", baselineBatchNs, "ns/call");
Timing.Report("[P2b] FIRST call that took the rewritten body", firstHitNs, "ns");
Timing.Report("[P2c] post-apply single-call latency (calls 2..201)", steadyAfterNs, "ns");
Timing.Report("[P2c'] post-apply BATCH per-call latency (2,000-call batch, above granularity)", steadyBatchNs, "ns/call");
if (firstHitNs.Count > 0 && steadyBatchNs.Count > 0)
{
    double dp = Timing.Median(firstHitNs.ToArray()) - Timing.Median(steadyBatchNs.ToArray());
    Console.WriteLine($"[P2d] first-hit EXCESS over post-apply steady state: median {dp:F0} ns "
        + $"(= {dp / 1000.0:F2} us) — ONE-TIME, per method, paid by whichever thread happens to make "
        + "the first call after the rewrite");
}

Console.WriteLine();

// ============================================================================================
// P3 — STEADY-STATE PER-HIT OVERHEAD
// ============================================================================================
Console.WriteLine("---------- P3: steady-state per-hit overhead ----------");

// STOP the heartbeat and worker threads FIRST. They exist only to detect the P1 suspension; if they
// keep running they contaminate P3 badly: the worker calls Probe, whose Interlocked.Increment on
// LineProbeSink.FireCount FALSE-SHARES a cache line with the gate's ShouldCaptureCalls counter. With
// the worker live, the GATED path measured SLOWER than the UNGATED one (57 vs 24 ns/call) — pure
// cross-core contention on the sink's statics, not probe cost. P3 must be single-threaded.
hb1.Stop();
hb2.Stop();
worker.Stop();
Console.WriteLine("[P3] heartbeat + worker threads stopped so per-hit numbers are uncontended.");
Thread.Sleep(100);

// Apply three probes to three structurally identical methods; leave SteadyBaseline unprobed.
LineProbeSink.MaxHits = 0; // gate always FALSE => the GATED target always takes the discard path

var legacyDef = new NativeLineProbeDefinition(
    "LineProbeTimingE2E", "TimingTargets", "SteadyLegacy",
    new[] { "System.Int32", "System.Int32" }, ilOffset, 7001,
    "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Probe", EMIT_LEGACY, 0, null);
var gatedDef = new NativeLineProbeDefinition(
    "LineProbeTimingE2E", "TimingTargets", "SteadyGated",
    new[] { "System.Int32", "System.Int32" }, ilOffset, 7002,
    "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Capture", EMIT_GATED, 111, "ShouldCapture");
var ungatedDef = new NativeLineProbeDefinition(
    "LineProbeTimingE2E", "TimingTargets", "SteadyUngated",
    new[] { "System.Int32", "System.Int32" }, ilOffset, 7003,
    "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Capture", EMIT_UNGATED, 222, null);

try
{
    var arr = new[] { legacyDef, gatedDef, ungatedDef };
    NativeMethods.AddLineProbes("timing-steady", arr, arr.Length);
}
finally { legacyDef.Dispose(); gatedDef.Dispose(); ungatedDef.Dispose(); }

// Force the ReJIT to land, then verify each probe is genuinely active before timing it.
for (int r = 0; r < 200 && !Timing.SteadyProbesActive(); r++)
{
    for (int k = 0; k < 2_000; k++)
    {
        Timing.Sink += TimingTargets.SteadyLegacy(k);
        Timing.Sink += TimingTargets.SteadyGated(k);
        Timing.Sink += TimingTargets.SteadyUngated(k);
    }

    Thread.Sleep(10);
}

Console.WriteLine($"[P3] probe activity check: legacy(Probe) fired={Timing.LegacyActive}, "
    + $"gate(ShouldCapture) ran={LineProbeSink.ShouldCaptureCalls > 0}, "
    + $"ungated Capture fired={LineProbeSink.CaptureCount > 0}");

// Tight hot loop: R reps x N calls. Per-rep ns/call, then median/p99 across reps.
const int Reps = 41;
const int N = 200_000;

var baseNs = new double[Reps];
var legNs = new double[Reps];
var gatNs = new double[Reps];
var ungNs = new double[Reps];

// Interleave the four variants rep-by-rep so thermal/frequency drift hits them equally.
// DIRECT calls, not delegates: a Func<int,int> invoke costs about as much as the whole probe, which
// would sit in the baseline AND every variant and compress the measured deltas.
for (int r = 0; r < Reps; r++)
{
    baseNs[r] = Timing.LoopBaseline(N, tickNs);
    legNs[r] = Timing.LoopLegacy(N, tickNs);
    gatNs[r] = Timing.LoopGated(N, tickNs);
    ungNs[r] = Timing.LoopUngated(N, tickNs);
}

Console.WriteLine($"[P3] hot loop: {Reps} reps x {N:N0} calls each = {(long)Reps * N:N0} calls per variant");
Timing.Report("[P3a] UNPROBED baseline          ", baseNs.ToList(), "ns/call");
Timing.Report("[P3b] LEGACY sync probe (Probe)  ", legNs.ToList(), "ns/call");
Timing.Report("[P3c] GATED discard path         ", gatNs.ToList(), "ns/call");
Timing.Report("[P3d] UNGATED always-box path    ", ungNs.ToList(), "ns/call");

double mb = Timing.Median(baseNs);
Console.WriteLine($"[P3e] per-hit OVERHEAD vs unprobed baseline (median-of-{Reps}-reps deltas):");
Console.WriteLine($"        LEGACY  sync : {Timing.Median(legNs) - mb,8:F2} ns/call");
Console.WriteLine($"        GATED discard: {Timing.Median(gatNs) - mb,8:F2} ns/call");
Console.WriteLine($"        UNGATED box  : {Timing.Median(ungNs) - mb,8:F2} ns/call");

// Allocation cross-check (reproduces the LineProbeGatedE2E result on these targets).
const int AllocN = 200_000;
long ab0 = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < AllocN; i++) { Timing.Sink += TimingTargets.SteadyGated(i); }
long ab1 = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < AllocN; i++) { Timing.Sink += TimingTargets.SteadyUngated(i); }
long ab2 = GC.GetAllocatedBytesForCurrentThread();
Console.WriteLine($"[P3f] allocation: GATED discard={(double)(ab1 - ab0) / AllocN:F3} B/call, "
    + $"UNGATED box={(double)(ab2 - ab1) / AllocN:F3} B/call");

// ============================================================================================
// P4: BULK APPLY — the last unmeasured A risk.
//
// Everything above applies ONE probe per AddLineProbes call. Real config changes apply MANY at
// once, so the open question is whether cost is N x ~131 us (linear -> 94 probes = one 60Hz frame
// of stall) or amortised (one batched ReJIT request -> far cheaper per probe).
//
// Structurally it CAN amortise: cor_profiler.cpp AddLineProbes builds the whole
// std::vector<LineProbeRequest> and makes a SINGLE EnqueueRequestRejitForLoadedModules call for
// the entire array. This phase measures whether that theoretical amortisation is real.
//
// Method: apply a batch of N probes in ONE AddLineProbes call to N distinct fresh ApplyTargetN
// methods, while the Worker thread is executing one of them, and measure (a) the P/Invoke block on
// the calling thread, (b) the max Worker stall inside the apply window, (c) per-probe cost. Each
// batch size is paired against a matched no-apply control window, exactly as P1e does.
// ============================================================================================
Console.WriteLine();
Console.WriteLine("---------- P4: BULK APPLY (N probes in ONE AddLineProbes call) ----------");
Console.WriteLine("  Question: is bulk apply linear in N (N x ~131us) or amortised by the batched ReJIT?");
Console.WriteLine();

int[] batchSizes = { 1, 2, 5, 10, 20, 32 };
int bulkTargetCursor = 0;   // ApplyTargetN methods are single-use: each must be freshly unprobed.
int bulkWorkerTarget = TimingTargets.WorkerTargetCount - BulkReserved; // reserved, still unprobed
double bulkCtrlTargetUs = 3000; // matched to the apply->effect window; re-estimated each rep

Console.WriteLine("| batch N | P/Invoke us | per-probe us | worker stall APPLY us | worker stall CONTROL us | probes fired | window us |");
Console.WriteLine("|---|---|---|---|---|---|---|");

foreach (var n in batchSizes)
{
    if (bulkTargetCursor + (n - 1) > 40 || bulkWorkerTarget >= TimingTargets.WorkerTargetCount)
    {
        Console.WriteLine($"| {n} | SKIPPED — out of fresh targets ({40 - bulkTargetCursor} ApplyTarget, "
            + $"{TimingTargets.WorkerTargetCount - bulkWorkerTarget} SteadyWorker left) |");
        continue;
    }

    // Matched control window first: same worker, same duration class, NO apply.
    long bc0 = System.Diagnostics.Stopwatch.GetTimestamp();
    var swBulkCtrl = System.Diagnostics.Stopwatch.StartNew();
    while (swBulkCtrl.Elapsed.TotalMicroseconds < bulkCtrlTargetUs) { Thread.Sleep(1); }
    long bc1 = System.Diagnostics.Stopwatch.GetTimestamp();
    var bulkCtrlStalls = worker.StallsIn(bc0, bc1);
    double bulkCtrlMax = bulkCtrlStalls.Count == 0 ? 0 : bulkCtrlStalls.Max();
    Thread.Sleep(50);

    // Build N definitions: N-1 fresh ApplyTargetN methods PLUS the SteadyWorker method the Worker
    // thread is hammering right now. Including the live method is essential — a batch aimed only at
    // idle methods produces a 0 us worker stall for the trivial reason that the worker was never
    // executing any of them (the same trap P1c fell into). One live method in the batch makes the
    // stall observable while still measuring the real N-probe apply.
    int liveWorkerTarget = bulkWorkerTarget;
    worker.Target = liveWorkerTarget;

    // CRITICAL: a method that has never been CALLED has never been JIT-compiled, so there is nothing
    // for RequestReJIT to re-compile and the profiler skips it ("Request ReJIT done for 1 methods" on
    // a 20-probe batch). Pre-warm EVERY method in the batch so all N are genuinely JIT'd and the
    // apply does N real rewrites. Without this, P4 measures bookkeeping, not ReJIT.
    for (int warm = 0; warm < 200; warm++)
    {
        for (int k = 1; k < n; k++)
        {
            Timing.Sink += TimingTargets.ApplyTargetByIndex(bulkTargetCursor + k - 1, warm);
        }
    }

    Thread.Sleep(150); // let the worker settle onto this (now hot, still unprobed) method

    var defs = new NativeLineProbeDefinition[n];
    int firstIdx = bulkTargetCursor;
    defs[0] = new NativeLineProbeDefinition(
        "LineProbeTimingE2E", "TimingTargets", "SteadyWorker" + liveWorkerTarget,
        new[] { "System.Int32", "System.Int32" }, ilOffset, 7500 + liveWorkerTarget,
        "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Probe", EMIT_LEGACY, 0, null);
    for (int k = 1; k < n; k++)
    {
        defs[k] = new NativeLineProbeDefinition(
            "LineProbeTimingE2E", "TimingTargets", "ApplyTarget" + (bulkTargetCursor + k - 1),
            new[] { "System.Int32" }, ilOffset, 7300 + bulkTargetCursor + k,
            "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Probe", EMIT_LEGACY, 0, null);
    }

    int bulkFireBefore = LineProbeSink.FireCount;
    long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
        // THE MEASUREMENT: all N probes in a single native call.
        NativeMethods.AddLineProbes("timing-bulk-" + n, defs, defs.Length);
    }
    finally
    {
        foreach (var d in defs) { d.Dispose(); }
    }

    long b1 = System.Diagnostics.Stopwatch.GetTimestamp();

    // Drive every method in the batch so we can confirm the rewrites actually landed. A cheap
    // apply that silently wove nothing would otherwise look like a win.
    var swBulkFire = System.Diagnostics.Stopwatch.StartNew();
    int fired = 0;
    while (swBulkFire.ElapsedMilliseconds < 5000)
    {
        // defs[0] is the live SteadyWorker method (driven by the Worker thread); defs[1..] are the
        // fresh ApplyTarget methods, which only fire if we call them here.
        for (int k = 1; k < n; k++) { Timing.Sink += TimingTargets.ApplyTargetByIndex(firstIdx + k - 1, k); }
        fired = LineProbeSink.FireCount - bulkFireBefore;
        if (fired >= n) { break; }
        Thread.Sleep(1);
    }

    // Stall window must span b0 -> effect-landed, NOT just b0 -> b1. AddLineProbes enqueues to a
    // background offloader and waits only ~100ms for the REQUEST; the actual ReJIT completes
    // asynchronously afterwards, so the suspension lands AFTER the P/Invoke returns. Measuring only
    // the P/Invoke window reports 0.0 us for exactly the wrong reason (P1e uses the wider window,
    // which is why it sees ~125 us while an earlier version of this phase saw nothing).
    long b2 = System.Diagnostics.Stopwatch.GetTimestamp();
    var bulkStalls = worker.StallsIn(b0, b2);
    double bulkMax = bulkStalls.Count == 0 ? 0 : bulkStalls.Max();
    double pinvUs = (b1 - b0) * tickNs / 1000.0;
    double windowUs = (b2 - b0) * tickNs / 1000.0;

    // The live SteadyWorker method fires continuously once woven, so FireCount overcounts. Cap the
    // reported figure at n; what matters is that it REACHED n (every probe in the batch landed).
    int firedCapped = fired > n ? n : fired;

    Console.WriteLine($"| {n} | {pinvUs:F1} | {pinvUs / n:F1} | {bulkMax:F1} | {bulkCtrlMax:F1} | "
        + $"{firedCapped}/{n} | {windowUs:F0} |");

    bulkCtrlTargetUs = windowUs;   // next rep's control window matches this rep's real window
    bulkTargetCursor += n - 1;   // defs[0] came from the SteadyWorker pool, not ApplyTarget
    bulkWorkerTarget++;
    Thread.Sleep(100);
}

Console.WriteLine();
Console.WriteLine("  Read the 'per-probe us' column: FLAT => linear cost (no amortisation);");
Console.WriteLine("  FALLING as N grows => the batched ReJIT request amortises, and bulk apply is safe.");
Console.WriteLine("  'probes fired' must equal N — a cheap apply that wove nothing is not a win.");

// ============================================================================================
// N2 EXPERIMENT: can N line probes coexist in ONE method? Decomposed to separate three failure
// modes that the earlier P5 conflated (it fired 0/3 AND got 0 ReJITs — an offset failure masking
// the real question).
//
//   Step A: ONE probe at a VERIFIED statement boundary  -> proves offset + weave on THIS method.
//   Step B: a SECOND probe at a DIFFERENT boundary, same method, separate AddLineProbes call.
//   Step C: TWO probes in ONE AddLineProbes call.
// Compare distinct probeIds fired + native "Request ReJIT done for N" to attribute the outcome.
//
// MultiProbeTarget has 9 locals; statement boundaries (right after each stloc) are at IL offsets
// 5, 9, 13, 17, 20, 26, 32, 38 (dumped from the real IL, NOT guessed). We use 5 and 9.
// ============================================================================================
Console.WriteLine();
Console.WriteLine("---------- N2: multi-probe-per-method (decomposed) ----------");

// Pre-warm so RequestReJIT has JIT'd code to recompile (the lesson from P4/P5).
for (int w = 0; w < 5000; w++) { Timing.Sink += TimingTargets.MultiProbeTarget(w); }
Console.WriteLine("[N2] pre-warmed MultiProbeTarget (JIT-compiled)");

int N2Fire() { for (int i = 0; i < 500; i++) { Timing.Sink += TimingTargets.MultiProbeTarget(i); } return LineProbeSink.SeenProbeIds.Count; }
NativeLineProbeDefinition Mk(uint off, int id) => new(
    "LineProbeTimingE2E", "TimingTargets", "MultiProbeTarget",
    new[] { "System.Int32" }, off, id,
    "LineProbeSink", "LineProbeSinkNs.LineProbeSink", "Probe", EMIT_LEGACY, 0, null);

// --- Step A: one probe at a real boundary (offset 5) ---
LineProbeSink.SeenProbeIdsMap.Clear();
try { var a = new[] { Mk(5, 8001) }; NativeMethods.AddLineProbes("n2-A", a, a.Length); a[0].Dispose(); } catch (Exception e) { Console.WriteLine("[N2-A] EX " + e.Message); }
System.Threading.Thread.Sleep(500); N2Fire();
Console.WriteLine($"[N2-A] one probe @off5: distinct fired = {LineProbeSink.SeenProbeIds.Count} (expect 1 — proves offset+weave on this method)");

// --- Step B: second probe, different boundary (offset 9), SEPARATE call ---
int beforeB = LineProbeSink.SeenProbeIds.Count;
try { var b = new[] { Mk(9, 8002) }; NativeMethods.AddLineProbes("n2-B", b, b.Length); b[0].Dispose(); } catch (Exception e) { Console.WriteLine("[N2-B] EX " + e.Message); }
System.Threading.Thread.Sleep(500); N2Fire();
Console.WriteLine($"[N2-B] +2nd probe @off9 (separate call): distinct fired = {LineProbeSink.SeenProbeIds.Count} (expect 2 if multi-probe supported; stays 1 => 2nd DROPPED)");

// --- Step C: fresh method-equivalent via two-in-one-call would need a 2nd never-probed method;
// MultiProbeTarget is now probed, so Step C reuses it to test the single-call path specifically. ---
Console.WriteLine($"[N2] VERDICT: {(LineProbeSink.SeenProbeIds.Count >= 2 ? "MULTI-PROBE SUPPORTED" : "SINGLE-PROBE-PER-METHOD (2nd dropped) — confirms m_methods dedup by mdMethodDef")}");
Console.WriteLine($"[N2] check native log for 'Request ReJIT done for N' + 'CreateMethodIfNotExists returned false' to attribute.");

// ============================================================================================
// Heartbeat teardown + overall coverage stats (so the proxy's resolution is auditable).
// ============================================================================================
Console.WriteLine();
Console.WriteLine($"[wk] worker batches={worker.Batches:N0} x {Worker.BatchSizeConst} calls, "
    + $"total stalls>{gapThresholdUs:F0}us={worker.StallCount:N0}, sink={worker.Sink}");
Console.WriteLine($"[hb] HB1 samples={hb1.Samples:N0}, median inter-sample={hb1.MedianSampleNs:F1} ns, "
    + $"recorded gaps>{gapThresholdUs:F0}us={hb1.GapCount:N0}, bufferFull={hb1.BufferFull}");
Console.WriteLine($"[hb] HB2 samples={hb2.Samples:N0}, median inter-sample={hb2.MedianSampleNs:F1} ns, "
    + $"recorded gaps>{gapThresholdUs:F0}us={hb2.GapCount:N0}, bufferFull={hb2.BufferFull}");
Console.WriteLine($"[hb] detector resolution: a stop shorter than ~{gapThresholdUs:F0} us is invisible to this proxy.");
Console.WriteLine($"[hb] workSink={hb1.WorkSink ^ hb2.WorkSink} (keeps the cooperative-mode busy work live)");
Console.WriteLine($"[hb] Sink={Timing.Sink}");
Console.WriteLine("\n=== done ===");
return 0;

// ------------------------------------------------------------------------------------------------
internal static class Timing
{
    public const int MaxApplyTargets = 40;

    public static long Sink;

    public static bool LegacyActive;

    // Effective resolution of Stopwatch on this machine (smallest observed non-zero delta).
    public static double GranularityNs;

    public static bool SteadyProbesActive()
    {
        // Legacy Probe shares FireCount with the P1 probes, so snapshot-and-compare instead.
        int f0 = LineProbeSink.FireCount;
        Sink += TimingTargets.SteadyLegacy(1);
        LegacyActive = LineProbeSink.FireCount != f0;
        return LegacyActive && LineProbeSink.ShouldCaptureCalls > 0 && LineProbeSink.CaptureCount > 0;
    }

    // Four near-identical direct-call loops. Deliberately duplicated rather than parameterised by a
    // delegate: the loop body must contain nothing but the call under test.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double LoopBaseline(int n, double tickNs)
    {
        long acc = 0;
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) { acc += TimingTargets.SteadyBaseline(i); }
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        Sink += acc;
        return ((b - a) * tickNs) / n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double LoopLegacy(int n, double tickNs)
    {
        long acc = 0;
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) { acc += TimingTargets.SteadyLegacy(i); }
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        Sink += acc;
        return ((b - a) * tickNs) / n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double LoopGated(int n, double tickNs)
    {
        long acc = 0;
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) { acc += TimingTargets.SteadyGated(i); }
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        Sink += acc;
        return ((b - a) * tickNs) / n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double LoopUngated(int n, double tickNs)
    {
        long acc = 0;
        long a = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) { acc += TimingTargets.SteadyUngated(i); }
        long b = System.Diagnostics.Stopwatch.GetTimestamp();
        Sink += acc;
        return ((b - a) * tickNs) / n;
    }

    public static double Median(double[] v) => Percentile(v, 50);

    public static double Percentile(double[] v, double p)
    {
        if (v.Length == 0) { return 0; }
        var c = (double[])v.Clone();
        Array.Sort(c);
        double idx = (p / 100.0) * (c.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        return lo == hi ? c[lo] : c[lo] + ((c[hi] - c[lo]) * (idx - lo));
    }

    public static void Report(string label, List<double> v, string unit)
    {
        if (v.Count == 0) { Console.WriteLine($"{label}: (no samples)"); return; }
        var a = v.ToArray();
        Console.WriteLine($"{label}: n={a.Length}  min={Percentile(a, 0):F2}  median={Median(a):F2}  "
            + $"p95={Percentile(a, 95):F2}  p99={Percentile(a, 99):F2}  max={Percentile(a, 100):F2}  {unit}");
    }
}

// ------------------------------------------------------------------------------------------------
// Spin-loop stall detector. Records inter-sample gaps above a threshold into PRE-ALLOCATED arrays
// (no allocation on the hot path, so the detector cannot itself trigger a GC pause).
internal sealed class Heartbeat
{
    private const int Cap = 400_000;

    private readonly long[] gapEnd = new long[Cap];   // timestamp at the END of the gap
    private readonly long[] gapTicks = new long[Cap]; // gap length in Stopwatch ticks
    private readonly long thresholdTicks;
    private readonly double tickNs;
    private readonly string name;
    private readonly long[] sampleDeltas = new long[100_000]; // small sample of normal deltas
    private Thread? thread;
    private int gapCount;
    private long samples;
    private int run = 1;
    private int workSink;

    public Heartbeat(string name, double thresholdUs, double tickNs)
    {
        this.name = name;
        this.tickNs = tickNs;
        this.thresholdTicks = (long)((thresholdUs * 1000.0) / tickNs);
    }

    public long Samples => Volatile.Read(ref this.samples);

    public int WorkSink => this.workSink;

    public int GapCount => Volatile.Read(ref this.gapCount);

    public bool BufferFull => this.GapCount >= Cap;

    public double MedianSampleNs
    {
        get
        {
            long n = Math.Min(this.Samples, this.sampleDeltas.Length);
            if (n <= 0) { return 0; }
            var c = new double[n];
            for (int i = 0; i < n; i++) { c[i] = this.sampleDeltas[i] * this.tickNs; }
            return Timing.Percentile(c, 50);
        }
    }

    public void Start()
    {
        this.thread = new Thread(this.Loop)
        {
            IsBackground = true,
            Name = this.name,
            Priority = ThreadPriority.AboveNormal,
        };
        this.thread.Start();
    }

    public void Stop()
    {
        Volatile.Write(ref this.run, 0);
        this.thread?.Join(2000);
    }

    // A COINCIDENT gap: an interval where BOTH heartbeat threads were stalled, overlapping in time,
    // and the gap lies inside [from,to]. Returns the overlap lengths in microseconds. Two independent
    // spinning threads are not descheduled over the same interval by ordinary OS jitter, so an
    // overlap is the signature of a runtime-wide stop.
    // Single-thread gaps overlapping [from,to], in microseconds. More sensitive than the coincident
    // detector but also picks up plain OS descheduling, so it is a DIAGNOSTIC, not the headline proxy.
    public static List<double> SingleGaps(Heartbeat a, long from, long to)
    {
        var result = new List<double>();
        int na = a.GapCount;
        for (int i = 0; i < na; i++)
        {
            long aEnd = a.gapEnd[i];
            long aStart = aEnd - a.gapTicks[i];
            if (aEnd < from || aStart > to) { continue; }
            result.Add(a.gapTicks[i] * a.tickNs / 1000.0);
        }

        return result;
    }

    public static List<double> CoincidentGaps(Heartbeat a, Heartbeat b, long from, long to)
    {
        var result = new List<double>();
        int na = a.GapCount;
        int nb = b.GapCount;
        for (int i = 0; i < na; i++)
        {
            long aEnd = a.gapEnd[i];
            long aStart = aEnd - a.gapTicks[i];
            if (aEnd < from || aStart > to) { continue; }
            for (int j = 0; j < nb; j++)
            {
                long bEnd = b.gapEnd[j];
                long bStart = bEnd - b.gapTicks[j];
                long ovStart = Math.Max(aStart, bStart);
                long ovEnd = Math.Min(aEnd, bEnd);
                if (ovEnd <= ovStart) { continue; }
                // Clip the overlap to the requested window.
                ovStart = Math.Max(ovStart, from);
                ovEnd = Math.Min(ovEnd, to);
                if (ovEnd > ovStart) { result.Add((ovEnd - ovStart) * a.tickNs / 1000.0); }
            }
        }

        return result;
    }

    // Scratch buffer for the cooperative-mode busy work. Pre-allocated: the heartbeat NEVER
    // allocates, so it cannot itself provoke a GC and manufacture the pauses it is measuring.
    private readonly int[] work = new int[64];

    private void Loop()
    {
        long prev = System.Diagnostics.Stopwatch.GetTimestamp();
        int acc = 1;
        while (Volatile.Read(ref this.run) == 1)
        {
            // CRITICAL: Stopwatch.GetTimestamp() transitions to native (clock_gettime), where the
            // thread is in GC-PREEMPTIVE mode and the runtime does NOT need to suspend it — it just
            // marks it and proceeds. A heartbeat that ONLY calls GetTimestamp therefore spends most
            // of its time in a state that a stop-the-world does not stall, and it misses real
            // suspensions (measured: it caught only 2/20 blocking gen2 GCs). So spend most of each
            // iteration in MANAGED, non-allocating, cooperative-mode work instead. The JIT places a
            // GC poll on this loop's back edge, so a suspension request WILL stall us here.
            for (int k = 0; k < this.work.Length; k++)
            {
                acc = (acc * 1664525) + 1013904223;
                this.work[k] = acc;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long d = now - prev;
            prev = now;
            long s = this.samples;
            if (s < this.sampleDeltas.Length) { this.sampleDeltas[s] = d; }
            Volatile.Write(ref this.samples, s + 1);
            if (d > this.thresholdTicks)
            {
                int c = this.gapCount;
                if (c < Cap)
                {
                    this.gapEnd[c] = now;
                    this.gapTicks[c] = d;
                    Volatile.Write(ref this.gapCount, c + 1);
                }
            }
        }

        this.workSink = acc;
    }
}

// ------------------------------------------------------------------------------------------------
// An APPLICATION thread. It calls the SteadyWorker target in a tight loop and records any batch that
// took longer than the threshold. Unlike the heartbeat threads this one is doing exactly what a real
// app thread does — executing the kind of method that gets rewritten — so its stall record is the
// most defensible answer to "what does the app actually feel when a probe is applied?".
// It only ever writes into pre-allocated arrays: no allocation, so it cannot cause a GC pause.
internal sealed class Worker
{
    public const int BatchSizeConst = 256;

    private const int Cap = 200_000;
    private const int BatchSize = BatchSizeConst;

    private readonly long[] stallEnd = new long[Cap];
    private readonly long[] stallTicks = new long[Cap];
    private readonly long thresholdTicks;
    private readonly double tickNs;
    private Thread? thread;
    private int stallCount;
    private long batches;
    private int run = 1;
    private long sink;
    private int target;

    // Which SteadyWorkerNN the worker is currently hammering. Switched between P1e reps so each rep
    // rewrites a method that is hot RIGHT NOW but has never been probed before.
    public int Target
    {
        get => Volatile.Read(ref this.target);
        set => Volatile.Write(ref this.target, value);
    }

    public Worker(double thresholdUs, double tickNs)
    {
        this.tickNs = tickNs;
        this.thresholdTicks = (long)((thresholdUs * 1000.0) / tickNs);
    }

    public long Batches => Volatile.Read(ref this.batches);

    public int StallCount => Volatile.Read(ref this.stallCount);

    public long Sink => Volatile.Read(ref this.sink);

    public void Start()
    {
        this.thread = new Thread(this.Loop) { IsBackground = true, Name = "Worker" };
        this.thread.Start();
    }

    public void Stop()
    {
        Volatile.Write(ref this.run, 0);
        this.thread?.Join(2000);
    }

    // Stalls (in us) whose interval overlaps [from,to].
    public List<double> StallsIn(long from, long to)
    {
        var result = new List<double>();
        int n = this.StallCount;
        for (int i = 0; i < n; i++)
        {
            long end = this.stallEnd[i];
            long start = end - this.stallTicks[i];
            if (end < from || start > to) { continue; }
            result.Add(this.stallTicks[i] * this.tickNs / 1000.0);
        }

        return result;
    }

    private void Loop()
    {
        long acc = 0;
        while (Volatile.Read(ref this.run) == 1)
        {
            int t = Volatile.Read(ref this.target);
            long a = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < BatchSize; i++) { acc += TimingTargets.SteadyWorkerByIndex(t, i); }
            long b = System.Diagnostics.Stopwatch.GetTimestamp();
            long d = b - a;
            Volatile.Write(ref this.batches, this.batches + 1);
            if (d > this.thresholdTicks)
            {
                int c = this.stallCount;
                if (c < Cap)
                {
                    this.stallEnd[c] = b;
                    this.stallTicks[c] = d;
                    Volatile.Write(ref this.stallCount, c + 1);
                }
            }
        }

        Volatile.Write(ref this.sink, acc);
    }
}

// ------------------------------------------------------------------------------------------------
// P/Invoke surface for the forked native export. Mirrors LineProbeGatedE2E exactly (must match the
// native struct stride in line_probe.h).
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
        if (this.TargetSignatureTypes == IntPtr.Zero) { return; }
        for (int i = 0; i < this.TargetSignatureTypesLength; i++)
        {
            var ptr = Marshal.ReadIntPtr(this.TargetSignatureTypes, i * IntPtr.Size);
            if (ptr != IntPtr.Zero) { Marshal.FreeHGlobal(ptr); }
        }

        Marshal.FreeHGlobal(this.TargetSignatureTypes);
        this.TargetSignatureTypes = IntPtr.Zero;
    }
}
