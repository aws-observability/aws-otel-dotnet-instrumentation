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

    /// <summary>
    /// A local endpoint that completes the TCP handshake and then never answers.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UnreachableEndpoint" />, and necessary for the pipeline test: an
    /// unroutable address fails at connect, whose duration the OS decides, whereas a hung service
    /// accepts and leaves the request outstanding for as long as the client allows. The latter is the
    /// failure this bound exists for, and the only one where the elapsed time is attributable to our
    /// deadline rather than to a connect timeout.
    /// </remarks>
    private sealed class HangingEndpoint : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly List<System.Net.Sockets.TcpClient> accepted = new();

        public HangingEndpoint()
        {
            this.listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            this.listener.Start();
            var port = ((System.Net.IPEndPoint)this.listener.LocalEndpoint).Port;
            this.Url = $"http://127.0.0.1:{port}/v1/logs";

            // Accept and hold. Never write a response, so the client waits until its own limit.
            _ = Task.Run(
                async () =>
                {
                    while (!this.cts.IsCancellationRequested)
                    {
                        var client = await this.listener.AcceptTcpClientAsync(this.cts.Token).ConfigureAwait(false);
                        lock (this.accepted)
                        {
                            this.accepted.Add(client);
                        }
                    }
                },
                this.cts.Token);
        }

        public string Url { get; }

        public void Dispose()
        {
            this.cts.Cancel();
            lock (this.accepted)
            {
                foreach (var client in this.accepted)
                {
                    client.Dispose();
                }
            }

            this.listener.Stop();
            this.cts.Dispose();
        }
    }

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
    /// A shutdown timeout of <c>0</c> expires the deadline immediately, rather than leaving the exporter
    /// unbounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>0</c> and <c>Timeout.Infinite</c> are opposites in the OTel contract — give up now versus no
    /// limit — and an earlier guard of <c>&gt; 0</c> collapsed them, so a zero budget produced a
    /// ten-second attempt against the static client timeout.
    /// </para>
    /// <para>
    /// Reachable rather than theoretical: <c>BatchExportProcessor.OnShutdown</c> forwards
    /// <c>exporter.Shutdown(0)</c> verbatim when its own timeout is 0, and clamps to 0 whenever the drain
    /// has already consumed the whole budget — the hung-export case this bound exists for.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithAZeroShutdownTimeout_ExportIsAbandonedImmediately()
    {
        using var listener = new Listener();
        using var exporter = new ServiceEventsOtlpLogExporter(UnreachableEndpoint);

        exporter.Shutdown(timeoutMilliseconds: 0);

        var sw = Stopwatch.StartNew();
        var result = exporter.Export(OneRecord());
        sw.Stop();

        result.Should().Be(ExportResult.Failure);
        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "a zero budget means give up now, not run unbounded");

        listener.Events
            .Should().Contain(
                e => e.EventId == 5
                     && string.Equals(e.Payload![0] as string, UnreachableEndpoint, StringComparison.Ordinal),
                "abandoning on a zero budget is still an abandonment and must be reported");
    }

    /// <summary>
    /// Through the real pipeline — logger factory, batch processor, exporter — teardown against a hung
    /// endpoint is bounded by the armed budget rather than by the SDK's grace period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test whose absence let an ineffective version of this bound ship. The other cases here
    /// call <c>exporter.Shutdown()</c> directly, which exercises the exporter but not the integration,
    /// and the integration is where the mechanism actually has to work.
    /// </para>
    /// <para>
    /// Two SDK details make the direct route insufficient. <c>exporterTimeoutMilliseconds</c> on the batch
    /// processor is never read in 1.16.0 — the export is a bare <c>Export(batch)</c> with no timeout — and
    /// <c>BatchExportProcessor.OnShutdown</c> drains <i>before</i> calling <c>exporter.Shutdown</c>, so the
    /// exporter's own hook cannot bound the drain it was written for. What governs teardown otherwise is a
    /// hardcoded <c>Processor?.Shutdown(5000)</c> in <c>LoggerProviderSdk.Dispose</c>.
    /// </para>
    /// <para>
    /// So the ceiling below is chosen to discriminate between the two outcomes, not to pin a latency:
    /// bounded teardown finishes near the budget, unbounded finishes near the SDK's 5s, and 3s separates
    /// them with room for a slow CI agent on either side.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThroughTheRealPipeline_TeardownIsBoundedByTheArmedBudget()
    {
        using var hanging = new HangingEndpoint();
        using var listener = new Listener();

        var exporter = new ServiceEventsOtlpLogExporter(hanging.Url);
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.AddProcessor(new BatchLogRecordExportProcessor(exporter));
            });
        });

        // Something has to be queued, or the drain has nothing to do and the test proves nothing.
        factory.CreateLogger("pipeline-shutdown-bound").LogInformation("queued for the drain");

        exporter.BeginShutdown(TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        factory.Dispose();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(3),
            "the armed budget must bound the drain; unbounded teardown waits out the SDK's 5s grace period");

        listener.Events
            .Should().Contain(
                e => (e.EventId == 1 || e.EventId == 5)
                     && e.Payload!.OfType<string>().Any(p => p.Contains(hanging.Url, StringComparison.Ordinal)),
                "giving up on the final batch must be reported, whichever way it was given up on");
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
