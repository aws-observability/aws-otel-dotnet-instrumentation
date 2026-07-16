// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
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

    // Serializes OnConfigurationsChanged: the poller calls it from both poll threads. Lock order is always initLock -> configChangeLock.
    private readonly object configChangeLock = new();

    // Configs already handed to the profiler; applied once each. Cleared on Cleanup (C3). Guarded by configChangeLock.
    private readonly HashSet<string> appliedInstrumentations = new();

    private volatile bool initialized;
    private DynamicInstrumentationConfig? config;
    private CancellationTokenSource? cts;

    private HttpClient? httpClient;
    private DynamicInstrumentationClient? client;
    private ConfigurationPoller? poller;

    // Capture engine: registry of active instrumentations + profiler translator.
    private InstrumentationRegistry? registry;
    private ProfilerTranslator? profilerTranslator;

    // Output subsystems (snapshot drain + status reporting) land in PR3.
    private DynamicInstrumentationManager()
    {
    }

    /// <summary>Gets the singleton instance.</summary>
    public static DynamicInstrumentationManager Instance => LazyInstance.Value;

    /// <summary>Gets a value indicating whether the manager has been initialized.</summary>
    public bool IsInitialized => this.initialized;

    /// <summary>Gets the active configuration, if initialized.</summary>
    public DynamicInstrumentationConfig? Config => this.config;

    /// <summary>Gets the registry of active instrumentations, or null before initialization.</summary>
    internal InstrumentationRegistry? Registry => this.registry;

    /// <summary>Hook invoked once the TracerProvider is built. Currently a no-op.</summary>
    /// <param name="provider">The initialized tracer provider.</param>
    public static void OnTracerProviderInitialized(TracerProvider provider)
    {
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
                this.httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                this.client = new DynamicInstrumentationClient(
                    this.httpClient, config.ApiUrl, config.ServiceName, config.Environment);

                // Capture engine must exist before the poller starts, or the first poll hits a null registry.
                this.registry = new InstrumentationRegistry();
                this.profilerTranslator = new ProfilerTranslator();
                DiIntegrationHelper.Configure(this.registry);

                // Output subsystems (drain + status reporting) land in PR3.

                // Poller started last so its OnConfigurationsChanged dependencies are all live.
                this.poller = new ConfigurationPoller(
                    this.client,
                    config.ProbePollIntervalSeconds,
                    config.BreakpointPollIntervalSeconds,
                    this.OnConfigurationsChanged,
                    this.cts.Token);
                this.poller.Start();

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
    /// Called by <see cref="ConfigurationPoller"/> on config change: registers supported targets, removes stale ones, and applies each new config to the profiler once.
    /// </summary>
    /// <param name="configs">The merged active configuration set.</param>
    /// <returns>True when every supported config was applied (safe for the poller to latch this set); false when at least one target could not be applied yet and the next poll should retry.</returns>
    internal bool OnConfigurationsChanged(List<InstrumentationConfiguration> configs)
    {
        // Serialize the callback: the poller drives it from both poll threads.
        lock (this.configChangeLock)
        {
            return this.OnConfigurationsChangedLocked(configs);
        }
    }

    /// <summary>
    /// A config is supported when it is method-level and not an unsupported target
    /// (constructors/static constructors, which the profiler cannot weave here).
    /// </summary>
    private static bool IsSupported(InstrumentationConfiguration config) =>
        config.IsMethodLevel && !ProfilerTranslator.IsUnsupportedTarget(config);

    private bool OnConfigurationsChangedLocked(List<InstrumentationConfiguration> configs)
    {
        var reg = this.registry;
        if (reg == null)
        {
            return true;
        }

        // Register only supported targets; unsupported ones never enter the registry.
        var activeKeys = new HashSet<string>();
        foreach (var config in configs)
        {
            if (!IsSupported(config))
            {
                // Report refused method-level targets (ctor/static-init); skip line-level silently to avoid status spam.
                if (config.IsMethodLevel)
                {
                    // TODO(PR3): this.statusReporter.ReportError(config, "UNSUPPORTED_TARGET");
                }

                continue;
            }

            activeKeys.Add(config.InstrumentationKey);
            reg.Register(config);
        }

        // Drop stale configs and forget their applied-state so a re-add re-applies them.
        foreach (var removedKey in reg.RemoveStale(activeKeys))
        {
            this.appliedInstrumentations.Remove(removedKey);
        }

        // If any target can't be applied yet, signal the poller not to latch so the next poll retries.
        var retryNeeded = false;

        // Apply each newly-registered config to the profiler exactly once.
        foreach (var registered in reg.GetAll())
        {
            var config = registered.Config;
            var key = config.InstrumentationKey;
            if (!this.appliedInstrumentations.Add(key))
            {
                continue; // Already applied on a previous poll.
            }

            IReadOnlyCollection<int> appliedArities = Array.Empty<int>();
            var result = this.profilerTranslator?.ApplyInstrumentation(config, out appliedArities)
                ?? InstrumentationApplyResult.TypeNotLoaded;
            switch (result)
            {
                case InstrumentationApplyResult.Applied:
                    // Index the woven arities so the capture hot path resolves this call by (type, arity),
                    // disambiguating co-located methods that differ in parameter count (#3).
                    if (reg.IndexArities(config.TypeName, key, appliedArities))
                    {
                        // Same-arity collision: another configured method on this type has the same
                        // parameter count, so args.Length can't tell them apart — captures may be
                        // attributed to the wrong probe. Documented #3 residual.
                        // Note: this only fires for the SECOND (colliding) config to apply — the first
                        // applied cleanly before its peer existed, so it's never flagged even though it's
                        // equally ambiguous. TODO(PR3): report ERROR for BOTH keys sharing the bucket
                        // (this.statusReporter.ReportError on each), not just this incoming one, or the
                        // operator sees only half the ambiguous pair.
                    }

                    // TODO(PR3): this.statusReporter.ReportReadyForNew();
                    break;

                case InstrumentationApplyResult.TypeNotLoaded:
                    // Target assembly likely not loaded yet; forget applied-state so a later poll retries. No ERROR (would spam every poll).
                    this.appliedInstrumentations.Remove(key);

                    retryNeeded = true; // Don't latch, or the fingerprint gate never revisits this config.
                    break;

                case InstrumentationApplyResult.Skipped:
                    // Unsupported slipped past IsSupported (shouldn't happen); drop applied-state without reporting.
                    this.appliedInstrumentations.Remove(key);
                    break;

                default:
                    // Permanent failure: keep the key so we report it exactly once, not every poll.
                    // TODO(PR3): this.statusReporter.ReportError(config, MapErrorCause(result));
                    break;
            }
        }

        // Latch only when nothing is pending a retry.
        return !retryNeeded;
    }

    private void Cleanup()
    {
        this.poller?.Dispose();
        this.httpClient?.Dispose();
        this.cts?.Dispose();

        this.poller = null;
        this.client = null;
        this.httpClient = null;

        // Reset the capture engine; clear appliedInstrumentations with the registry so they don't diverge on restart (C3).
        // configChangeLock guards against interleaving with an in-flight callback (order: initLock -> configChangeLock).
        this.registry = null;
        this.profilerTranslator = null;
        lock (this.configChangeLock)
        {
            this.appliedInstrumentations.Clear();
        }

        DiIntegrationHelper.Configure(null);
    }
}
