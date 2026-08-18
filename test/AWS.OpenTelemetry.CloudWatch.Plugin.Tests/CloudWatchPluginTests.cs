// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using AWS.OpenTelemetry.CloudWatch.Plugin.Tests.Implementation.SpanMetrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests;

[Collection(SpanMetricsTestsCollection.Name)]
public class CloudWatchPluginTests
{
    private const string CloudWatchPluginName =
        "AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, AWS.OpenTelemetry.CloudWatch.Plugin";

    [Fact]
    public void AutoPluginRecordsMetricsWithoutExportingAlwaysOffSpans()
    {
        using var environment = new SamplerEnvironment("always_off", null);
        using var pluginsEnvironment = new PluginsEnvironment(
            "Example.Plugin, Example:" + CloudWatchPluginName);
        var metrics = new List<Metric>();
        var exportedActivities = new List<Activity>();
        var sourceName = UniqueName();
        var plugin = new CloudWatchPlugin();
        var meterBuilder = plugin.AfterConfigureMeterProvider(
            Sdk.CreateMeterProviderBuilder().AddInMemoryExporter(metrics));
        using var meterProvider = meterBuilder.Build();
        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddInMemoryExporter(exportedActivities);
        plugin.AfterConfigureTracerProvider(tracerBuilder);
        using var tracerProvider = tracerBuilder.Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("auto-plugin"))
        {
            Assert.NotNull(activity);
        }

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        Assert.Empty(exportedActivities);
        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.calls",
                "auto-plugin").GetSumLong());
        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.duration",
                "auto-plugin").GetHistogramCount());
    }

    [Fact]
    public void AutoPluginDisablesSpanMetricsWhenCloudWatchIsNotLast()
    {
        using var samplerEnvironment = new SamplerEnvironment("always_off", null);
        using var pluginsEnvironment = new PluginsEnvironment(
            CloudWatchPluginName + ":Example.Plugin, Example");
        using var errorOutput = new StringWriter();
        var originalError = Console.Error;
        CloudWatchPlugin plugin;
        try
        {
            Console.SetError(errorOutput);
            plugin = new CloudWatchPlugin();
        }
        finally
        {
            Console.SetError(originalError);
        }

        var metrics = new List<Metric>();
        var exportedActivities = new List<Activity>();
        var sourceName = UniqueName();
        var meterBuilder = plugin.AfterConfigureMeterProvider(
            Sdk.CreateMeterProviderBuilder().AddInMemoryExporter(metrics));
        using var meterProvider = meterBuilder.Build();
        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddInMemoryExporter(exportedActivities);
        plugin.AfterConfigureTracerProvider(tracerBuilder);
        using var tracerProvider = tracerBuilder.Build();
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("disabled-plugin"))
        {
            Assert.NotNull(activity);
            Assert.False(activity.IsAllDataRequested);
        }

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        Assert.Empty(exportedActivities);
        Assert.Empty(metrics);
        Assert.Contains(
            "CloudWatchPlugin must be the last entry in OTEL_DOTNET_AUTO_PLUGINS",
            errorOutput.ToString());
        Assert.Contains("Example.Plugin", errorOutput.ToString());
    }

    [Fact]
    [Trait("Category", "AutoInstrumentation")]
    public void UpstreamAutoInstrumentationLoadsPluginAndRecordsMetrics()
    {
        using var environment = new SamplerEnvironment("always_off", null);
        var metrics = new List<Metric>();
        var exportedActivities = new List<Activity>();
        var sourceName = UniqueName();
        var pluginManager = CreateUpstreamPluginManager();
        var meterBuilder = Sdk.CreateMeterProviderBuilder().AddInMemoryExporter(metrics);
        meterBuilder = InvokePluginManager(
            pluginManager,
            "AfterConfigureMeterProviderBuilder",
            meterBuilder);
        using var meterProvider = meterBuilder.Build();
        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .AddInMemoryExporter(exportedActivities);
        tracerBuilder = InvokePluginManager(
            pluginManager,
            "AfterConfigureTracerProviderBuilder",
            tracerBuilder);
        using var tracerProvider = tracerBuilder.Build();
        InvokePluginManagerAction(pluginManager, "InitializedProvider", tracerProvider);
        using var source = new ActivitySource(sourceName);

        using (var activity = source.StartActivity("upstream-auto-plugin"))
        {
            Assert.NotNull(activity);
            Assert.False(activity.Recorded);
        }

        tracerProvider.ForceFlush();
        meterProvider.ForceFlush();

        Assert.Empty(exportedActivities);
        Assert.Equal(
            1,
            SpanMetricsConnectorTests.GetPoint(
                metrics,
                "traces.span.metrics.calls",
                "upstream-auto-plugin").GetSumLong());
    }

    [Fact]
    public void PluginDoesNotExposePostBuildRegistrationHook()
    {
        Assert.Null(typeof(CloudWatchPlugin).GetMethod("TracerProviderInitialized"));
    }

    [Fact]
    public void PluginHooksRejectNullBuilders()
    {
        var plugin = new CloudWatchPlugin();

        Assert.Throws<ArgumentNullException>(() => plugin.AfterConfigureTracerProvider(null!));
        Assert.Throws<ArgumentNullException>(() => plugin.AfterConfigureMeterProvider(null!));
    }

    private static object CreateUpstreamPluginManager()
    {
        var autoInstrumentationAssembly = Assembly.Load("OpenTelemetry.AutoInstrumentation");
        var settingsType = autoInstrumentationAssembly.GetType(
            "OpenTelemetry.AutoInstrumentation.Configurations.PluginsSettings");
        Assert.NotNull(settingsType);
        var settings = Activator.CreateInstance(settingsType, nonPublic: true);
        Assert.NotNull(settings);
        var pluginsProperty = settingsType.GetProperty("Plugins");
        Assert.NotNull(pluginsProperty);
        var plugins = Assert.IsAssignableFrom<IList<string>>(pluginsProperty.GetValue(settings));
        plugins.Add(CloudWatchPluginName);

        var pluginManagerType = autoInstrumentationAssembly.GetType(
            "OpenTelemetry.AutoInstrumentation.Plugins.PluginManager");
        Assert.NotNull(pluginManagerType);
        var pluginManager = Activator.CreateInstance(pluginManagerType, settings);
        Assert.NotNull(pluginManager);
        return pluginManager;
    }

    private static T InvokePluginManager<T>(object pluginManager, string methodName, T argument)
    {
        var method = pluginManager.GetType().GetMethod(methodName, new[] { typeof(T) });
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<T>(method.Invoke(pluginManager, new object[] { argument! }));
    }

    private static void InvokePluginManagerAction<T>(object pluginManager, string methodName, T argument)
    {
        var method = pluginManager.GetType().GetMethod(methodName, new[] { typeof(T) });
        Assert.NotNull(method);
        method.Invoke(pluginManager, new object[] { argument! });
    }

    private static string UniqueName()
    {
        return "span-metrics-registration-" + Guid.NewGuid().ToString("N");
    }
}
