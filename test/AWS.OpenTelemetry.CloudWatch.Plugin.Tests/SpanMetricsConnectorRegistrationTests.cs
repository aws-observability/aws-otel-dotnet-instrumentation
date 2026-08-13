// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests;

[Collection(SpanMetricsConnectorCollection.Name)]
public class SpanMetricsConnectorRegistrationTests
{
    private const string CloudWatchPluginName =
        "AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, AWS.OpenTelemetry.CloudWatch.Plugin";

    [Fact]
    public void SpanMetricsConnectorManualRegistrationRecordsMetrics()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SpanMetricsConnector.ScopeName)
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysRecordSampler())
            .AddProcessor(new SpanMetricsConnector())
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
    }

    [Fact]
    public void SpanMetricsConnectorManualRegistrationPreservesCustomSamplerDecision()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(SpanMetricsConnector.ScopeName)
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysRecordSampler(new AlwaysOffSampler()))
            .AddProcessor(new SpanMetricsConnector())
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
    public void SpanMetricsConnectorManualSamplerCanBeOverwrittenByLaterSetSampler()
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetSampler(new AlwaysRecordSampler(new AlwaysOffSampler()))
            .AddProcessor(new SpanMetricsConnector())
            .SetSampler(new AlwaysOffSampler())
            .Build();

        Assert.IsType<AlwaysOffSampler>(GetInstalledSampler(tracerProvider));
    }

    [Fact]
    public void SpanMetricsConnectorManualRegistrationRequiresMeterRegistration()
    {
        var metrics = new List<Metric>();
        var sourceName = UniqueName();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddInMemoryExporter(metrics)
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetSampler(new AlwaysRecordSampler())
            .AddProcessor(new SpanMetricsConnector())
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
    public void SpanMetricsConnectorAutoPluginRecordsMetricsWithoutExportingAlwaysOffSpans()
    {
        using var environment = new SamplerEnvironment("always_off", null);
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
        plugin.TracerProviderInitialized(tracerProvider);
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
    }

    [Fact]
    [Trait("Category", "AutoInstrumentation")]
    public void SpanMetricsConnectorUpstreamAutoInstrumentationPluginRecordsMetrics()
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

    [Theory]
    [InlineData(null, null)]
    [InlineData("always_on", null)]
    [InlineData("always_off", null)]
    [InlineData("traceidratio", "0.25")]
    [InlineData("parentbased_always_on", null)]
    [InlineData("parentbased_always_off", null)]
    [InlineData("parentbased_traceidratio", "0.25")]
    [InlineData("unknown", null)]
    public void SpanMetricsConnectorAutoPluginInstallsAlwaysRecordSampler(
        string? samplerName,
        string? samplerArgument)
    {
        using var environment = new SamplerEnvironment(samplerName, samplerArgument);
        var rootSampler = CreateExpectedRootSampler(samplerName, samplerArgument);
        var builder = Sdk.CreateTracerProviderBuilder();
        new CloudWatchPlugin().AfterConfigureTracerProvider(builder);
        using var provider = builder.Build();

        var installed = GetInstalledSampler(provider);

        Assert.IsType<AlwaysRecordSampler>(installed);
        Assert.Equal($"AlwaysRecordSampler{{{rootSampler.Description}}}", installed.Description);
    }

    [Fact]
    public void SpanMetricsConnectorAutoPluginReadsSamplerEnvironmentForEveryCall()
    {
        var plugin = new CloudWatchPlugin();

        using var alwaysOffEnvironment = new SamplerEnvironment("always_off", null);
        var alwaysOffBuilder = Sdk.CreateTracerProviderBuilder();
        plugin.AfterConfigureTracerProvider(alwaysOffBuilder);
        using var alwaysOffProvider = alwaysOffBuilder.Build();

        using var alwaysOnEnvironment = new SamplerEnvironment("always_on", null);
        var alwaysOnBuilder = Sdk.CreateTracerProviderBuilder();
        plugin.AfterConfigureTracerProvider(alwaysOnBuilder);
        using var alwaysOnProvider = alwaysOnBuilder.Build();

        Assert.Equal(
            "AlwaysRecordSampler{AlwaysOffSampler}",
            GetInstalledSampler(alwaysOffProvider).Description);
        Assert.Equal(
            "AlwaysRecordSampler{AlwaysOnSampler}",
            GetInstalledSampler(alwaysOnProvider).Description);
    }

    private static Sampler GetInstalledSampler(TracerProvider provider)
    {
        var property = provider.GetType().GetProperty(
            "Sampler",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<Sampler>(property.GetValue(provider));
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

    private static Sampler CreateExpectedRootSampler(string? samplerName, string? samplerArgument)
    {
        return samplerName switch
        {
            "always_on" => new AlwaysOnSampler(),
            "always_off" => new AlwaysOffSampler(),
            "traceidratio" => new TraceIdRatioBasedSampler(
                double.Parse(samplerArgument!, CultureInfo.InvariantCulture)),
            "parentbased_always_on" => new ParentBasedSampler(new AlwaysOnSampler()),
            "parentbased_always_off" => new ParentBasedSampler(new AlwaysOffSampler()),
            "parentbased_traceidratio" => new ParentBasedSampler(
                new TraceIdRatioBasedSampler(double.Parse(samplerArgument!, CultureInfo.InvariantCulture))),
            _ => new ParentBasedSampler(new AlwaysOnSampler()),
        };
    }

    private sealed class SamplerEnvironment : IDisposable
    {
        private readonly string? originalSampler;
        private readonly string? originalSamplerArgument;

        public SamplerEnvironment(string? sampler, string? samplerArgument)
        {
            this.originalSampler = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER");
            this.originalSamplerArgument = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG");
            Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", sampler);
            Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG", samplerArgument);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", this.originalSampler);
            Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG", this.originalSamplerArgument);
        }
    }
}
