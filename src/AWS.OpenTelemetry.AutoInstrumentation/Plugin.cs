// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Contrib.Extensions.AWSXRay.Resources;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// TODO: Add documentation here
/// </summary>
public class Plugin
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddConsole());
    private static readonly ILogger Logger = Factory.CreateLogger<AwsMetricAttributeGenerator>();
    private static readonly string APP_SIGNALS_ENABLED_CONFIG = "OTEL_AWS_APP_SIGNALS_ENABLED";
    private static readonly string ApplicationSignalsEnabledConfig = "OTEL_AWS_APPLICATION_SIGNALS_ENABLED";
    private static readonly string APP_SIGNALS_EXPORTER_ENDPOINT_CONFIG = "OTEL_AWS_APP_SIGNALS_EXPORTER_ENDPOINT";
    private static readonly string ApplicationSignalsExporterEndpointConfig = "OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT";
    private static readonly string MetricExportIntervalConfig = "OTEL_METRIC_EXPORT_INTERVAL";
    private static readonly int DefaultMetricExportInternal = 60000;
    private static readonly string OtelTracesSampler = "OTEL_TRACES_SAMPLER";
    private static readonly string OtelTracesSamplerArg = "OTEL_TRACES_SAMPLER_ARG";
    private static readonly string DefaultProtocolEnvVarName = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>
    /// To configure plugin, before OTel SDK configuration is called.
    /// </summary>public void Initializing()
    public void Initializing()
    {
        // My custom logic here
    }

    /// <summary>
    /// To access TracerProvider right after TracerProviderBuilder.Build() is executed.
    /// </summary>
    /// <param name="tracerProvider"><see cref="TracerProvider"/> Provider to configure</param>
    public void TracerProviderInitialized(TracerProvider tracerProvider)
    {
        if (this.IsApplicationSignalsEnabled())
        {
            // add new export processor here.
            // https://stackoverflow.com/questions/12993962/set-value-of-private-field
            // use reflection to get the internal exporter and set the new modified exporter.
            // I need to get the composite processor after SDK init and replace the exporter.
            var processor = tracerProvider.GetType().GetProperty("Processor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tracerProvider);
            var exporter = processor.GetType().GetField("exporter", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(processor);

            tracerProvider.AddProcessor(AttributePropagatingSpanProcessorBuilder.Create().Build());

            string? intervalConfigString = System.Environment.GetEnvironmentVariable(MetricExportIntervalConfig);
            int exportInterval = DefaultMetricExportInternal;
            try
            {
                exportInterval = Convert.ToInt32(intervalConfigString);
            }
            catch (Exception)
            {
                Logger.Log(LogLevel.Trace, "Could not convert OTEL_METRIC_EXPORT_INTERVAL to integer. Using default value 60000.");
            }

            if (exportInterval.CompareTo(DefaultMetricExportInternal) > 0)
            {
                exportInterval = DefaultMetricExportInternal;
                Logger.Log(LogLevel.Information, "AWS Application Signals metrics export interval capped to {0}", exportInterval);
            }

            // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md#enable-metric-exporter
            // for setting the temporatityPref.
            var metricReader = new PeriodicExportingMetricReader(this.ApplicationSignalsExporterProvider(), exportInterval);
            MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddReader(metricReader)
            .ConfigureResource(builder => builder
                .AddDetector(new AWSEC2ResourceDetector())
                .AddDetector(new AWSECSResourceDetector())
                .AddDetector(new AWSEKSResourceDetector()))
            .AddMeter("AwsSpanMetricsProcessor")
            .Build();

            Resource resource = provider.GetResource();
            BaseProcessor<Activity> spanMetricsProcessor = AwsSpanMetricsProcessorBuilder.Create(resource).Build();
            tracerProvider.AddProcessor(spanMetricsProcessor);
        }
    }

    private OtlpMetricExporter ApplicationSignalsExporterProvider()
    {
        // https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/docs/metrics/customizing-the-sdk#configuring-the-aggregation-of-a-histogram
        // use the above to check about adding aggregation type.
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

    /// <summary>
    /// To access MeterProvider right after MeterProviderBuilder.Build() is executed.
    /// </summary>
    /// <param name="meterProvider"><see cref="MeterProvider"/> Provider to configure</param>
    public void MeterProviderInitialized(MeterProvider meterProvider)
    {
        // My custom logic here
    }

    /// <summary>
    /// To configure tracing SDK before Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public TracerProviderBuilder BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
        // My custom logic here
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
            Sampler alwaysRecordSampler = AlwaysRecordSampler.Create(this.GetSampler());
            builder.SetSampler(alwaysRecordSampler);
        }

        builder.AddXRayTraceId();
        return builder;
    }

    // This function is based on an internal function in Otel:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/1bbafaa7b7bed6470ff52fc76b6e881cd19692a5/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L408
    // Unfortunately, that function is private.
    private Sampler GetSampler()
    {
        string? tracesSampler = System.Environment.GetEnvironmentVariable(OtelTracesSampler);
        string? tracesSamplerArg = System.Environment.GetEnvironmentVariable(OtelTracesSamplerArg);
        double samplerProbability = 1.0;
        if (tracesSampler != null)
        {
            try
            {
                samplerProbability = Convert.ToDouble(tracesSamplerArg);
            }
            catch (Exception)
            {
                Logger.Log(LogLevel.Trace, "Could not convert OTEL_TRACES_SAMPLER_ARG to double. Using default value 1.0.");
            }
        }

        // based on the list of available samplers:
        // https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/blob/77256e3a9666ee0f1f72fec5f4ca1a6d8500f229/docs/config.md#samplers
        // Need to also add XRay Sampler here.
        // Currently, this is the only way to get the sampler as there is no factory and we can't get the sampler
        // that was already set in the TracerProviderBuilder
        switch (tracesSampler)
        {
            case "always_on":
                return new AlwaysOnSampler();
            case "always_off":
                return new AlwaysOffSampler();
            case "traceidratio":
                return new TraceIdRatioBasedSampler(samplerProbability);
            case "parentbased_always_off":
                Sampler alwaysOffSampler = new AlwaysOffSampler();
                return new ParentBasedSampler(alwaysOffSampler);
            case "parentbased_traceidratio":
                Sampler traceIdRatioSampler = new TraceIdRatioBasedSampler(samplerProbability);
                return new ParentBasedSampler(traceIdRatioSampler);
            case "parentbased_always_on":
            default:
                Sampler alwaysOnSampler = new AlwaysOnSampler();
                return new ParentBasedSampler(alwaysOnSampler);
        }
    }

    // To configure any metrics options used by OpenTelemetry .NET Automatic Instrumentation
    public void ConfigureMetricsOptions(MetricReaderOptions options)
    {
        // My custom logic here
        // Find supported options below
    }

    /// <summary>
    /// To configure metrics SDK before Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public MeterProviderBuilder BeforeConfigureMeterProvider(MeterProviderBuilder builder)
    {
        // My custom logic here
        return builder;
    }

    /// <summary>
    /// To configure metrics SDK after Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        // My custom logic here
        return builder;
    }

    /// <summary>
    /// To configure Resource
    /// </summary>
    /// <param name="builder"><see cref="ResourceBuilder"/> Builder to configure</param>
    /// <returns>Returns configured builder</returns>
    public ResourceBuilder ConfigureResource(ResourceBuilder builder)
    {
        // My custom logic here
        // Please note this method is common to set the resource for trace, logs and metrics.
        // This method could be overridden by ConfigureTracesOptions, ConfigureMeterProvider and ConfigureLogsOptions
        // by calling SetResourceBuilder with new object.
        return builder;
    }

    private bool IsApplicationSignalsEnabled()
    {
        return System.Environment.GetEnvironmentVariable(ApplicationSignalsEnabledConfig) == "true";
    }
}
