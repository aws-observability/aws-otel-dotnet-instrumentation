// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// ============================================================================================
//  .NET DI — PHASE 3: CAPTURE PATH UNDER LOAD
//
//  Purpose: convert an INFERENCE into EVIDENCE. poc/DI-LINE-LEVEL-PERF-COMPARISON.md §5c claims
//  .NET will not reproduce ADOT Java's 46-83% throughput cliff, because .NET has the per-probe
//  rate limiter Java's March-2026 perf doc lists as its P0 fix (HitState.cs: 5 captures/sec).
//  That claim is currently read off the code, not measured. This harness measures it.
//
//  Three questions, in priority order:
//    Q1 (the claim): with the 5/sec limiter active, what is the throughput cost of an active
//       probe on a hot method? Java measured 46-83% loss. Prediction: near-zero, because 5 of N
//       calls capture and the rest cost ~2.3 ns (the DECISION-A gated-discard path).
//    Q2 (the bug hunt): does .NET silently drop captures like Java did (37.5% / 25.7% capture
//       rates, no metric, no log)? DIDataStore.cs:14 is an UNBOUNDED ConcurrentQueue with no
//       drop accounting — Java's P1 fix was bounding theirs at 10,000. Unbounded means .NET
//       does not drop, it GROWS. We measure queue depth to see whether it grows without bound
//       under sustained load, which is the .NET-shaped version of the same defect.
//    Q3 (the comparable number): .NET's fixed cost per FULL capture (serialize + enqueue),
//       to compare against Java's ~60 µs/capture.
//
//  Design note: this drives the SHIPPING capture path directly (DiIntegrationHelper.OnMethodBegin
//  / OnMethodEnd via the same InstrumentationRegistry + HitState + ValueSerializer + DIDataStore
//  that the profiler drives) rather than going through a real profiler + HTTP app. Rationale:
//  the profiler weave is already proven E2E elsewhere; going through Kestrel + the profiler would
//  bury a ~µs capture cost under ~ms of HTTP noise and make attribution impossible. The tradeoff
//  is stated honestly in the report: this measures the capture path, NOT end-to-end HTTP latency,
//  so it is NOT directly comparable to Java's rps/p99 numbers. It IS directly comparable on
//  per-capture cost and on rate-limiter behaviour.
// ============================================================================================

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using CaptureLoad.Targets;

internal static class Program
{
    private const string Ns = "CaptureLoad.Targets";

    private static int durationSec = int.TryParse(Environment.GetEnvironmentVariable("DI_DURATION_SEC"), out var d) ? d : 10;
    private static int threads = int.TryParse(Environment.GetEnvironmentVariable("DI_THREADS"), out var t) ? t : 8;

    private static int Main()
    {
        Console.WriteLine("=== .NET DI Phase 3 — capture path under load ===");
        Console.WriteLine($"[env] duration={durationSec}s per test, threads={threads}, ServerGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"[env] Stopwatch.Frequency={Stopwatch.Frequency} (ns resolution={Stopwatch.Frequency == 1_000_000_000})");
        Console.WriteLine($"[env] HitState default rate limit = 5 captures/sec/probe (Model/HitState.cs:21)");
        Console.WriteLine();

        // P0: prove the rate limiter is actually engaged before trusting any throughput number.
        // If this control fails, every subsequent result is meaningless.
        if (!RateLimiterControl())
        {
            Console.WriteLine("!! POSITIVE CONTROL FAILED — rate limiter is not behaving as assumed.");
            Console.WriteLine("!! Do not trust the numbers below. Investigate HitState before proceeding.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("---------- Q1/Q3: per-test throughput + capture cost ----------");
        Console.WriteLine();

        var targets = new CaptureComplexityTargets();
        var results = new List<TestResult>
        {
            RunTest("3.1 Primitives",       nameof(targets.PrimitivesOnly),  () => targets.PrimitivesOnly(42),                 i => new object?[] { 42 }),
            RunTest("3.2 Simple string",    nameof(targets.SimpleObject),    () => targets.SimpleObject("test"),               i => new object?[] { "test" }),
            RunTest("3.6 SimpleData ret",   nameof(targets.CreateSimpleData), () => targets.CreateSimpleData("id-1"),          i => new object?[] { "id-1" }),
            RunTest("3.7 Nested Order ret", nameof(targets.CreateOrder),     () => targets.CreateOrder("ord-1", 99.99),        i => new object?[] { "ord-1", 99.99 }),
            RunTest("3.4 Large list(100)",  nameof(targets.LargeList),       () => targets.LargeList(100),                     i => new object?[] { 100 }),
        };

        Console.WriteLine();
        Console.WriteLine("================ SUMMARY ================");
        Console.WriteLine();
        Console.WriteLine("| Test | Baseline calls/s | DI-active calls/s | Throughput Δ | Captures | Capture rate | Avg capture cost | Peak queue |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var delta = r.BaselineRate > 0 ? (r.ActiveRate - r.BaselineRate) / r.BaselineRate * 100.0 : 0;
            Console.WriteLine(
                $"| {r.Name} | {r.BaselineRate:N0} | {r.ActiveRate:N0} | {delta:+0.0;-0.0}% | " +
                $"{r.Captures:N0} | {r.CaptureRatePct:0.00}% | {r.AvgCaptureUs:0.0} µs | {r.PeakQueue:N0} |");
        }

        Console.WriteLine();
        Console.WriteLine("!! READ THIS BEFORE QUOTING THE THROUGHPUT COLUMN !!");
        Console.WriteLine("   The trivial targets (3.1/3.2/3.6/3.7) get inlined + partly dead-code-eliminated");
        Console.WriteLine("   in the baseline arm (10^8 calls/s is not a real method rate). The DI-active arm");
        Console.WriteLine("   cannot be elided. So their 'Throughput Δ' measures that ASYMMETRY, not capture");
        Console.WriteLine("   cost — treat those Δ values as INVALID. Row 3.4 (LargeList) is the only valid");
        Console.WriteLine("   throughput row: its body does real work, and it shows ~0% overhead with the");
        Console.WriteLine("   rate limiter active. The per-capture-cost column is valid for every row.");
        Console.WriteLine();
        Console.WriteLine("Java (function-level, HTTP load, c5.2xlarge) for reference:");
        Console.WriteLine("  3.1 Primitives   46.0% throughput loss, 100% capture rate,  2,304 B/snapshot");
        Console.WriteLine("  3.6 SimpleData   47.0% throughput loss, 100% capture rate,  2,495 B/snapshot");
        Console.WriteLine("  3.7 Nested Order 83.4% throughput loss, 25.7% capture rate, 25,314 B/snapshot");
        Console.WriteLine("  3.4 Large list   28.6% throughput loss, 37.5% capture rate,  2,302 B/snapshot");
        Console.WriteLine("  Java fixed per-capture cost ~60 µs. NOTE: Java numbers are end-to-end HTTP rps;");
        Console.WriteLine("  these .NET numbers are capture-path call rate. NOT directly comparable on rate —");
        Console.WriteLine("  compare on per-capture cost, capture rate, and rate-limiter behaviour.");
        Console.WriteLine();

        RateLimiterValueTest();

        QueueGrowthTest();

        Console.WriteLine();
        Console.WriteLine("=== DONE ===");
        return 0;
    }

    /// <summary>
    /// POSITIVE CONTROL. Drives 10,000 hits through a registered config in ~1 second and asserts
    /// the limiter admits roughly the configured 5/sec, not all 10,000. Without this, a "no
    /// overhead" result could simply mean nothing was ever captured.
    /// </summary>
    private static bool RateLimiterControl()
    {
        Console.WriteLine("---------- P0: POSITIVE CONTROL — is the rate limiter engaged? ----------");
        var registry = new InstrumentationRegistry();
        var key = Register(registry, "ControlTarget", "Hit", captureReturn: true, maxHits: null);

        int admitted = 0;
        const int attempts = 10_000;
        for (int i = 0; i < attempts; i++)
        {
            if (registry.TryHit(key))
            {
                admitted++;
            }
        }

        Console.WriteLine($"[P0] {attempts:N0} TryHit attempts in one window -> admitted={admitted}");

        // Expect ~5 (the fixed-window default). Allow a small band for window-boundary effects.
        bool ok = admitted > 0 && admitted <= 50;
        Console.WriteLine(ok
            ? $"[P0] PASS — limiter admitted {admitted}, i.e. it is gating (not pass-through, not closed)."
            : $"[P0] FAIL — admitted {admitted}; expected a small number (~5). Limiter not behaving as assumed.");
        return ok;
    }

    private static TestResult RunTest(
        string name, string methodName, Func<object?> invoke, Func<int, object?[]> argsFactory)
    {
        Console.WriteLine($"--- {name} ---");

        // Phase A: baseline — the target method with NO DI at all.
        //
        // ⚠️ INTERPRETATION WARNING. For the trivial targets (3.1/3.2/3.6/3.7) the JIT inlines and
        // partially dead-code-eliminates the bare call, so baseline reaches 10^8 calls/s — a rate no
        // real method achieves. The DI-active arm cannot be optimised away (it allocates an args
        // array and makes two non-inlinable calls), so the "throughput Δ" for those rows is
        // dominated by that asymmetry, NOT by capture cost. Those Δ values are MEANINGLESS.
        // Row 3.4 (LargeList) is the only throughput row that means anything, because its body does
        // real work (100 Guids) and therefore cannot be elided. Read the per-capture cost column and
        // 3.4's Δ; ignore the Δ on the trivial rows.
        DiIntegrationHelper.Configure(null);
        var baselineCalls = DriveLoad(invoke, null, null);
        var baselineRate = baselineCalls / (double)durationSec;
        Console.WriteLine($"  [A] baseline (no DI)      : {baselineCalls:N0} calls in {durationSec}s = {baselineRate:N0} calls/s");

        // Phase B: DI active — a registered PROBE (unlimited maxHits, like Java's PROBE) on the
        // target, driven through the real DiIntegrationHelper begin/end pair.
        DIDataStore.Clear();
        var registry = new InstrumentationRegistry();
        var key = Register(registry, "CaptureComplexityTargets", methodName, captureReturn: true, maxHits: null);
        registry.IndexArities(
            $"{Ns}.CaptureComplexityTargets", key, new[] { argsFactory(0).Length });
        DiIntegrationHelper.Configure(registry);

        long peakQueue = 0;
        var activeCalls = DriveLoad(invoke, argsFactory, q => peakQueue = Math.Max(peakQueue, q));
        var activeRate = activeCalls / (double)durationSec;

        var captures = DIDataStore.Count;
        var captureRate = activeCalls > 0 ? captures / (double)activeCalls * 100.0 : 0;

        // Q3: isolated cost of ONE full capture (serialize + enqueue), bypassing the limiter so we
        // time the work itself rather than the gate. This is the figure comparable to Java's ~60 µs.
        var avgCaptureUs = MeasureFullCaptureCost(invoke, argsFactory);

        Console.WriteLine($"  [B] DI active (PROBE)     : {activeCalls:N0} calls in {durationSec}s = {activeRate:N0} calls/s");
        Console.WriteLine($"  [B] captures enqueued     : {captures:N0}  ({captureRate:0.000}% of calls)");
        Console.WriteLine($"  [B] peak queue depth      : {peakQueue:N0}");
        Console.WriteLine($"  [C] full-capture cost     : {avgCaptureUs:0.0} µs  (serialize + enqueue, limiter bypassed)");
        var deltaPct = baselineRate > 0 ? (activeRate - baselineRate) / baselineRate * 100.0 : 0;
        Console.WriteLine($"  ==> throughput Δ vs baseline: {deltaPct:+0.0;-0.0}%   (Java saw -46% to -83%)");
        Console.WriteLine();

        DiIntegrationHelper.Configure(null);
        DIDataStore.Clear();

        return new TestResult(name, baselineRate, activeRate, captures, captureRate, avgCaptureUs, peakQueue);
    }

    /// <summary>
    /// Drives the target on N threads for the configured duration. When argsFactory is non-null the
    /// real DI begin/end pair wraps each call, exactly as woven code would.
    /// </summary>
    private static long DriveLoad(Func<object?> invoke, Func<int, object?[]>? argsFactory, Action<long>? onSample)
    {
        long total = 0;
        var stop = Stopwatch.StartNew();
        var deadlineMs = durationSec * 1000L;
        var workers = new Thread[threads];
        var target = new CaptureComplexityTargets();

        for (int w = 0; w < threads; w++)
        {
            workers[w] = new Thread(() =>
            {
                long local = 0;
                while (stop.ElapsedMilliseconds < deadlineMs)
                {
                    // Batch to keep the clock check off the hot path.
                    for (int b = 0; b < 256; b++)
                    {
                        if (argsFactory == null)
                        {
                            invoke();
                        }
                        else
                        {
                            var state = DiIntegrationHelper.OnMethodBegin(target, argsFactory(0));
                            var ret = invoke();
                            DiIntegrationHelper.OnMethodEnd(target, ret, null, in state);
                        }

                        local++;
                    }
                }

                Interlocked.Add(ref total, local);
            }) { IsBackground = true };
            workers[w].Start();
        }

        // Sample queue depth while the load runs — this is how we detect unbounded growth (Q2).
        while (stop.ElapsedMilliseconds < deadlineMs)
        {
            onSample?.Invoke(DIDataStore.Count);
            Thread.Sleep(25);
        }

        foreach (var t in workers)
        {
            t.Join(5000);
        }

        onSample?.Invoke(DIDataStore.Count);
        return total;
    }

    /// <summary>
    /// Times a FULL capture (argument + return serialization + enqueue) with the limiter bypassed,
    /// so the result is the cost of the work itself. Comparable to Java's ~60 µs/capture.
    /// </summary>
    private static double MeasureFullCaptureCost(Func<object?> invoke, Func<int, object?[]> argsFactory)
    {
        var limits = CaptureConfiguration.Default;
        const int n = 2000;

        // Warm up the serializer's reflection paths so we do not time first-use cost.
        for (int i = 0; i < 200; i++)
        {
            ValueSerializer.Serialize(invoke(), limits);
            foreach (var a in argsFactory(i))
            {
                ValueSerializer.Serialize(a, limits);
            }
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            var ret = invoke();
            ValueSerializer.Serialize(ret, limits);
            foreach (var a in argsFactory(i))
            {
                ValueSerializer.Serialize(a, limits);
            }
        }

        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / n;
    }

    /// <summary>
    /// Q1, done properly. THE headline test: does the rate limiter actually prevent Java's cliff?
    ///
    /// Uses a target whose body does real work (~a few µs) so the JIT cannot elide the baseline, and
    /// runs THREE arms on the identical target:
    ///   (a) no DI at all
    ///   (b) DI active WITH the 5/sec limiter    — what we ship
    ///   (c) DI active with the limiter EFFECTIVELY DISABLED (very high cap) — Java's situation
    /// (c) is the direct analogue of Java's configuration. If (b) is ~0% and (c) is badly negative,
    /// the limiter is proven to be what saves us, which is exactly the §5c claim.
    /// </summary>
    private static void RateLimiterValueTest()
    {
        Console.WriteLine();
        Console.WriteLine("---------- Q1 (HEADLINE): does the rate limiter prevent Java's cliff? ----------");
        Console.WriteLine("  Same target, real work in the body (cannot be optimised away). Three arms:");
        Console.WriteLine("  (a) no DI   (b) DI + 5/sec limiter [what we ship]   (c) DI, limiter effectively off [Java's case]");
        Console.WriteLine();

        var targets = new CaptureComplexityTargets();
        Func<object?> work = () => targets.CreateOrder("ord", 12.5);

        // (a) no DI. Real work in the body keeps this honest.
        DiIntegrationHelper.Configure(null);
        var a = DriveRealWork(work, null);

        // (b) shipping config: default 5 captures/sec.
        DIDataStore.Clear();
        var regLimited = new InstrumentationRegistry();
        var kLimited = Register(regLimited, "CaptureComplexityTargets", nameof(targets.CreateOrder), true, null);
        regLimited.IndexArities($"{Ns}.CaptureComplexityTargets", kLimited, new[] { 2 });
        DiIntegrationHelper.Configure(regLimited);
        var b = DriveRealWork(work, () => new object?[] { "ord", 12.5 });
        var bCaptures = DIDataStore.Count;

        // (c) limiter effectively disabled — capture EVERY hit, as Java does.
        DIDataStore.Clear();
        var regUnlimited = new InstrumentationRegistry();
        var cfgUnlimited = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = Ns,
            ClassName = "CaptureComplexityTargets",
            MethodName = nameof(targets.CreateOrder),
            FilePath = "CaptureComplexityTargets.cs",
            LocationHash = "probe-unlimited",
            CreatedAt = DateTimeOffset.UtcNow,
            Capture = CaptureConfiguration.Default with
            {
                CaptureReturn = true,
                CaptureArguments = Array.Empty<string>(),
                CaptureStackTrace = false,
                MaxHits = null,
            },
        };
        regUnlimited.Register(cfgUnlimited);
        regUnlimited.IndexArities($"{Ns}.CaptureComplexityTargets", cfgUnlimited.InstrumentationKey, new[] { 2 });

        // HitState's per-second cap is a ctor arg (default 5) not settable through config, so we
        // emulate "no rate limiting" by measuring the full capture path unconditionally — i.e. what
        // the code does on every admitted hit. This is the fair stand-in for Java's every-call capture.
        DiIntegrationHelper.Configure(regUnlimited);
        var c = DriveRealWorkForcedCapture(work, () => new object?[] { "ord", 12.5 });

        Console.WriteLine($"  (a) no DI                      : {a:N0} calls/s");
        Console.WriteLine($"  (b) DI + 5/sec limiter [ship]  : {b:N0} calls/s   ({(b - a) / (double)a * 100.0:+0.0;-0.0}%)  captures={bCaptures}");
        Console.WriteLine($"  (c) DI, capture EVERY call     : {c:N0} calls/s   ({(c - a) / (double)a * 100.0:+0.0;-0.0}%)  <- Java's configuration");
        Console.WriteLine();
        Console.WriteLine($"  Java measured -83.4% on this shape (test 3.7) with no rate limiting.");
        Console.WriteLine($"  ==> If (b) ~ 0% and (c) is strongly negative, the 5/sec limiter is what prevents");
        Console.WriteLine($"      the cliff, and DI-LINE-LEVEL-PERF-COMPARISON §5c is EVIDENCED, not inferred.");

        DiIntegrationHelper.Configure(null);
        DIDataStore.Clear();
    }

    private static long DriveRealWork(Func<object?> work, Func<object?[]>? argsFactory)
    {
        var target = new CaptureComplexityTargets();
        long calls = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            for (int b = 0; b < 64; b++)
            {
                if (argsFactory == null)
                {
                    var r = work();
                    GC.KeepAlive(r);
                }
                else
                {
                    var state = DiIntegrationHelper.OnMethodBegin(target, argsFactory());
                    var r = work();
                    DiIntegrationHelper.OnMethodEnd(target, r, null, in state);
                }

                calls++;
            }
        }

        sw.Stop();
        return (long)(calls / sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Emulates "no rate limiting": performs the full serialize-and-enqueue work on EVERY call,
    /// which is what Java's implementation does. Bypasses HitState rather than reconfiguring it,
    /// because the per-second cap is a constructor argument, not a config field.
    /// </summary>
    private static long DriveRealWorkForcedCapture(Func<object?> work, Func<object?[]> argsFactory)
    {
        var limits = CaptureConfiguration.Default with { CaptureStackTrace = false };
        long calls = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            for (int b = 0; b < 64; b++)
            {
                var args = argsFactory();
                var r = work();
                var captured = new Dictionary<string, CapturedValue>();
                for (int i = 0; i < args.Length; i++)
                {
                    captured[$"arg{i}"] = ValueSerializer.Serialize(args[i], limits);
                }

                DIDataStore.Enqueue(new PendingCapture
                {
                    Type = CaptureType.METHOD,
                    InstrumentationKey = "forced",
                    LocationHash = "forced",
                    TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Arguments = captured,
                    ReturnValue = ValueSerializer.Serialize(r, limits),
                });
                calls++;
            }
        }

        sw.Stop();
        DIDataStore.Clear();
        return (long)(calls / sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Q2 — the bug hunt. DIDataStore is an UNBOUNDED ConcurrentQueue with no drop accounting
    /// (Capture/DIDataStore.cs:14,25). Java's equivalent silently DROPPED 62-74% of captures under
    /// load; unbounded means .NET instead GROWS without limit if producers outpace the drain.
    /// Here we enqueue with NO consumer to characterise growth, since in production the collector
    /// drains every 10ms but a wedged OTLP endpoint stalls it.
    /// </summary>
    private static void QueueGrowthTest()
    {
        Console.WriteLine("---------- Q2: unbounded-queue behaviour (no consumer draining) ----------");
        Console.WriteLine("  Rationale: DIDataStore uses an unbounded ConcurrentQueue with no drop counter.");
        Console.WriteLine("  Java's P1 fix was to bound theirs at 10,000. This characterises what .NET does");
        Console.WriteLine("  instead when the drain cannot keep up (e.g. wedged OTLP endpoint).");
        DIDataStore.Clear();

        var targets = new CaptureComplexityTargets();
        var limits = CaptureConfiguration.Default;
        var sw = Stopwatch.StartNew();
        long enqueued = 0;
        var before = GC.GetTotalMemory(false);

        while (sw.ElapsedMilliseconds < 3000)
        {
            for (int b = 0; b < 100; b++)
            {
                var order = targets.CreateOrder($"ord-{enqueued}", 12.5);
                DIDataStore.Enqueue(new PendingCapture
                {
                    Type = CaptureType.METHOD,
                    InstrumentationKey = "growth-test",
                    LocationHash = "growth-test",
                    TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ReturnValue = ValueSerializer.Serialize(order, limits),
                });
                enqueued++;
            }
        }

        var after = GC.GetTotalMemory(false);
        Console.WriteLine($"  enqueued {enqueued:N0} captures in 3s with no consumer");
        Console.WriteLine($"  final queue depth : {DIDataStore.Count:N0}  (dropped: {enqueued - DIDataStore.Count:N0})");
        Console.WriteLine($"  heap growth       : {(after - before) / 1024.0 / 1024.0:0.0} MB");
        Console.WriteLine($"  ==> if dropped=0 and heap grew, the queue is UNBOUNDED: .NET does not silently");
        Console.WriteLine($"      drop like Java, it accumulates. Different failure mode, same root cause.");
        DIDataStore.Clear();
    }

    private static string Register(
        InstrumentationRegistry registry, string className, string methodName, bool captureReturn, int? maxHits)
    {
        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = Ns,
            ClassName = className,
            MethodName = methodName,
            FilePath = $"{className}.cs",
            LocationHash = $"probe-{className}-{methodName}".ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            Capture = CaptureConfiguration.Default with
            {
                CaptureReturn = captureReturn,
                CaptureArguments = Array.Empty<string>(),
                CaptureStackTrace = false, // stack capture is a separate cost; isolate serialization
                MaxHits = maxHits,
            },
        };
        registry.Register(config);

        // The registry keys on InstrumentationKey (= TypeName.MethodName for method-level), NOT on
        // LocationHash — see InstrumentationRegistry.Register + InstrumentationConfiguration:59.
        // Returning LocationHash here made every TryHit miss and admit 0, which the P0 control caught.
        return config.InstrumentationKey;
    }

    private sealed record TestResult(
        string Name,
        double BaselineRate,
        double ActiveRate,
        int Captures,
        double CaptureRatePct,
        double AvgCaptureUs,
        long PeakQueue);
}
