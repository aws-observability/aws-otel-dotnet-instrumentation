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

        // Spec §5 adjacency-list model: no frame may be its own caller, and no two
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
