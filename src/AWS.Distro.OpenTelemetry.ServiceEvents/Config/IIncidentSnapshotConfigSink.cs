// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Config;

/// <summary>
/// Sink interface implemented by the incident-snapshot collector (M4) so
/// the syncer can push updated config without a hard dependency on the
/// collector type.
/// </summary>
internal interface IIncidentSnapshotConfigSink
{
    /// <summary>Apply a fresh incident-snapshot configuration.</summary>
    /// <param name="maxPerPeriod">Max snapshots per rate-limit window.</param>
    /// <param name="maxSameError">Per-error dedup ceiling.</param>
    void UpdateIncidentConfig(int maxPerPeriod, int maxSameError);
}
