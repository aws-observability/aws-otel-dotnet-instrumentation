// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Telemetry;

/// <summary>
/// Verifies <see cref="ServiceEventsCloudWatchMetricFileExporter" /> emits the
/// <c>service.function.duration</c> ExponentialHistogram as the canonical
/// OTLP/JSON <c>exponentialHistogram</c> shape when driven through a real MeterProvider
/// with the base-2 exponential view.
/// </summary>
public class ServiceEventsCloudWatchMetricFileExporterTests
{
    [Fact]
    public void Export_ExponentialHistogram_WritesOtlpJsonShape()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-fc-metric-{Guid.NewGuid():N}.ndjson");

        try
        {
            using (var meter = new Meter(ServiceEventsOtlpEmitter.InstrumentationScopeName, ServiceEventsOtlpEmitter.InstrumentationScopeVersion))
            {
                using var provider = Sdk.CreateMeterProviderBuilder()
                    .AddMeter(ServiceEventsOtlpEmitter.InstrumentationScopeName)
                    .AddView(
                        instrumentName: "service.function.duration",
                        metricStreamConfiguration: new Base2ExponentialBucketHistogramConfiguration())
                    .AddReader(new PeriodicExportingMetricReader(
                        new ServiceEventsCloudWatchMetricFileExporter(outputFile),
                        exportIntervalMilliseconds: 600_000)
                    {
                        TemporalityPreference = MetricReaderTemporalityPreference.Delta,
                    })
                    .Build();

                var histogram = meter.CreateHistogram<double>(
                    name: "service.function.duration",
                    unit: "Microseconds",
                    description: "Function call duration");

                var tags = new TagList
                {
                    { "Telemetry.Source", "ServiceEvents" },
                    { "function.name", "Test.FnSource.HttpRequestOut" },
                    { "status", "success" },
                };

                histogram.Record(1500, tags); // µs
                histogram.Record(2500, tags);

                provider.ForceFlush();
            }

            var line = File.ReadAllLines(outputFile).Single(l => !string.IsNullOrWhiteSpace(l));
            var root = JsonDocument.Parse(line).RootElement;

            var metric = root
                .GetProperty("resourceMetrics")[0]
                .GetProperty("scopeMetrics")[0]
                .GetProperty("metrics")[0];

            metric.GetProperty("name").GetString().Should().Be("service.function.duration");
            metric.GetProperty("unit").GetString().Should().Be("Microseconds");
            metric.GetProperty("description").GetString().Should().Be("Function call duration");

            var expo = metric.GetProperty("exponentialHistogram");
            expo.GetProperty("aggregationTemporality").GetInt32().Should().Be(1); // DELTA

            var dp = expo.GetProperty("dataPoints")[0];

            // count + zeroCount are uint64 → encoded as strings.
            dp.GetProperty("count").GetString().Should().Be("2");
            dp.GetProperty("sum").GetDouble().Should().BeApproximately(4000, 0.5);
            dp.GetProperty("zeroCount").GetString().Should().Be("0");
            dp.GetProperty("scale").ValueKind.Should().Be(JsonValueKind.Number);

            var positive = dp.GetProperty("positive");
            positive.GetProperty("offset").ValueKind.Should().Be(JsonValueKind.Number);
            positive.GetProperty("bucketCounts").GetArrayLength().Should().BeGreaterThan(0);

            // Per-call dimensions are present; service-level context rides on the resource.
            var attrKeys = dp.GetProperty("attributes").EnumerateArray()
                .Select(a => a.GetProperty("key").GetString())
                .ToList();
            attrKeys.Should().Contain(new[] { "Telemetry.Source", "function.name", "status" });
        }
        finally
        {
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }
}
