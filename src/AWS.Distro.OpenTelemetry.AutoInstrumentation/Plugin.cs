// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Extensions.AWS.Trace;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.ResourceDetectors.AWS;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using B3Propagator = OpenTelemetry.Extensions.Propagators.B3Propagator;

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
    private static readonly string ResourceDetectorEnableConfig = "RESOURCE_DETECTORS_ENABLED";

    private static readonly Dictionary<string, object> DistroAttributes = new Dictionary<string, object>
        {
            { "telemetry.distro.name", "aws-otel-dotnet-instrumentation" },
            { "telemetry.distro.version", Version.version + "-aws" },
        };

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
            // setting the default propagators to be W3C tracecontext, b3, b3multi and xray
            // Calling in the TracerProviderInitialized function to override whatever is set by
            // the otel instrumentation. For Application Signals, these propagators are required.
            // This is the function that sets the propagators in OTEL:
            // https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/blob/5d438056871e9eeaa483840693139491407c136f/src/OpenTelemetry.AutoInstrumentation/Configurations/EnvironmentConfigurationSdkHelper.cs#L44
            // and this is where where it's being called: https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/blob/5d438056871e9eeaa483840693139491407c136f/src/OpenTelemetry.AutoInstrumentation/Instrumentation.cs#L133
            Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator(new List<TextMapPropagator>
            {
                new TraceContextPropagator(), // W3C tracecontext
                new B3Propagator(singleHeader: true), // b3
                new B3Propagator(singleHeader: false), // b3multi
                new AWSXRayPropagator(), // xray
            }));

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
            .ConfigureResource(builder => this.ResourceBuilderCustomizer(builder))
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
            resource.Attributes.ToList().ForEach(attribute => Console.WriteLine(attribute.Key + " " + attribute.Value));
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
            var resourceBuilder = this.ResourceBuilderCustomizer(ResourceBuilder.CreateDefault());
            var resource = resourceBuilder.Build();
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
            var resourceBuilder = this.ResourceBuilderCustomizer(ResourceBuilder.CreateDefault());
            var resource = resourceBuilder.Build();
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
        builder.AddAttributes(DistroAttributes);
        return builder;
    }

    /// <summary>
    /// To configure HttpOptions and skip instrumentation for certain APIs
    /// </summary>
    /// <param name="options"><see cref="HttpClientTraceInstrumentationOptions"/> options to configure</param>
    public void ConfigureTracesOptions(HttpClientTraceInstrumentationOptions options)
    {
        options.FilterHttpRequestMessage = request =>
        {
            if (request.RequestUri?.AbsolutePath == "/GetSamplingRules" || request.RequestUri?.AbsolutePath == "/SamplingTargets")
            {
                return false;
            }

            return true;
        };
    }

    private bool IsApplicationSignalsEnabled()
    {
        return System.Environment.GetEnvironmentVariable(ApplicationSignalsEnabledConfig) == "true";
    }

    private ResourceBuilder ResourceBuilderCustomizer(ResourceBuilder builder)
    {
        builder.AddAttributes(DistroAttributes);

        // ResourceDetectors are enabled by default. Adding config to be able to disable during local testing
        var resourceDetectorsEnabled = System.Environment.GetEnvironmentVariable(ResourceDetectorEnableConfig) ?? "true";

        if (resourceDetectorsEnabled != "true")
        {
            return builder;
        }

        // The current version of the AWS Resource Detectors doesn't build the EKS and ECS resource detectors
        // for NETFRAMEWORK. More details are found here: https://github.com/open-telemetry/opentelemetry-dotnet-contrib/pull/1177#discussion_r1193329666
        // We need to work with upstream to support these detectors for windows.
        builder.AddDetector(new AWSEC2ResourceDetector());
#if !NETFRAMEWORK
        builder
            .AddDetector(new AWSEKSResourceDetector())
            .AddDetector(new AWSECSResourceDetector());
#endif

        return builder;
    }

    private OtlpMetricExporter ApplicationSignalsExporterProvider()
    {
        var options = new OtlpExporterOptions();

        string? applicationSignalsEndpoint = System.Environment.GetEnvironmentVariable(ApplicationSignalsExporterEndpointConfig);
        string? protocolString = System.Environment.GetEnvironmentVariable(DefaultProtocolEnvVarName) ?? "http/protobuf";
        OtlpExportProtocol protocol;
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
            throw new NotSupportedException("Unsupported AWS Application Signals export protocol: " + protocolString);
        }

        options.Endpoint = new Uri(applicationSignalsEndpoint);
        options.Protocol = protocol;

        Logger.Log(
          LogLevel.Debug, "AWS Application Signals export protocol: %{0}", options.Protocol);
        Logger.Log(
          LogLevel.Debug, "AWS Application Signals export endpoint: %{0}", options.Endpoint);

        return new OtlpMetricExporter(options);
    }
}
