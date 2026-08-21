// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// Pointer to an incident snapshot produced during the window. Linked back
/// from <c>EndpointMetricEvent.IncidentsExemplar</c>.
/// </summary>
/// <param name="SnapshotId">Snapshot identifier, e.g. <c>"snap_abc123"</c>.</param>
/// <param name="TriggerType"><c>"exception"</c>, <c>"error_status"</c>, or <c>"latency"</c>.</param>
/// <param name="Severity"><c>"critical" | "high" | "medium" | "low"</c>.</param>
/// <param name="Timestamp">Epoch milliseconds.</param>
public sealed record IncidentExemplar(
    string SnapshotId,
    string TriggerType,
    string Severity,
    long Timestamp);
