// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.ServiceEvents;

/// <summary>
/// Singleton holder for the ServiceEvents lifecycle — orchestrates collectors,
/// the OTLP emitter, and dynamic-config callbacks.
/// </summary>
/// <remarks>
/// Mirrors the Python SDK's <c>ServiceEventsInstrumentation</c> class
/// (<c>aws-opentelemetry-distro/.../telemend/telemend_instrumentation.py</c>).
/// One instance per process, created by the AWS distro's plugin during its
/// <c>Initializing()</c> hook (ServiceEvents is hosted by that plugin rather than
/// registered as a separate <c>OTEL_DOTNET_AUTO_PLUGINS</c> entry).
/// </remarks>
public sealed class ServiceEventsInstrumentation : IDisposable
{
    /// <summary>Logger category name used for the general signal pipeline.</summary>
    internal const string GeneralLoggerCategory = "AWS.Distro.OpenTelemetry.ServiceEvents.General";

    private static readonly object InitLock = new();

    private static ServiceEventsInstrumentation? instance;

    private readonly ServiceEventsConfig config;
    private readonly ILogger logger;
    private readonly ILoggerFactory? hostLoggerFactory;
    private readonly WatcherConfigSyncer watcherSyncer = new();

    private ILoggerFactory? generalLoggerFactory;
    private MeterProvider? meterProvider;
    private Meter? meter;
    private ServiceEventsOtlpEmitter? emitter;
    private DeploymentEventEmitter? deploymentEventEmitter;
    private EndpointMetricCollector? endpointCollector;
    private bool initialized;

    private ServiceEventsInstrumentation(ServiceEventsConfig config, ILoggerFactory hostLoggerFactory)
    {
        this.config = config;
        this.hostLoggerFactory = hostLoggerFactory;
        this.logger = hostLoggerFactory.CreateLogger<ServiceEventsInstrumentation>();
    }

    /// <summary>Gets the current singleton instance, or null if not yet created.</summary>
    public static ServiceEventsInstrumentation? Current => instance;

    /// <summary>Gets the resolved configuration.</summary>
    public ServiceEventsConfig Config => this.config;

    /// <summary>Gets a value indicating whether <see cref="Initialize"/> has run successfully.</summary>
    public bool IsInitialized => this.initialized;

    /// <summary>Gets the watcher syncer for dynamic-config updates.</summary>
    internal WatcherConfigSyncer WatcherSyncer => this.watcherSyncer;

    /// <summary>Gets the OTLP emitter (null until initialization completes).</summary>
    internal ServiceEventsOtlpEmitter? Emitter => this.emitter;

    /// <summary>Gets the endpoint metric collector (null until initialization completes). Visible for tests + tracer wiring.</summary>
    internal EndpointMetricCollector? EndpointCollector => this.endpointCollector;

    /// <summary>
    /// Gets the singleton instance, creating it on first call.
    /// </summary>
    /// <param name="config">
    /// Configuration to build the instance from, normally
    /// <see cref="ServiceEventsConfig.FromEnvironment" /> with the hosting distro's version
    /// supplied via <see cref="ServiceEventsConfig.DistroVersion" />. Ignored if an instance
    /// already exists.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory for the host's logging pipeline. When null, an internal
    /// factory is used.
    /// </param>
    /// <returns>The process-wide singleton.</returns>
    public static ServiceEventsInstrumentation GetOrCreate(ServiceEventsConfig config, ILoggerFactory? loggerFactory = null)
    {
        if (instance is not null)
        {
            return instance;
        }

        lock (InitLock)
        {
            instance ??= new ServiceEventsInstrumentation(config, loggerFactory ?? NullLoggerFactory.Instance);
            return instance;
        }
    }

    /// <summary>
    /// Initialize ServiceEvents for the current process.
    /// </summary>
    /// <remarks>
    /// Applies the spec §3.11 enablement rules (bundling with Application
    /// Signals, Lambda exclusion, explicit override). When force-enabled
    /// without Application Signals, the OTLP endpoints are required.
    /// </remarks>
    public void Initialize()
    {
        if (this.initialized)
        {
            this.logger.LogDebug("ServiceEvents already initialized; skipping");
            return;
        }

        if (!ServiceEventsConfig.DetermineEnabled(this.config))
        {
            this.logger.LogInformation("ServiceEvents disabled (App Signals bundling rule, Lambda exclusion, or explicit OTEL_AWS_SERVICE_EVENTS_ENABLED=false)");
            return;
        }

        if (!this.ValidateEndpoints())
        {
            return;
        }

        try
        {
            this.BuildEmitterPipeline();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "ServiceEvents OTLP emitter construction failed; aborting initialization");
            this.DisposeProviders();
            return;
        }

        // M2: emit DeploymentEvent at process start and schedule the 24h re-emission timer.
        // This is the first signal that flows end-to-end.
        this.deploymentEventEmitter = DeploymentEventEmitter.StartAndEmit(this.emitter!, this.config);

        // M3: start the endpoint metric collector. Fed by EndpointActivityProcessor,
        // which is registered on the customer's TracerProvider via the plugin's
        // AfterConfigureTracerProvider hook (see RegisterTracerProcessors).
        // EndpointSummary is suppressed under Application Signals (it carries equivalent
        // per-endpoint data); the collector still runs so error metrics + latency
        // histograms are produced.
        this.endpointCollector = new EndpointMetricCollector(
            flushIntervalMs: this.config.EndpointFlushInterval,
            emitter: this.emitter!,
            serviceName: this.config.ServiceName,
            environment: this.config.Environment,
            suppressEndpointSummary: this.config.ApplicationSignalsEnabled);
        this.endpointCollector.Start();

        this.initialized = true;
        this.logger.LogInformation(
            "ServiceEvents initialized (service={ServiceName}, environment={Environment}, output_file={OutputFile})",
            this.config.ServiceName,
            this.config.Environment,
            string.IsNullOrEmpty(this.config.OutputFile) ? "<network>" : this.config.OutputFile);
    }

    /// <summary>Tear down OTLP providers. Idempotent.</summary>
    public void Dispose()
    {
        this.DisposeProviders();
        this.initialized = false;
    }

    /// <summary>
    /// Register ServiceEvents' tracer processors on the customer's
    /// <c>TracerProvider</c>. Called from the plugin's
    /// <c>AfterConfigureTracerProvider</c> hook after <see cref="Initialize" />
    /// has created the collectors. No-op if not initialized.
    /// </summary>
    /// <param name="builder">The customer's tracer provider builder.</param>
    public void RegisterTracerProcessors(TracerProviderBuilder builder)
    {
        if (this.endpointCollector is not null)
        {
            builder.AddProcessor(new EndpointActivityProcessor(this.endpointCollector, this.config));
        }
    }

    /// <summary>Reset the singleton. Visible to tests only.</summary>
    internal static void ResetForTests()
    {
        lock (InitLock)
        {
            instance?.Dispose();
            instance = null;
        }
    }

    /// <summary>
    /// Whether resource detectors should run. Mirrors the AWS distro's
    /// <c>RESOURCE_DETECTORS_ENABLED</c> switch (default on); disabled only when explicitly
    /// set to <c>false</c>. ServiceEvents never runs on Lambda (gated off in enablement), so
    /// no Lambda check is needed here.
    /// </summary>
    private static bool DetectorsEnabled() =>
        !string.Equals(
            System.Environment.GetEnvironmentVariable("RESOURCE_DETECTORS_ENABLED"),
            "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Build a resource carrying the AWS infrastructure attributes discovered by querying the
    /// environment the process runs in (<c>host.*</c>, <c>container.id</c>, <c>cloud.*</c>).
    /// These detectors reach out to instance metadata endpoints, which is what
    /// <c>RESOURCE_DETECTORS_ENABLED=false</c> is for.
    /// </summary>
    private static Resource BuildDetectedResource()
    {
        var builder = ResourceBuilder.CreateEmpty()
            .AddAWSEC2Detector()
            .AddAWSEKSDetector()
            .AddAWSECSDetector();
        return builder.Build();
    }

    /// <summary>
    /// Attributes every signal must carry regardless of infrastructure detection:
    /// <c>telemetry.sdk.*</c>, <c>process.pid</c>, and whatever the operator injected through
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c> (<c>k8s.pod.name</c> / <c>k8s.node.name</c> /
    /// <c>k8s.namespace.name</c> / <c>service.instance.id</c> / <c>cloud.platform=aws_eks</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately outside the <c>RESOURCE_DETECTORS_ENABLED</c> gate. That switch exists to skip
    /// the AWS infra detectors' instance-metadata lookups, which are slow or absent off-AWS —
    /// reading an environment variable is neither, and the distro itself applies
    /// <c>AddEnvironmentVariableDetector</c> unconditionally (Plugin.cs), with its own gate
    /// covering only EC2/EKS/ECS. Gating it here also had a worse consequence than dropping
    /// attributes: <c>service.instance.id</c> falls back to a fresh <see cref="Guid" /> below, so a
    /// disabled gate replaced the operator's instance id with a random one and broke correlation
    /// with the rest of the distro's telemetry.
    /// </remarks>
    private static Resource BuildBaseResource()
    {
        return ResourceBuilder.CreateEmpty()
            .AddEnvironmentVariableDetector()
            .AddTelemetrySdk()
            .Build();
    }

    private static ILoggerFactory BuildLoggerFactory(ServiceEventsConfig config, Dictionary<string, object> resourceAttrs)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = true;
                options.ParseStateValues = true;

                options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(resourceAttrs));

                if (!string.IsNullOrEmpty(config.OutputFile))
                {
                    options.AddProcessor(new SimpleLogRecordExportProcessor(new ServiceEventsCloudWatchFileExporter(config.OutputFile)));
                    return;
                }

                var endpoint = string.IsNullOrEmpty(config.LogsEndpoint)
                    ? "http://localhost:4316/v1/logs"
                    : config.LogsEndpoint;

                // Custom OTLP/JSON exporter (not the stock AddOtlpExporter): emits the
                // structured nested body + serviceevents/1.0 scope that the cross-SDK wire
                // format requires. OTel .NET's string-only LogRecord.Body makes the stock
                // exporter emit a stringified body + wrong scope. See ServiceEventsOtlpLogExporter.
                options.AddProcessor(new BatchLogRecordExportProcessor(
                    new ServiceEventsOtlpLogExporter(endpoint, config.LogGroup, config.LogStream)));
            });
        });
    }

    private static MeterProvider BuildMeterProvider(ServiceEventsConfig config, Dictionary<string, object> resourceAttrs)
    {
        var builder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(resourceAttrs))
            .AddMeter(ServiceEventsOtlpEmitter.InstrumentationScopeName)

            // FunctionCall (§4) must be a base-2 exponential-bucket histogram on the wire;
            // the SDK default is explicit-bucket, so map this instrument explicitly.
            .AddView(
                instrumentName: "service.function.duration",
                metricStreamConfiguration: new Base2ExponentialBucketHistogramConfiguration());

        if (!string.IsNullOrEmpty(config.OutputFile))
        {
            builder.AddReader(new PeriodicExportingMetricReader(
                exporter: new ServiceEventsCloudWatchMetricFileExporter(config.OutputFile),
                exportIntervalMilliseconds: 60_000)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Delta,
            });
        }
        else
        {
            var endpoint = string.IsNullOrEmpty(config.MetricsEndpoint)
                ? "http://localhost:4316/v1/metrics"
                : config.MetricsEndpoint;

            builder.AddOtlpExporter((opts, readerOpts) =>
            {
                opts.Endpoint = new Uri(endpoint);
                opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                readerOpts.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
                readerOpts.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 60_000;
            });
        }

        return builder.Build()
            ?? throw new InvalidOperationException("Failed to build meter provider.");
    }

    // Shared by the normal Dispose() path and the abort path in Initialize(), which is why it is not
    // named for either one: the teardown order and the shutdown budget are identical whether we are
    // shutting down cleanly or unwinding a half-built pipeline.
    private void DisposeProviders()
    {
        // One deadline for every wait below. Each step's own cap is generous on its own, but they run
        // sequentially and the process-exit window they share is not — overrunning it gets the
        // process killed mid-flush, losing exactly the telemetry the final drain exists to save.
        // The exporter flushes that follow (logger factory, meter provider) get whatever is left.
        var budget = ShutdownBudget.FromNow(ShutdownBudget.Default);

        // Dispose the collector first — its final Collect() flushes through the emitter,
        // so the emitter/providers must still be alive at this point.
        try
        {
            this.endpointCollector?.Dispose(budget);
        }
        catch
        { /* swallow */
        }

        try
        {
            this.deploymentEventEmitter?.Dispose(budget);
        }
        catch
        { /* swallow */
        }

        try
        {
            this.generalLoggerFactory?.Dispose();
        }
        catch
        { /* swallow */
        }

        try
        {
            this.meterProvider?.Dispose();
        }
        catch
        { /* swallow */
        }

        this.endpointCollector = null;
        this.deploymentEventEmitter = null;
        this.generalLoggerFactory = null;
        this.meterProvider = null;
        this.meter?.Dispose();
        this.meter = null;
        this.emitter = null;
    }

    /// <summary>
    /// Apply the spec §3.11 endpoint policy: required when force-enabled
    /// without App Signals; defaults to the CW Agent <c>localhost:4316</c>
    /// when bundled.
    /// </summary>
    private bool ValidateEndpoints()
    {
        if (!string.IsNullOrEmpty(this.config.OutputFile))
        {
            return true;
        }

        if (this.config.ApplicationSignalsEnabled)
        {
            return true;
        }

        if (string.IsNullOrEmpty(this.config.LogsEndpoint) || string.IsNullOrEmpty(this.config.MetricsEndpoint))
        {
            this.logger.LogError(
                "ServiceEvents is force-enabled (OTEL_AWS_SERVICE_EVENTS_ENABLED=true) without Application Signals, but OTEL_AWS_OTLP_LOGS_ENDPOINT and/or OTEL_AWS_OTLP_METRICS_ENDPOINT are unset. Refusing to initialize — set both endpoints explicitly or enable Application Signals.");
            return false;
        }

        return true;
    }

    /// <summary>Construct the ILoggerFactory + MeterProvider pair and wrap them in an emitter.</summary>
    private void BuildEmitterPipeline()
    {
        var resourceAttrs = this.BuildResourceAttributes();

        this.generalLoggerFactory = BuildLoggerFactory(this.config, resourceAttrs);
        this.meterProvider = BuildMeterProvider(this.config, resourceAttrs);
        this.meter = new Meter(ServiceEventsOtlpEmitter.InstrumentationScopeName, ServiceEventsOtlpEmitter.InstrumentationScopeVersion);

        var generalLogger = this.generalLoggerFactory.CreateLogger(GeneralLoggerCategory);

        this.emitter = new ServiceEventsOtlpEmitter(
            generalLogger,
            this.meter,
            deploymentId: this.config.DeploymentId,
            gitCommitSha: this.config.GitCommitSha,
            gitRepoUrl: this.config.GitRepoUrl);
    }

    private Dictionary<string, object> BuildResourceAttributes()
    {
        var attrs = new Dictionary<string, object>
        {
            ["service.name"] = this.config.ServiceName,
            ["aws.local.service"] = this.config.ServiceName, // duplicate for backend compatibility (spec §2)

            // Distro provenance — mirrors the AWS distro's DistroAttributes (Plugin.cs) so
            // ServiceEvents signals self-identify the same way the other SDKs do (Java/Python
            // emit telemetry.distro.{name,version}). Our pipeline builds its own Resource, so
            // these are not inherited from the main telemetry resource and must be set here.
            ["telemetry.distro.name"] = "aws-otel-dotnet-instrumentation",
        };

        // Supplied by the hosting distro plugin (which owns the version string) rather than read
        // across the assembly boundary. Omitted when unset so we never report a wrong version.
        if (!string.IsNullOrEmpty(this.config.DistroVersion))
        {
            attrs["telemetry.distro.version"] = this.config.DistroVersion + "-aws";
        }

        // deployment.environment(.name): omit entirely when unset — no sentinel (spec v2.5 §2).
        if (!string.IsNullOrEmpty(this.config.Environment))
        {
            attrs["deployment.environment.name"] = this.config.Environment;
        }

        // Deployment + VCS provenance as resource attributes so they surface as
        // @resource.* on the metrics (and logs), matching the other SDKs' wire format.
        if (!string.IsNullOrEmpty(this.config.DeploymentId))
        {
            attrs["aws.service_events.deployment.id"] = this.config.DeploymentId;
        }

        if (!string.IsNullOrEmpty(this.config.GitCommitSha))
        {
            attrs["vcs.ref.head.revision"] = this.config.GitCommitSha;
        }

        if (!string.IsNullOrEmpty(this.config.GitRepoUrl))
        {
            attrs["vcs.repository.url.full"] = this.config.GitRepoUrl;
        }

        // Infra/SDK resource detectors (telemetry.sdk.*, host.*, container.id, k8s.pod.name,
        // cloud.*) so ServiceEvents signals carry the same Resource as the other SDKs. Gated
        // on RESOURCE_DETECTORS_ENABLED (default true), mirroring the AWS distro; tests set it
        // false to avoid IMDS/EKS metadata lookups at build time. Detectors run once here (the
        // dictionary is shared by the logger + meter providers), and our explicit attributes
        // above win over any detector-supplied key.
        // telemetry.sdk.* and process.pid are not infrastructure detection, so they are applied
        // unconditionally — RESOURCE_DETECTORS_ENABLED=false must not leave signals without the
        // SDK identity that every other signal in the pipeline carries.
        foreach (var kvp in BuildBaseResource().Attributes)
        {
            if (!attrs.ContainsKey(kvp.Key))
            {
                attrs[kvp.Key] = kvp.Value;
            }
        }

        if (!attrs.ContainsKey("process.pid"))
        {
            attrs["process.pid"] = System.Environment.ProcessId;
        }

        if (DetectorsEnabled())
        {
            foreach (var kvp in BuildDetectedResource().Attributes)
            {
                if (!attrs.ContainsKey(kvp.Key))
                {
                    attrs[kvp.Key] = kvp.Value;
                }
            }
        }

        // service.instance.id: prefer an operator-injected value (picked up above by the
        // environment-variable detector from OTEL_RESOURCE_ATTRIBUTES, as Java/the AWS distro
        // do). Generate a GUID fallback only when none was supplied, so each replica is still
        // distinguishable. Placed after detectors (injected value wins) and before the config
        // override below (explicit config still wins).
        if (!attrs.ContainsKey("service.instance.id"))
        {
            attrs["service.instance.id"] = Guid.NewGuid().ToString();
        }

        foreach (var (key, value) in this.config.ResourceAttributes.ToDictionary())
        {
            attrs[key] = value;
        }

        return attrs;
    }
}
