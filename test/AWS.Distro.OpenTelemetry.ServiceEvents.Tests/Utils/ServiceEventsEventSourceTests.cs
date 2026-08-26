// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Tracing;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Utils;

/// <summary>
/// Tests for <see cref="ServiceEventsEventSource" />.
/// </summary>
/// <remarks>
/// These exist because a malformed <see cref="EventSource" /> does not throw — the runtime disables it
/// and every write becomes a no-op, which is indistinguishable from "nothing went wrong". A mismatch
/// between an <c>[Event(n)]</c> attribute and its <c>WriteEvent(n, ...)</c> call, or between the
/// declared parameters and the arguments passed, would ship silently without a test that actually
/// attaches a listener and reads the payload back.
/// <para>
/// Mutation-verified: dropping one argument from a <c>WriteEvent</c> call while leaving its declared
/// parameters intact fails <see cref="EveryEvent_ReachesAListener_WithItsPayload" />. The observed
/// signature is worth knowing, because it is not an exception — the runtime silently substitutes an
/// <c>EventId 0</c> event on its own internal error channel and the real event never arrives. That is
/// exactly how such a defect would reach production unnoticed.
/// </para>
/// </remarks>
public class ServiceEventsEventSourceTests
{
    /// <summary>Captures events from the ServiceEvents provider for the lifetime of the instance.</summary>
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
                // Verbose so the IncidentDropped event (deliberately verbose) is captured too.
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

    /// <summary>
    /// The provider is well-formed and discoverable under its contracted name.
    /// </summary>
    /// <remarks>
    /// <c>EventSource.ConstructionException</c> is the runtime's record of a definition it rejected.
    /// Non-null here means the type is malformed and every event on it is silently dropped.
    /// </remarks>
    [Fact]
    public void EventSource_IsWellFormed()
    {
        ServiceEventsEventSource.Log.ConstructionException.Should().BeNull(
            "a malformed EventSource is disabled by the runtime rather than throwing");
        ServiceEventsEventSource.Log.Name.Should().Be("OpenTelemetry-AWS-ServiceEvents");
    }

    /// <summary>
    /// Every event reaches a listener with the right ID, level and payload.
    /// </summary>
    /// <remarks>
    /// Asserting the payload matters as much as the ID: an <c>[Event]</c> whose <c>WriteEvent</c>
    /// arguments do not match its declared parameters is accepted at compile time and dropped at
    /// runtime.
    /// </remarks>
    [Fact]
    public void EveryEvent_ReachesAListener_WithItsPayload()
    {
        using var listener = new Listener();

        ServiceEventsEventSource.Log.ExportFailed("http://localhost:4316/v1/logs", new InvalidOperationException("boom"));
        ServiceEventsEventSource.Log.FileWriteFailed("/tmp/out.json", new IOException("disk full"));
        ServiceEventsEventSource.Log.CollectFailed("IncidentSnapshot", new InvalidOperationException("collect boom"));
        ServiceEventsEventSource.Log.IncidentDropped(ServiceEventsEventSource.DropReason.RateLimit, "GET /orders");
        ServiceEventsEventSource.Log.ExportAbandonedOnShutdown("http://localhost:4316/v1/logs", 0);
        ServiceEventsEventSource.Log.OutputFileRotated("/tmp/out.json", 1024L);

        var events = listener.Events;

        events.Select(e => e.EventId).Should().BeEquivalentTo(
            new[] { 1, 2, 3, 4, 5, 6 },
            "every declared event should have been delivered exactly once");

        var export = events.Single(e => e.EventId == 1);
        export.Level.Should().Be(EventLevel.Error);
        export.Payload!.Should().HaveCount(2);
        export.Payload![0].Should().Be("http://localhost:4316/v1/logs");
        (export.Payload![1] as string).Should().Contain("boom");

        var fileWrite = events.Single(e => e.EventId == 2);
        fileWrite.Level.Should().Be(EventLevel.Error);
        (fileWrite.Payload![1] as string).Should().Contain("disk full");

        var collect = events.Single(e => e.EventId == 3);
        collect.Level.Should().Be(EventLevel.Error);
        collect.Payload![0].Should().Be("IncidentSnapshot");

        var dropped = events.Single(e => e.EventId == 4);
        dropped.Level.Should().Be(EventLevel.Verbose, "suppression is normal and should not look like an error");
        dropped.Payload![0].Should().Be("rate_limit");
        dropped.Payload![1].Should().Be("GET /orders");

        var abandoned = events.Single(e => e.EventId == 5);
        abandoned.Level.Should().Be(EventLevel.Warning);
        abandoned.Payload![1].Should().Be(0);

        var rotated = events.Single(e => e.EventId == 6);
        rotated.Level.Should().Be(EventLevel.Informational);
        rotated.Payload![1].Should().Be(1024L);
    }

    /// <summary>
    /// The exception-taking overloads cost nothing when no listener is attached — they must not call
    /// <c>ToString()</c> on the exception, which is the expensive part.
    /// </summary>
    /// <remarks>
    /// Verified by observing the side effect rather than the timing: a custom exception counts its own
    /// <c>ToString()</c> calls. With no listener the count must stay at zero, which is what makes it
    /// safe to call these from a bare catch on any path.
    /// </remarks>
    [Fact]
    public void WithNoListener_ExceptionIsNotStringified()
    {
        var ex = new CountingException();

        ServiceEventsEventSource.Log.ExportFailed("endpoint", ex);
        ServiceEventsEventSource.Log.FileWriteFailed("path", ex);
        ServiceEventsEventSource.Log.CollectFailed("collector", ex);

        ex.ToStringCalls.Should().Be(0, "the IsEnabled guard should short-circuit before stringifying");
    }

    private sealed class CountingException : Exception
    {
        private int toStringCalls;

        public int ToStringCalls => Volatile.Read(ref this.toStringCalls);

        public override string ToString()
        {
            Interlocked.Increment(ref this.toStringCalls);
            return base.ToString();
        }
    }
}
