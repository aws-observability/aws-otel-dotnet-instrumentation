// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation;

public sealed class DynamicInstrumentationManager : IDisposable
{
    private static readonly Lazy<DynamicInstrumentationManager> LazyInstance = new(() => new DynamicInstrumentationManager());
    private readonly object _initLock = new();
    private volatile bool _initialized;
    private DynamicInstrumentationConfig? _config;
    private CancellationTokenSource? _cts;

    private HttpClient? _httpClient;
    private DynamicInstrumentationClient? _client;
    private ConfigurationPoller? _poller;
    // TODO (PR 2): InstrumentationRegistry, ProfilerTranslator fields
    // TODO (PR 3): DISnapshotCollector, DISnapshotOtlpEmitter, StatusReporter fields

    private DynamicInstrumentationManager() { }

    public static DynamicInstrumentationManager Instance => LazyInstance.Value;

    public bool IsInitialized => _initialized;

    public DynamicInstrumentationConfig? Config => _config;

    public void Initialize(DynamicInstrumentationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_initialized)
            return;

        lock (_initLock)
        {
            if (_initialized)
                return;

            _config = config;
            _cts = new CancellationTokenSource();

            try
            {
                // 1. HTTP Client
                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                _client = new DynamicInstrumentationClient(
                    _httpClient, config.ApiUrl, config.ServiceName, config.Environment);

                // 2. Configuration Poller (starts 2 background threads)
                _poller = new ConfigurationPoller(
                    _client,
                    config.ProbePollIntervalSeconds,
                    config.BreakpointPollIntervalSeconds,
                    OnConfigurationsChanged,
                    _cts.Token);
                _poller.Start();

                // TODO: InstrumentationRegistry (PR 2)
                // TODO: ProfilerTranslator + DiIntegrationHelper (PR 2)
                // TODO: DISnapshotCollector (PR 3)
                // TODO: DISnapshotOtlpEmitter (PR 3)
                // TODO: StatusReporter (PR 3)

                _initialized = true;
            }
            catch (Exception)
            {
                Cleanup();
                throw;
            }
        }
    }

    public void OnTracerProviderInitialized(TracerProvider provider)
    {
        // TracerProvider available — could extract Resource attributes if needed.
    }

    /// <summary>
    /// Called by ConfigurationPoller when configs change.
    /// PR 2 will wire registry + profiler translator here.
    /// </summary>
    internal void OnConfigurationsChanged(List<InstrumentationConfiguration> configs)
    {
        // TODO (PR 2): Register configs, apply via ProfilerTranslator, remove stale
    }

    public void Shutdown()
    {
        if (!_initialized)
            return;

        lock (_initLock)
        {
            if (!_initialized)
                return;

            _cts?.Cancel();
            _initialized = false;
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _poller?.Dispose();
        _httpClient?.Dispose();
        _cts?.Dispose();
        _poller = null;
        _client = null;
        _httpClient = null;
        // TODO (PR 2): nullify registry, profilerTranslator
        // TODO (PR 3): dispose/nullify snapshotCollector, otlpEmitter, statusReporter
    }

    public void Dispose()
    {
        Shutdown();
    }
}
