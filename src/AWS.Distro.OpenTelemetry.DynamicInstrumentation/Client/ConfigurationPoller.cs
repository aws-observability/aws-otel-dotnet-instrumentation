// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;

/// <summary>Polls the configuration API on background threads, applying backoff and change detection, and invoking a callback when the active set changes.</summary>
/// <param name="client">The API client used to fetch configurations.</param>
/// <param name="probeIntervalSeconds">Base interval between probe polls.</param>
/// <param name="breakpointIntervalSeconds">Base interval between breakpoint polls.</param>
/// <param name="onConfigurationsChanged">Callback invoked with the merged active configuration set.</param>
/// <param name="ct">Cancellation token that stops the poll loops.</param>
public sealed class ConfigurationPoller(
    DynamicInstrumentationClient client,
    int probeIntervalSeconds,
    int breakpointIntervalSeconds,
    Action<List<InstrumentationConfiguration>> onConfigurationsChanged,
    CancellationToken ct) : IDisposable
{
    private const int DegradedIntervalMs = 300_000;
    private const double JitterFactor = 0.25;
    private const int MaxIntervalSeconds = 86_400; // 1 day; guards against *1000 int overflow

    private static readonly int[] BackoffDelaysMs = [10_000, 30_000, 120_000];

    private readonly DynamicInstrumentationClient client = client;
    private readonly int probeIntervalMs = ToIntervalMs(probeIntervalSeconds);
    private readonly int breakpointIntervalMs = ToIntervalMs(breakpointIntervalSeconds);
    private readonly Action<List<InstrumentationConfiguration>> onConfigurationsChanged = onConfigurationsChanged;
    private readonly CancellationToken ct = ct;
    private readonly object configLock = new();

    private Thread? probeThread;
    private Thread? breakpointThread;

    // Opaque sync cursors echoed back to the backend; JsonElement because the live backend sends SyncedAt as an epoch-seconds number, not the spec's ISO string.
    private JsonElement? probeSyncedAt;
    private JsonElement? breakpointSyncedAt;

    // TODO: staleness tracking — force full resync when no success for 30m (probes) / 5m (breakpoints)
    private HashSet<string> lastFingerprint = [];
    private List<InstrumentationConfiguration> cachedProbeConfigs = [];
    private List<InstrumentationConfiguration> cachedBreakpointConfigs = [];

    /// <summary>Starts the probe and breakpoint polling threads.</summary>
    public void Start()
    {
        this.probeThread = new Thread(() => this.PollLoop(InstrumentationType.PROBE)) { IsBackground = true, Name = "DI-ProbePoller" };
        this.breakpointThread = new Thread(() =>
        {
            this.WaitWithCancellation(500);
            this.PollLoop(InstrumentationType.BREAKPOINT);
        })
        { IsBackground = true, Name = "DI-BreakpointPoller" };
        this.probeThread.Start();
        this.breakpointThread.Start();
    }

    /// <summary>Disposes the poller. Threads exit when the cancellation token is cancelled.</summary>
    public void Dispose()
    {
        // Threads exit when the (externally managed) CancellationToken is cancelled.
    }

    // Degrade only after all backoff tiers are used (> not >=, else the last tier is skipped).
    internal static bool ShouldDegrade(int failedAttempts) => failedAttempts > BackoffDelaysMs.Length;

    internal static int ComputeSleepMs(int normalIntervalMs, int failedAttempts, bool degraded)
    {
        if (degraded)
        {
            return AddJitter(DegradedIntervalMs);
        }

        if (failedAttempts > 0 && failedAttempts <= BackoffDelaysMs.Length)
        {
            return BackoffDelaysMs[failedAttempts - 1];
        }

        return AddJitter(normalIntervalMs);
    }

    internal static int AddJitter(int baseMs)
    {
        var jitter = (int)(baseMs * JitterFactor * Random.Shared.NextDouble());
        return baseMs + jitter;
    }

    private static int ToIntervalMs(int seconds) =>
        Math.Min(Math.Max(seconds, 0), MaxIntervalSeconds) * 1000;

    private void PollLoop(InstrumentationType type)
    {
        var intervalMs = type == InstrumentationType.PROBE ? this.probeIntervalMs : this.breakpointIntervalMs;
        var failedAttempts = 0;
        var degraded = false;

        while (!this.ct.IsCancellationRequested)
        {
            try
            {
                var success = this.FetchAndApply(type).GetAwaiter().GetResult();

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
                this.WaitWithCancellation(sleepMs);
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
                this.WaitWithCancellation(sleepMs);
            }
        }
    }

    private async Task<bool> FetchAndApply(InstrumentationType type)
    {
        var syncedAt = type == InstrumentationType.PROBE ? this.probeSyncedAt : this.breakpointSyncedAt;
        var response = await this.client.FetchConfigurationsAsync(type, syncedAt, this.ct);

        if (!response.Success)
        {
            return false;
        }

        if (!response.Changed)
        {
            return true;
        }

        if (type == InstrumentationType.PROBE)
        {
            this.probeSyncedAt = response.SyncedAt;
        }
        else
        {
            this.breakpointSyncedAt = response.SyncedAt;
        }

        var configs = response.Configurations
            .Select(InstrumentationConfiguration.Parse)
            .Where(c => c != null)
            .Cast<InstrumentationConfiguration>()
            .ToList();

        // Update cache and merge — snapshot under lock, invoke callback outside
        List<InstrumentationConfiguration> snapshot;
        HashSet<string> fingerprint;
        lock (this.configLock)
        {
            if (type == InstrumentationType.PROBE)
            {
                this.cachedProbeConfigs = configs;
            }
            else
            {
                this.cachedBreakpointConfigs = configs;
            }

            var allConfigs = this.cachedProbeConfigs.Concat(this.cachedBreakpointConfigs).ToList();

            // Fingerprint check — skip if unchanged.
            fingerprint = new HashSet<string>(
                allConfigs.Select(c => $"{c.LocationHash}:{c.CreatedAt?.ToUnixTimeMilliseconds()}"));

            if (fingerprint.SetEquals(this.lastFingerprint))
            {
                return true;
            }

            snapshot = allConfigs;
        }

        // Persist the fingerprint only after the callback succeeds; if it throws, the stale fingerprint forces the next poll to re-apply instead of silently dropping the change.
        this.onConfigurationsChanged(snapshot);
        this.lastFingerprint = fingerprint;
        return true;
    }

    private void WaitWithCancellation(int ms)
    {
        try
        {
            Task.Delay(ms, this.ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
