// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Integration;

/// <summary>
/// End-to-end smoke test for M5: config → FunctionCallProcessor → emitter histogram →
/// MeterProvider → OUTPUT_FILE. Drives <see cref="ServiceEventsInstrumentation" /> the way
/// the plugin does (register processors on a TracerProvider), ends a downstream Activity
/// from an allowlisted source, and asserts the <c>service.function.duration</c>
/// ExponentialHistogram lands in the file with the right per-call dimensions.
/// </summary>
[Collection("EnvironmentVariables")]
public class FunctionCallSmokeTests
{
    private const string SourceName = "SmokeApp.Worker";

    [Fact]
    public void FunctionCall_WhenEnabledAndActivityEnds_WritesHistogramToFile()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-fc-{Guid.NewGuid():N}.ndjson");
        using var source = new ActivitySource(SourceName);

        try
        {
            using (var _ = EnvScope.Set(new()
            {
                ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
                ["OTEL_SERVICE_NAME"] = "fc-smoke",
                ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false",
                ["OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED"] = "true",
                ["OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE"] = "SmokeApp.*",
                ["OTEL_AWS_SERVICE_EVENTS_SAMPLING_MODE"] = "always",
                ["RESOURCE_DETECTORS_ENABLED"] = "false",
            }))
            {
                ServiceEventsInstrumentation.ResetForTests();
                var config = ServiceEventsConfig.FromEnvironment();
                var inst = ServiceEventsInstrumentation.GetOrCreate(config);
                inst.Initialize();
                inst.IsInitialized.Should().BeTrue();
                inst.FunctionSampler.Should().NotBeNull("function instrumentation is enabled with a non-empty allowlist");

                // Register ServiceEvents' processors on a tracer provider the way the plugin does.
                var builder = Sdk.CreateTracerProviderBuilder().AddSource(SourceName);
                inst.RegisterTracerProcessors(builder);
                using (var tracerProvider = builder.Build())
                {
                    // Server span (the endpoint) → skipped by FunctionCall, but its method+route
                    // become the downstream call's `operation`. The child Client call is the
                    // FunctionCall recorded on OnEnd.
                    using var server = source.StartActivity("HttpRequestIn", ActivityKind.Server);
                    server!.SetTag("http.request.method", "GET");
                    server.SetTag("http.route", "/work");
                    using var activity = source.StartActivity("FetchData", ActivityKind.Client);
                    activity.Should().NotBeNull("the tracer provider listens to the smoke source");
                }

                // Dispose the instrumentation (incl. its MeterProvider) → final metric export to file.
                ServiceEventsInstrumentation.ResetForTests();
            }

            File.Exists(outputFile).Should().BeTrue();

            var fnMetric = FindFunctionDurationMetric(outputFile);
            fnMetric.Should().NotBeNull("the FunctionCall histogram should be exported to OUTPUT_FILE");

            var metric = fnMetric!.Value;
            metric.GetProperty("unit").GetString().Should().Be("Microseconds");

            var dp = metric.GetProperty("exponentialHistogram").GetProperty("dataPoints")[0];
            long.Parse(dp.GetProperty("count").GetString()!).Should().BeGreaterThanOrEqualTo(1);

            var attrs = dp.GetProperty("attributes").EnumerateArray()
                .ToDictionary(
                    a => a.GetProperty("key").GetString()!,
                    a => a.GetProperty("value").GetProperty("stringValue").GetString());

            attrs["Telemetry.Source"].Should().Be("ServiceEvents");
            attrs["function.name"].Should().Be("SmokeApp.Worker.FetchData");
            attrs["status"].Should().Be("success");
            attrs["operation"].Should().Be("GET /work");
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

    /// <summary>Scan the NDJSON output for the <c>service.function.duration</c> metric node.</summary>
    private static JsonElement? FindFunctionDurationMetric(string outputFile)
    {
        foreach (var line in File.ReadAllLines(outputFile))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var root = JsonDocument.Parse(line).RootElement;
            if (!root.TryGetProperty("resourceMetrics", out var resourceMetrics))
            {
                continue;
            }

            foreach (var rm in resourceMetrics.EnumerateArray())
            {
                foreach (var sm in rm.GetProperty("scopeMetrics").EnumerateArray())
                {
                    foreach (var m in sm.GetProperty("metrics").EnumerateArray())
                    {
                        if (m.GetProperty("name").GetString() == "service.function.duration")
                        {
                            return m.Clone();
                        }
                    }
                }
            }
        }

        return null;
    }
}
