// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Amazon.Runtime;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

/// <summary>
/// Holds resolved AWS signing settings for an OTLP exporter.
/// </summary>
internal sealed class OtlpAwsExporterOptions
{
    /// <summary>The exporter selection token.</summary>
    internal const string ExporterName = "otlp/sigv4";

    /// <summary>The environment variable for the global SigV4 service.</summary>
    internal const string ExporterServiceEnvironmentVariable = "AWS_EXPORTER_SERVICE";

    /// <summary>The environment variable for the traces SigV4 service.</summary>
    internal const string TracesExporterServiceEnvironmentVariable = "AWS_TRACES_EXPORTER_SERVICE";

    /// <summary>The environment variable for the metrics SigV4 service.</summary>
    internal const string MetricsExporterServiceEnvironmentVariable = "AWS_METRICS_EXPORTER_SERVICE";

    /// <summary>The environment variable for the logs SigV4 service.</summary>
    internal const string LogsExporterServiceEnvironmentVariable = "AWS_LOGS_EXPORTER_SERVICE";

    /// <summary>The default SigV4 service for traces.</summary>
    internal const string DefaultTracesService = "xray";

    /// <summary>The default SigV4 service for metrics.</summary>
    internal const string DefaultMetricsService = "monitoring";

    /// <summary>The default SigV4 service for logs.</summary>
    internal const string DefaultLogsService = "logs";

    private static readonly Regex AwsEndpointRegion = new(
        @"\A(?:[a-z0-9-]+\.)*([a-z]+(?:-[a-z]+)+-\d+)(?:\.[a-z0-9-]+)*\.(?:amazonaws\.com(?:\.cn)?|api\.aws|c2s\.ic\.gov|sc2s\.sgov\.gov|cloud\.adc-e\.uk|csp\.hci\.ic\.gov)\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Cache both successful resolution and failure so IMDS is probed at most once at startup.
    private static readonly Lazy<string?> SdkRegion = new(() =>
    {
        try
        {
            return FallbackRegionFactory.GetRegionEndpoint()?.SystemName;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsExporterOptions"/> class.
    /// </summary>
    /// <param name="signal">The OTLP signal.</param>
    /// <param name="options">The configured upstream exporter options.</param>
    /// <param name="sdkRegionProvider">An optional replacement for the cached SDK region provider.</param>
    /// <exception cref="ArgumentException">No signing region can be resolved.</exception>
    internal OtlpAwsExporterOptions(OtlpSignal signal, OtlpExporterOptions options, Func<string?>? sdkRegionProvider = null)
    {
        this.Service = GetAwsService(signal);
        this.Region = GetAwsRegion(options.Endpoint, sdkRegionProvider)
            ?? throw new ArgumentException("The AWS OTLP exporter requires a signing region.", nameof(options));
    }

    /// <summary>
    /// The OTLP signals supported by AWS exporter configuration.
    /// </summary>
    internal enum OtlpSignal
    {
        Traces,
        Logs,
        Metrics,
    }

    /// <summary>
    /// Gets the resolved SigV4 service name.
    /// </summary>
    internal string Service { get; }

    /// <summary>
    /// Gets the resolved signing region.
    /// </summary>
    internal string Region { get; }

    /// <summary>
    /// Resolves the signing service from signal settings, global settings, or the signal default.
    /// </summary>
    /// <param name="signal">The OTLP signal.</param>
    /// <returns>The SigV4 service name.</returns>
    internal static string GetAwsService(OtlpSignal signal)
    {
        string serviceEnvironmentVariable;
        string defaultService;
        switch (signal)
        {
            case OtlpSignal.Traces:
                serviceEnvironmentVariable = TracesExporterServiceEnvironmentVariable;
                defaultService = DefaultTracesService;
                break;
            case OtlpSignal.Metrics:
                serviceEnvironmentVariable = MetricsExporterServiceEnvironmentVariable;
                defaultService = DefaultMetricsService;
                break;
            case OtlpSignal.Logs:
                serviceEnvironmentVariable = LogsExporterServiceEnvironmentVariable;
                defaultService = DefaultLogsService;
                break;
            default:
                throw new ArgumentException("Unknown OTLP signal.", nameof(signal));
        }

        var service = GetEnvironmentValue(serviceEnvironmentVariable);
        if (service != null)
        {
            return service;
        }

        service = GetEnvironmentValue(ExporterServiceEnvironmentVariable);
        if (service != null)
        {
            return service;
        }

        return defaultService;
    }

    /// <summary>
    /// Resolves the region from environment variables, a recognized AWS endpoint, or the SDK chain.
    /// </summary>
    /// <param name="endpoint">The OTLP endpoint.</param>
    /// <param name="sdkRegionProvider">An optional replacement for the cached SDK region provider.</param>
    /// <returns>The signing region, or null if no source resolves it.</returns>
    internal static string? GetAwsRegion(Uri endpoint, Func<string?>? sdkRegionProvider = null)
    {
        var region = GetEnvironmentValue("AWS_REGION") ?? GetEnvironmentValue("AWS_DEFAULT_REGION");
        if (region != null)
        {
            return region;
        }

        var match = AwsEndpointRegion.Match(endpoint.Host);
        return match.Success ? match.Groups[1].Value : (sdkRegionProvider ?? (() => SdkRegion.Value))();
    }

    private static string? GetEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
