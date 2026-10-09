using System;
using System.Diagnostics;
using System.Threading;

// ---------------------------------------------------------------------------
// TARGET process for the ICorDebug "stop the world" cost proxy.
//
// Two things run here:
//   1. The main thread calls Work(...) in a loop. The debugger host sets a
//      breakpoint at an IL offset INSIDE Work, so every call is a stop cycle.
//   2. A background "freeze sensor" thread spins on a monotonic clock and
//      records the wall-clock gap between consecutive samples. When the
//      debugger performs a cooperative stop of ALL managed threads, THIS
//      thread is frozen too -> the gap between its samples spikes to the
//      duration of the freeze. That spike is the app-perceived, target-thread
//      blocked time per hit, measured entirely from inside the target with no
//      trust in the debugger's own numbers.
//
// The host and target measure the same event from opposite sides:
//   host  = wall time from breakpoint-callback entry to Continue() return.
//   target= extra wall time the sensor thread was frozen.
// They should agree in order of magnitude; both are reported.
// ---------------------------------------------------------------------------

class Program
{
    // Volatile so the JIT cannot hoist/cache; keeps Work honest.
    static volatile int s_sink;

    // Freeze sensor state.
    static volatile bool s_run = true;

    static void Main(string[] args)
    {
        int iterations = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 2000;
        int callGapMs  = args.Length > 1 && int.TryParse(args[1], out var g) ? g : 5;

        Console.WriteLine($"[target] pid={Environment.ProcessId} iterations={iterations} callGapMs={callGapMs}");
        Console.WriteLine($"[target] Stopwatch.IsHighResolution={Stopwatch.IsHighResolution} freq={Stopwatch.Frequency}");
        Console.Out.Flush();

        // Freeze sensor thread: tight loop measuring self-cadence.
        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
        double thrUs = double.TryParse(Environment.GetEnvironmentVariable("DI_FREEZE_THR_US"), out var tu) ? tu : 200.0;
        long freezeThresholdTicks = (long)(thrUs / tickToUs);
        var freezes = new System.Collections.Generic.List<double>(1024);
        long maxGapTicks = 0;

        bool sensorOn = (Environment.GetEnvironmentVariable("DI_SENSOR") ?? "1") != "0";
        var sensor = new Thread(() =>
        {
            long last = Stopwatch.GetTimestamp();
            while (s_run)
            {
                long now = Stopwatch.GetTimestamp();
                long gap = now - last;
                last = now;
                if (gap > maxGapTicks) maxGapTicks = gap;
                if (gap > freezeThresholdTicks)
                {
                    double us = gap * tickToUs;
                    lock (freezes) freezes.Add(us);
                }
                // no sleep: we want the finest-grained sampling so a freeze is
                // captured as ~one big gap, not spread across sleeps.
            }
        });
        sensor.IsBackground = true;
        sensor.Name = "freeze-sensor";
        // The spin sensor burns a full core. On a 4-cpu container that starves the
        // debugger host and inflates its numbers, so it is optional (DI_SENSOR=0).
        if (sensorOn) sensor.Start();
        else Console.WriteLine("[target] freeze-sensor DISABLED (DI_SENSOR=0)");

        // Extra managed threads. ICorDebug's stop is process-wide, so if the cost
        // scales with managed thread count this is where it shows up. These threads
        // sleep-loop (like real app threads), they do not burn CPU.
        int workers = int.TryParse(Environment.GetEnvironmentVariable("DI_WORKERS"), out var w) ? w : 0;
        for (int k = 0; k < workers; k++)
        {
            var t = new Thread(() => { int x = 0; while (s_run) { x += 7; s_sink = x; Thread.Sleep(1); } });
            t.IsBackground = true; t.Name = "worker" + k; t.Start();
        }
        if (workers > 0) Console.WriteLine($"[target] started {workers} extra managed worker threads");

        // ================= BLAST-RADIUS PROBES (DI_BLAST=1) =================
        // The per-hit freeze is process-wide, so it hits subsystems with NO relationship to the
        // instrumented method. These four probes measure the consequences that actually matter for
        // a service (SLO/timeout/health), rather than the raw stop duration.
        //
        // KEY POINT each probe tests: wall-clock keeps running while all managed threads are
        // frozen, so every clock-based deadline keeps counting down with nothing able to service it.
        bool blast = (Environment.GetEnvironmentVariable("DI_BLAST") ?? "0") != "0";

        // (1) HEALTH CHECK: a responder that must answer within a deadline, like an LB/k8s probe.
        //     A miss is what gets an instance pulled from rotation or restarted.
        double healthDeadlineMs = double.TryParse(Environment.GetEnvironmentVariable("DI_HEALTH_DEADLINE_MS"), out var hd) ? hd : 5.0;
        int healthIntervalMs = int.TryParse(Environment.GetEnvironmentVariable("DI_HEALTH_INTERVAL_MS"), out var hi) ? hi : 10;
        var healthLatencies = new System.Collections.Generic.List<double>(4096);
        int healthChecks = 0, healthMisses = 0;

        // (2) LOCK CONVOY: a lock the hot method's thread also takes. If the freeze lands while the
        //     lock is held, every waiter stalls for the whole stop -> amplification beyond 207us.
        var sharedLock = new object();
        var lockWaits = new System.Collections.Generic.List<double>(4096);

        // (3) TIMER DRIFT: scheduled work fires late; drift accumulates.
        var timerDrift = new System.Collections.Generic.List<double>(4096);

        // (4) THREADPOOL: queue->execute latency. Frozen workers don't drain the queue.
        var poolLatency = new System.Collections.Generic.List<double>(4096);

        if (blast)
        {
            Console.WriteLine($"[target] BLAST-RADIUS probes ON (health deadline={healthDeadlineMs}ms every {healthIntervalMs}ms)");

            var health = new Thread(() =>
            {
                // A real health check is measured by the PROBER (outside the process): the interval
                // from "request sent" to "response received". Timing only the handler body
                // understates it badly, because the freeze mostly lands BETWEEN checks — the thread
                // is frozen while parked, so its next check is issued late. So we measure
                // RESPONSE-TO-RESPONSE interval vs the expected cadence: excess over the interval
                // is time the prober was waiting with no answer.
                long prev = Stopwatch.GetTimestamp();
                while (s_run)
                {
                    Thread.Sleep(healthIntervalMs);
                    long t0 = Stopwatch.GetTimestamp();
                    Thread.MemoryBarrier();
                    int _ = s_sink;              // trivial handler body
                    long t1 = Stopwatch.GetTimestamp();

                    // Effective response latency as the prober would see it: how much longer than
                    // the scheduled cadence this answer took to arrive.
                    double intervalMs = (t1 - prev) * tickToUs / 1000.0;
                    prev = t1;
                    double effectiveMs = intervalMs - healthIntervalMs;
                    if (effectiveMs < 0) effectiveMs = (t1 - t0) * tickToUs / 1000.0;

                    Interlocked.Increment(ref healthChecks);
                    if (effectiveMs > healthDeadlineMs) Interlocked.Increment(ref healthMisses);
                    lock (healthLatencies) healthLatencies.Add(effectiveMs);
                }
            }) { IsBackground = true, Name = "health-check" };
            health.Start();

            var contender = new Thread(() =>
            {
                while (s_run)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    lock (sharedLock)
                    {
                        long t1 = Stopwatch.GetTimestamp();
                        lock (lockWaits) lockWaits.Add((t1 - t0) * tickToUs);
                    }
                    Thread.Sleep(1);
                }
            }) { IsBackground = true, Name = "lock-contender" };
            contender.Start();

            var timerThread = new Thread(() =>
            {
                const int periodMs = 10;
                long next = Stopwatch.GetTimestamp() + (long)(periodMs * 1000.0 / tickToUs);
                while (s_run)
                {
                    Thread.Sleep(periodMs);
                    long now = Stopwatch.GetTimestamp();
                    double lateUs = (now - next) * tickToUs;
                    if (lateUs > 0) { lock (timerDrift) timerDrift.Add(lateUs); }
                    next = now + (long)(periodMs * 1000.0 / tickToUs);
                }
            }) { IsBackground = true, Name = "timer-drift" };
            timerThread.Start();

            var poolFeeder = new Thread(() =>
            {
                while (s_run)
                {
                    long q = Stopwatch.GetTimestamp();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        long e = Stopwatch.GetTimestamp();
                        lock (poolLatency) poolLatency.Add((e - q) * tickToUs);
                    });
                    Thread.Sleep(2);
                }
            }) { IsBackground = true, Name = "pool-feeder" };
            poolFeeder.Start();
        }

        // Give the host time to attach + arm the breakpoint before the hot loop.
        // The host signals readiness by creating a file; we poll for it.
        string readyFile = Environment.GetEnvironmentVariable("DI_HOST_READY_FILE");
        if (!string.IsNullOrEmpty(readyFile))
        {
            Console.WriteLine("[target] waiting for host-ready file: " + readyFile);
            Console.Out.Flush();
            for (int i = 0; i < 600 && !System.IO.File.Exists(readyFile); i++)
                Thread.Sleep(100);
            Console.WriteLine("[target] host ready (or timed out); starting hot loop");
            Console.Out.Flush();
        }

        // PRIMARY victim-side measurement: time each individual Work() call from
        // the calling thread. Work() is ~3 arithmetic ops, so its uninstrumented
        // cost is nanoseconds; when a breakpoint is armed inside it, the entire
        // measured duration IS the stop-the-world freeze this thread suffered.
        var callUs = new double[iterations];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            long a = Stopwatch.GetTimestamp();
            if (blast)
            {
                // Hold the shared lock ACROSS the instrumented call, so a freeze landing inside
                // Work() also blocks the contender thread -> measures convoy amplification.
                lock (sharedLock) { Work(i, i * 2 + 1); }
            }
            else
            {
                Work(i, i * 2 + 1);
            }

            long b = Stopwatch.GetTimestamp();
            callUs[i] = (b - a) * tickToUs;
            if (callGapMs > 0) Thread.Sleep(callGapMs);
        }
        sw.Stop();

        s_run = false;
        if (sensorOn) sensor.Join(2000);

        Array.Sort(callUs);
        Console.WriteLine("[target] ==== PER-CALL Work() COST (calling thread's own view) ====");
        Console.WriteLine($"[target] calls={callUs.Length} us: min={callUs[0]:F1} p50={Pct(callUs,50):F1} p90={Pct(callUs,90):F1} p99={Pct(callUs,99):F1} max={callUs[callUs.Length-1]:F1} mean={Avg(callUs):F1}");
        int nBig = 0; foreach (var v in callUs) if (v > 100) nBig++;
        Console.WriteLine($"[target] calls slower than 100us = {nBig} (these are the breakpoint hits)");

        // Report the app-side view.
        double[] arr;
        lock (freezes) arr = freezes.ToArray();
        Array.Sort(arr);
        Console.WriteLine("[target] ==== APP-SIDE FREEZE REPORT (sensor thread) ====");
        Console.WriteLine($"[target] hot-loop wall={sw.Elapsed.TotalMilliseconds:F1} ms over {iterations} calls");
        Console.WriteLine($"[target] freeze events (>{thrUs:F0}us gaps)={arr.Length}  [expected ~1 per breakpoint hit]");
        Console.WriteLine($"[target] baseline max gap seen overall={maxGapTicks * tickToUs:F1} us");
        if (arr.Length > 0)
        {
            Console.WriteLine($"[target] freeze us: min={arr[0]:F1} p50={Pct(arr,50):F1} p90={Pct(arr,90):F1} p99={Pct(arr,99):F1} max={arr[arr.Length-1]:F1}");
        }
        if (blast)
        {
            Console.WriteLine("[target] ==== BLAST RADIUS: what the process-wide freeze actually breaks ====");
            ReportBlast("HEALTH-CHECK latency (ms)", healthLatencies, 1000.0 / 1000.0, "ms");
            Console.WriteLine($"[target] HEALTH: checks={healthChecks} MISSED DEADLINE(>{healthDeadlineMs}ms)={healthMisses}" +
                              (healthChecks > 0 ? $"  ({healthMisses * 100.0 / healthChecks:F2}% of checks)" : ""));
            Console.WriteLine($"[target]   ^ a missed health check is what makes an LB pull the instance / k8s restart it");
            ReportBlast("LOCK-ACQUIRE wait (us)", lockWaits, 1.0, "us");
            Console.WriteLine($"[target]   ^ convoy: waiters stall for the freeze because the lock was held across it");
            ReportBlast("TIMER lateness (us)", timerDrift, 1.0, "us");
            ReportBlast("THREADPOOL queue->exec (us)", poolLatency, 1.0, "us");
            Console.WriteLine($"[target]   ^ frozen pool threads cannot drain the queue");
        }

        Console.WriteLine("[target] DONE");
        Console.Out.Flush();
    }

    static double Avg(double[] a) { double t = 0; foreach (var v in a) t += v; return a.Length == 0 ? 0 : t / a.Length; }

    static void ReportBlast(string label, System.Collections.Generic.List<double> samples, double scale, string unit)
    {
        double[] arr;
        lock (samples) arr = samples.ToArray();
        if (arr.Length == 0) { Console.WriteLine($"[target] {label}: no samples"); return; }
        for (int i = 0; i < arr.Length; i++) arr[i] *= scale;
        Array.Sort(arr);
        Console.WriteLine($"[target] {label}: n={arr.Length} p50={Pct(arr,50):F2} p95={Pct(arr,95):F2} " +
                          $"p99={Pct(arr,99):F2} max={arr[arr.Length-1]:F2} {unit}");
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Length) idx = sorted.Length - 1;
        return sorted[idx];
    }

    // The hot method. The host sets a breakpoint at an IL offset inside here.
    // `local` is a genuine local slot the host reads on each hit to exercise
    // the full "read one local from the frame" leg of the stop cycle.
    static void Work(int a, int b)
    {
        int local = a * 31 + b;   // <-- host reads this local
        local ^= (a << 3);
        s_sink = local;
    }
}
