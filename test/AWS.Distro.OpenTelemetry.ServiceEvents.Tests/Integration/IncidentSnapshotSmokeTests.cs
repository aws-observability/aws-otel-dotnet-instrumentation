// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// End-to-end smoke test for M4: config → IncidentSnapshotCollector → emitter →
/// OUTPUT_FILE. Drives <see cref="ServiceEventsInstrumentation" /> the way the plugin
/// does, feeds an exception-triggered and a latency-triggered incident through the
/// collector, and asserts the <c>aws.service_events.incident_snapshot</c> LogRecords land
/// in the file with the right trigger type, exception_info, and trace context.
/// </summary>
[Collection("EnvironmentVariables")]
public class IncidentSnapshotSmokeTests
{
    private const string TraceIdHex = "0123456789abcdef0123456789abcdef";
    private const string SpanIdHex = "0123456789abcdef";

    [Fact]
    public void IncidentSnapshot_WhenExceptionAndLatencyTriggered_WriteSnapshotsToFile()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-incident-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (var _ = EnvScope.Set(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "incident-smoke",
                ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false",
                ["RESOURCE_DETECTORS_ENABLED"] = "false",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var config = ServiceEventsConfig.FromEnvironment();
                var inst = ServiceEventsInstrumentation.GetOrCreate(config);
                inst.Initialize();
                inst.IsInitialized.Should().BeTrue();

                var collector = inst.IncidentCollector;
                collector.Should().NotBeNull("M4 initialization starts the incident collector");

                // Exception incident: 500 + captured exception, with trace context.
                var exc = collector!.ProcessPotentialIncident(
                    route: "/checkout",
                    method: "POST",
                    statusCode: 500,
                    durationMs: 27.5,
                    exceptionType: "TypeError",
                    exceptionMessage: "did not return a valid response",
                    stackTrace: "Traceback...\n  at Checkout()",
                    traceId: TraceIdHex,
                    spanId: SpanIdHex,
                    requestTimestampMs: 1_775_673_990_638);
                exc.Should().NotBeNull();
                exc!.TriggerType.Should().Be("exception");

                // Latency incident: 200 but slower than the default 5000ms threshold.
                var lat = collector.ProcessPotentialIncident(
                    route: "/slow",
                    method: "GET",
                    statusCode: 200,
                    durationMs: 6000,
                    exceptionType: null,
                    exceptionMessage: null,
                    stackTrace: null,
                    traceId: null,
                    spanId: null,
                    requestTimestampMs: 1_775_673_991_000);
                lat.Should().NotBeNull();
                lat!.TriggerType.Should().Be("latency");

                // Dispose triggers the collector's final flush → emitter → file.
                ServiceEventsInstrumentation.ResetForTests();
            }

            File.Exists(outputFile).Should().BeTrue();
            var docs = File.ReadAllLines(outputFile)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .ToList();

            var snapshots = docs
                .Where(d => d.TryGetProperty("eventName", out var n) && n.GetString() == "aws.service_events.incident_snapshot")
                .ToList();
            snapshots.Should().HaveCount(2, "one exception + one latency snapshot were triggered");

            // --- Exception snapshot ---
            var excSnap = snapshots.Single(s =>
                s.GetProperty("attributes").GetProperty("aws.service_events.trigger_type").GetString() == "exception");
            var ea = excSnap.GetProperty("attributes");
            ea.GetProperty("aws.service_events.operation").GetString().Should().Be("POST /checkout");
            ea.GetProperty("http.response.status_code").GetInt32().Should().Be(500);
            ea.GetProperty("aws.service_events.is_partial").GetBoolean().Should().BeTrue(
                "exception incidents derive call_path from the stack trace, whose frames carry no per-frame timing (duration_ns == 0)");
            ea.GetProperty("aws.service_events.snapshot_id").GetString().Should().StartWith("snap_");

            // Trace context is emitted top-level (IncidentSnapshot is the only signal that
            // carries it). The trace id (the backend join key) is preserved exactly; the
            // span id is the emitter's synthetic child span (a .NET ILogger-bridge limitation).
            excSnap.GetProperty("traceId").GetString().Should().Be(TraceIdHex);
            excSnap.GetProperty("spanId").GetString().Should().NotBeNullOrEmpty().And.HaveLength(16);

            // Body: exception_info with type + message.
            var excInfo = excSnap.GetProperty("body").GetProperty("exception_info");
            excInfo.GetArrayLength().Should().Be(1);
            excInfo[0].GetProperty("exception_type").GetString().Should().Be("TypeError");
            excInfo[0].GetProperty("exception_message").GetString().Should().Be("did not return a valid response");

            // call_path is parsed from the stack trace (C1): the innermost/throw frame is first,
            // its function_name is the parsed method, and it is flagged as the error frame.
            var excCallPath = excInfo[0].GetProperty("call_path");
            excCallPath.GetArrayLength().Should().BeGreaterThan(0, "exception call_path is derived from the stack trace");
            excCallPath[0].GetProperty("function_name").GetString().Should().Be("Checkout");
            excCallPath[0].GetProperty("error").GetBoolean().Should().BeTrue("the innermost frame is the throw site");

            // --- Latency snapshot ---
            var latSnap = snapshots.Single(s =>
                s.GetProperty("attributes").GetProperty("aws.service_events.trigger_type").GetString() == "latency");
            latSnap.GetProperty("attributes").GetProperty("aws.service_events.operation").GetString().Should().Be("GET /slow");
        }
        finally
        {
            ServiceEventsInstrumentation.ResetForTests();
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }
}
