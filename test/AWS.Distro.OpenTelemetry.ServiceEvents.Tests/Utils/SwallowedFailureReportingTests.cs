// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Diagnostics.Metrics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Utils;

/// <summary>
/// Proves the swallowed-failure paths actually report, rather than merely having an EventSource
/// available to them.
/// </summary>
/// <remarks>
/// The failures these cover are, by design, invisible: every one of them ends in a catch that drops
/// the data and returns normally. A test that only checked "no exception escaped" would pass whether
/// or not anything was reported, so each case here forces a real failure and asserts the event.
/// <para>
/// An <see cref="EventSource" /> is <b>process global</b>, which shapes how these assert. A listener
/// sees every event any test in the assembly produces, and most of this suite exercises the incident
/// path — one concurrency test alone suppresses thousands of incidents. So a test must never assert
/// over <i>all</i> captured events, only over its own: each one below uses a route no other test uses
/// and filters on it. Sharing a collection with <see cref="ServiceEventsEventSourceTests" /> keeps
/// those two from interleaving, but it cannot isolate them from the rest of the assembly, which runs
/// in parallel.
/// </para>
/// </remarks>
[Collection("ServiceEventsDiagnostics")]
public class SwallowedFailureReportingTests
{
    /// <summary>Routes unique to this file, so events can be attributed to the test that caused them.</summary>
    private const string DedupRoute = "/diag-dedup-reasons";

    /// <summary>Companion to <see cref="DedupRoute" />; see its remarks.</summary>
    private const string RateLimitAdmittedRoute = "/diag-ratelimit-admitted";

    /// <summary>Companion to <see cref="DedupRoute" />; see its remarks.</summary>
    private const string RateLimitRejectedRoute = "/diag-ratelimit-rejected";

    /// <summary>Captures ServiceEvents diagnostics for the lifetime of the instance.</summary>
    private sealed class Listener : EventListener
    {
        private readonly List<EventWrittenEventArgs> events = new();
        private readonly object gate = new();

        public IReadOnlyList<EventWrittenEventArgs> Events
        {
            get
            {
                lock (this.gate)
                {
                    return this.events.ToList();
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, ServiceEventsEventSource.EventSourceName, StringComparison.Ordinal))
            {
                this.EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (this.gate)
            {
                this.events.Add(eventData);
            }
        }

        /// <summary>
        /// The suppression reasons reported for one operation, in order.
        /// </summary>
        /// <remarks>
        /// Filtering by operation is what makes these tests deterministic: the provider is process
        /// global, so the capture also contains incidents suppressed by tests running in parallel.
        /// </remarks>
        /// <param name="operation">The operation to attribute events to.</param>
        /// <returns>Reason strings for that operation only.</returns>
        public IReadOnlyList<string> DropReasonsFor(string operation)
            => this.Events
                .Where(e => e.EventId == 4
                            && string.Equals(e.Payload![1] as string, operation, StringComparison.Ordinal))
                .Select(e => (e.Payload![0] as string)!)
                .ToList();
    }

    /// <summary>
    /// Each way an incident can be suppressed reports its own reason, so an operator can tell a quiet
    /// service from a throttled one — and can tell which limit was responsible.
    /// </summary>
    /// <remarks>
    /// The per-error ceiling and the cardinality guard are the pair worth separating. Hitting the
    /// ceiling means one error is noisy and raising it would admit more. Hitting the guard means the
    /// service is producing more distinct errors than the window tracks, so raising the ceiling changes
    /// nothing. Before <c>DedupOutcome</c> both returned <c>false</c> and were indistinguishable.
    /// </remarks>
    [Fact]
    public void SuppressedIncidents_ReportTheirReason()
    {
        using var listener = new Listener();

        // maxSameError 1 so the second identical error is rejected by the per-error ceiling, and a
        // generous global cap so the rate limiter is not what rejects.
        var collector = NewCollector(new ServiceEventsConfig
        {
            IncidentSnapshotMaxPerMinute = int.MaxValue,
            IncidentSnapshotMaxSameError = 1,
        });

        // First is admitted, and claims the hash for this flush cycle.
        Trigger(collector, DedupRoute, "ExA").Should().NotBeNull();

        // Same hash, same cycle: stopped by batch dedup before any limiter is consulted.
        Trigger(collector, DedupRoute, "ExA").Should().BeNull();

        // Clear the batch claim so the next identical error reaches the per-error gate instead.
        collector.Flush();
        Trigger(collector, DedupRoute, "ExA").Should().BeNull();

        var reasons = listener.DropReasonsFor("GET " + DedupRoute);

        reasons.Should().Contain("batch_duplicate");
        reasons.Should().Contain(
            "per_error_limit",
            "the per-error ceiling, not batch dedup, is what rejects once the batch claim is cleared");
    }

    /// <summary>A rate-limited incident reports the rate limit, not a dedup reason.</summary>
    [Fact]
    public void RateLimitedIncident_ReportsTheRateLimit()
    {
        using var listener = new Listener();

        var collector = NewCollector(new ServiceEventsConfig
        {
            IncidentSnapshotMaxPerMinute = 1,
            IncidentSnapshotMaxSameError = int.MaxValue,
        });

        Trigger(collector, RateLimitAdmittedRoute, "ExA").Should().NotBeNull();

        // A different signature, so batch dedup and the per-error gate both pass and the global cap
        // is what rejects.
        Trigger(collector, RateLimitRejectedRoute, "ExB").Should().BeNull();

        listener.DropReasonsFor("GET " + RateLimitRejectedRoute)
            .Should().Equal("rate_limit");

        listener.DropReasonsFor("GET " + RateLimitAdmittedRoute)
            .Should().BeEmpty("the admitted incident was not suppressed");
    }

    private static IncidentSnapshotCollector NewCollector(ServiceEventsConfig config)
    {
        var emitter = new ServiceEventsOtlpEmitter(
            NullLogger.Instance,
            new Meter("drop-reasons-" + Guid.NewGuid().ToString("N")),
            deploymentId: "dep",
            gitCommitSha: "sha",
            gitRepoUrl: "repo",
            serviceCodeNamespace: string.Empty);

        return new IncidentSnapshotCollector(flushIntervalMs: 60_000, emitter, config);
    }

    private static object? Trigger(IncidentSnapshotCollector collector, string route, string exceptionType)
        => collector.ProcessPotentialIncident(
            route: route,
            method: "GET",
            statusCode: 500,
            durationMs: 1,
            exceptionType: exceptionType,
            exceptionMessage: "m",
            stackTrace: "   at A.B() in /f.cs:line 1",
            traceId: null,
            spanId: null,
            requestTimestampMs: 1000);

    /// <summary>A collector whose flush always throws.</summary>
    private sealed class ThrowingCollector : CollectorBase
    {
        public ThrowingCollector()
            : base(flushIntervalMs: 60_000, name: "ThrowingTestCollector")
        {
        }

        protected override void Collect() => throw new InvalidOperationException("collect exploded");
    }

    /// <summary>
    /// A collector throwing during flush reports it, and still does not propagate.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Propagation would break the host — the original contract. Silence would
    /// lose a whole flush window leaving no trace that it happened.
    /// </remarks>
    [Fact]
    public void CollectorThrowingDuringFlush_IsReported_AndDoesNotPropagate()
    {
        using var listener = new Listener();
        var collector = new ThrowingCollector();

        // Dispose runs the final flush, which is the cheapest way to drive one Collect().
        var act = () => collector.Dispose();

        act.Should().NotThrow("telemetry must never crash the host");

        // Filtered to this collector's name, since the provider is process global.
        var collectFailed = listener.Events.Should().ContainSingle(
            e => e.EventId == 3
                 && string.Equals(e.Payload![0] as string, "ThrowingTestCollector", StringComparison.Ordinal))
            .Subject;
        (collectFailed.Payload![1] as string).Should().Contain("collect exploded");
    }

    /// <summary>
    /// A processor failing on a malformed activity reports it against the right component and phase.
    /// </summary>
    /// <remarks>
    /// Driven through <c>OnEnd</c> with a recorder that throws, which is the realistic shape: the
    /// activity is fine and a downstream collector is what fails.
    /// </remarks>
    [Fact]
    public void ProcessorFailingOnEnd_IsReported_WithComponentAndPhase()
    {
        using var listener = new Listener();
        using var source = new ActivitySource("swallowed-failure-tests");
        using var sourceListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(sourceListener);

        var processor = new EndpointActivityProcessor(
            new ThrowingRecorder(),
            new ServiceEventsConfig());

        using var activity = source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/orders");
        activity.Stop();

        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();

        // Filtered on this test's own exception text: other tests also drive OnEnd.
        var failed = listener.Events.Should().ContainSingle(
            e => e.EventId == 7 && (e.Payload![1] as string)!.Contains("recorder exploded", StringComparison.Ordinal))
            .Subject;
        failed.Payload![0].Should().Be("EndpointActivityProcessor.OnEnd");
    }

    private sealed class ThrowingRecorder : IEndpointRecorder
    {
        public void RecordRequest(
            string route,
            string method,
            int statusCode,
            long durationNs,
            string? errorType = null,
            string? functionName = null)
            => throw new InvalidOperationException("recorder exploded");

        public void RecordIncidentExemplar(
            string operation,
            string snapshotId,
            string triggerType,
            string severity,
            long timestamp)
        {
        }
    }
}
