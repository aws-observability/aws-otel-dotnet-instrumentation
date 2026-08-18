// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Tracing;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.SpanMetrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests;

[Collection(SpanMetricsTestsCollection.Name)]
public class EventSourceTests
{
    [Fact]
    public void CloudWatchPluginEventSourceHasValidManifest()
    {
        var manifest = EventSource.GenerateManifest(
            typeof(CloudWatchPluginEventSource),
            "assemblyPathForValidation",
            EventManifestOptions.Strict);

        Assert.NotNull(manifest);
    }

    [Theory]
    [InlineData("OnStart")]
    [InlineData("OnEnd")]
    public void SpanProcessingExceptionIsLoggedAtErrorLevel(string callback)
    {
        using var environment = new MetricsExporterEnvironment("otlp");
        using var listener = new CloudWatchPluginEventListener();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddCloudWatchSpanMetrics()
            .AddInMemoryExporter(new List<Metric>())
            .Build();
        var processor = new SpanMetricsConnector();

        var exception = Record.Exception(() =>
        {
            if (callback == "OnStart")
            {
                processor.OnStart(null!);
            }
            else
            {
                processor.OnEnd(null!);
            }
        });

        Assert.Null(exception);
        var eventData = Assert.Single(listener.Events);
        Assert.Equal(1, eventData.EventId);
        Assert.Equal(EventLevel.Error, eventData.Level);
        Assert.Equal(callback, eventData.Payload![0]);
        Assert.Contains(nameof(NullReferenceException), Assert.IsType<string>(eventData.Payload[1]));
    }

    [Fact]
    public void UnsupportedSamplerConfigurationIsLoggedAtErrorLevel()
    {
        using var environment = new SamplerEnvironment("xray", null);
        using var listener = new CloudWatchPluginEventListener();

        Assert.Throws<NotSupportedException>(() => SamplerFactory.Create());

        var eventData = Assert.Single(listener.Events);
        Assert.Equal(2, eventData.EventId);
        Assert.Equal(EventLevel.Error, eventData.Level);
        Assert.Equal("xray", eventData.Payload![0]);
    }

    [Fact]
    public void PluginDisabledByOrderingIsLoggedAtErrorLevel()
    {
        const string cloudWatchPluginName =
            "AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, AWS.OpenTelemetry.CloudWatch.Plugin";
        using var environment = new PluginsEnvironment(
            cloudWatchPluginName + ":Example.Plugin, Example");
        using var listener = new CloudWatchPluginEventListener();
        using var errorOutput = new StringWriter();
        var originalError = Console.Error;
        try
        {
            Console.SetError(errorOutput);
            _ = new CloudWatchPlugin();
        }
        finally
        {
            Console.SetError(originalError);
        }

        var eventData = Assert.Single(listener.Events);
        Assert.Equal(3, eventData.EventId);
        Assert.Equal(EventLevel.Error, eventData.Level);
        Assert.Equal("Example.Plugin", eventData.Payload![0]);
    }

    private sealed class CloudWatchPluginEventListener : EventListener
    {
        private readonly int creatingThreadId = Environment.CurrentManagedThreadId;

        public List<EventWrittenEventArgs> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "OpenTelemetry-AWS-CloudWatch-Plugin")
            {
                this.EnableEvents(eventSource, EventLevel.Error);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (Environment.CurrentManagedThreadId == this.creatingThreadId)
            {
                this.Events.Add(eventData);
            }
        }
    }
}
