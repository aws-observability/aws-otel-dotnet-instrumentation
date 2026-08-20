// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Unit tests for <see cref="IncidentSnapshotCollector" /> — trigger determination,
/// severity, batch dedup, and latency thresholds. Window/period-dedup/rate-limit
/// behaviour is covered by <see cref="IncidentRateLimiterTests" />; the emit/wire path
/// is covered by the smoke test.
/// </summary>
public class IncidentSnapshotCollectorTests
{
    private static IncidentSnapshotCollector NewCollector(ServiceEventsConfig? config = null)
    {
        // A no-op emitter (NullLogger) — the collector's emit path is exercised but
        // produces no output; we assert on ProcessPotentialIncident's return value.
        var emitter = new ServiceEventsOtlpEmitter(
            NullLogger.Instance,
            new Meter("test-" + Guid.NewGuid().ToString("N")),
            deploymentId: "dep",
            gitCommitSha: "sha",
            gitRepoUrl: "repo");

        return new IncidentSnapshotCollector(flushIntervalMs: 60_000, emitter, config ?? new ServiceEventsConfig());
    }

    /// <summary>
    /// A collector wired to a logger that captures what the emitter actually produces, so tests can
    /// assert on the emitted record rather than on the collector's return value.
    /// </summary>
    private static (IncidentSnapshotCollector Collector, CapturingLogger Logger) NewCapturingCollector(
        ServiceEventsConfig? config = null)
    {
        var logger = new CapturingLogger();
        var emitter = new ServiceEventsOtlpEmitter(
            logger,
            new Meter("test-" + Guid.NewGuid().ToString("N")),
            deploymentId: "dep",
            gitCommitSha: "sha",
            gitRepoUrl: "repo");

        return (new IncidentSnapshotCollector(flushIntervalMs: 60_000, emitter, config ?? new ServiceEventsConfig()), logger);
    }

    /// <summary>
    /// Force the collector's final flush and return <c>exception_info[0]</c> from the emitted
    /// IncidentSnapshot record's body.
    /// </summary>
    private static JsonElement EmittedExceptionInfo(IncidentSnapshotCollector collector, CapturingLogger logger)
    {
        // Dispose runs the final Collect, which drains the queue through the emitter.
        collector.Dispose();

        var record = logger.Records.Should().ContainSingle(
            r => r.Any(kv => kv.Key == "event.name"
                             && (kv.Value as string) == "aws.service_events.incident_snapshot"))
            .Subject;

        var body = record.Single(kv => kv.Key == "body").Value as string;
        body.Should().NotBeNull("the incident record carries exception_info in its body");

        using var parsed = JsonDocument.Parse(body!);
        var info = parsed.RootElement.GetProperty("exception_info");
        info.GetArrayLength().Should().Be(1, "BuildExceptionInfo always returns exactly one entry");

        // Cloned because the JsonDocument is disposed on return.
        return info[0].Clone();
    }

    /// <summary>
    /// Captures the attribute lists the emitter passes to the logging bridge. The emitter's state type
    /// is private, but it implements <see cref="IReadOnlyList{T}" /> over its attributes, which is all
    /// this needs.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> Records { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> attributes)
            {
                this.Records.Add(attributes.ToList());
            }
        }

        private sealed class NoopScope : IDisposable
        {
            internal static readonly NoopScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The incident path's counterpart to <c>EndpointFlushRaceTests</c>. Three pieces of shared
    /// state are touched concurrently here: the rate limiter's tumbling window (swapped with
    /// <c>Interlocked.CompareExchange</c>), the batch-dedup hash set (replaced under a lock on every
    /// flush), and the pending snapshot queue (drained while producers are still enqueuing).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scope worth stating precisely, because it is narrower than it looks. This establishes that
    /// concurrent triggering and flushing do not throw, deadlock, corrupt the dedup set, or admit
    /// wildly past the cap. It is <b>not</b> a proof that the counters are atomic: most calls are
    /// rejected by batch dedup before the global counter is consulted, and the per-call hashing and
    /// snapshot construction dominate the read-modify-write window, so the test is not sensitive to
    /// losing an increment. Verified by mutation — replacing the interlocked increment with a plain
    /// <c>++</c> does not fail this test. Atomicity is covered instead by
    /// <c>IncidentRateLimiterTests.CheckRateLimit_UnderConcurrency_AdmitsExactlyTheCap</c>, which
    /// contends on the counter and nothing else.
    /// </para>
    /// <para>
    /// Flushing concurrently with the producers is the part this test does cover well: <c>Collect</c>
    /// replaces the batch-dedup set and drains the queue while requests are still arriving, which is
    /// the shape that lost data on the endpoint path before it was fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConcurrentIncidentsAcrossFlushBoundaries_NeverExceedTheRateLimit()
    {
        const int maxPerMinute = 50;
        var collector = NewCollector(new ServiceEventsConfig
        {
            IncidentSnapshotMaxPerMinute = maxPerMinute,

            // Distinct error signatures per thread would otherwise hit the per-error ceiling long
            // before the global one; a high ceiling isolates the global cap as the binding limit.
            IncidentSnapshotMaxSameError = int.MaxValue,
        });

        var admitted = 0;
        var flushes = 0;

        // Many distinct operations so each ProcessPotentialIncident does real work (hashing,
        // severity, snapshot construction) rather than hitting a fast rejection path.
        Parallel.For(0, 16, worker =>
        {
            for (var i = 0; i < 200; i++)
            {
                var result = collector.ProcessPotentialIncident(
                    route: $"/op{i % 40}", method: "GET", statusCode: 500, durationMs: 5,
                    exceptionType: $"Ex{worker}_{i % 7}", exceptionMessage: null, stackTrace: null,
                    traceId: null, spanId: null, requestTimestampMs: 1000 + i);

                if (result is not null)
                {
                    Interlocked.Increment(ref admitted);
                }

                // Interleave flushes with production, from the worker threads themselves.
                if (i % 50 == 0)
                {
                    collector.Flush();
                    Interlocked.Increment(ref flushes);
                }
            }
        });

        flushes.Should().BeGreaterThan(0, "the test is meaningless if no flush overlapped production");

        admitted.Should().BeLessThanOrEqualTo(
            maxPerMinute,
            "the global per-minute cap must hold under concurrency; exceeding it means the window " +
            "counter or its rollover is not atomic");

        admitted.Should().BeGreaterThan(
            0,
            "and the limiter must not reject everything — that would pass the cap assertion for the " +
            "wrong reason");

        // A final flush must not throw after concurrent use, and must leave the collector usable.
        var finalFlush = () => collector.Flush();
        finalFlush.Should().NotThrow();
    }

    [Theory]
    [InlineData(500, "exception", "critical")]
    [InlineData(503, "exception", "critical")]
    [InlineData(504, "exception", "high")]
    [InlineData(502, "exception", "critical")]
    public void Exception5xx_TriggersException_WithExpectedSeverity(int status, string trigger, string severity)
    {
        var collector = NewCollector();

        var result = collector.ProcessPotentialIncident(
            route: "/x", method: "GET", statusCode: status, durationMs: 10,
            exceptionType: null, exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1000);

        result.Should().NotBeNull();
        result!.TriggerType.Should().Be(trigger);
        result.Severity.Should().Be(severity);
        result.Operation.Should().Be("GET /x");
        result.SnapshotId.Should().StartWith("snap_");
    }

    [Fact]
    public void CapturedException_TriggersException_EvenWithoutStatus()
    {
        var collector = NewCollector();

        var result = collector.ProcessPotentialIncident(
            route: "/x", method: "GET", statusCode: 0, durationMs: 10,
            exceptionType: "ArgumentException", exceptionMessage: "bad", stackTrace: "at X",
            traceId: null, spanId: null, requestTimestampMs: 1000);

        result.Should().NotBeNull();
        result!.TriggerType.Should().Be("exception");
        result.Severity.Should().Be("high", "no 5xx status, but an exception is present");
    }

    [Fact]
    public void FastSuccess_DoesNotTrigger()
    {
        var collector = NewCollector();

        var result = collector.ProcessPotentialIncident(
            route: "/x", method: "GET", statusCode: 200, durationMs: 10,
            exceptionType: null, exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1000);

        result.Should().BeNull();
    }

    [Fact]
    public void SlowSuccess_TriggersLatency_WithMediumSeverity()
    {
        // Default latency threshold is 5000ms.
        var collector = NewCollector();

        var result = collector.ProcessPotentialIncident(
            route: "/slow", method: "GET", statusCode: 200, durationMs: 6000,
            exceptionType: null, exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1000);

        result.Should().NotBeNull();
        result!.TriggerType.Should().Be("latency");
        result.Severity.Should().Be("medium");
    }

    /// <summary>
    /// The exemplar timestamp is the incident time — when the request finished and the breach became
    /// true — not when the request started. It is derived from request start plus duration, so it
    /// stays consistent with the emitted <c>duration_ms</c> without a second clock read.
    /// </summary>
    /// <remarks>
    /// This matters most for latency incidents, which by definition ran past the threshold: anchoring
    /// on request start would place the exemplar at least a threshold's worth of time in the past.
    /// Asserted with a duration far larger than any plausible clock jitter so the two candidate
    /// anchors cannot be confused.
    /// </remarks>
    [Fact]
    public void OnTrigger_ExemplarTimestampIsRequestEnd_NotRequestStart()
    {
        var collector = NewCollector();

        var result = collector.ProcessPotentialIncident(
            route: "/slow", method: "GET", statusCode: 200, durationMs: 6000,
            exceptionType: null, exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1_000_000);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be(1_006_000, "request start (1_000_000) plus the 6000ms duration");
    }

    /// <summary>
    /// A rejected request must release its batch-dedup claim, or the error is treated as already
    /// handled for the rest of the flush cycle and every later occurrence is silently suppressed —
    /// on behalf of a snapshot that was never emitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scope stated precisely: this covers the <b>rate-limit</b> rejection path. That is the reachable
    /// claim-then-reject ordering — a request cannot be rejected by per-error dedup on its own hash's
    /// first appearance in a cycle, because the ceiling is clamped to at least one, and any later
    /// same-hash request in the same cycle is stopped by batch dedup before a limiter is consulted.
    /// The two release calls are otherwise identical.
    /// </para>
    /// <para>
    /// Mutation-verified: deleting the <c>UnclaimBatchHash</c> call on the rate-limit path makes the
    /// final assertion fail, because the leaked claim then makes batch dedup reject the retry.
    /// </para>
    /// <para>
    /// The two requests use <i>different</i> error signatures on purpose. If the rejected request
    /// shared a hash with the admitted one, the admitted request would legitimately still hold that
    /// claim for the rest of the cycle and the test would prove nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void RateLimitedRequest_ReleasesItsBatchClaim_SoARetryInTheSameCycleStillEmits()
    {
        var collector = NewCollector(new ServiceEventsConfig
        {
            IncidentSnapshotMaxPerMinute = 1,
            IncidentSnapshotMaxSameError = int.MaxValue,
        });

        // Burn the single global slot on one signature. Admitted, and legitimately keeps its claim.
        var first = collector.ProcessPotentialIncident(
            route: "/a", method: "GET", statusCode: 500, durationMs: 1,
            exceptionType: "ExA", exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1000);
        first.Should().NotBeNull();

        // A different signature: claims its own hash, passes per-error dedup, then is rejected by the
        // exhausted global cap — the one ordering where a claim must be given back.
        var rejected = collector.ProcessPotentialIncident(
            route: "/b", method: "GET", statusCode: 500, durationMs: 1,
            exceptionType: "ExB", exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1001);
        rejected.Should().BeNull("the global cap of 1 was already spent by the first request");

        // Same flush cycle, same signature as the rejected one. With the cap raised this must be
        // admitted; if the rejected request had left "/b + ExB" claimed, batch dedup would reject it.
        collector.UpdateIncidentConfig(maxPerMinute: 100, maxSameError: int.MaxValue);

        var retried = collector.ProcessPotentialIncident(
            route: "/b", method: "GET", statusCode: 500, durationMs: 1,
            exceptionType: "ExB", exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 1002);

        retried.Should().NotBeNull("the rate-limited request released its batch claim");
    }

    /// <summary>
    /// Concurrent same-hash requests are serialized by the atomic claim, so only one of them reaches
    /// the limiters and spends global budget — the rest are stopped at the claim and cost nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The observable is deliberately <b>not</b> the number of snapshots admitted for the contended
    /// hash. That number is one under either design, because a per-error ceiling would reject the
    /// duplicates anyway; a test asserting it proves nothing about the claim. Verified: an earlier
    /// version of this test did exactly that and passed under the mutation below.
    /// </para>
    /// <para>
    /// What distinguishes the designs is what the losers <i>consume</i>. With a non-claiming check,
    /// every racing thread reaches <c>CheckRateLimit</c>, and each one increments the global counter
    /// whether or not it goes on to produce a snapshot — so a modest global cap is exhausted by a
    /// single hot error, and an unrelated error later in the same window is silenced. With the atomic
    /// claim, exactly one request per hash is ever counted.
    /// </para>
    /// <para>
    /// Mutation-verified: reverting the claim to a non-claiming <c>Contains</c> check fails this test,
    /// because the global cap is spent by the contended hash and the unrelated error is rejected.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConcurrentSameHashRequests_DoNotExhaustTheGlobalCapForOtherErrors()
    {
        const int threads = 32;
        const int callsPerThread = 200;
        const int maxPerMinute = 10;

        var collector = NewCollector(new ServiceEventsConfig
        {
            IncidentSnapshotMaxPerMinute = maxPerMinute,

            // Isolate the batch claim as the only thing that can stop the duplicates: with a low
            // per-error ceiling the dedup gate would reject them first and mask the difference.
            IncidentSnapshotMaxSameError = int.MaxValue,
        });

        var admitted = 0;
        var workers = new Thread[threads];
        using var start = new ManualResetEventSlim(false);

        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                start.Wait();
                for (var i = 0; i < callsPerThread; i++)
                {
                    var r = collector.ProcessPotentialIncident(
                        route: "/hot", method: "GET", statusCode: 500, durationMs: 1,
                        exceptionType: "TheSameError", exceptionMessage: null, stackTrace: null,
                        traceId: null, spanId: null, requestTimestampMs: 1000 + i);

                    if (r is not null)
                    {
                        Interlocked.Increment(ref admitted);
                    }
                }
            });
            workers[t].Start();
        }

        start.Set();
        foreach (var w in workers)
        {
            w.Join();
        }

        admitted.Should().Be(1, "one hash yields one snapshot per flush cycle");

        // The real assertion: the contended hash consumed one global slot, not thousands, so an
        // unrelated error in the same window still has budget.
        var unrelated = collector.ProcessPotentialIncident(
            route: "/elsewhere", method: "GET", statusCode: 500, durationMs: 1,
            exceptionType: "ADifferentError", exceptionMessage: null, stackTrace: null,
            traceId: null, spanId: null, requestTimestampMs: 2000);

        unrelated.Should().NotBeNull(
            "{0} racing duplicates of one hash must spend one global slot between them, not one each",
            threads * callsPerThread);
    }

    /// <summary>
    /// The emitted exception message and stack trace are length-bounded, and the bound is marked so a
    /// consumer can distinguish a truncated value from a naturally short one.
    /// </summary>
    /// <remarks>
    /// <c>call_path</c> is deliberately derived from the <i>untruncated</i> stack trace, so capping the
    /// emitted string does not cost frames. Asserted here by building a trace whose frames sit beyond
    /// the character cap and checking they still parse.
    /// </remarks>
    [Fact]
    public void OverlongExceptionText_IsTruncatedWithAMarker_AndCallPathIsUnaffected()
    {
        var (collector, logger) = NewCapturingCollector();

        var hugeMessage = new string('m', IncidentSnapshotCollector.MaxExceptionMessageChars + 500);

        // A trace long enough to pass the cap, with real frames spread throughout so parsing is
        // exercised on text that extends past the truncation point.
        var frame = "   at Contoso.Service.Layer.Handle(System.String arg) in /src/Layer.cs:line 42\n";
        var repeats = (IncidentSnapshotCollector.MaxStackTraceChars / frame.Length) + 50;
        var hugeStack = string.Concat(Enumerable.Range(0, repeats).Select(i =>
            frame.Replace("Layer.Handle", "Layer" + i + ".Handle", StringComparison.Ordinal)));
        hugeStack.Length.Should().BeGreaterThan(IncidentSnapshotCollector.MaxStackTraceChars);

        var result = collector.ProcessPotentialIncident(
            route: "/x", method: "GET", statusCode: 500, durationMs: 1,
            exceptionType: "BoomException", exceptionMessage: hugeMessage, stackTrace: hugeStack,
            traceId: null, spanId: null, requestTimestampMs: 1000);

        result.Should().NotBeNull();

        var info = EmittedExceptionInfo(collector, logger);

        var message = info.GetProperty("exception_message").GetString()!;
        message.Should().HaveLength(
            IncidentSnapshotCollector.MaxExceptionMessageChars + IncidentSnapshotCollector.TruncatedSuffix.Length);
        message.Should().EndWith(IncidentSnapshotCollector.TruncatedSuffix);

        var stack = info.GetProperty("stack_trace").GetString()!;
        stack.Should().HaveLength(
            IncidentSnapshotCollector.MaxStackTraceChars + IncidentSnapshotCollector.TruncatedSuffix.Length);
        stack.Should().EndWith(IncidentSnapshotCollector.TruncatedSuffix);

        // Frames come from the untruncated text, so capping the emitted string costs no frames:
        // the 100-frame cap is still reached and the sentinel appended.
        var callPath = info.GetProperty("call_path");
        callPath.GetArrayLength().Should().Be(CallPathCapture.MaxFrames + 1);
        callPath[0].GetProperty("function_name").GetString().Should().Be("Contoso.Service.Layer0.Handle");
    }

    /// <summary>Exception text at or under the cap is emitted unchanged, with no marker.</summary>
    [Fact]
    public void ShortExceptionText_IsEmittedUnchanged()
    {
        var (collector, logger) = NewCapturingCollector();

        var result = collector.ProcessPotentialIncident(
            route: "/x", method: "GET", statusCode: 500, durationMs: 1,
            exceptionType: "BoomException", exceptionMessage: "a short message",
            stackTrace: "   at Contoso.Api.Do() in /src/Api.cs:line 7",
            traceId: null, spanId: null, requestTimestampMs: 1000);

        result.Should().NotBeNull();

        var info = EmittedExceptionInfo(collector, logger);
        info.GetProperty("exception_message").GetString().Should().Be("a short message");
        info.GetProperty("stack_trace").GetString().Should()
            .NotContain(IncidentSnapshotCollector.TruncatedSuffix);
    }

    [Fact]
    public void LatencyThreshold_HonorsPerOperationOverride()
    {
        var config = new ServiceEventsConfig
        {
            LatencyThresholds = new[] { "GET /fast:50" },
        };
        var collector = NewCollector(config);

        // 200ms exceeds the 50ms override for GET /fast.
        collector.ProcessPotentialIncident(
            "/fast", "GET", 200, 200, null, null, null, null, null, 1000)
            !.TriggerType.Should().Be("latency");

        // 40ms is under the override → no trigger.
        collector.ProcessPotentialIncident(
            "/fast", "GET", 200, 40, null, null, null, null, null, 1000)
            .Should().BeNull();
    }

    [Fact]
    public void BatchDedup_BlocksSecondIdenticalErrorInSameCycle()
    {
        var collector = NewCollector();

        var first = collector.ProcessPotentialIncident(
            "/x", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000);
        var second = collector.ProcessPotentialIncident(
            "/x", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000);

        first.Should().NotBeNull();
        second.Should().BeNull("the same error hash is batch-deduplicated within a flush cycle");
    }

    [Fact]
    public void BatchDedup_ResetsAfterFlush()
    {
        // Two limiters gate the same error: batch dedup (one snapshot per hash per flush cycle,
        // reset by Collect) and the rate limiter's per-error ceiling (maxSameError per minute).
        // This test is about the first, so the second is given headroom — at the default
        // maxSameError of 1 the per-minute ceiling binds first and the batch reset is invisible,
        // which would make the assertion below pass or fail for the wrong reason.
        var collector = NewCollector(new ServiceEventsConfig { IncidentSnapshotMaxSameError = 2 });

        collector.ProcessPotentialIncident("/x", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000)
            .Should().NotBeNull();
        collector.ProcessPotentialIncident("/x", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000)
            .Should().BeNull("batch dedup allows one snapshot per error hash per flush cycle");

        collector.Flush(); // new batch window

        collector.ProcessPotentialIncident("/x", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000)
            .Should().NotBeNull("a new flush cycle resets batch dedup (still within the per-error ceiling)");
    }

    [Fact]
    public void DifferentOperations_NotDedupedAgainstEachOther()
    {
        var collector = NewCollector();

        collector.ProcessPotentialIncident("/a", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000)
            .Should().NotBeNull();
        collector.ProcessPotentialIncident("/b", "GET", 500, 10, "ArgumentException", "m", "st", null, null, 1000)
            .Should().NotBeNull("a different operation is a different error hash");
    }

    [Fact]
    public void ParseStackTrace_CollapsesConsecutiveDuplicateFrames()
    {
        // Real .NET shape: an inner exception re-lists the throwing method (line 153 then 158
        // across the `--->` / `--- End of inner exception ---` block) and ASP.NET repeats the
        // `…Logged|` wrapper frame. Without dedup this yields a frame whose caller is itself.
        var stackTrace =
            "PetSite.PetSiteDemoFault: Adoption fee calculation failed.\n" +
            " ---> System.DivideByZeroException: Attempted to divide by zero.\n" +
            "   at PetSite.Controllers.AdoptionController.CalculateAdoptionFee(String pettype, Pet pet) in /src/Controllers/AdoptionController.cs:line 153\n" +
            "   --- End of inner exception stack trace ---\n" +
            "   at PetSite.Controllers.AdoptionController.CalculateAdoptionFee(String pettype, Pet pet) in /src/Controllers/AdoptionController.cs:line 158\n" +
            "   at PetSite.Controllers.AdoptionController.TakeMeHome(SearchParams searchParams, String userId) in /src/Controllers/AdoptionController.cs:line 125\n" +
            "   at Microsoft.AspNetCore.Mvc.Infrastructure.ResourceInvoker.<InvokeAsync>g__Logged|17_1(ResourceInvoker invoker)\n" +
            "   at Microsoft.AspNetCore.Mvc.Infrastructure.ResourceInvoker.<InvokeAsync>g__Logged|17_1(ResourceInvoker invoker)\n" +
            "   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol.ProcessRequests[TContext](IHttpApplication`1 application)";

        var frames = IncidentSnapshotCollector.ParseStackTrace(stackTrace);

        // 4 distinct frames after collapsing the two CalculateAdoptionFee and the two Logged| dups.
        frames.Select(f => f.FunctionName).Should().Equal(
            "PetSite.Controllers.AdoptionController.CalculateAdoptionFee",
            "PetSite.Controllers.AdoptionController.TakeMeHome",
            "Microsoft.AspNetCore.Mvc.Infrastructure.ResourceInvoker.<InvokeAsync>g__Logged|17_1",
            "Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol.ProcessRequests[TContext]");

        // Adjacency-list model: no frame may be its own caller, and no two
        // consecutive frames may share a function name.
        frames.Should().OnlyContain(f => f.CallerFunctionName != f.FunctionName, "no frame calls itself");
        for (var i = 1; i < frames.Count; i++)
        {
            frames[i].FunctionName.Should().NotBe(frames[i - 1].FunctionName, "consecutive duplicates are collapsed");
        }

        // The innermost/throw frame is first, points at its real caller, and is the only error frame.
        frames[0].CallerFunctionName.Should().Be("PetSite.Controllers.AdoptionController.TakeMeHome");
        frames[0].Error.Should().BeTrue();
        frames.Skip(1).Should().OnlyContain(f => !f.Error, "only the innermost frame is the throw site");

        // The outermost frame has no caller.
        frames[^1].CallerFunctionName.Should().BeNull();
    }

    [Fact]
    public void ParseStackTrace_EmptyOrNull_ReturnsNoFrames()
    {
        IncidentSnapshotCollector.ParseStackTrace(null).Should().BeEmpty();
        IncidentSnapshotCollector.ParseStackTrace(string.Empty).Should().BeEmpty();
    }
}
