// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Exemplar returned when a request produces an incident snapshot. Linked onto the
/// endpoint summary window so the two signals cross-reference each other.
/// </summary>
/// <param name="Operation">Operation the incident was recorded against.</param>
/// <param name="SnapshotId">Identifier of the emitted snapshot.</param>
/// <param name="TriggerType">What triggered it, e.g. <c>"exception"</c> or <c>"latency"</c>.</param>
/// <param name="Severity">Severity assigned to the incident.</param>
/// <param name="Timestamp">Epoch milliseconds when the snapshot was captured.</param>
internal sealed record IncidentTriggerResult(
    string Operation,
    string SnapshotId,
    string TriggerType,
    string Severity,
    long Timestamp);
