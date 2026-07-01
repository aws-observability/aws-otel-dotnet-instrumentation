// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation;

public sealed class DynamicInstrumentationManager : IDisposable
{
    private static readonly Lazy<DynamicInstrumentationManager> LazyInstance = new(() => new DynamicInstrumentationManager());
    private volatile bool _initialized;
    private DynamicInstrumentationConfig? _config;
    private CancellationTokenSource? _cts;

    private HttpClient? _httpClient;
    private DynamicInstrumentationClient? _client;
    private ConfigurationPoller? _poller;
    private InstrumentationRegistry? _registry;
    private ProfilerTranslator? _profilerTranslator;

    private DynamicInstrumentationManager() { }

    public static DynamicInstrumentationManager Instance => LazyInstance.Value;

    public bool IsInitialized => _initialized;

    public DynamicInstrumentationConfig? Config => _config;

    internal InstrumentationRegistry? Registry => _registry;

    public void Initialize(DynamicInstrumentationConfig config)
    {
        if (_initialized)
            return;

        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cts = new CancellationTokenSource();

        try
        {
            // 1. Registry
            _registry = new InstrumentationRegistry();

            // 2. Wire DiIntegrationHelper to registry
            DiIntegrationHelper.Configure(_registry);

            // 3. HTTP Client
            _httpClient = new HttpClient();
            _client = new DynamicInstrumentationClient(
                _httpClient, config.ApiUrl, config.ServiceName, config.Environment);

            // 4. Profiler Translator
            _profilerTranslator = new ProfilerTranslator();

            // 5. Configuration Poller (starts 2 background threads)
            _poller = new ConfigurationPoller(
                _client,
                config.ProbePollIntervalSeconds,
                config.BreakpointPollIntervalSeconds,
                OnConfigurationsChanged,
                _cts.Token);
            _poller.Start();

            // 6. TODO: DISnapshotCollector (drain thread)
            // 7. TODO: DISnapshotOtlpEmitter (LoggerProvider)
            // 8. TODO: StatusReporter (60s timer)

            _initialized = true;
        }
        catch (Exception)
        {
            Cleanup();
            throw;
        }
    }

    public void OnTracerProviderInitialized(TracerProvider provider)
    {
        // TracerProvider available — could extract Resource attributes if needed.
    }

    /// <summary>
    /// Called by ConfigurationPoller when configs change.
    /// Applies new instrumentations and removes stale ones.
    /// </summary>
    internal void OnConfigurationsChanged(List<InstrumentationConfiguration> configs)
    {
        if (_registry == null || _profilerTranslator == null)
            return;

        var activeKeys = new HashSet<string>();

        foreach (var config in configs)
        {
            // Skip unsupported targets
            if (ProfilerTranslator.IsUnsupportedTarget(config))
                continue;

            _registry.Register(config);
            activeKeys.Add(config.InstrumentationKey);

            // Apply instrumentation via native profiler
            _profilerTranslator.ApplyInstrumentation(config);
        }

        // Remove configs no longer in the active set
        _registry.RemoveStale(activeKeys);
    }

    public void Shutdown()
    {
        if (!_initialized)
            return;

        _cts?.Cancel();
        _initialized = false;
        Cleanup();
    }

    private void Cleanup()
    {
        _poller?.Dispose();
        _httpClient?.Dispose();
        _cts?.Dispose();
        _poller = null;
        _client = null;
        _httpClient = null;
        _registry = null;
        _profilerTranslator = null;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
