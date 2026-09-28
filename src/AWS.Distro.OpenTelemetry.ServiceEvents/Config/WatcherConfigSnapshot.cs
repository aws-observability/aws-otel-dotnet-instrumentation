// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Config;

/// <summary>
/// Snapshot of the ServiceEvents-relevant fields delivered by a WATCHER config
/// update. Only the fields that v1 routes dynamically appear here.
/// </summary>
/// <param name="IncidentSnapshotMaxPerMinute">Max snapshots per minute; the window is fixed at one minute.</param>
/// <param name="IncidentSnapshotMaxSameError">Per-error dedup ceiling.</param>
internal sealed record WatcherConfigSnapshot(
    int IncidentSnapshotMaxPerMinute,
    int IncidentSnapshotMaxSameError);
