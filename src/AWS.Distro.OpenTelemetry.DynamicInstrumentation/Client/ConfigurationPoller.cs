// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;

public sealed class ConfigurationPoller : IDisposable
{
    private static readonly int[] BackoffDelaysMs = { 10_000, 30_000, 120_000 };
    private const int DegradedIntervalMs = 300_000;
    private const double JitterFactor = 0.25;
    private const int MaxIntervalSeconds = 86_400; // 1 day; guards against *1000 int overflow

    private readonly DynamicInstrumentationClient _client;
    private readonly int _probeIntervalMs;
    private readonly int _breakpointIntervalMs;
    private readonly Action<List<InstrumentationConfiguration>> _onConfigurationsChanged;
    private readonly CancellationToken _ct;

    private Thread? _probeThread;
    private Thread? _breakpointThread;

    private string? _probeSyncedAt;
    private string? _breakpointSyncedAt;
    // TODO: staleness tracking — force full resync when no success for 30m (probes) / 5m (breakpoints)
    private HashSet<string> _lastFingerprint = new();

    private readonly object _configLock = new();
    private List<InstrumentationConfiguration> _cachedProbeConfigs = new();
    private List<InstrumentationConfiguration> _cachedBreakpointConfigs = new();

    public ConfigurationPoller(
        DynamicInstrumentationClient client,
        int probeIntervalSeconds,
        int breakpointIntervalSeconds,
        Action<List<InstrumentationConfiguration>> onConfigurationsChanged,
        CancellationToken ct)
    {
        _client = client;
        _probeIntervalMs = ToIntervalMs(probeIntervalSeconds);
        _breakpointIntervalMs = ToIntervalMs(breakpointIntervalSeconds);
        _onConfigurationsChanged = onConfigurationsChanged;
        _ct = ct;
    }

    public void Start()
    {
        _probeThread = new Thread(() => PollLoop(InstrumentationType.PROBE)) { IsBackground = true, Name = "DI-ProbePoller" };
        _breakpointThread = new Thread(() =>
        {
            WaitWithCancellation(500);
            PollLoop(InstrumentationType.BREAKPOINT);
        }) { IsBackground = true, Name = "DI-BreakpointPoller" };
        _probeThread.Start();
        _breakpointThread.Start();
    }

    private void PollLoop(InstrumentationType type)
    {
        var intervalMs = type == InstrumentationType.PROBE ? _probeIntervalMs : _breakpointIntervalMs;
        int failedAttempts = 0;
        bool degraded = false;

        while (!_ct.IsCancellationRequested)
        {
            try
            {
                var success = FetchAndApply(type).GetAwaiter().GetResult();

                if (success)
                {
                    failedAttempts = 0;
                    degraded = false;
                }
                else
                {
                    failedAttempts++;
                    degraded = ShouldDegrade(failedAttempts);
                }

                var sleepMs = ComputeSleepMs(intervalMs, failedAttempts, degraded);
                WaitWithCancellation(sleepMs);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                failedAttempts++;
                degraded = ShouldDegrade(failedAttempts);
                var sleepMs = ComputeSleepMs(intervalMs, failedAttempts, degraded);
                WaitWithCancellation(sleepMs);
            }
        }
    }

    private async Task<bool> FetchAndApply(InstrumentationType type)
    {
        var syncedAt = type == InstrumentationType.PROBE ? _probeSyncedAt : _breakpointSyncedAt;
        var response = await _client.FetchConfigurationsAsync(
            type, string.IsNullOrEmpty(syncedAt) ? null : syncedAt, _ct);

        if (!response.Success)
            return false;

        if (!response.Changed)
            return true;

        // Update SyncedAt
        if (type == InstrumentationType.PROBE)
            _probeSyncedAt = response.SyncedAt;
        else
            _breakpointSyncedAt = response.SyncedAt;

        // Parse configurations
        var configs = response.Configurations
            .Select(InstrumentationConfiguration.Parse)
            .Where(c => c != null)
            .Cast<InstrumentationConfiguration>()
            .ToList();

        // Update cache and merge — snapshot under lock, invoke callback outside
        List<InstrumentationConfiguration> snapshot;
        lock (_configLock)
        {
            if (type == InstrumentationType.PROBE)
                _cachedProbeConfigs = configs;
            else
                _cachedBreakpointConfigs = configs;

            var allConfigs = _cachedProbeConfigs.Concat(_cachedBreakpointConfigs).ToList();

            // Fingerprint check — skip if unchanged
            var fingerprint = new HashSet<string>(
                allConfigs.Select(c => $"{c.LocationHash}:{c.CreatedAt?.ToUnixTimeMilliseconds()}"));

            if (fingerprint.SetEquals(_lastFingerprint))
                return true;

            _lastFingerprint = fingerprint;
            snapshot = allConfigs;
        }

        _onConfigurationsChanged(snapshot);
        return true;
    }

    private static int ToIntervalMs(int seconds) =>
        Math.Min(Math.Max(seconds, 0), MaxIntervalSeconds) * 1000;

    // Degrade only after all backoff tiers are used (> not >=, else the last tier is skipped).
    internal static bool ShouldDegrade(int failedAttempts) => failedAttempts > BackoffDelaysMs.Length;

    internal static int ComputeSleepMs(int normalIntervalMs, int failedAttempts, bool degraded)
    {
        if (degraded)
            return AddJitter(DegradedIntervalMs);

        if (failedAttempts > 0 && failedAttempts <= BackoffDelaysMs.Length)
            return BackoffDelaysMs[failedAttempts - 1];

        return AddJitter(normalIntervalMs);
    }

    internal static int AddJitter(int baseMs)
    {
        var jitter = (int)(baseMs * JitterFactor * Random.Shared.NextDouble());
        return baseMs + jitter;
    }

    private void WaitWithCancellation(int ms)
    {
        try { Task.Delay(ms, _ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        // Threads exit when CancellationToken is cancelled (managed externally)
    }
}
