// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// The hot-path recording surface the <see cref="EndpointActivityProcessor" />
/// depends on. Implemented by <see cref="EndpointMetricCollector" />; lets the
/// processor be unit-tested with a fake recorder.
/// </summary>
internal interface IEndpointRecorder
{
    /// <summary>Record one completed HTTP request.</summary>
    void RecordRequest(string route, string method, int statusCode, long durationNs, string? errorType = null, string? functionName = null);

    /// <summary>Attach an incident exemplar to an operation's window (called when the incident-snapshot
    /// collector produces a snapshot).</summary>
    void RecordIncidentExemplar(string operation, string snapshotId, string triggerType, string severity, long timestamp);
}
