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
using System.Text.RegularExpressions;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
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
    internal static readonly string LambdaApplicationSignalsRemoteEnvironment = "LAMBDA_APPLICATION_SIGNALS_REMOTE_ENVIRONMENT";
    private static readonly string XRayOtlpEndpointPattern = "^https://xray\\.([a-z0-9-]+)\\.amazonaws\\.com/v1/traces$";
    private static readonly string SigV4EnabledConfig = "OTEL_AWS_SIG_V4_ENABLED";
    private static readonly string TracesExporterConfig = "OTEL_TRACES_EXPORTER";
    private static readonly string OtelExporterOtlpTracesTimeout = "OTEL_EXPORTER_OTLP_TIMEOUT";
    private static readonly int DefaultOtlpTracesTimeoutMilli = 10000;
#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = Factory.CreateLogger<Plugin>();
    private static readonly string ApplicationSignalsExporterEndpointConfig = "OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT";
    private static readonly string ApplicationSignalsRuntimeEnabledConfig = "OTEL_AWS_APPLICATION_SIGNALS_RUNTIME_ENABLED";
    private static readonly string MetricExporterConfig = "OTEL_METRICS_EXPORTER";
    private static readonly string MetricExportIntervalConfig = "OTEL_METRIC_EXPORT_INTERVAL";
    private static readonly int DefaultMetricExportInterval = 60000;
    private static readonly string DefaultProtocolEnvVarName = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private static readonly string ResourceDetectorEnableConfig = "RESOURCE_DETECTORS_ENABLED";
    private static readonly string BackupSamplerEnabledConfig = "BACKUP_SAMPLER_ENABLED";
    private static readonly string BackupSamplerEnabled = System.Environment.GetEnvironmentVariable(BackupSamplerEnabledConfig) ?? "true";

    private static readonly string AwsXrayDaemonAddressConfig = "AWS_XRAY_DAEMON_ADDRESS";
    private static readonly string? AwsXrayDaemonAddress = System.Environment.GetEnvironmentVariable(AwsXrayDaemonAddressConfig);

    private static readonly string OtelExporterOtlpTracesEndpointConfig = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
    private static readonly string? OtelExporterOtlpTracesEndpoint = System.Environment.GetEnvironmentVariable(OtelExporterOtlpTracesEndpointConfig);

    private static readonly string OtelExporterOtlpEndpointConfig = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private static readonly string? OtelExporterOtlpEndpoint = System.Environment.GetEnvironmentVariable(OtelExporterOtlpEndpointConfig);

    private static readonly string FormatOtelSampledTracesBinaryPrefix = "T1S";
    private static readonly string FormatOtelUnSampledTracesBinaryPrefix = "T1U";
    private static readonly string RuntimeMetricMeterName = "OpenTelemetry.Instrumentation.Runtime";

    // As per https://opentelemetry.io/docs/specs/semconv/resource/#service
    // If service name is not specified, SDK defaults the service name starting with unknown_service
    private static readonly string OtelUnknownServicePrefix = "unknown_service";

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

            // Disable Application Metrics for Lambda environment
            if (!AwsSpanProcessingUtil.IsLambdaEnvironment())
            {
                // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md#enable-metric-exporter
                // for setting the temporatityPref.
                var metricReader = new PeriodicExportingMetricReader(this.CreateApplicationSignalsMetricExporter(), GetMetricExportInterval())
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

        // We want to be adding the exporter as the last processor in the traceProvider since processors
        // are executed in the order they were added to the provider.
        if (AwsSpanProcessingUtil.IsLambdaEnvironment())
        {
            tracerProvider.AddProcessor(new AwsLambdaSpanProcessor());

            if (!this.HasCustomTracesEndpoint())
            {
                Resource processResource = tracerProvider.GetResource();

                // UDP exporter for sampled spans
                var sampledSpanExporter = new XrayUdpExporter(processResource, AwsXrayDaemonAddress, FormatOtelSampledTracesBinaryPrefix);
                tracerProvider.AddProcessor(new BatchActivityExportProcessor(exporter: sampledSpanExporter, maxExportBatchSize: LambdaSpanExportBatchSize));
                if (this.IsApplicationSignalsEnabled())
                {
                    // Register UDP Exporter to export unsampled traces in Lambda
                    // only when Application Signals enabled
                    var unsampledSpanExporter = new XrayUdpExporter(processResource, AwsXrayDaemonAddress, FormatOtelUnSampledTracesBinaryPrefix);
                    tracerProvider.AddProcessor(new AwsBatchUnsampledSpanExportProcessor(exporter: unsampledSpanExporter, maxExportBatchSize: LambdaSpanExportBatchSize));
                }
            }
        }

        if (this.IsSigV4AuthEnabled())
        {
            OtlpExporterOptions options = new OtlpExporterOptions();
#pragma warning disable CS8604 // Possible null reference argument.

            // This is already checked in isSigV4Enabled predicate
            options.Endpoint = new Uri(OtelExporterOtlpTracesEndpoint);
#pragma warning restore CS8604 // Possible null reference argument.
            options.TimeoutMilliseconds = this.GetTracesOtlpTimeout();
            var otlpAwsSpanExporter = new OtlpAwsSpanExporter(options, tracerProvider.GetResource());

            tracerProvider.AddProcessor(new BatchActivityExportProcessor(exporter: otlpAwsSpanExporter));
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
        if (BackupSamplerEnabled == "true" && SamplerUtil.IsXraySampler())
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
    /// // To configure metrics SDK after Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder">The metric provider builder</param>
    /// <returns>The configured metric provider builder</returns>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        if (!this.IsApplicationSignalsRuntimeEnabled())
        {
            return builder;
        }

        var exporters = System.Environment.GetEnvironmentVariable(MetricExporterConfig);
        if (!string.IsNullOrEmpty(exporters) && exporters.Contains("none"))
        {
            Logger.Log(LogLevel.Information, "Install runtime metric filter in metrics collection.");
            builder.AddView(instrument => instrument.Meter.Name == RuntimeMetricMeterName
                ? null
                : MetricStreamConfiguration.Drop);
        }

        var runtimeScopeName = new HashSet<string>() { RuntimeMetricMeterName };
        var metricReader = new PeriodicExportingMetricReader(
            this.CreateScopeBasedOtlpMetricExporter(runtimeScopeName), GetMetricExportInterval())
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Delta,
        };

        builder.AddReader(metricReader);
        Logger.Log(LogLevel.Information, "AWS Application Signals runtime metrics enabled.");

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

            if (request.RequestUri?.AbsolutePath.Contains("/runtime/invocation/") == true)
            {
                return false;
            }

            return true;
        };

        options.EnrichWithHttpRequestMessage = (activity, request) =>
        {
            if (this.sampler != null && SamplerUtil.IsXraySampler())
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
            if (this.sampler != null && SamplerUtil.IsXraySampler())
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

            if (this.sampler != null && SamplerUtil.IsXraySampler())
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

            if (this.sampler != null && SamplerUtil.IsXraySampler())
            {
                this.ShouldSampleParent(activity);
            }
        };
    }
#endif

    private static int GetMetricExportInterval()
    {
        var intervalConfigString = System.Environment.GetEnvironmentVariable(MetricExportIntervalConfig);
        var exportInterval = DefaultMetricExportInterval;
        try
        {
            var parsedExportInterval = Convert.ToInt32(intervalConfigString);
            exportInterval = parsedExportInterval != 0 ? parsedExportInterval : DefaultMetricExportInterval;
        }
        catch (Exception)
        {
            Logger.Log(LogLevel.Warning, "Could not convert OTEL_METRIC_EXPORT_INTERVAL to integer. Using default value 60000.");
        }

        if (exportInterval.CompareTo(DefaultMetricExportInterval) > 0)
        {
            exportInterval = DefaultMetricExportInterval;
            Logger.Log(LogLevel.Information, "AWS Application Signals metrics export interval capped to {0}", exportInterval);
        }

        return exportInterval;
    }

    private static void ConfigureOtlpExporterOptions(OtlpExporterOptions options)
    {
        var applicationSignalsEndpoint = System.Environment.GetEnvironmentVariable(ApplicationSignalsExporterEndpointConfig);
        var protocolString = System.Environment.GetEnvironmentVariable(DefaultProtocolEnvVarName) ?? "http/protobuf";
        OtlpExportProtocol protocol;

        switch (protocolString)
        {
            case "http/protobuf":
                applicationSignalsEndpoint = applicationSignalsEndpoint ?? "http://localhost:4316/v1/metrics";
                protocol = OtlpExportProtocol.HttpProtobuf;
                break;
            case "grpc":
                applicationSignalsEndpoint = applicationSignalsEndpoint ?? "http://localhost:4315";
                protocol = OtlpExportProtocol.Grpc;
                break;
            default:
                throw new NotSupportedException("Unsupported AWS Application Signals export protocol: " + protocolString);
        }

        options.Endpoint = new Uri(applicationSignalsEndpoint);
        options.Protocol = protocol;

        Logger.Log(
            LogLevel.Debug, "AWS Application Signals export protocol: %{0}", options.Protocol);
        Logger.Log(
            LogLevel.Debug, "AWS Application Signals export endpoint: %{0}", options.Endpoint);
    }

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
        // An activity can still have a parent even if the parent object is null. This is the case if the
        // parent is remote. In this case, the child span will inherit the sampling decision from the parent context
        // but won't have a Parent object.
        if (activity.Parent != null || activity.HasRemoteParent || activity.ParentId != null)
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

    private bool IsApplicationSignalsRuntimeEnabled()
    {
        return this.IsApplicationSignalsEnabled() &&
               !"false".Equals(System.Environment.GetEnvironmentVariable(ApplicationSignalsRuntimeEnabledConfig));
    }

    private ResourceBuilder ResourceBuilderCustomizer(ResourceBuilder builder)
    {
        builder.AddAttributes(DistroAttributes);
        var resource = builder.Build();
        var serviceName = (string?)resource.Attributes.FirstOrDefault(attr => attr.Key == ResourceSemanticConventions.AttributeServiceName).Value;
        if (serviceName == null || serviceName.StartsWith(OtelUnknownServicePrefix))
        {
            Logger.Log(LogLevel.Warning, "No valid service name provided.");
            serviceName = AwsSpanProcessingUtil.UnknownService;
        }

        builder.AddAttributes(new Dictionary<string, object> { { AwsAttributeKeys.AttributeAWSLocalService, serviceName } });

        // ResourceDetectors are enabled by default. Adding config to be able to disable during local testing
        var resourceDetectorsEnabled = System.Environment.GetEnvironmentVariable(ResourceDetectorEnableConfig) ?? "true";

        // Resource detectors are disabled if the environment variable is explicitly set to false or if the
        // application is in a lambda environment
        if (resourceDetectorsEnabled != "true" || AwsSpanProcessingUtil.IsLambdaEnvironment())
        {
            return builder;
        }

        // The current version of the AWS Resource Detectors doesn't build the EKS and ECS resource detectors
        // for NETFRAMEWORK. More details are found here: https://github.com/open-telemetry/opentelemetry-dotnet-contrib/pull/1177#discussion_r1193329666
        // We need to work with upstream to support these detectors for windows.
        builder.AddAWSEC2Detector();
#if !NETFRAMEWORK
        builder
            .AddAWSEKSDetector()
            .AddAWSECSDetector();
#endif

        return builder;
    }

    private OtlpMetricExporter CreateApplicationSignalsMetricExporter()
    {
        var options = new OtlpExporterOptions();
        ConfigureOtlpExporterOptions(options);
        return new OtlpMetricExporter(options);
    }

    private ScopeBasedOtlpMetricExporter CreateScopeBasedOtlpMetricExporter(HashSet<string> registeredScopeNames)
    {
        var options = new ScopeBasedOtlpMetricExporter.ScopeBasedOtlpExporterOptions();
        ConfigureOtlpExporterOptions(options);
        options.RegisteredScopeNames = registeredScopeNames;
        return new ScopeBasedOtlpMetricExporter(options);
    }

    private bool HasCustomTracesEndpoint()
    {
        // detect if running in AWS Lambda environment
        return OtelExporterOtlpTracesEndpoint != null || OtelExporterOtlpEndpoint != null;
    }

    // The setup here requires OTEL_TRACES_EXPORTER to be set to none in order to avoid exporting the spans twice.
    // However that introduces the problem of overriding the default behavior of when OTEL_TRACES_EXPORTER is set to none which is
    // why we introduce a new environment variable that confirms traces are exported to the OTLP XRay endpoint.
    private bool IsSigV4AuthEnabled()
    {
        bool isXrayOtlpEndpoint = OtelExporterOtlpTracesEndpoint != null && new Regex(XRayOtlpEndpointPattern, RegexOptions.Compiled).IsMatch(OtelExporterOtlpTracesEndpoint);

        if (isXrayOtlpEndpoint)
        {
            Logger.Log(LogLevel.Information, "Detected using AWS OTLP XRay Endpoint.");
            string? sigV4EnabledConfig = System.Environment.GetEnvironmentVariable(Plugin.SigV4EnabledConfig);

            if (sigV4EnabledConfig == null || !sigV4EnabledConfig.Equals("true"))
            {
                Logger.Log(LogLevel.Information, $"Please enable SigV4 authentication when exporting traces to OTLP XRay Endpoint by setting {SigV4EnabledConfig}=true");
                return false;
            }

            Logger.Log(LogLevel.Information, $"SigV4 authentication is enabled");

            string? tracesExporter = System.Environment.GetEnvironmentVariable(Plugin.TracesExporterConfig);

            if (tracesExporter == null || tracesExporter != "none")
            {
                Logger.Log(LogLevel.Information, $"Please disable other tracing exporters by setting {TracesExporterConfig}=none");
                return false;
            }

            Logger.Log(LogLevel.Information, $"Proper configuration has been detected, now exporting spans to {OtelExporterOtlpTracesEndpoint}");

            return true;
        }

        return false;
    }

    // https://opentelemetry.io/docs/languages/sdk-configuration/otlp-exporter/#otel_exporter_otlp_timeout:~:text=traces%20in%20milliseconds.-,Default%20value%3A%2010000%20(10s),-Example%3A%20export
    private int GetTracesOtlpTimeout()
    {
        string? timeout = System.Environment.GetEnvironmentVariable(OtelExporterOtlpTracesTimeout);

        if (timeout != null)
        {
            try
            {
                return int.Parse(timeout);
            }
            catch (Exception)
            {
                return DefaultOtlpTracesTimeoutMilli;
            }
        }

        return DefaultOtlpTracesTimeoutMilli;
    }
}
