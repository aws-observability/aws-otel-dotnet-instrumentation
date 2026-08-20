// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Client;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

public class StatusReporterTests
{
    private static InstrumentationConfiguration CreateConfig(string hash = "hash1", int maxHits = 100, string method = "Run") =>
        new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "MyApp",
            ClassName = "Svc",
            MethodName = method,
            LocationHash = hash,
            Capture = new CaptureConfiguration(
                null, null, true, false,
                255, 20, 3, 3, 20, 20, maxHits)
        };

    [Fact]
    public void ReportReadyForNew_ReportsOncePerConfig()
    {
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        var config = CreateConfig();
        registry.Register(config);
        reporter.MarkApplied(config); // READY requires a successful apply, not just registration

        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew(); // second call should not report again
        reporter.FlushPending();

        sentBodies.Should().HaveCount(1);
        sentBodies[0].Should().Contain("READY");
        sentBodies[0].Should().Contain("hash1");
    }

    [Fact]
    public void ReportReadyForNew_SkipsIfAlreadyHit()
    {
        var sentCount = 0;
        var handler = new MockHttpHandler(_ => { sentCount++; return new HttpResponseMessage(HttpStatusCode.OK); });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        var config = CreateConfig();
        registry.Register(config);
        reporter.MarkApplied(config);
        registry.TryHit(config.InstrumentationKey); // hit it before reporting

        reporter.ReportReadyForNew();
        reporter.FlushPending();

        sentCount.Should().Be(0); // not ready — already has hits
    }

    [Fact]
    public void ReportError_SendsErrorStatus()
    {
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        reporter.ReportError(CreateConfig(hash: "bad_hash"), "UNSUPPORTED_TARGET");
        reporter.FlushPending();

        sentBodies.Should().HaveCount(1);
        sentBodies[0].Should().Contain("ERROR");
        sentBodies[0].Should().Contain("UNSUPPORTED_TARGET");
        sentBodies[0].Should().Contain("bad_hash");
    }

    [Fact]
    public void ReportReadyForNew_ConcurrentWithRegistration_DoesNotThrowOrDuplicate()
    {
        // The dedup sets + GetAll enumeration are shared between the poller-thread ReportReadyForNew and the
        // timer-thread status pass. Without the gate, concurrent registration + repeated ReportReadyForNew
        // could throw "Collection was modified" or double-report a key.
        // Hammer it: many threads register configs while others report, and assert no throw and no dup READY.
        var sentBodies = new List<string>();
        var sendLock = new object();
        var handler = new MockHttpHandler(req =>
        {
            var body = new StreamReader(req.Content!.ReadAsStream()).ReadToEnd();
            lock (sendLock)
            {
                sentBodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        // Distinct InstrumentationKeys require distinct type/method (the key is {CodeUnit}.{ClassName}.
        // {MethodName}); LocationHash alone does not vary the key, so each config gets its own method name.
        const int configCount = 200;
        Parallel.For(0, configCount, i =>
        {
            var config = CreateConfig(hash: $"hash-{i}", method: $"Run{i}");
            registry.Register(config);
            reporter.MarkApplied(config);
            reporter.ReportReadyForNew();
        });

        // Drain any stragglers registered late.
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        // Every registered config is READY exactly once — count READY occurrences across all sent bodies.
        var readyCount = sentBodies.Sum(b => CountOccurrences(b, "\"Status\":\"READY\""));
        readyCount.Should().Be(configCount, "each config must be reported READY exactly once, with no loss or duplication");
    }

    [Fact]
    public void Forget_ReEnablesReadyReporting_ForReAddedConfig()
    {
        // Parity with Java/JS: status dedup is keyed by LocationHash and cleared on removal, so a config
        // that is removed and re-added (or changed in place → new LocationHash) reports READY again rather
        // than being suppressed for the process lifetime.
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        var config = CreateConfig(hash: "loc-1");
        registry.Register(config);
        reporter.MarkApplied(config);
        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew(); // deduped — still one READY
        reporter.FlushPending();
        sentBodies.Should().HaveCount(1, "the first apply reports READY once");

        // Config removed → manager forgets it by LocationHash.
        registry.RemoveStale(new HashSet<string>());
        reporter.Forget("loc-1");

        // Same location re-added and re-applied → READY must be reported again.
        registry.Register(config);
        reporter.MarkApplied(config);
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        sentBodies.Should().HaveCount(2, "a re-added config reports READY again after Forget");
        sentBodies[1].Should().Contain("READY");
        sentBodies[1].Should().Contain("loc-1");
    }

    [Fact]
    public void ReportReadyForNew_WithoutForget_DoesNotReReport_SameLocation()
    {
        // Complement to the Forget test: absent a Forget, the same LocationHash stays deduped (so we don't
        // spam READY every poll). Proves the dedup is real and only Forget lifts it.
        var sentCount = 0;
        var handler = new MockHttpHandler(_ => { sentCount++; return new HttpResponseMessage(HttpStatusCode.OK); });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        var config = CreateConfig(hash: "loc-1");
        registry.Register(config);
        reporter.MarkApplied(config);
        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        sentCount.Should().Be(1, "same LocationHash is reported READY once until Forget clears it");
    }

    [Fact]
    public void ReportReadyForNew_DoesNotReportReady_ForAConfigThatNeverApplied()
    {
        // The manager registers every SUPPORTED config, then tries to apply each one. An apply that returns
        // TypeNotLoaded (target assembly not loaded yet) deliberately reports no ERROR and is retried on a
        // later poll — but the config is already in the registry, so a registry-driven READY told the backend
        // the probe was instrumented and waiting when nothing had been woven at all.
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        // Registered, but the apply never succeeded → no MarkApplied.
        var notApplied = CreateConfig(hash: "not-loaded-yet");
        registry.Register(notApplied);

        reporter.ReportReadyForNew();
        reporter.FlushPending();

        sentBodies.Should().BeEmpty("a config that is registered but not woven is not READY");

        // The retry on a later poll succeeds → now it is READY.
        reporter.MarkApplied(notApplied);
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        sentBodies.Should().HaveCount(1, "once the apply succeeds the config reports READY");
        sentBodies[0].Should().Contain("READY");
        sentBodies[0].Should().Contain("not-loaded-yet");
    }

    [Fact]
    public void ReportReadyForNew_DoesNotReportReady_AfterAnErrorForTheSameLocation()
    {
        // A permanently-failed config stays in the registry on purpose (that is how the manager reports it
        // exactly once instead of every poll). The apply loop then ends with ReportReadyForNew, so the
        // backend's last word on a broken probe was READY — it looked healthy in the console.
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        // Worst case for the READY path: the target WAS woven (so it is marked applied), and an ERROR is
        // reported anyway — exactly what OVERLOADED_METHODS does.
        var config = CreateConfig(hash: "loc-broken");
        registry.Register(config);
        reporter.MarkApplied(config);
        reporter.ReportError(config, "OVERLOADED_METHODS");

        reporter.ReportReadyForNew(); // same poll, right after the ERROR
        reporter.ReportReadyForNew(); // and again on the next poll
        reporter.FlushPending();

        var allBodies = string.Join("\n", sentBodies);
        allBodies.Should().Contain("OVERLOADED_METHODS");
        allBodies.Should().NotContain("READY", "an errored location must not then be reported READY");

        // Removed and re-added → judged afresh, so the stale ERROR must not condemn it forever.
        registry.RemoveStale(new HashSet<string>());
        reporter.Forget("loc-broken");
        registry.Register(config);
        reporter.MarkApplied(config);
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        string.Join("\n", sentBodies).Should().Contain("READY", "Forget clears the errored state for a re-added location");
    }

    [Fact]
    public void ReadyDroppedByAFullQueue_IsReportedOnALaterPass_NotSuppressedForever()
    {
        // The dedup claim is made under the gate BEFORE the hand-off (that is what stops two threads both
        // reporting the same location). If the bounded queue is full, the entry is dropped — and a claim that
        // was never delivered used to stay recorded, so the probe was woven and capturing while the console
        // never showed it, with no recovery short of the config being removed and re-added.
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        // One more config than the queue holds, and no worker draining it, so exactly one READY is dropped.
        // Distinct method names because the instrumentation key is {CodeUnit}.{ClassName}.{MethodName}.
        const int queueCapacity = 1_000;
        const int configCount = queueCapacity + 1;
        for (var i = 0; i < configCount; i++)
        {
            var config = CreateConfig(hash: $"loc-{i}", method: $"Run{i}");
            registry.Register(config);
            reporter.MarkApplied(config);
        }

        reporter.ReportReadyForNew();
        reporter.DroppedStatusCount.Should().Be(1, "the queue holds 1000, so the 1001st READY has nowhere to go");

        // Drain, then report again: the dropped location must be reported now that there is room.
        reporter.FlushPending();
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        var readyCount = sentBodies.Sum(b => CountOccurrences(b, "\"Status\":\"READY\""));
        readyCount.Should().Be(
            configCount,
            "every applied config must be reported READY exactly once — the one dropped by the full queue on "
            + "the first pass has to be retried, not suppressed for the process lifetime");
    }

    [Fact]
    public void FlushPending_WhileTheWorkerIsRunning_Throws()
    {
        // FlushPending drains on the calling thread; doing that alongside the worker would split batches
        // between two threads. The contract is enforced, not just documented.
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, new InstrumentationRegistry(), cts.Token);
        reporter.Start();

        try
        {
            var act = reporter.FlushPending;
            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            reporter.Dispose();
        }
    }

    [Fact]
    public void EditedInPlace_ReportsReadyForTheNewIdentity_NotTheDeletedOne()
    {
        // A line-level InstrumentationKey is Type.Method:Line, so editing a probe (change the captured locals,
        // change MaxHits) arrives as the SAME key with a NEW LocationHash. The manager must retire the old
        // identity and apply the new one; the reporter must then treat the new hash as a fresh config. Before
        // the fix the edit was skipped entirely: the new hash got no status of any kind, and the console showed
        // the pre-edit identity forever.
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);

        var original = CreateConfig(hash: "hash-v1");
        registry.Register(original);
        reporter.MarkApplied(original);
        reporter.ReportReadyForNew();
        reporter.FlushPending();
        string.Join("\n", sentBodies).Should().Contain("hash-v1");

        // The edit: same key (same CodeUnit/ClassName/MethodName), new LocationHash. This is what
        // RetireAppliedConfiguration + a fresh apply do in the manager.
        var edited = CreateConfig(hash: "hash-v2");
        edited.InstrumentationKey.Should().Be(
            original.InstrumentationKey,
            "the premise of this test is that an edit does not change the key");

        registry.Register(edited);
        reporter.Forget("hash-v1");
        reporter.MarkApplied(edited);
        reporter.ReportReadyForNew();
        reporter.FlushPending();

        var all = string.Join("\n", sentBodies);
        all.Should().Contain("hash-v2", "the edited configuration must report READY under its NEW identity");
    }

    [Fact]
    public void ReportError_WithWedgedBackend_DoesNotBlockTheCaller()
    {
        // The manager calls ReportError/ReportReadyForNew from inside configChangeLock while applying a
        // configuration set. When the send happened on the calling thread, a wedged backend held that lock
        // for up to the HttpClient timeout (30s) and every poll thread stacked up behind it. The send now
        // happens on the worker, so the caller returns immediately no matter how slow the backend is.
        using var release = new ManualResetEventSlim(false);
        var handler = new MockHttpHandler(_ =>
        {
            // Stands in for a wedged backend. Bounded so a broken fix fails the assertion below instead of
            // hanging the test run forever.
            release.Wait(TimeSpan.FromSeconds(30));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        var registry = new InstrumentationRegistry();
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, registry, cts.Token);
        reporter.Start();

        var applied = CreateConfig(hash: "loc-1");
        registry.Register(applied);
        reporter.MarkApplied(applied);

        var elapsed = Stopwatch.StartNew();
        reporter.ReportError(CreateConfig(hash: "bad"), "UNSUPPORTED_TARGET");
        reporter.ReportReadyForNew();
        elapsed.Stop();

        try
        {
            elapsed.Elapsed.Should().BeLessThan(
                TimeSpan.FromSeconds(2),
                "status reporting must hand off to the worker, never block the config-application thread on HTTP");
        }
        finally
        {
            // Let the wedged send finish so Dispose can join the worker promptly.
            release.Set();
            reporter.Dispose();
        }
    }

    [Fact]
    public void Start_SendsQueuedStatuses_OnTheWorkerThread()
    {
        // Complement to the non-blocking test: proves the hand-off actually delivers, and delivers from the
        // worker — a queue that no one drains would pass the timing assertion above while sending nothing.
        var sentThreadNames = new ConcurrentQueue<string>();
        var handler = new MockHttpHandler(_ =>
        {
            sentThreadNames.Enqueue(Thread.CurrentThread.Name ?? "<unnamed>");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, new InstrumentationRegistry(), cts.Token);
        reporter.Start();

        try
        {
            reporter.ReportError(CreateConfig(hash: "bad"), "UNSUPPORTED_TARGET");

            // Delivery is asynchronous now, so poll rather than assert immediately.
            var deadline = Stopwatch.StartNew();
            while (sentThreadNames.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
            }

            sentThreadNames.Should().HaveCount(1, "the queued ERROR must actually reach the backend");
            sentThreadNames.TryDequeue(out var threadName).Should().BeTrue();
            threadName.Should().Be("DI-StatusReporter", "the send must happen on the reporter's worker thread");
            reporter.DroppedStatusCount.Should().Be(0);
        }
        finally
        {
            reporter.Dispose();
        }
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, new InstrumentationRegistry(), cts.Token);

        // Dispose before Start (timer is null) must be a safe no-op.
        var act = reporter.Dispose;
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_AfterStart_CompletesAndIsIdempotent()
    {
        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DynamicInstrumentationClient(new HttpClient(handler), "http://localhost:2000", "svc", "env");
        using var cts = new CancellationTokenSource();
        var reporter = new StatusReporter(client, new InstrumentationRegistry(), cts.Token);
        reporter.Start();

        // Dispose waits for any in-flight timer callback (bounded) and must return promptly; a second
        // Dispose must be a no-op, not a double-dispose throw.
        var act = () =>
        {
            reporter.Dispose();
            reporter.Dispose();
        };
        act.Should().NotThrow();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
