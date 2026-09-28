// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
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
/// Mirrors the Python distro's
/// <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/serviceevents_instrumentation.py"><c>ServiceEventsInstrumentation</c></see>.
/// One instance per process, created by the AWS distro's plugin during its
/// <c>Initializing()</c> hook (ServiceEvents is hosted by that plugin rather than
/// registered as a separate <c>OTEL_DOTNET_AUTO_PLUGINS</c> entry).
/// </remarks>
public sealed class ServiceEventsInstrumentation : IDisposable
{
    /// <summary>Logger category name used for the general signal pipeline.</summary>
    internal const string GeneralLoggerCategory = "AWS.Distro.OpenTelemetry.ServiceEvents.General";

    /// <summary>
    /// Time the log export pipeline may spend draining during shutdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drain follows the collectors' <see cref="ShutdownBudget.Default" />, and the two together have
    /// to fit inside the runtime's process-exit allowance. Overrunning it is worse than dropping the last
    /// batch: the process is terminated mid-flush, losing it regardless, having delayed the host to do so.
    /// </para>
    /// <para>
    /// Chosen against two measured bounds rather than picked to look tidy. Below, a healthy flush must
    /// finish: an export to a live endpoint costs single-digit milliseconds warm, but the first one pays
    /// connection setup, and on a loaded host that reaches about a second — a 1000 ms budget was observed
    /// abandoning successful loopback exports in CI, which is telemetry lost for no benefit. Above, only
    /// values under 5000 buy anything at all: <c>LoggerProviderSdk.Dispose</c> hardcodes
    /// <c>Processor?.Shutdown(5000)</c>, so that is what teardown costs without a budget of our own, and
    /// anything at or beyond it is decoration.
    /// </para>
    /// <para>
    /// 3000 sits clear of both: roughly three times the observed cost of a healthy flush, and still a
    /// meaningful reduction of the worst case the SDK would otherwise allow. The earlier 1000 was
    /// reasoned from the exit window alone without measuring what a successful export costs, which put it
    /// inside the range where healthy and pathological flushes overlap — the one place the value must not
    /// be.
    /// </para>
    /// <para>
    /// This is passed to <see cref="ServiceEventsOtlpLogExporter.BeginShutdown" /> from
    /// <c>DisposeProviders</c>, not to the batch processor. It is deliberately not the processor's
    /// <c>exporterTimeoutMilliseconds</c>, which OTel .NET 1.16.0 accepts and never reads.
    /// </para>
    /// </remarks>
    internal const int ShutdownExportTimeoutMs = 3000;

    private static readonly object InitLock = new();

    private static ServiceEventsInstrumentation? instance;

    private readonly ServiceEventsConfig config;
    private readonly ILogger logger;
    private readonly ILoggerFactory? hostLoggerFactory;
    private readonly WatcherConfigSyncer watcherSyncer = new();

    private ILoggerFactory? generalLoggerFactory;
    private MeterProvider? meterProvider;

    // Retained solely so DisposeProviders can arm its shutdown deadline before the SDK drains the
    // batch queue. Null on the file-output path, which builds no OTLP exporter.
    private ServiceEventsOtlpLogExporter? logExporter;
    private Meter? meter;
    private ServiceEventsOtlpEmitter? emitter;
    private DeploymentEventEmitter? deploymentEventEmitter;
    private EndpointMetricCollector? endpointCollector;
    private IncidentSnapshotCollector? incidentSnapshotCollector;
    private FunctionCallSampler? functionCallSampler;
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

    /// <summary>Gets the incident snapshot collector (null until initialization completes). Visible for tracer wiring + tests.</summary>
    internal IncidentSnapshotCollector? IncidentCollector => this.incidentSnapshotCollector;

    /// <summary>Gets the FunctionCall sampler (null when function instrumentation is disabled). Visible for tests.</summary>
    internal FunctionCallSampler? FunctionSampler => this.functionCallSampler;

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
    /// Applies the enablement rules (bundling with Application
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

        // DeploymentEvent: emit at process start and schedule the 24h re-emission timer.
        this.deploymentEventEmitter = DeploymentEventEmitter.StartAndEmit(this.emitter!, this.config);

        // EndpointSummary / EndpointErrorMetrics: start the endpoint metric collector. Fed by
        // EndpointActivityProcessor,
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

        // FunctionCall: create the sampler when function instrumentation is enabled
        // AND an allowlist is set (the spec gate — empty allowlist instruments nothing).
        // The FunctionCallProcessor is registered on the TracerProvider in
        // RegisterTracerProcessors; the sampler is shared with the incident collector so
        // the incident path can drive adaptive hot-marking.
        if (this.config.FunctionInstrumentEnabled && this.config.PackagesToInstrument.Count > 0)
        {
            this.functionCallSampler = new FunctionCallSampler(this.config);
        }

        // IncidentSnapshot: start the incident snapshot collector and register it for dynamic (WATCHER)
        // config updates. It's fed by the EndpointActivityProcessor's incident trigger
        // seam (see RegisterTracerProcessors).
        this.incidentSnapshotCollector = new IncidentSnapshotCollector(
            flushIntervalMs: this.config.IncidentSnapshotFlushInterval,
            emitter: this.emitter!,
            config: this.config);
        this.incidentSnapshotCollector.Start();
        this.watcherSyncer.SetIncidentSnapshotSink(this.incidentSnapshotCollector);

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
            // Passing the incident collector as the trigger is what makes call-path capture happen at
            // all: CallPathCapture.Begin is gated on a trigger being present, since the trigger is
            // the only thing that drains the buffer.
            builder.AddProcessor(new EndpointActivityProcessor(
                this.endpointCollector,
                this.config,
                this.incidentSnapshotCollector));
        }

        // FunctionCall. Present only when function instrumentation is enabled with a
        // non-empty allowlist (the sampler is created under that gate in Initialize).
        if (this.functionCallSampler is not null)
        {
            builder.AddProcessor(new FunctionCallProcessor(this.emitter!, this.config, this.functionCallSampler));
        }
    }

    /// <summary>
    /// Record the exception that failed a request, privately, for IncidentSnapshot to report.
    /// </summary>
    /// <remarks>
    /// The plugin-facing entry point for <c>EnrichWithException</c>. The capture mechanism itself
    /// stays internal to this assembly; this exposes it the same way
    /// <see cref="RegisterTracerProcessors" /> exposes processor registration. Deliberately does not
    /// touch the customer's span — see <c>ExceptionCapture</c> for why that matters.
    /// </remarks>
    /// <param name="activity">The request's activity.</param>
    /// <param name="exception">The exception being reported.</param>
    public void CaptureException(Activity activity, Exception exception)
        => ExceptionCapture.Stash(activity, exception);

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

    /// <summary>
    /// Build the logger factory for the general signal pipeline.
    /// </summary>
    /// <param name="config">Resolved configuration.</param>
    /// <param name="resourceAttrs">Resource attributes to stamp on every record.</param>
    /// <param name="logExporter">
    /// Receives the OTLP exporter instance when one is built, or <c>null</c> on the file-output path.
    /// Surfaced because <see cref="DisposeProviders" /> has to arm its shutdown deadline before the
    /// SDK's drain starts; see <see cref="ServiceEventsOtlpLogExporter.BeginShutdown" />.
    /// </param>
    /// <returns>The configured factory.</returns>
    private static ILoggerFactory BuildLoggerFactory(
        ServiceEventsConfig config,
        Dictionary<string, object> resourceAttrs,
        out ServiceEventsOtlpLogExporter? logExporter)
    {
        // Assigned from inside the configure callback below, which LoggerFactory.Create runs
        // synchronously, so it is populated before this method returns.
        ServiceEventsOtlpLogExporter? captured = null;

        var factory = LoggerFactory.Create(builder =>
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
                //
                // exporterTimeoutMilliseconds is deliberately NOT set. In OTel .NET 1.16.0 it has no
                // effect: BatchExportProcessor stores it, hands it to the worker and exposes it as a
                // property, and nothing reads it — the export is a bare Exporter.Export(batch) with no
                // timeout and no cancellation token. An earlier version of this code passed
                // ShutdownExportTimeoutMs here and claimed it bounded the shutdown drain; measured
                // against an endpoint that accepts and never answers, teardown took the same ~5s at
                // every value including 1. What actually governs the drain is a hardcoded constant in
                // LoggerProviderSdk.Dispose, which calls Processor?.Shutdown(5000).
                //
                // The drain is bounded instead by arming the exporter's deadline from our own teardown,
                // before the SDK's drain begins — see DisposeProviders and
                // ServiceEventsOtlpLogExporter.BeginShutdown. That is why the exporter instance is
                // surfaced through the out parameter rather than constructed and forgotten here.
                //
                // maxQueueSize and scheduledDelayMilliseconds are deliberately left at their defaults
                // (2048 records, 5s). The queue is generous for this signal's volume, which the
                // incident rate limiter already bounds, and it is the SDK's queue: it accounts for its
                // own drops on its own diagnostic channel. The 5s delay adds latency between a
                // collector's flush and the network write, which is acceptable for telemetry and not
                // worth changing without evidence.
                captured = new ServiceEventsOtlpLogExporter(endpoint, config.LogGroup, config.LogStream);
                options.AddProcessor(new BatchLogRecordExportProcessor(captured));
            });
        });

        logExporter = captured;
        return factory;
    }

    private static MeterProvider BuildMeterProvider(ServiceEventsConfig config, Dictionary<string, object> resourceAttrs)
    {
        var builder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(resourceAttrs))
            .AddMeter(ServiceEventsOtlpEmitter.InstrumentationScopeName)

            // The FunctionCall duration metric must be a base-2 exponential-bucket histogram on the wire;
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
        //
        // What the steps below would ask for unclamped already exceeds this: 500 ms of timer drain
        // for each collector, 250 ms for the endpoint collector's in-flight writes, and 250 ms for
        // the deployment emitter. That overshoot is intentional — Clamp hands each wait the lesser of
        // its request and what remains, so the total stays inside the window while any single step
        // may still use most of it when the others finish early.
        //
        // The provider disposals at the end are NOT bounded by this budget, and cannot be: neither
        // ILoggerFactory nor MeterProvider takes a timeout on Dispose. What they use instead is a
        // hardcoded SDK constant — LoggerProviderSdk.Dispose calls Processor?.Shutdown(5000) and
        // MeterProviderSdk.Dispose calls Reader?.Shutdown(5000) — which no configuration influences.
        // Two earlier versions of this comment were wrong about this: the first claimed the disposals
        // received whatever remained of this budget, the second claimed the SDK derived their timeout
        // from the batch processor's configured export timeout. Neither is true.
        //
        // So the log drain is bounded the only way available: by arming the exporter's own deadline
        // immediately before the logger factory is disposed, which is the last moment we control before
        // the SDK's drain starts. See ServiceEventsOtlpLogExporter.BeginShutdown for why the exporter's
        // OnShutdown hook cannot do this (the SDK calls it after the drain, not before).
        //
        // The metric drain is knowingly left unbounded. meterProvider?.Dispose() reaches
        // Reader.Shutdown(5000) with the stock OTLP exporter at its own 10s default, so its worst case
        // is the 5s the SDK allows. Arming it as tightly as the log path would also clip the ordinary
        // 60s-interval exports, since the same exporter serves both, and that trade is not obviously
        // right — the exposure is a slower exit, not lost data the log path would have saved.
        //
        // Every call below must therefore use the Dispose(budget) overload. The parameterless
        // IDisposable.Dispose() on these types starts a *fresh* budget, which is correct for
        // standalone use but here would give each step its own full window and let the total grow
        // with the number of steps. CollectorBaseTests pins the shared-budget bound.
        var budget = ShutdownBudget.FromNow(ShutdownBudget.Default);

        // Dispose the collectors first — their final Collect() flushes through the emitter,
        // so the emitter/providers must still be alive at this point. The incident collector goes
        // before the endpoint collector because the endpoint window carries exemplars that reference
        // snapshots, so the snapshots should already be on their way out.
        try
        {
            this.incidentSnapshotCollector?.Dispose(budget);
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.ComponentFailed(nameof(IncidentSnapshotCollector) + ".Dispose", ex);
        }

        try
        {
            this.endpointCollector?.Dispose(budget);
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.ComponentFailed(nameof(EndpointMetricCollector) + ".Dispose", ex);
        }

        try
        {
            this.deploymentEventEmitter?.Dispose(budget);
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.ComponentFailed(nameof(DeploymentEventEmitter) + ".Dispose", ex);
        }

        // Arm the drain's deadline here, not inside the exporter's shutdown hook. This is the last point
        // we control before the SDK starts draining the batch queue, and that hook runs after the drain
        // rather than before it. The budget is its own slice rather than whatever remains above: the
        // collector budget and this one are sequential parts of the exit window, so handing the drain the
        // collectors' leftovers would usually give it nothing.
        this.logExporter?.BeginShutdown(TimeSpan.FromMilliseconds(ShutdownExportTimeoutMs));

        try
        {
            this.generalLoggerFactory?.Dispose();
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.ComponentFailed("GeneralLoggerFactory.Dispose", ex);
        }

        try
        {
            this.meterProvider?.Dispose();
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.ComponentFailed("MeterProvider.Dispose", ex);
        }

        this.endpointCollector = null;
        this.incidentSnapshotCollector = null;
        this.functionCallSampler = null;
        this.deploymentEventEmitter = null;
        this.generalLoggerFactory = null;
        this.meterProvider = null;
        this.logExporter = null;
        this.meter?.Dispose();
        this.meter = null;
        this.emitter = null;
    }

    /// <summary>
    /// Apply the endpoint policy: required when force-enabled
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

        this.generalLoggerFactory = BuildLoggerFactory(this.config, resourceAttrs, out var builtLogExporter);
        this.logExporter = builtLogExporter;
        this.meterProvider = BuildMeterProvider(this.config, resourceAttrs);
        this.meter = new Meter(ServiceEventsOtlpEmitter.InstrumentationScopeName, ServiceEventsOtlpEmitter.InstrumentationScopeVersion);

        var generalLogger = this.generalLoggerFactory.CreateLogger(GeneralLoggerCategory);

        this.emitter = new ServiceEventsOtlpEmitter(
            generalLogger,
            this.meter,
            deploymentId: this.config.DeploymentId,
            gitCommitSha: this.config.GitCommitSha,
            gitRepoUrl: this.config.GitRepoUrl,
            serviceCodeNamespace: this.config.DotnetServiceCodeNamespace);
    }

    private Dictionary<string, object> BuildResourceAttributes()
    {
        var attrs = new Dictionary<string, object>
        {
            ["service.name"] = this.config.ServiceName,
            ["aws.local.service"] = this.config.ServiceName, // duplicate for backend compatibility

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

        // deployment.environment(.name): omit entirely when unset — no sentinel.
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

        return attrs;
    }
}
