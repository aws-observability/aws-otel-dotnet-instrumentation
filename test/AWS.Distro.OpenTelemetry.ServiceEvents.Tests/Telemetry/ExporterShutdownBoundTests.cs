// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Tracing;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Telemetry;

/// <summary>
/// Tests that the OTLP log exporter stops attempting exports once its shutdown deadline has passed,
/// rather than blocking the host's exit on a network timeout.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour under test is a time bound, so these use an endpoint that cannot be reached. Every
/// case drives the exporter through the same public seam the SDK uses — <c>Shutdown</c>, which invokes
/// <c>OnShutdown</c> — rather than reaching into internals.
/// </para>
/// <para>
/// Shares the diagnostics collection because the assertions read the process-global EventSource, and
/// filters on an endpoint no other test uses.
/// </para>
/// </remarks>
[Collection("ServiceEventsDiagnostics")]
public class ExporterShutdownBoundTests
{
    /// <summary>
    /// Unroutable by construction: 203.0.113.0/24 is reserved for documentation, so a connection
    /// attempt cannot succeed and cannot reach a real service by accident.
    /// </summary>
    private const string UnreachableEndpoint = "http://203.0.113.1:4316/v1/logs";

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

    /// <summary>
    /// Once the shutdown deadline has passed, an export returns immediately instead of attempting the
    /// network call.
    /// </summary>
    /// <remarks>
    /// The assertion is on elapsed time, which is the only observable that distinguishes the two
    /// behaviours. A generous ceiling is used deliberately: the point is to separate "returned without
    /// dialling" from "waited on a connection", and the unbounded path would previously have spent up
    /// to the client's ten-second timeout here. A tight bound would trade a real signal for CI flakiness.
    /// </remarks>
    [Fact]
    public void AfterTheShutdownDeadlinePasses_ExportReturnsWithoutAttempting()
    {
        using var exporter = new ServiceEventsOtlpLogExporter(UnreachableEndpoint);

        // One millisecond, then let it lapse.
        exporter.Shutdown(timeoutMilliseconds: 1).Should().BeTrue();
        Thread.Sleep(30);

        var sw = Stopwatch.StartNew();
        var result = exporter.Export(OneRecord());
        sw.Stop();

        result.Should().Be(ExportResult.Failure, "the batch could not be delivered");
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "an expired deadline must short-circuit rather than dial an unreachable endpoint");
    }

    /// <summary>Abandoning the final batch is reported, since losing telemetry silently is the thing
    /// the diagnostics channel exists to prevent.</summary>
    [Fact]
    public void AbandoningAnExportOnShutdown_IsReported()
    {
        using var listener = new Listener();
        using var exporter = new ServiceEventsOtlpLogExporter(UnreachableEndpoint);

        exporter.Shutdown(timeoutMilliseconds: 1);
        Thread.Sleep(30);
        exporter.Export(OneRecord());

        var abandoned = listener.Events.Should().ContainSingle(
            e => e.EventId == 5
                 && string.Equals(e.Payload![0] as string, UnreachableEndpoint, StringComparison.Ordinal))
            .Subject;

        abandoned.Level.Should().Be(EventLevel.Warning);
    }

    /// <summary>
    /// A shutdown timeout of <c>Timeout.Infinite</c> leaves the exporter unbounded, preserving the
    /// SDK's meaning for it rather than treating it as "expire now".
    /// </summary>
    /// <remarks>
    /// Asserted by observing that the export still attempts the connection — it takes real time and
    /// reports a transport failure rather than an abandonment. Without this, a caller passing the
    /// SDK's own "no limit" value would silently lose every record.
    /// </remarks>
    [Fact]
    public void WithAnInfiniteShutdownTimeout_ExportStillAttempts()
    {
        using var listener = new Listener();
        using var exporter = new ServiceEventsOtlpLogExporter(UnreachableEndpoint);

        exporter.Shutdown(Timeout.Infinite);
        exporter.Export(OneRecord()).Should().Be(ExportResult.Failure);

        listener.Events
            .Where(e => e.EventId == 5
                        && string.Equals(e.Payload![0] as string, UnreachableEndpoint, StringComparison.Ordinal))
            .Should().BeEmpty("an infinite timeout is not an expired deadline");
    }

    /// <summary>
    /// A single-record batch, which is all these tests need: <c>Export</c> returns early on an empty
    /// batch, before the path under test.
    /// </summary>
    /// <remarks>
    /// A <see cref="LogRecord" /> cannot be constructed directly — the SDK owns its lifetime — so one is
    /// captured from a real pipeline. Its field values are irrelevant here; only that there is a record
    /// to serialize, so the export proceeds as far as the network call.
    /// </remarks>
    /// <returns>A batch of one record.</returns>
    private static Batch<LogRecord> OneRecord()
    {
        LogRecord? captured = null;

        using var factory = LoggerFactory.Create(builder =>
            builder.AddOpenTelemetry(options =>
                options.AddProcessor(new CapturingProcessor(r => captured = r))));

        factory.CreateLogger("shutdown-bound-tests").LogInformation("record");

        captured.Should().NotBeNull("a LogRecord is required to exercise Export");
        return new Batch<LogRecord>(new[] { captured! }, 1);
    }

    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        private readonly Action<LogRecord> onEnd;

        public CapturingProcessor(Action<LogRecord> onEnd) => this.onEnd = onEnd;

        public override void OnEnd(LogRecord data) => this.onEnd(data);
    }
}
