// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation;

/// <summary>
/// Singleton orchestrator for Dynamic Instrumentation. Owns the HTTP client, configuration
/// poller, and (in later PRs) the capture engine and output subsystems.
/// </summary>
public sealed class DynamicInstrumentationManager : IDisposable
{
    private static readonly Lazy<DynamicInstrumentationManager> LazyInstance = new(() => new DynamicInstrumentationManager());

    private readonly object initLock = new();
    private volatile bool initialized;
    private DynamicInstrumentationConfig? config;
    private CancellationTokenSource? cts;

    private HttpClient? httpClient;
    private DynamicInstrumentationClient? client;
    private ConfigurationPoller? poller;

    // TODO (PR 2): InstrumentationRegistry, ProfilerTranslator fields
    // TODO (PR 3): DISnapshotCollector, DISnapshotOtlpEmitter, StatusReporter fields
    private DynamicInstrumentationManager()
    {
    }

    /// <summary>Gets the singleton instance.</summary>
    public static DynamicInstrumentationManager Instance => LazyInstance.Value;

    /// <summary>Gets a value indicating whether the manager has been initialized.</summary>
    public bool IsInitialized => this.initialized;

    /// <summary>Gets the active configuration, if initialized.</summary>
    public DynamicInstrumentationConfig? Config => this.config;

    /// <summary>Hook invoked once the TracerProvider is built. Currently a no-op.</summary>
    /// <param name="provider">The initialized tracer provider.</param>
    public static void OnTracerProviderInitialized(TracerProvider provider)
    {
        // TracerProvider available — could extract Resource attributes if needed.
    }

    /// <summary>Initializes the manager and starts configuration polling. Idempotent.</summary>
    /// <param name="config">The resolved configuration.</param>
    public void Initialize(DynamicInstrumentationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (this.initialized)
        {
            return;
        }

        lock (this.initLock)
        {
            if (this.initialized)
            {
                return;
            }

            this.config = config;
            this.cts = new CancellationTokenSource();

            try
            {
                // 1. HTTP Client
                this.httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                this.client = new DynamicInstrumentationClient(
                    this.httpClient, config.ApiUrl, config.ServiceName, config.Environment);

                // 2. Configuration Poller (starts 2 background threads)
                this.poller = new ConfigurationPoller(
                    this.client,
                    config.ProbePollIntervalSeconds,
                    config.BreakpointPollIntervalSeconds,
                    this.OnConfigurationsChanged,
                    this.cts.Token);
                this.poller.Start();

                // TODO: InstrumentationRegistry (PR 2)
                // TODO: ProfilerTranslator + DiIntegrationHelper (PR 2)
                // TODO: DISnapshotCollector (PR 3)
                // TODO: DISnapshotOtlpEmitter (PR 3)
                // TODO: StatusReporter (PR 3)
                this.initialized = true;
            }
            catch (Exception)
            {
                this.Cleanup();
                throw;
            }
        }
    }

    /// <summary>Stops polling and releases resources. Idempotent.</summary>
    public void Shutdown()
    {
        if (!this.initialized)
        {
            return;
        }

        lock (this.initLock)
        {
            if (!this.initialized)
            {
                return;
            }

            this.cts?.Cancel();
            this.initialized = false;
            this.Cleanup();
        }
    }

    /// <summary>Disposes the manager by shutting it down.</summary>
    public void Dispose()
    {
        this.Shutdown();
    }

    /// <summary>
    /// Called by <see cref="ConfigurationPoller"/> when the active configuration set changes.
    /// PR 2 will wire the registry and profiler translator here.
    /// </summary>
    /// <param name="configs">The merged active configuration set.</param>
    internal void OnConfigurationsChanged(List<InstrumentationConfiguration> configs)
    {
        // TODO (PR 2): Register configs, apply via ProfilerTranslator, remove stale
    }

    private void Cleanup()
    {
        this.poller?.Dispose();
        this.httpClient?.Dispose();
        this.cts?.Dispose();
        this.poller = null;
        this.client = null;
        this.httpClient = null;

        // TODO (PR 2): nullify registry, profilerTranslator
        // TODO (PR 3): dispose/nullify snapshotCollector, otlpEmitter, statusReporter
    }
}
