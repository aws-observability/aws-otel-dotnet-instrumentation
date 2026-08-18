// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.OpenTelemetry.CloudWatch.Plugin.Tests.Implementation.SpanMetrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests;

[Collection(SpanMetricsTestsCollection.Name)]
public class CloudWatchSpanMetricsMeterProviderBuilderExtensionsTests
{
    [Fact]
    public void AddCloudWatchSpanMetricsIsRequiredToCollectMetrics()
    {
        var metrics = new List<Metric>();
        var sourceName = "span-metrics-meter-builder-" + Guid.NewGuid().ToString("N");
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddCloudWatchSpanMetrics()
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("no-meter-registration"))
        {
            Assert.NotNull(activity);
        }

        meterProvider.ForceFlush();

        Assert.Empty(metrics);
    }

    [Fact]
    public void AddCloudWatchSpanMetricsRejectsNullBuilder()
    {
        MeterProviderBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.AddCloudWatchSpanMetrics());
    }
}
