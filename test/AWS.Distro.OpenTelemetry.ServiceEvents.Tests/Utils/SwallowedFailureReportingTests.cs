// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Tracing;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;

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
/// Shares a collection with <see cref="ServiceEventsEventSourceTests" /> because an
/// <see cref="EventSource" /> is <b>process global</b> — run in parallel, each class's listener
/// captures the other's events and both fail on traffic they did not produce.
/// </para>
/// </remarks>
[Collection("ServiceEventsDiagnostics")]
public class SwallowedFailureReportingTests
{
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
    }

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
    /// lose a whole flush window with no trace, which is the finding.
    /// </remarks>
    [Fact]
    public void CollectorThrowingDuringFlush_IsReported_AndDoesNotPropagate()
    {
        using var listener = new Listener();
        var collector = new ThrowingCollector();

        // Dispose runs the final flush, which is the cheapest way to drive one Collect().
        var act = () => collector.Dispose();

        act.Should().NotThrow("telemetry must never crash the host");

        var collectFailed = listener.Events.Should().ContainSingle(e => e.EventId == 3).Subject;
        collectFailed.Payload![0].Should().Be("ThrowingTestCollector");
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

        var failed = listener.Events.Should().ContainSingle(e => e.EventId == 7).Subject;
        failed.Payload![0].Should().Be("EndpointActivityProcessor.OnEnd");
        (failed.Payload![1] as string).Should().Contain("recorder exploded");
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
