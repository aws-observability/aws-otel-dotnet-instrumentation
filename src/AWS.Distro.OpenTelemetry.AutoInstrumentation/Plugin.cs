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
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
#if !NETFRAMEWORK
using AWS.Distro.OpenTelemetry.DynamicInstrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
#endif
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
    private static readonly string CloudWatchLogsOtlpEndpointPattern = "^https://logs\\.([a-z0-9-]+)\\.amazonaws\\.com/v1/logs$";
    private static readonly string SigV4EnabledConfig = "OTEL_AWS_SIG_V4_ENABLED";
    private static readonly string TracesExporterConfig = "OTEL_TRACES_EXPORTER";
    private static readonly string OtelExporterOtlpTracesTimeout = "OTEL_EXPORTER_OTLP_TIMEOUT";
    private static readonly string OtelExporterOtlpLogsEndpointConfig = "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT";
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
#if !NETFRAMEWORK
        this.InitializeDynamicInstrumentation();
        this.InitializeServiceEvents();
#endif
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
                .ConfigureResource(builder => this.ResourceBuilderCustomizer(builder, tracerProvider.GetResource()))
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

        // Adaptive sampling: parse config, create processor, wire AwsBatchUnsampledSpanProcessor for capture
        var xraySampler = SamplerUtil.LastCreatedXRaySampler;
        if (this.IsApplicationSignalsEnabled() && xraySampler != null)
        {
            var adaptiveConfig = AdaptiveSamplingConfigParser.Parse(
                System.Environment.GetEnvironmentVariable(AdaptiveSamplingConfigParser.EnvVar));
            if (adaptiveConfig != null)
            {
                var adaptiveSampler = new AdaptiveSampler(adaptiveConfig, xraySampler);

                // Anomaly-captured spans are unsampled (traceFlags=0) so they need
                // AwsBatchUnsampledSpanProcessor which exports regardless of sampling flag.
                // Not registered on TracerProvider to avoid exporting ALL unsampled spans globally.
                Resource captureResource = tracerProvider.GetResource();
                var captureExporter = new XrayUdpExporter(captureResource, AwsXrayDaemonAddress, FormatOtelUnSampledTracesBinaryPrefix);
                var captureProcessor = new AwsBatchUnsampledSpanExportProcessor(exporter: captureExporter);

                adaptiveSampler.SetSpanBatcher(span => captureProcessor.OnEnd(span));
                tracerProvider.AddProcessor(new AdaptiveSamplingSpanProcessor(adaptiveSampler));

                Logger.Log(LogLevel.Information, "Adaptive sampling enabled with anomaly capture via AwsBatchUnsampledSpanExportProcessor");
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

#if !NETFRAMEWORK
        // No-op unless Dynamic Instrumentation was initialized in Initializing().
        DynamicInstrumentationManager.OnTracerProviderInitialized(tracerProvider);
#endif
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
            var resourceBuilder = ResourceBuilder
                .CreateEmpty() // Don't use CreateDefault because it puts service name unknown by default.
                .AddEnvironmentVariableDetector()
                .AddTelemetrySdk();

            resourceBuilder = this.ResourceBuilderCustomizer(resourceBuilder);
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
        var resourceBuilder = ResourceBuilder
                .CreateEmpty() // Don't use CreateDefault because it puts service name unknown by default.
                .AddEnvironmentVariableDetector()
                .AddTelemetrySdk();

        resourceBuilder = this.ResourceBuilderCustomizer(resourceBuilder);
        var resource = resourceBuilder.Build();
        this.sampler = SamplerUtil.GetSampler(resource);

        if (this.IsApplicationSignalsEnabled())
        {
            Logger.Log(LogLevel.Information, "AWS Application Signals enabled");
        }

        // AlwaysRecordSampler upgrades a Drop decision to RecordOnly so processors still observe
        // the activity. Application Signals needs that for its span metrics; ServiceEvents needs it
        // for exactly the same reason — EndpointActivityProcessor.OnEnd only runs for activities the
        // SDK considers recorded, so without this a standalone ServiceEvents deployment using a
        // sampling sampler (OTEL_TRACES_SAMPLER=traceidratio, always_off, ...) would silently thin
        // out or lose its endpoint metrics. The customer's own sampling decision is untouched: the
        // configured sampler still decides what gets exported as a trace.
        if (this.IsApplicationSignalsEnabled() || IsServiceEventsActive())
        {
            builder.SetSampler(AlwaysRecordSampler.Create(this.sampler));
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
            if (this.IsApplicationSignalsEnabled() || IsServiceEventsActive())
            {
                builder.SetSampler(AlwaysRecordSampler.Create(alwaysOnSampler));
            }
            else
            {
                builder.SetSampler(alwaysOnSampler);
            }
        }

#if !NETFRAMEWORK
        // ServiceEvents registers its BaseProcessor<Activity> collectors last, so they observe
        // activities after the distro's own processors. No-op when ServiceEvents is disabled
        // (Current is null), and never allowed to break the customer's tracer pipeline.
        try
        {
            // Gated on IsInitialized for the same reason as the RecordException site below:
            // Current is non-null even when ServiceEvents is disabled. RegisterTracerProcessors
            // also no-ops internally when the collectors were never created, but relying on that
            // would be an implicit contract rather than an explicit gate.
            if (ServiceEventsInstrumentation.Current?.IsInitialized == true)
            {
                ServiceEventsInstrumentation.Current.RegisterTracerProcessors(builder);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ServiceEvents processor registration failed; feature degraded.");
        }
#endif

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
    /// To configure logs SDK. In Lambda, registers CompactConsoleLogRecordExporter for console
    /// output and a SigV4-signed OTLP exporter (via SimpleLogRecordExportProcessor for
    /// synchronous flush before Lambda freezes the container).
    /// </summary>
    /// <param name="options">The OpenTelemetry logger options</param>
    public void ConfigureLogsOptions(global::OpenTelemetry.Logs.OpenTelemetryLoggerOptions options)
    {
        if (!AwsSpanProcessingUtil.IsLambdaEnvironment())
        {
            return;
        }

        options.AddProcessor(new global::OpenTelemetry.SimpleLogRecordExportProcessor(
            new AutoInstrumentation.Exporter.Console.Logs.CompactConsoleLogRecordExporter()));

        string? logsEndpoint = System.Environment.GetEnvironmentVariable(OtelExporterOtlpLogsEndpointConfig);
        if (!string.IsNullOrEmpty(logsEndpoint) && System.Text.RegularExpressions.Regex.IsMatch(
            logsEndpoint, CloudWatchLogsOtlpEndpointPattern))
        {
            string region = new Uri(logsEndpoint).Host.Split('.')[1];
            var headers = ParseOtlpHeaders(System.Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_HEADERS"));
            var exporter = new SigV4OtlpLogExporter(new Uri(logsEndpoint), region, headers);
            options.AddProcessor(new global::OpenTelemetry.SimpleLogRecordExportProcessor(exporter));
            Logger.Log(LogLevel.Information, "Registered SigV4-signed OTLP log exporter for Lambda: {0}", logsEndpoint);
        }
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

#if !NETFRAMEWORK
    /// <summary>
    /// Used to call ShouldSampleParent function
    /// </summary>
    /// <param name="options"><see cref="AspNetCoreTraceInstrumentationOptions"/> options to configure</param>
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

        // Deliberately does NOT set options.RecordException, even though ServiceEvents wants the
        // exception type for EndpointErrorMetrics' `exception` dimension.
        //
        // RecordException makes OTel attach an `exception` event carrying exception.message and
        // exception.stacktrace to the customer's own server spans, which their trace pipeline then
        // exports. Messages and stacks routinely contain connection strings, tokens and user
        // identifiers, so switching it on would leak that into customer-visible telemetry — and
        // because ServiceEvents is on by default with Application Signals, it would do so silently
        // on upgrade, for customers who never asked for ServiceEvents at all. Self-telemetry must
        // not change what the customer's spans contain.
        //
        // ServiceEvents gets the type from the `error.type` tag instead, which the ASP.NET Core
        // instrumentation sets on the error path regardless of this option, and which is a type name
        // rather than a message. See EndpointActivityProcessor.ReadExceptionDetails.
        //
        // IncidentSnapshot additionally needs the message and stack trace, which no span tag carries.
        // Those are captured through EnrichWithException, which hands us the live Exception without
        // touching the span, and stashed privately on the Activity for ServiceEvents' own collectors
        // to read. Gated on ServiceEvents actually running, for the same reason CallPathCapture is
        // gated on an incident trigger existing: otherwise this allocates per failed request for a
        // consumer that is not there.
        if (ServiceEventsInstrumentation.Current?.IsInitialized == true)
        {
            // Chain rather than assign. This is the customer's options object and they may have set
            // their own enrichment; overwriting it would silently delete their callback.
            var customerEnrich = options.EnrichWithException;
            options.EnrichWithException = (activity, exception) =>
            {
                try
                {
                    ServiceEventsInstrumentation.Current?.CaptureException(activity, exception);
                }
                catch (Exception captureEx)
                {
                    // Telemetry must never interfere with the customer's request pipeline.
                    Logger.LogWarning(captureEx, "ServiceEvents exception capture failed.");
                }

                customerEnrich?.Invoke(activity, exception);
            };
        }
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

    /// <summary>
    /// Read a boolean environment flag the way this distro reads every boolean environment flag:
    /// case-insensitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted so there is exactly one answer to "does this value mean true", because there being
    /// two answers was a real bug. These comparisons used to be written inline and ordinally, so
    /// <c>OTEL_AWS_APPLICATION_SIGNALS_ENABLED=True</c> read as disabled here while ServiceEvents
    /// (<c>ServiceEventsConfig.GetBool</c>, case-insensitive, matching the Java/Python/JS distros)
    /// read it as enabled. ServiceEvents then suppressed its own EndpointSummary on the grounds that
    /// App Signals already carries that data, while this side never configured App Signals at all —
    /// so the per-endpoint summary was emitted by neither pipeline, silently, and the customer's
    /// sampler was swapped for AlwaysRecordSampler as well.
    /// </para>
    /// <para>
    /// Use this rather than an inline comparison for any new flag, so the next reader cannot
    /// reintroduce the disagreement.
    /// </para>
    /// </remarks>
    /// <param name="envVar">Name of the environment variable to read.</param>
    /// <returns><c>true</c> when the variable is set to <c>true</c> in any casing.</returns>
    internal static bool IsEnvFlagTrue(string envVar) =>
        string.Equals(
            System.Environment.GetEnvironmentVariable(envVar),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a boolean environment flag is explicitly set to <c>false</c>, in any casing. Used for
    /// flags that default to on, where only an explicit false turns them off.
    /// </summary>
    /// <param name="envVar">Name of the environment variable to read.</param>
    /// <returns><c>true</c> when the variable is set to <c>false</c> in any casing.</returns>
    internal static bool IsEnvFlagFalse(string envVar) =>
        string.Equals(
            System.Environment.GetEnvironmentVariable(envVar),
            "false",
            StringComparison.OrdinalIgnoreCase);

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
#if NET8_0_OR_GREATER
            // error CS0618: 'OtlpExportProtocol.Grpc' is obsolete: 'CAUTION: OTLP/gRPC is no longer supported for
            // .NET Framework or .NET Standard targets without supplying a properly configured HttpClientFactory.
            // It is strongly encouraged that you migrate to using OTLP/HTTPPROTOBUF.
            case "grpc":
                applicationSignalsEndpoint = applicationSignalsEndpoint ?? "http://localhost:4315";
                protocol = OtlpExportProtocol.Grpc;
                break;
#endif
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

    private static Dictionary<string, string> ParseOtlpHeaders(string? headersString)
    {
        var headers = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(headersString))
        {
            return headers;
        }

        foreach (var pair in headersString!.Split(','))
        {
            var parts = pair.Split(new[] { '=' }, 2);
            if (parts.Length == 2)
            {
                headers[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return headers;
    }

    // Whether ServiceEvents actually came up. Initializing() runs before AfterConfigureTracerProvider,
    // so by the time the tracer pipeline is configured this reflects the real outcome of enablement
    // (including the Lambda opt-out and the refusal-to-start path) rather than just the env flag.
    //
    // Deliberately declared outside the #if !NETFRAMEWORK region below. The sampler gate in
    // AfterConfigureTracerProvider calls this unconditionally, so the method has to exist on every
    // target framework — including net472, where ServiceEvents is not shipped and it returns false.
    private static bool IsServiceEventsActive()
    {
#if !NETFRAMEWORK
        return ServiceEventsInstrumentation.Current?.IsInitialized == true;
#else
        return false;
#endif
    }

#if !NETFRAMEWORK
    // Dynamic Instrumentation is hosted by this plugin (rather than a separate plugin/DLL) so it
    // ships and loads with the existing distribution — no extra OTEL_DOTNET_AUTO_PLUGINS entry.
    // Gated by the ENABLED flag (off by default); an opt-in feature must never abort startup, so
    // failures are logged, not thrown. Skipped in Lambda (no CloudWatch Agent). net8.0+ only —
    // DI is a modern-profiler feature not shipped in the .NET Framework build.
    private void InitializeDynamicInstrumentation()
    {
        try
        {
            if (AwsSpanProcessingUtil.IsLambdaEnvironment())
            {
                return;
            }

            var config = DynamicInstrumentationConfig.FromEnvironment();
            if (!config.Enabled)
            {
                return;
            }

            DynamicInstrumentationManager.Instance.Initialize(config);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Dynamic Instrumentation initialization failed; feature disabled.");
        }
    }

    // ServiceEvents is hosted by this plugin (rather than a separate plugin/DLL) so it ships and
    // loads with the existing distribution — customers get the feature on upgrade with no
    // OTEL_DOTNET_AUTO_PLUGINS change. Enablement follows Application Signals unless
    // OTEL_AWS_SERVICE_EVENTS_ENABLED is set explicitly, and is always off in Lambda; that rule
    // lives in ServiceEventsConfig.DetermineEnabled, which Initialize() applies. Telemetry must
    // never abort startup, so failures are logged, not thrown. net8.0+ only — ServiceEvents is not
    // shipped in the .NET Framework build.
    private void InitializeServiceEvents()
    {
        try
        {
            // The distro owns the authoritative version string; pass it in so ServiceEvents'
            // resource reports the same telemetry.distro.version as DistroAttributes above.
            var config = ServiceEventsConfig.FromEnvironment() with { DistroVersion = Version.version };
            var instrumentation = ServiceEventsInstrumentation.GetOrCreate(config);
            instrumentation.Initialize();

            // Without this, Dispose() never runs outside tests: the agent does not own the
            // providers ServiceEvents builds privately, so the final endpoint drain, the shutdown
            // DeploymentEvent and any buffered logs are lost on a graceful exit. ProcessExit is the
            // broadest hook available here — it covers a normal return from Main and SIGTERM on
            // Linux (which the runtime surfaces as ProcessExit), though not SIGKILL or a crash,
            // where no in-process hook could help anyway. The runtime allows only a couple of
            // seconds in this handler, so Dispose must stay bounded; it is idempotent, so a later
            // ResetForTests or explicit dispose is harmless.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    instrumentation.Dispose();
                }
                catch (Exception disposeEx)
                {
                    Logger.LogWarning(disposeEx, "ServiceEvents shutdown flush failed.");
                }
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ServiceEvents initialization failed; feature disabled.");
        }
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

    private bool IsApplicationSignalsEnabled() => IsEnvFlagTrue(ApplicationSignalsEnabledConfig);

    private bool IsApplicationSignalsRuntimeEnabled()
    {
        // Defaults to on, so only an explicit false disables it. An operator writing
        // RUNTIME_ENABLED=False plainly means off, and the previous ordinal comparison left it on.
        return this.IsApplicationSignalsEnabled() &&
               !IsEnvFlagFalse(ApplicationSignalsRuntimeEnabledConfig);
    }

    private ResourceBuilder ResourceBuilderCustomizer(ResourceBuilder builder, Resource? existingResource = null)
    {
        // base case: If there is an already existing resource passed as a parameter, we will copy
        // those resource attributes into the resource builder.
        if (existingResource != null)
        {
            builder.AddAttributes(existingResource.Attributes);
        }

        builder.AddAttributes(DistroAttributes);
        var resource = builder.Build();
        if (!resource.Attributes.Any(kvp => kvp.Key == ResourceSemanticConventions.AttributeServiceName))
        {
            // service.name was not configured yet use the fallback.
            Logger.Log(LogLevel.Warning, "No valid service name provided. Using fallback logic of using assembly name!");
            builder.AddAttributes(new Dictionary<string, object> { { ResourceSemanticConventions.AttributeServiceName, this.GetFallbackServiceName() } });
        }

        // Incase the above logic failed to get assembly or process name for any reason
        var serviceName = (string?)resource.Attributes.FirstOrDefault(attr => attr.Key == ResourceSemanticConventions.AttributeServiceName).Value;
        if (serviceName == null || serviceName.StartsWith(OtelUnknownServicePrefix))
        {
            Logger.Log(LogLevel.Warning, $"Fallback logic failed. Using {AwsSpanProcessingUtil.UnknownService} as service name!");
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
        // TODO: Remove explicit SemanticConventionVersion once upstream is fixed:
        // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/4768
        builder.AddAWSEC2Detector(opts => opts.SemanticConventionVersion = global::OpenTelemetry.Resources.AWS.SemanticConventionVersion.V1_40_0);
#if !NETFRAMEWORK
        builder
            .AddAWSEKSDetector()
            .AddAWSECSDetector(opts => opts.SemanticConventionVersion = global::OpenTelemetry.Resources.AWS.SemanticConventionVersion.V1_40_0);
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

    private string GetFallbackServiceName()
    {
        try
        {
#if NETFRAMEWORK
            // System.Web.dll is only available on .NET Framework
            if (System.Web.Hosting.HostingEnvironment.IsHosted)
            {
                // if this app is an ASP.NET application, return "SiteName/ApplicationVirtualPath".
                // note that ApplicationVirtualPath includes a leading slash.
                return (System.Web.Hosting.HostingEnvironment.SiteName + System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath).TrimEnd('/');
            }
#endif
            return Assembly.GetEntryAssembly()?.GetName().Name ?? this.GetCurrentProcessName();
        }
        catch
        {
            return OtelUnknownServicePrefix;
        }
    }

    /// <summary>
    /// <para>Wrapper around <see cref="Process.GetCurrentProcess"/> and <see cref="Process.ProcessName"/></para>
    /// <para>
    /// On .NET Framework the <see cref="Process"/> class is guarded by a
    /// LinkDemand for FullTrust, so partial trust callers will throw an exception.
    /// This exception is thrown when the caller method is being JIT compiled, NOT
    /// when Process.GetCurrentProcess is called, so this wrapper method allows
    /// us to catch the exception.
    /// </para>
    /// </summary>
    /// <returns>Returns the name of the current process.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string GetCurrentProcessName()
    {
        using var currentProcess = Process.GetCurrentProcess();
        return currentProcess.ProcessName;
    }
}
