// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Config;

/// <summary>
/// Bridge between the dynamic-instrumentation WATCHER channel and ServiceEvents's
/// runtime configuration.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Python SDK's <c>watcher_config_syncer.py</c>. When the
/// debugger client receives a config update from the WATCHER pipeline
/// (<c>APMPulseDynamicInstrumentation</c>), it invokes
/// <see cref="OnWatcherUpdate" />, which in turn applies the changes to
/// the registered collectors.
/// </para>
/// <para>
/// In v1, the dynamic-config integration is wired only for incident-snapshot
/// settings (per Phase 1 §5.1). Endpoint patterns and adaptive-sampling
/// thresholds are read from env vars at startup and not refreshed via
/// WATCHER.
/// </para>
/// <para>
/// The class is deliberately small and dependency-free at this stage —
/// concrete collector references are added in M4 (IncidentSnapshot) and M5
/// (FunctionCall) when those collectors land.
/// </para>
/// </remarks>
internal sealed class WatcherConfigSyncer
{
    private readonly object stateLock = new();
    private IIncidentSnapshotConfigSink? incidentSink;

    /// <summary>
    /// Register the incident-snapshot collector so config updates can be
    /// pushed to it. Called by <see cref="ServiceEventsInstrumentation" /> when
    /// the collector is constructed (M4).
    /// </summary>
    /// <param name="sink">The incident-snapshot configuration sink.</param>
    public void SetIncidentSnapshotSink(IIncidentSnapshotConfigSink sink)
    {
        lock (this.stateLock)
        {
            this.incidentSink = sink;
        }
    }

    /// <summary>
    /// Apply a fresh configuration snapshot from the WATCHER pipeline.
    /// </summary>
    /// <param name="snapshot">The new configuration values.</param>
    public void OnWatcherUpdate(WatcherConfigSnapshot snapshot)
    {
        IIncidentSnapshotConfigSink? sinkSnapshot;
        lock (this.stateLock)
        {
            sinkSnapshot = this.incidentSink;
        }

        sinkSnapshot?.UpdateIncidentConfig(
            maxPerPeriod: snapshot.IncidentSnapshotMaxPerPeriod,
            maxSameError: snapshot.IncidentSnapshotMaxSameError);
    }
}
