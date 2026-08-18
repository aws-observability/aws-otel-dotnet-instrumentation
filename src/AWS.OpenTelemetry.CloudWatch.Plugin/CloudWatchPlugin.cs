// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin;

/// <summary>
/// CloudWatch auto-instrumentation plugin for OpenTelemetry .NET.
/// </summary>
public sealed class CloudWatchPlugin
{
    private const string AutoPluginsEnvironmentVariable = "OTEL_DOTNET_AUTO_PLUGINS";
    private const string PluginSeparator = ":";
    private readonly bool enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudWatchPlugin"/> class.
    /// </summary>
    public CloudWatchPlugin()
    {
        this.enabled = IsLastConfiguredPlugin(out var lastPlugin);
        if (!this.enabled)
        {
            CloudWatchPluginEventSource.Log.PluginDisabledByOrdering(lastPlugin);
            Console.Error.WriteLine(
                $"CloudWatchPlugin must be the last entry in {AutoPluginsEnvironmentVariable}. " +
                $"The last effective plugin is '{lastPlugin}'. CloudWatch span metrics were disabled.");
        }
    }

    /// <summary>
    /// Installs an always-record wrapper around the sampler selected by OpenTelemetry environment variables.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The configured tracer provider builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (!this.enabled)
        {
            return builder;
        }

        return builder.AddCloudWatchSpanMetrics(SamplerFactory.Create());
    }

    /// <summary>
    /// Subscribes the application meter provider to the span metrics instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <returns>The configured meter provider builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (!this.enabled)
        {
            return builder;
        }

        return builder.AddCloudWatchSpanMetrics();
    }

    private static bool IsLastConfiguredPlugin(out string lastPlugin)
    {
        lastPlugin = string.Empty;
        var configuredPlugins = Environment.GetEnvironmentVariable(AutoPluginsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPlugins))
        {
            return true;
        }

        var effectivePlugins = new List<string>();
        var seenPlugins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuredPlugin in configuredPlugins.Split(
                     new[] { PluginSeparator },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var pluginTypeName = configuredPlugin.Split(',')[0].Trim();
            if (pluginTypeName.Length > 0 && seenPlugins.Add(pluginTypeName))
            {
                effectivePlugins.Add(pluginTypeName);
            }
        }

        var cloudWatchPluginName = typeof(CloudWatchPlugin).FullName;
        var cloudWatchPluginIndex = effectivePlugins.IndexOf(cloudWatchPluginName!);
        if (cloudWatchPluginIndex < 0)
        {
            // The plugin may have been loaded from file-based configuration, whose
            // final ordering is not exposed to plugin instances.
            return true;
        }

        lastPlugin = effectivePlugins[effectivePlugins.Count - 1];
        return cloudWatchPluginIndex == effectivePlugins.Count - 1;
    }
}
