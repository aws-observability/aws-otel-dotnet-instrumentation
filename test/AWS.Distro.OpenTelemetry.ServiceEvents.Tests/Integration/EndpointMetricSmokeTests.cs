// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// End-to-end smoke test for M3: config → EndpointMetricCollector → emitter →
/// OUTPUT_FILE. Drives <see cref="ServiceEventsInstrumentation" /> the way the
/// plugin does, feeds requests through the collector, and asserts both the
/// <c>EndpointSummary</c> LogRecord and the <c>count</c> Sum metric land in the
/// file with correct values. This is the runtime proof the collector + both
/// pipelines work, not just that they build.
/// </summary>
[Collection("EnvironmentVariables")]
public class EndpointMetricSmokeTests
{
    [Fact]
    public void EndpointMetrics_WhenRequestsRecorded_WriteSummaryAndErrorMetricToFile()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-endpoint-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (var _ = EnvScope.Set(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "endpoint-smoke",
                // App Signals off → EndpointSummary is NOT suppressed, so we can assert it.
                ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false",
                ["RESOURCE_DETECTORS_ENABLED"] = "false",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var config = ServiceEventsConfig.FromEnvironment();
                var inst = ServiceEventsInstrumentation.GetOrCreate(config);
                inst.Initialize();
                inst.IsInitialized.Should().BeTrue();

                var collector = inst.EndpointCollector;
                collector.Should().NotBeNull("M3 initialization starts the endpoint collector");

                // 3 successful + 2 failing (500 with RuntimeError) requests to one endpoint.
                for (var i = 0; i < 3; i++)
                {
                    collector!.RecordRequest("/orders/{id}", "POST", 200, durationNs: 2_000_000);
                }

                for (var i = 0; i < 2; i++)
                {
                    collector!.RecordRequest("/orders/{id}", "POST", 500, durationNs: 8_000_000, errorType: "RuntimeError", functionName: "checkout");
                }

                // A 4xx with a captured exception: increments request.errors but must NOT produce
                // an exception_breakdown entry or a count data point (fault-only gate, spec §3/§7).
                collector!.RecordRequest("/orders/{id}", "POST", 400, durationNs: 1_000_000, errorType: "ValidationError", functionName: "parse");

                // Dispose triggers the collector's final flush → emitter → file.
                ServiceEventsInstrumentation.ResetForTests();
            }

            File.Exists(outputFile).Should().BeTrue();
            var lines = File.ReadAllLines(outputFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var docs = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();

            // --- EndpointSummary LogRecord ---
            var summary = docs.FirstOrDefault(d =>
                d.TryGetProperty("eventName", out var n) && n.GetString() == "aws.service_events.endpoint_summary");
            summary.ValueKind.Should().NotBe(JsonValueKind.Undefined, "an EndpointSummary LogRecord must be written");

            var attrs = summary.GetProperty("attributes");
            attrs.GetProperty("aws.service_events.operation").GetString().Should().Be("POST /orders/{id}");
            attrs.GetProperty("aws.service_events.request.count").GetInt64().Should().Be(6);
            attrs.GetProperty("aws.service_events.request.faults").GetInt64().Should().Be(2);
            attrs.GetProperty("aws.service_events.request.errors").GetInt64().Should().Be(1);

            var duration = summary.GetProperty("body").GetProperty("duration");
            duration.GetProperty("Count").GetInt64().Should().Be(6);

            // --- EndpointErrorMetrics (count Sum metric) ---
            var metricLine = docs.FirstOrDefault(d => d.TryGetProperty("resourceMetrics", out _));
            metricLine.ValueKind.Should().NotBe(JsonValueKind.Undefined, "an OTLP count metric must be written");

            var metricJson = metricLine.GetRawText();
            metricJson.Should().Contain("\"name\":\"count\"");
            metricJson.Should().Contain("RuntimeError", "the error metric carries the 5xx exception dimension");
            metricJson.Should().Contain("POST /orders/{id}");
            metricJson.Should().NotContain("ValidationError", "a 4xx exception is excluded from the fault-only count metric");
            metricJson.Should().NotContain("\"environment\"", "deployment.environment is unset → the attribute is omitted (no sentinel)");
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

    [Fact]
    public void EndpointMetrics_WhenAppSignalsEnabled_SuppressesSummaryButStillEmitsErrorMetric()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-endpoint-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (var _ = EnvScope.Set(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "endpoint-smoke",
                // App Signals ON → EndpointSummary suppressed; error metric still emitted.
                ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "true",
                ["RESOURCE_DETECTORS_ENABLED"] = "false",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var config = ServiceEventsConfig.FromEnvironment();
                var inst = ServiceEventsInstrumentation.GetOrCreate(config);
                inst.Initialize();

                inst.EndpointCollector!.RecordRequest("/x", "GET", 500, 5_000_000, "BoomError", "h");

                ServiceEventsInstrumentation.ResetForTests();
            }

            var docs = File.ReadAllLines(outputFile)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .ToList();

            var hasSummary = docs.Any(d => IsEventName(d, "aws.service_events.endpoint_summary"));
            hasSummary.Should().BeFalse("EndpointSummary is suppressed when Application Signals is enabled");

            var hasMetric = docs.Any(d => d.TryGetProperty("resourceMetrics", out _));
            hasMetric.Should().BeTrue("the EndpointErrorMetrics count metric still emits under App Signals");
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

    /// <summary>True when the record's <c>eventName</c> matches.</summary>
    private static bool IsEventName(JsonElement doc, string eventName) =>
        doc.TryGetProperty("eventName", out var n) && n.GetString() == eventName;
}
