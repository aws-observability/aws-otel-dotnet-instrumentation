// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

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

        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew(); // second call should not report again

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
        registry.TryHit(config.InstrumentationKey); // hit it before reporting

        reporter.ReportReadyForNew();

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
            registry.Register(CreateConfig(hash: $"hash-{i}", method: $"Run{i}"));
            reporter.ReportReadyForNew();
        });

        // Drain any stragglers registered late.
        reporter.ReportReadyForNew();

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
        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew(); // deduped — still one READY
        sentBodies.Should().HaveCount(1, "the first apply reports READY once");

        // Config removed → manager forgets it by LocationHash.
        registry.RemoveStale(new HashSet<string>());
        reporter.Forget("loc-1");

        // Same location re-added → READY must be reported again.
        registry.Register(config);
        reporter.ReportReadyForNew();

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

        registry.Register(CreateConfig(hash: "loc-1"));
        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew();
        reporter.ReportReadyForNew();

        sentCount.Should().Be(1, "same LocationHash is reported READY once until Forget clears it");
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
