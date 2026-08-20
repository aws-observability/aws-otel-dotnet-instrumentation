// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;
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
    // InstrumentationKey -> the LocationHash that was applied for it. Applied once per identity.
    //
    // WHY THE HASH AND NOT JUST THE KEY. An in-place edit of a probe (different captured arguments or locals, a
    // different MaxHits) arrives as the SAME key with a NEW LocationHash — a line-level key is Type.Method:Line,
    // so it is stable across an edit for the same reason a method-level one is. With a key-only set that edit was
    // invisible: RemoveStale did not consider the key stale, so nothing was forgotten or unregistered, and this
    // loop saw the key already present and skipped it. The edited configuration was never applied and never
    // reported any status, while the previous incarnation's probes kept firing under the identity the backend
    // knew about.
    private readonly Dictionary<string, string> appliedInstrumentations = new();

    private volatile bool initialized;
    private DynamicInstrumentationConfig? config;
    private CancellationTokenSource? cts;

    private HttpClient? httpClient;
    private DynamicInstrumentationClient? client;
    private ConfigurationPoller? poller;

    // Capture engine: registry of active instrumentations + profiler translator.
    private InstrumentationRegistry? registry;
    private ProfilerTranslator? profilerTranslator;

    // Line-level: a separate translator (interior IL offset, not a method boundary) and the sink that
    // attributes an opaque probeId back to a config when the woven callback fires.
    private LineProbeTranslator? lineProbeTranslator;
    private LineProbeSink? lineProbeSink;

    // Output subsystems: drain the capture queue to OTLP, and report per-config status to the backend.
    private DISnapshotOtlpEmitter? snapshotEmitter;
    private DISnapshotCollector? snapshotCollector;
    private StatusReporter? statusReporter;

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

                // Line-level. The sink must be configured BEFORE any probe is applied: weaving is what makes
                // the callback reachable, so an applied probe can fire on a customer thread the instant the
                // ReJIT completes. With no sink the callback is a silent no-op and that first hit is lost.
                this.lineProbeTranslator = new LineProbeTranslator();
                this.lineProbeSink = new LineProbeSink(this.registry);
                DiLineIntegrationHelper.Configure(this.lineProbeSink);

                // Output subsystems must be live before the poller starts: the first OnConfigurationsChanged
                // reports READY/ERROR via statusReporter, and woven captures begin enqueuing immediately, so
                // the collector must already be draining. The emitter routes snapshots to the configured OTLP
                // logs endpoint and is enriched from the registry.
                this.snapshotEmitter = DISnapshotOtlpEmitter.Create(config.LogsEndpoint, this.registry);
                this.snapshotCollector = new DISnapshotCollector(this.snapshotEmitter, this.cts.Token);
                this.snapshotCollector.Start();

                this.statusReporter = new StatusReporter(this.client, this.registry, this.cts.Token);
                this.statusReporter.Start();

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
    /// A config is supported when it is line-level, or method-level and not an unsupported target
    /// (constructors/static constructors, which the profiler cannot weave here).
    /// </summary>
    // Line-level configs have no equivalent of the ctor/static-init refusal: the native rewriter injects at
    // an interior offset inside an already-resolved method body, so whether that method happens to be a
    // constructor is irrelevant to it. Their feasibility is decided by resolution (PDB present, line
    // executable, local in scope) rather than by the target's kind, and that verdict comes from
    // LineProbeTranslator at apply time — not from a shape check here.
    private static bool IsSupported(InstrumentationConfiguration config) =>
        config.IsLineLevel || !ProfilerTranslator.IsUnsupportedTarget(config);

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
                // Unconditional, where this used to be gated on IsMethodLevel. That gate existed to stop
                // line-level configs — unsupported at the time — from spamming ERROR on every poll. Line-level
                // is now supported, so IsSupported returns true for it and it never reaches here; anything that
                // does is either a method-level target refused on shape (ctor/static-init) or a malformed
                // LineNumber, and both deserve to be reported rather than dropped in silence.
                this.statusReporter?.ReportError(config, "UNSUPPORTED_TARGET");
                continue;
            }

            activeKeys.Add(config.InstrumentationKey);
            reg.Register(config);
        }

        // Drop stale configs and forget their applied-state so a re-add re-applies them.
        //
        // NOTE ON UNINSTRUMENTING: the profiler exposes no revert/remove export (no RequestRevert; the
        // native ABI is AddInstrumentations only — see NativeMethods.cs), so we cannot un-weave a method
        // whose IL was already rewritten. Removal is therefore a *logical* uninstrument: dropping the key
        // from the registry makes the still-woven DiIntegration callback a no-op on its next invocation —
        // registry.TryHit / registry.Get return false/null for the missing key, so OnMethodBegin returns
        // CallTargetState.GetDefault() and OnMethodEnd finds no paired entry, enqueuing nothing. The method
        // keeps the (cheap) woven prologue/epilogue that immediately short-circuits; no snapshot is ever
        // produced for a removed config. Forgetting applied-state here also lets a later re-add re-apply it.
        foreach (var removedConfig in reg.RemoveStale(activeKeys))
        {
            this.appliedInstrumentations.Remove(removedConfig.InstrumentationKey);

            // Line-level removal is the same LOGICAL uninstrument, with one extra step: forget the
            // probeId->config registration (which is what makes the still-woven callback resolve nothing)
            // and tell the native side to stop dispatching it. RemoveLineProbe is best-effort — the IL
            // cannot be un-rewritten — so dropping the registration, not the native call, is what
            // guarantees no further captures.
            // Every probe the config owns, not just one: a multi-local config applies one probe per captured
            // local, so removing only the first would leave the rest woven AND registered — still capturing
            // after the operator deleted the configuration.
            if (removedConfig.IsLineLevel &&
                this.lineProbeSink?.Unregister(removedConfig.InstrumentationKey, out var removedProbeIds) == true)
            {
                foreach (var removedProbeId in removedProbeIds)
                {
                    this.lineProbeTranslator?.RemoveLineProbe(removedProbeId);
                }
            }

            // Clear the status-dedup state for this config so a later re-add reports READY again (matches
            // the Java/JS reference SDKs, which forget on removal). Keyed by LocationHash — the config's
            // identity — not by InstrumentationKey, so an in-place config change (new LocationHash on the
            // same method) is treated as a fresh config for status purposes.
            this.statusReporter?.Forget(removedConfig.LocationHash);
        }

        // If any target can't be applied yet, signal the poller not to latch so the next poll retries.
        var retryNeeded = false;

        // Apply each newly-registered config to the profiler exactly once.
        foreach (var registered in reg.GetAll())
        {
            var config = registered.Config;
            var key = config.InstrumentationKey;
            if (this.appliedInstrumentations.TryGetValue(key, out var appliedHash))
            {
                if (string.Equals(appliedHash, config.LocationHash, StringComparison.Ordinal))
                {
                    continue; // Already applied on a previous poll, and unchanged.
                }

                // EDITED IN PLACE: same target, new configuration identity. Retire the previous incarnation
                // before applying this one, or its still-registered probes keep reporting under a
                // LocationHash the operator has already replaced.
                this.RetireAppliedConfiguration(key, appliedHash, config.IsLineLevel);
            }

            this.appliedInstrumentations[key] = config.LocationHash;

            // Line-level takes a different translator and a different outcome taxonomy, so it branches
            // before the method-level switch rather than being folded into it.
            if (config.IsLineLevel)
            {
                if (!this.ApplyLineProbe(config))
                {
                    retryNeeded = true;
                }

                continue;
            }

            // EXPLICIT, because IsLineLevel (LineNumber > 0) and IsMethodLevel (LineNumber == 0) do NOT
            // partition: a malformed negative LineNumber satisfies neither. Falling through to the
            // method-level branch wove a full method probe for a config the operator scoped to a line —
            // capturing entry/exit arguments they never asked for. Refuse instead.
            if (!config.IsMethodLevel)
            {
                this.statusReporter?.ReportError(config, "UNSUPPORTED_TARGET");
                this.appliedInstrumentations.Remove(key);
                continue;
            }

            this.appliedInstrumentations[key] = config.LocationHash;

            IReadOnlyCollection<int> appliedArities = Array.Empty<int>();
            var result = this.profilerTranslator?.ApplyInstrumentation(config, out appliedArities)
                ?? InstrumentationApplyResult.TypeNotLoaded;
            switch (result)
            {
                case InstrumentationApplyResult.Applied:
                    // The target is woven, so this config may now report READY. Marked here rather than
                    // inferred from registration: every supported config is registered, including ones that did
                    // not apply (TypeNotLoaded, permanent failures), and READY for those claimed a probe was
                    // live when nothing had been instrumented.
                    this.statusReporter?.MarkApplied(config);

                    // Index the woven arities so the capture hot path resolves this call by (type, arity),
                    // disambiguating co-located methods that differ in parameter count (#3). A same-arity
                    // collision (two configured methods on one type with the same parameter count) can't be
                    // told apart by args.Length, so captures may be misattributed — report OVERLOADED_METHODS
                    // on EVERY config in the ambiguous bucket (both the incoming one and its already-applied
                    // peer), so the operator sees the full ambiguous set, not just the side that applied last.
                    foreach (var collidingKey in reg.IndexArities(config.TypeName, key, appliedArities))
                    {
                        var collidingConfig = reg.Get(collidingKey)?.Config;
                        if (collidingConfig != null)
                        {
                            this.statusReporter?.ReportError(collidingConfig, "OVERLOADED_METHODS");
                        }
                    }

                    // READY is reported once after the apply loop (ReportReadyForNew scans the whole
                    // registry and self-dedups), not per-config here.
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
                    // Permanent instrumentation failure (MethodNotFound / NoSupportedArity / RuntimeError):
                    // keep the key in appliedInstrumentations so we report it EXACTLY ONCE, not every poll.
                    // This is an instrumentation-level ERROR (the target couldn't be woven) — a capture-level
                    // partial (NotCapturedReason) is emitted inside a snapshot instead, never here.
                    if (result.IsReportableFailure())
                    {
                        this.statusReporter?.ReportError(config, result.MapErrorCause()!);
                    }

                    break;
            }
        }

        // Report READY for any newly-applied configs that haven't been hit yet (self-dedups per key).
        this.statusReporter?.ReportReadyForNew();

        // Latch only when nothing is pending a retry.
        return !retryNeeded;
    }

    /// <summary>
    /// Drops the state belonging to a previously-applied configuration identity, so a re-applied or edited
    /// configuration starts clean.
    /// </summary>
    /// <param name="key">The instrumentation key, unchanged by the edit.</param>
    /// <param name="appliedHash">The LocationHash that was previously applied under this key.</param>
    /// <param name="isLineLevel">Whether the configuration is line-level, which owns probe registrations.</param>
    // Same LOGICAL uninstrument as removal (see the note in the RemoveStale loop): the IL cannot be
    // un-woven, so what makes the old probes inert is dropping their registration. The difference is that
    // RemoveStale never sees this case, because the key is still in the active set.
    private void RetireAppliedConfiguration(string key, string appliedHash, bool isLineLevel)
    {
        this.appliedInstrumentations.Remove(key);

        if (isLineLevel && this.lineProbeSink?.Unregister(key, out var retiredProbeIds) == true)
        {
            foreach (var retiredProbeId in retiredProbeIds)
            {
                this.lineProbeTranslator?.RemoveLineProbe(retiredProbeId);
            }
        }

        // Forget the OLD identity's status state so the edited configuration is judged on its own: it has to
        // earn READY through a fresh apply, and it must not inherit the previous incarnation's ERROR.
        this.statusReporter?.Forget(appliedHash);
    }

    /// <summary>
    /// Resolves and applies one line-level config, reporting status. Returns false when the caller should
    /// retry on a later poll (target assembly not loaded yet).
    /// </summary>
    private bool ApplyLineProbe(InstrumentationConfiguration config)
    {
        var translator = this.lineProbeTranslator;
        var sink = this.lineProbeSink;
        if (translator == null || sink == null)
        {
            // Shutting down mid-poll. Forget applied-state so a restart re-applies rather than latching a
            // config that was never actually woven.
            this.appliedInstrumentations.Remove(config.InstrumentationKey);
            return false;
        }

        var probeId = sink.AllocateProbeId();

        // REGISTRATION HAPPENS INSIDE THE APPLY, BEFORE THE NATIVE CALL, and that order is load-bearing.
        // AddLineProbes triggers a ReJIT, after which the woven callback can fire on a customer thread
        // immediately — before this method returns. Registering from `resolution` after the call returns would
        // be too late for that first hit: it would carry a probeId the sink cannot resolve and be dropped.
        // The translator therefore hands each resolved probe to this callback while nothing is woven yet.
        // Registering an id whose apply then fails is the strictly safer error: nothing weaves, so the entry
        // is unreachable, and it is removed below.
        //
        // The gated flag is false because ApplyLineProbe emits Legacy/LocalCapture (see LineProbeTranslator),
        // neither of which calls ShouldCapture. It is threaded through explicitly so that enabling GatedBox
        // later cannot silently double-charge MaxHits — the gate and the hit callback must never both count.
        // The allocator lets the translator claim one id per captured local (multi-local capture applies N
        // probes at the same offset). Passing it is what enables more than CaptureLocals[0].
        var resolution = translator.ApplyLineProbe(
            config,
            probeId,
            sink.AllocateProbeId,
            appliedProbe => sink.Register(appliedProbe.ProbeId, config, appliedProbe.Location, gated: false));
        if (resolution.IsResolved)
        {
            // Defensive: a resolution built by the single-location Success overload (a translator double in
            // tests) reaches here having invoked no register callback and carrying no Locations list. Register
            // the one probe rather than silently registering none.
            if (resolution.Locations.Count == 0)
            {
                sink.Register(probeId, config, resolution.Location!, gated: false);
            }

            // Resolved and woven, so this config may now report READY (reported once after the apply loop by
            // ReportReadyForNew, matching method-level). A line probe that failed to resolve falls through
            // below and is never marked, so it cannot be reported READY.
            this.statusReporter?.MarkApplied(config);

            return true;
        }

        // Failed: drop the registration so a stale probeId cannot accumulate.
        sink.Unregister(config.InstrumentationKey, out _);

        if (resolution.Status.IsRetryable())
        {
            // Target not loaded yet. Forget applied-state and do not report — an ERROR here would fire on
            // every poll for an app that is merely still warming up.
            this.appliedInstrumentations.Remove(config.InstrumentationKey);
            return false;
        }

        // Permanent. Keep the key in appliedInstrumentations so this reports EXACTLY ONCE rather than on
        // every poll, which is the same discipline the method-level path uses for its permanent failures.
        var cause = resolution.Status.MapErrorCause();
        if (cause != null)
        {
            this.statusReporter?.ReportError(config, cause);
        }

        return true;
    }

    private void Cleanup()
    {
        // Cancel before disposing anything. Shutdown already cancelled, and Cancel is idempotent, but the
        // failed-Initialize path reaches Cleanup with a LIVE token — and the poller's Dispose now waits for
        // its threads to observe cancellation, which would otherwise mean waiting out the full bound while
        // they kept polling.
        this.cts?.Cancel();

        // Stop the poller first (no new configs), then the output subsystems. The collector thread and the
        // reporter timer observe the already-cancelled token (Shutdown cancels cts before Cleanup); disposing
        // the collector/reporter releases their handles and the emitter disposes its LoggerFactory, flushing
        // any buffered snapshots on the way out.
        this.poller?.Dispose();
        this.snapshotCollector?.Dispose();
        this.statusReporter?.Dispose();
        this.snapshotEmitter?.Dispose();
        this.httpClient?.Dispose();
        this.cts?.Dispose();

        this.poller = null;
        this.snapshotCollector = null;
        this.statusReporter = null;
        this.snapshotEmitter = null;
        this.client = null;
        this.httpClient = null;

        // Reset the capture engine; clear appliedInstrumentations with the registry so they don't diverge on restart (C3).
        // configChangeLock guards against interleaving with an in-flight callback (order: initLock -> configChangeLock).
        this.registry = null;
        this.profilerTranslator = null;

        // Dispose, not just null: the translator owns a PdbReader holding an open PE + .pdb FileStream per
        // resolved assembly. Nulling the field alone leaked them on every Initialize cycle.
        this.lineProbeTranslator?.Dispose();
        this.lineProbeTranslator = null;
        this.lineProbeSink = null;
        lock (this.configChangeLock)
        {
            this.appliedInstrumentations.Clear();
        }

        DiIntegrationHelper.Configure(null);

        // Detach the line sink last. The woven callbacks stay in customer IL for the process lifetime, so
        // this is what makes them cheap no-ops again rather than reaching a sink over a disposed registry.
        DiLineIntegrationHelper.Configure(null);
    }
}
