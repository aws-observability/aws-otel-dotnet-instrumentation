// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.ResourceDetectors.AWS;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AWS SDK Plugin
/// </summary>
public class Plugin
{
    /// <summary>
    /// OTEL_AWS_APPLICATION_SIGNALS_ENABLED
    /// </summary>
    public static readonly string ApplicationSignalsEnabledConfig = "OTEL_AWS_APPLICATION_SIGNALS_ENABLED";
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddConsole());
    private static readonly ILogger Logger = Factory.CreateLogger<Plugin>();
    private static readonly string ApplicationSignalsExporterEndpointConfig = "OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT";
    private static readonly string MetricExportIntervalConfig = "OTEL_METRIC_EXPORT_INTERVAL";
    private static readonly int DefaultMetricExportInterval = 60000;
    private static readonly string DefaultProtocolEnvVarName = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>
    /// To configure plugin, before OTel SDK configuration is called.
    /// </summary>public void Initializing()
    public void Initializing()
    {
    }

    /// <summary>
    /// To access TracerProvider right after TracerProviderBuilder.Build() is executed.
    /// </summary>
    /// <param name="tracerProvider"><see cref="TracerProvider"/> Provider to configure</param>
    public void TracerProviderInitialized(TracerProvider tracerProvider)
    {
        if (this.IsApplicationSignalsEnabled())
        {
            tracerProvider.AddProcessor(AttributePropagatingSpanProcessorBuilder.Create().Build());

            string? intervalConfigString = System.Environment.GetEnvironmentVariable(MetricExportIntervalConfig);
            int exportInterval = DefaultMetricExportInterval;
            try
            {
                int parsedExportInterval = Convert.ToInt32(intervalConfigString);
                exportInterval = parsedExportInterval != 0 ? parsedExportInterval : DefaultMetricExportInterval;
            }
            catch (Exception)
            {
                Logger.Log(LogLevel.Trace, "Could not convert OTEL_METRIC_EXPORT_INTERVAL to integer. Using default value 60000.");
            }

            if (exportInterval.CompareTo(DefaultMetricExportInterval) > 0)
            {
                exportInterval = DefaultMetricExportInterval;
                Logger.Log(LogLevel.Information, "AWS Application Signals metrics export interval capped to {0}", exportInterval);
            }

            // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md#enable-metric-exporter
            // for setting the temporatityPref.
            var metricReader = new PeriodicExportingMetricReader(this.ApplicationSignalsExporterProvider(), exportInterval)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Delta,
            };

            MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddReader(metricReader)
            .ConfigureResource(builder => this.GetResourceBuilder())
            .AddMeter("AwsSpanMetricsProcessor")
            .AddView(instrument =>
            {
                // we currently only listen and meter Histograms and for that,
                // we use Base2ExponentialBucketHistogramConfiguration
                return instrument.GetType().GetGenericTypeDefinition() == typeof(Histogram<>)
                        ? new Base2ExponentialBucketHistogramConfiguration()
                        : null;
            })
            .Build();

            Resource resource = provider.GetResource();
            BaseProcessor<Activity> spanMetricsProcessor = AwsSpanMetricsProcessorBuilder.Create(resource).Build();
            tracerProvider.AddProcessor(spanMetricsProcessor);
        }
    }

    /// <summary>
    /// To configure tracing SDK before Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public TracerProviderBuilder BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
        if (this.IsApplicationSignalsEnabled())
        {
            var resource = this.GetResourceBuilder().Build();
            var processor = AwsMetricAttributesSpanProcessorBuilder.Create(resource).Build();
            builder.AddProcessor(processor);
        }

        // My custom logic here
        builder.AddAWSInstrumentation();
        return builder;
    }

    /// <summary>
    /// To configure tracing SDK after Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        if (this.IsApplicationSignalsEnabled())
        {
            Logger.Log(LogLevel.Information, "AWS Application Signals enabled");
            var resource = this.GetResourceBuilder().Build();
            Sampler alwaysRecordSampler = AlwaysRecordSampler.Create(SamplerUtil.GetSampler(resource));
            builder.SetSampler(alwaysRecordSampler);
        }

        return builder;
    }

    /// <summary>
    /// To configure Resource
    /// TODO: Add versioning similar to Python
    /// </summary>
    /// <param name="builder"><see cref="ResourceBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public ResourceBuilder ConfigureResource(ResourceBuilder builder)
    {
        Dictionary<string, object> attributes = new Dictionary<string, object>
        {
            { "telemetry.distro.name", "aws-otel-dotnet-instrumentation" },
        };
        builder
                .AddAttributes(attributes);
        return builder;
    }

    private bool IsApplicationSignalsEnabled()
    {
        return System.Environment.GetEnvironmentVariable(ApplicationSignalsEnabledConfig) == "true";
    }

    private ResourceBuilder GetResourceBuilder()
    {
        return ResourceBuilder.CreateDefault()
                .AddDetector(new AWSEC2ResourceDetector())
                .AddDetector(new AWSEKSResourceDetector())
                .AddDetector(new AWSECSResourceDetector());
    }

    private OtlpMetricExporter ApplicationSignalsExporterProvider()
    {
        var options = new OtlpExporterOptions();

        Logger.Log(
          LogLevel.Debug, "AWS Application Signals export protocol: %{0}", options.Protocol);

        string? applicationSignalsEndpoint = System.Environment.GetEnvironmentVariable(ApplicationSignalsExporterEndpointConfig);
        string? protocolString = System.Environment.GetEnvironmentVariable(DefaultProtocolEnvVarName);
        OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf;
        if (protocolString == "http/protobuf")
        {
            applicationSignalsEndpoint = applicationSignalsEndpoint ?? "http://localhost:4316/v1/metrics";
            protocol = OtlpExportProtocol.HttpProtobuf;
        }
        else if (protocolString == "grpc")
        {
            applicationSignalsEndpoint = applicationSignalsEndpoint ?? "http://localhost:4315";
            protocol = OtlpExportProtocol.Grpc;
        }
        else
        {
            throw new NotSupportedException("Unsupported AWS Application Signals export protocol: " + options.Protocol);
        }

        options.Endpoint = new Uri(applicationSignalsEndpoint);
        options.Protocol = protocol;

        return new OtlpMetricExporter(options);
    }
}
