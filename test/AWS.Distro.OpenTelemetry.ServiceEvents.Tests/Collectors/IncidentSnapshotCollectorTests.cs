// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using FluentAssertions;
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
