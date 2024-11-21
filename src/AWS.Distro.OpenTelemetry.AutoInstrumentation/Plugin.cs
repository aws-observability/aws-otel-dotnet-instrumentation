// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Extensions.AWS.Trace;
#if !NETFRAMEWORK
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.AWSLambda;
#else
using System.Web;
using OpenTelemetry.Instrumentation.AspNet;
#endif
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.ResourceDetectors.AWS;
using OpenTelemetry.Resources;
using OpenTelemetry.Sampler.AWS;
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
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<Plugin>();
    private static readonly string ApplicationSignalsExporterEndpointConfig = "OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT";
    private static readonly string MetricExportIntervalConfig = "OTEL_METRIC_EXPORT_INTERVAL";
    private static readonly int DefaultMetricExportInterval = 60000;
    private static readonly string DefaultProtocolEnvVarName = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private static readonly string ResourceDetectorEnableConfig = "RESOURCE_DETECTORS_ENABLED";
    private static readonly string BackupSamplerEnabledConfig = "BACKUP_SAMPLER_ENABLED";
    private static readonly string BackupSamplerEnabled = System.Environment.GetEnvironmentVariable(BackupSamplerEnabledConfig) ?? "true";

    private static readonly string AwsLambdaFunctionNameConfig = "AWS_LAMBDA_FUNCTION_NAME";
    private static readonly string? AwsLambdaFunctionName = System.Environment.GetEnvironmentVariable(AwsLambdaFunctionNameConfig);

    private static readonly string AwsXrayDaemonAddressConfig = "AWS_XRAY_DAEMON_ADDRESS";
    private static readonly string? AwsXrayDaemonAddress = System.Environment.GetEnvironmentVariable(AwsXrayDaemonAddressConfig);

    private static readonly string OtelExporterOtlpTracesEndpointConfig = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    private static readonly string? OtelExporterOtlpTracesEndpoint = System.Environment.GetEnvironmentVariable(OtelExporterOtlpTracesEndpointConfig);

    private static readonly string OtelExporterOtlpEndpointConfig = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private static readonly string? OtelExporterOtlpEndpoint = System.Environment.GetEnvironmentVariable(OtelExporterOtlpEndpointConfig);

    private static readonly string FormatOtelSampledTracesBinaryPrefix = "T1S";
    private static readonly string FormatOtelUnSampledTracesBinaryPrefix = "T1U";

    private static readonly int LambdaSpanExportBatchSize = 10;

    private static readonly Dictionary<string, object> DistroAttributes = new Dictionary<string, object>
        {
            { "telemetry.distro.name", "aws-otel-dotnet-instrumentation" },
            { "telemetry.distro.version", Version.version + "-aws" },
        };

    private Sampler? sampler;

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

            // We want to be adding the exporter as the last processor in the traceProvider since processors
            // are executed in the order they were added to the provider.
            if (this.IsLambdaEnvironment() && !this.HasCustomTracesEndpoint())
            {
                Resource processResource = tracerProvider.GetResource();

                // UDP exporter for sampled spans
                var sampledSpanExporter = new OtlpUdpExporter(processResource, AwsXrayDaemonAddress, FormatOtelSampledTracesBinaryPrefix);
                tracerProvider.AddProcessor(new BatchActivityExportProcessor(exporter: sampledSpanExporter, maxExportBatchSize: LambdaSpanExportBatchSize));

                // UDP exporter for unsampled spans
                var unsampledSpanExporter = new OtlpUdpExporter(processResource, AwsXrayDaemonAddress, FormatOtelUnSampledTracesBinaryPrefix);
                tracerProvider.AddProcessor(new AwsBatchUnsampledSpanExportProcessor(exporter: unsampledSpanExporter, maxExportBatchSize: LambdaSpanExportBatchSize));
            }

            // Disable Application Metrics for Lambda environment
            if (!this.IsLambdaEnvironment())
            {
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
                BaseProcessor<Activity> spanMetricsProcessor = AwsSpanMetricsProcessorBuilder.Create(resource, provider).Build();
                tracerProvider.AddProcessor(spanMetricsProcessor);
            }
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
#if !NETFRAMEWORK
        builder.AddAWSLambdaConfigurations();
#endif
        return builder;
    }

    /// <summary>
    /// To configure tracing SDK after Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        var resourceBuilder = this.ResourceBuilderCustomizer(ResourceBuilder.CreateDefault());
        var resource = resourceBuilder.Build();
        this.sampler = SamplerUtil.GetSampler(resource);

        if (this.IsApplicationSignalsEnabled())
        {
            Logger.Log(LogLevel.Information, "AWS Application Signals enabled");
            var alwaysRecordSampler = AlwaysRecordSampler.Create(this.sampler);
            builder.SetSampler(alwaysRecordSampler);
        }
        else
        {
            builder.SetSampler(this.sampler);
        }

        // If the backup sampler is enabled, there is no need to hook up the x-ray sampler into the main opentelemetry
        // sdk logic. In this case, we hook up the alwaysOnSampler to that all the activities go through before running
        // them against the xray sampler. Without this, the sampler will be run twice, once by the sdk and a second time
        // after http instrumentation happens which messes up the frontend sampler graphs.
        if (BackupSamplerEnabled == "true" && this.sampler.GetType() == typeof(AWSXRayRemoteSampler))
        {
            var alwaysOnSampler = new ParentBasedSampler(new AlwaysOnSampler());
            if (this.IsApplicationSignalsEnabled())
            {
                builder.SetSampler(AlwaysRecordSampler.Create(alwaysOnSampler));
            }
            else
            {
                builder.SetSampler(alwaysOnSampler);
            }
        }

        return builder;
    }

    /// <summary>
    /// To configure Resource with resource detectors and <see cref="DistroAttributes"/>
    /// Check <see cref="ResourceBuilderCustomizer"/> for more information.
    /// </summary>
    /// <param name="builder"><see cref="ResourceBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public ResourceBuilder ConfigureResource(ResourceBuilder builder)
    {
        this.ResourceBuilderCustomizer(builder);
        return builder;
    }

    /// <summary>
    /// To configure HttpOptions and skip instrumentation for certain APIs
    /// Used to call ShouldSampleParent function as well
    /// </summary>
    /// <param name="options"><see cref="HttpClientTraceInstrumentationOptions"/> options to configure</param>
    public void ConfigureTracesOptions(HttpClientTraceInstrumentationOptions options)
    {
#if !NETFRAMEWORK
        options.FilterHttpRequestMessage = request =>
        {
            if (request.RequestUri?.AbsolutePath == "/GetSamplingRules" || request.RequestUri?.AbsolutePath == "/SamplingTargets")
            {
                return false;
            }

            return true;
        };

        options.EnrichWithHttpRequestMessage = (activity, request) =>
        {
            if (this.sampler != null && this.sampler.GetType() == typeof(AWSXRayRemoteSampler))
            {
                this.ShouldSampleParent(activity);
            }
        };
#endif

#if NETFRAMEWORK
        options.FilterHttpWebRequest = request =>
        {
            if (request.RequestUri?.AbsolutePath == "/GetSamplingRules" || request.RequestUri?.AbsolutePath == "/SamplingTargets")
            {
                return false;
            }

            return true;
        };

        options.EnrichWithHttpWebRequest = (activity, request) =>
        {
            if (this.sampler != null && this.sampler.GetType() == typeof(AWSXRayRemoteSampler))
            {
                this.ShouldSampleParent(activity);
            }
        };
#endif
    }

    /// <summary>
    /// Used to call ShouldSampleParent function
    /// </summary>
    /// <param name="options"><see cref="AspNetCoreTraceInstrumentationOptions"/> options to configure</param>
#if !NETFRAMEWORK
    public void ConfigureTracesOptions(AspNetCoreTraceInstrumentationOptions options)
    {
        options.EnrichWithHttpRequest = (activity, request) =>
        {
            // Storing a weak reference of the httpContext to be accessed later by processors. Weak References allow the garbage collector
            // to reclaim memory if the object is no longer used.
            // We are storing references due to the following:
            //      1. When a request is received, an activity starts immediately and in that phase,
            //      the routing middleware hasn't executed and thus the routing data isn't available yet
            //      2. Once the routing middleware is executed, and the request is matched to the route template,
            //      we are certain the routing data is avaialble when any children activities are started.
            //      3. We then use this HttpContext object to access the now available route data.
            activity.SetCustomProperty("HttpContextWeakRef", new WeakReference<HttpContext>(request.HttpContext));

            if (this.sampler != null && this.sampler.GetType() == typeof(AWSXRayRemoteSampler))
            {
                this.ShouldSampleParent(activity);
            }
        };
    }
#endif

#if NETFRAMEWORK
    /// <summary>
    /// Used to call ShouldSampleParent function
    /// </summary>
    /// <param name="options"><see cref="AspNetTraceInstrumentationOptions"/> options to configure</param>
    public void ConfigureTracesOptions(AspNetTraceInstrumentationOptions options)
    {
        options.EnrichWithHttpRequest = (activity, request) =>
        {
            HttpContext currentContext = HttpContext.Current;

            if (currentContext == null)
            {
                Type requestType = typeof(HttpRequest);

                PropertyInfo contextProperty = requestType.GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic);

                if (contextProperty != null)
                {
                    currentContext = (HttpContext)contextProperty.GetValue(request);
                }
            }

            if (currentContext != null)
            {
                activity.SetCustomProperty("HttpContextWeakRef", new WeakReference<HttpContext>(currentContext));
            }

            if (this.sampler != null && this.sampler.GetType() == typeof(AWSXRayRemoteSampler))
            {
                this.ShouldSampleParent(activity);
            }
        };
    }
#endif

    // This new function runs the sampler a second time after the needed attributes (such as UrlPath and HttpTarget)
    // are finally available from the http instrumentation libraries. The sampler hooked into the Opentelemetry SDK
    // runs right before any activity is started so for the purposes of our X-Ray sampler, that isn't work and breaks
    // the X-Ray functionality. Running it a second time here allows us to retain the sampler functionality.
    private void ShouldSampleParent(Activity activity)
    {
        if (BackupSamplerEnabled != "true")
        {
            return;
        }

        // We should sample the parent span only as any trace flags set on the parent
        // automatically propagates to all child spans (the X-Ray sampler is wrapped by ParentBasedSampler).
        if (activity.Parent != null)
        {
            return;
        }

        var samplingParameters = new SamplingParameters(
            default(ActivityContext),
            activity.TraceId,
            activity.DisplayName,
            activity.Kind,
            activity.TagObjects,
            activity.Links);

#pragma warning disable CS8602 // Dereference of a possibly null reference.
        var result = this.sampler.ShouldSample(samplingParameters);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        if (result.Decision == SamplingDecision.RecordAndSample)
        {
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        }
        else
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
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

        // Resource detectors are disabled if the environment variable is explicitly set to false or if the
        // application is in a lambda environment
        if (resourceDetectorsEnabled != "true" || this.IsLambdaEnvironment())
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

    private bool IsLambdaEnvironment()
    {
        // detect if running in AWS Lambda environment
        return AwsLambdaFunctionName != null;
    }

    private bool HasCustomTracesEndpoint()
    {
        // detect if running in AWS Lambda environment
        return OtelExporterOtlpTracesEndpoint != null || OtelExporterOtlpEndpoint != null;
    }
}
