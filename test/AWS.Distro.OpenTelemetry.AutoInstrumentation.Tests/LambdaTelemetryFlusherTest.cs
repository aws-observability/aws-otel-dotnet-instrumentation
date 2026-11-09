// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// The pipelines use a 60 s batch delay and export interval, so telemetry only reaches the exporters
/// within a test if LambdaTelemetryFlusher flushes it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class LambdaTelemetryFlusherTest
{
    private const int NeverFires = 60_000;

    [Fact]
    public async Task ShortInvocationExportsLogsAndMetricsBeforeReturning()
    {
        using var pipeline = new TestPipeline();

        var result = await pipeline.InvokeAsync(() => Task.FromResult<object?>("ok"), "inv-1");

        Assert.Equal("ok", result);
        Assert.Equal(new[] { "inv-1" }, pipeline.LogExporter.Markers);
        Assert.Equal(new[] { "inv-1" }, pipeline.MetricExporter.Markers);
    }

    [Fact]
    public async Task WarmInvocationsEachExportTheirOwnTelemetry()
    {
        using var pipeline = new TestPipeline();

        for (var n = 1; n <= 3; n++)
        {
            await pipeline.InvokeAsync(() => Task.FromResult<object?>(null), $"inv-{n}");

            Assert.Equal($"inv-{n}", pipeline.LogExporter.Markers.Last());
            Assert.Equal($"inv-{n}", pipeline.MetricExporter.Markers.Last());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerExceptionIsRethrownAfterTelemetryIsFlushed(bool faultedTask)
    {
        using var pipeline = new TestPipeline();
        var handlerException = new InvalidOperationException("handler failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.InvokeAsync(
                () => faultedTask ? Task.FromException<object?>(handlerException) : throw handlerException,
                "inv-1"));

        Assert.Same(handlerException, thrown);
        Assert.Equal(new[] { "inv-1" }, pipeline.LogExporter.Markers);
        Assert.Equal(new[] { "inv-1" }, pipeline.MetricExporter.Markers);
    }

    [Fact]
    public async Task DisabledSignalsAreSkipped()
    {
        // No MeterProvider (metrics disabled), and a log processor with no pipeline ahead of a live one.
        // A failed null check would stay inside the flush task, so assert the live pipeline still exported.
        using var pipeline = new TestPipeline(metricsEnabled: false, includeUnattachedLogProcessor: true);

        var result = await pipeline.InvokeAsync(() => Task.FromResult<object?>("ok"), "inv-1");

        Assert.Equal("ok", result);
        Assert.Equal(new[] { "inv-1" }, pipeline.LogExporter.Markers);
    }

    [Fact]
    public async Task ExporterExceptionDoesNotFailTheInvocation()
    {
        // The SDK catches the exception; this guards that an exporter failure never fails the invocation.
        // Only the metric exporter throws: the SDK's batch export thread does not catch log exporter
        // exceptions, so a throwing log exporter would crash the test host.
        using var pipeline = new TestPipeline(metricExporterThrows: true);

        var result = await pipeline.InvokeAsync(() => Task.FromResult<object?>("ok"), "inv-1");

        Assert.Equal("ok", result);
        Assert.Equal(new[] { "inv-1" }, pipeline.LogExporter.Markers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneHangingExporterDoesNotStopTheOtherSignal(bool metricsHang)
    {
        using var release = new ManualResetEventSlim(false);
        using var pipeline = new TestPipeline(
            blockLogExportUntil: metricsHang ? null : release,
            blockMetricExportUntil: metricsHang ? release : null,
            flushTimeoutMilliseconds: 1000);

        try
        {
            await pipeline.InvokeAsync(() => Task.FromResult<object?>("ok"), "inv-1");

            var healthyMarkers = metricsHang ? pipeline.LogExporter.Markers : pipeline.MetricExporter.Markers;
            Assert.Equal(new[] { "inv-1" }, healthyMarkers);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task HangingExportersAreBoundedByTheFlushTimeout()
    {
        // Both endpoints hang. The metric reader exports on the flushing thread and ignores the timeout
        // given to ForceFlush, so the bound has to come from the flusher itself.
        using var release = new ManualResetEventSlim(false);
        using var pipeline = new TestPipeline(blockLogExportUntil: release, blockMetricExportUntil: release, flushTimeoutMilliseconds: 300);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await pipeline.InvokeAsync(() => Task.FromResult<object?>("ok"), "inv-1");

            Assert.Equal("ok", result);
            Assert.True(stopwatch.ElapsedMilliseconds < 3000, $"Flush took {stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.Set();
        }
    }

    // The Plugin tests use the shared LambdaTelemetryFlusher.LogProcessors, so they never assert absolute counts.
    [Fact]
    public async Task PluginRegistersTheLambdaLogPipelineForFlushing()
    {
        var exporter = new RecordingLogExporter();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            Plugin.AddLambdaConsoleProcessor(options);
            options.AddProcessor(new BatchLogRecordExportProcessor(exporter, scheduledDelayMilliseconds: NeverFires));
        }));
        var logger = loggerFactory.CreateLogger("LambdaTelemetryFlusherTest");

        await LambdaTelemetryFlusher.InvokeAndFlushAsync(
            () =>
            {
                logger.LogInformation("invocation {Marker}", "inv-1");
                return Task.FromResult<object?>(null);
            },
            meterProvider: null,
            LambdaTelemetryFlusher.LogProcessors,
            5000);

        Assert.Equal(new[] { "inv-1" }, exporter.Markers);
    }

    [Fact]
    public void PluginDoesNotRegisterLogPipelinesOutsideLambda()
    {
        Assert.False(AwsSpanProcessingUtil.IsLambdaEnvironment());
        var before = LambdaTelemetryFlusher.LogProcessors.Count;

        new Plugin().ConfigureLogsOptions(new OpenTelemetryLoggerOptions());

        Assert.Equal(before, LambdaTelemetryFlusher.LogProcessors.Count);
    }

    /// <summary>
    /// An ILogger pipeline with a batch processor and a MeterProvider with a periodic reader, as the
    /// auto-instrumentation builds them for OTLP. The tracked processor mirrors Plugin.ConfigureLogsOptions.
    /// </summary>
    private sealed class TestPipeline : IDisposable
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly Meter meter;
        private readonly Counter<long> counter;
        private readonly MeterProvider? meterProvider;
        private readonly List<BaseProcessor<LogRecord>> logProcessors = new();
        private readonly int flushTimeoutMilliseconds;

        public TestPipeline(
            ManualResetEventSlim? blockLogExportUntil = null,
            ManualResetEventSlim? blockMetricExportUntil = null,
            int flushTimeoutMilliseconds = 5000,
            bool metricsEnabled = true,
            bool metricExporterThrows = false,
            bool includeUnattachedLogProcessor = false)
        {
            this.flushTimeoutMilliseconds = flushTimeoutMilliseconds;
            this.LogExporter = new RecordingLogExporter(blockLogExportUntil);
            this.MetricExporter = new RecordingMetricExporter(blockMetricExportUntil, metricExporterThrows);

            if (includeUnattachedLogProcessor)
            {
                this.logProcessors.Add(new SimpleLogRecordExportProcessor(new RecordingLogExporter()));
            }

            this.loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
            {
                var tracked = new SimpleLogRecordExportProcessor(new RecordingLogExporter());
                options.AddProcessor(tracked);
                this.logProcessors.Add(tracked);
                options.AddProcessor(new BatchLogRecordExportProcessor(this.LogExporter, scheduledDelayMilliseconds: NeverFires));
            }));
            this.logger = this.loggerFactory.CreateLogger("LambdaTelemetryFlusherTest");

            var meterName = $"LambdaTelemetryFlusherTest.{Guid.NewGuid():N}";
            this.meter = new Meter(meterName);
            this.counter = this.meter.CreateCounter<long>("invocations");
            if (metricsEnabled)
            {
                this.meterProvider = Sdk.CreateMeterProviderBuilder()
                    .AddMeter(meterName)
                    .AddReader(new PeriodicExportingMetricReader(this.MetricExporter, NeverFires)
                    {
                        TemporalityPreference = MetricReaderTemporalityPreference.Delta,
                    })
                    .Build()!;
            }
        }

        public RecordingLogExporter LogExporter { get; }

        public RecordingMetricExporter MetricExporter { get; }

        public Task<object?> InvokeAsync(Func<Task<object?>> handler, string marker) =>
            LambdaTelemetryFlusher.InvokeAndFlushAsync(
                () =>
                {
                    this.logger.LogInformation("invocation {Marker}", marker);
                    this.counter.Add(1, new KeyValuePair<string, object?>("marker", marker));
                    return handler();
                },
                this.meterProvider,
                this.logProcessors,
                this.flushTimeoutMilliseconds);

        public void Dispose()
        {
            this.meterProvider?.Dispose();
            this.meter.Dispose();
            this.loggerFactory.Dispose();
        }
    }

    private sealed class RecordingLogExporter : BaseExporter<LogRecord>
    {
        private readonly ManualResetEventSlim? blockUntil;
        private readonly ConcurrentQueue<string> markers = new();

        public RecordingLogExporter(ManualResetEventSlim? blockUntil = null)
        {
            this.blockUntil = blockUntil;
        }

        public IReadOnlyList<string> Markers => this.markers.ToArray();

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            this.blockUntil?.Wait(TimeSpan.FromSeconds(30));
            foreach (var record in batch)
            {
                foreach (var attribute in record.Attributes ?? [])
                {
                    if (attribute.Key == "Marker")
                    {
                        this.markers.Enqueue(attribute.Value?.ToString() ?? string.Empty);
                    }
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed class RecordingMetricExporter : BaseExporter<Metric>
    {
        private readonly ManualResetEventSlim? blockUntil;
        private readonly bool throws;
        private readonly ConcurrentQueue<string> markers = new();

        public RecordingMetricExporter(ManualResetEventSlim? blockUntil, bool throws)
        {
            this.blockUntil = blockUntil;
            this.throws = throws;
        }

        public IReadOnlyList<string> Markers => this.markers.ToArray();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            this.blockUntil?.Wait(TimeSpan.FromSeconds(30));
            if (this.throws)
            {
                throw new InvalidOperationException("metric exporter failed");
            }

            foreach (var metric in batch)
            {
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    foreach (var tag in point.Tags)
                    {
                        if (tag.Key == "marker")
                        {
                            this.markers.Enqueue(tag.Value?.ToString() ?? string.Empty);
                        }
                    }
                }
            }

            return ExportResult.Success;
        }
    }
}
