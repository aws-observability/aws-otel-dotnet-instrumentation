// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using AWS.OpenTelemetry.CloudWatchPluginOtel.Tests.Implementation.SpanMetrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Tests;

[Collection(SpanMetricsTestsCollection.Name)]
public class SpanMetricsTracerProviderBuilderExtensionsTests
{
    [Fact]
    public void AddCloudWatchSpanMetricsRecordsMetrics()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddCloudWatchSpanMetrics()
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddCloudWatchSpanMetrics()
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("registered-once"))
        {
            Assert.NotNull(activity);
        }

        meterProvider.ForceFlush();

        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.calls",
                "registered-once").GetSumLong());
        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.duration",
                "registered-once").GetHistogramCount());
    }

    [Fact]
    public void AddCloudWatchSpanMetricsRegistersOnceWhenCalledMultipleTimes()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddCloudWatchSpanMetrics()
            .AddCloudWatchSpanMetrics()
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddCloudWatchSpanMetrics(new AlwaysOffSampler())
            .AddCloudWatchSpanMetrics(new AlwaysOffSampler())
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("registered-once"))
        {
            Assert.NotNull(activity);
            Assert.False(activity.Recorded);
        }

        meterProvider.ForceFlush();

        Assert.Equal(
            "AlwaysRecordSampler{AlwaysOffSampler}",
            GetInstalledSampler(tracerProvider).Description);
        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.calls",
                "registered-once").GetSumLong());
        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.duration",
                "registered-once").GetHistogramCount());
    }

    [Fact]
    public void AddCloudWatchSpanMetricsPreservesCustomSamplerDecision()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddCloudWatchSpanMetrics()
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddCloudWatchSpanMetrics(new AlwaysOffSampler())
            .Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("explicit-registration"))
        {
            Assert.NotNull(activity);
            Assert.False(activity.Recorded);
        }

        meterProvider.ForceFlush();

        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.calls",
                "explicit-registration").GetSumLong());
    }

    [Fact]
    public void AddCloudWatchSpanMetricsCanBeOverwrittenByLaterSetSampler()
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddCloudWatchSpanMetrics(new AlwaysOffSampler())
            .SetSampler(new AlwaysOffSampler())
            .Build();

        Assert.IsType<AlwaysOffSampler>(GetInstalledSampler(tracerProvider));
    }

    [Fact]
    public void AddCloudWatchSpanMetricsRejectsNullBuilder()
    {
        TracerProviderBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.AddCloudWatchSpanMetrics());
    }

    [Fact]
    public void AddCloudWatchSpanMetricsRejectsNullSampler()
    {
        Assert.Throws<ArgumentNullException>(
            () => Sdk.CreateTracerProviderBuilder().AddCloudWatchSpanMetrics(null!));
    }

    private static Sampler GetInstalledSampler(TracerProvider provider)
    {
        var property = provider.GetType().GetProperty(
            "Sampler",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<Sampler>(property.GetValue(provider));
    }

    private static string UniqueName()
    {
        return "span-metrics-tracer-builder-" + Guid.NewGuid().ToString("N");
    }
}
