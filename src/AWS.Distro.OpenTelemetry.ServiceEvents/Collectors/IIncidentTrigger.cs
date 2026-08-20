// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Models;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// The trigger surface the endpoint processor depends on. Implemented by the
/// IncidentSnapshot collector; lets the processor be unit-tested with a fake trigger,
/// and lets the endpoint path run without the incident collector present (the
/// dependency is optional — see <c>EndpointActivityProcessor</c>).
/// </summary>
internal interface IIncidentTrigger
{
    /// <summary>Evaluate a completed request for an incident and enqueue a snapshot if triggered.</summary>
    /// <returns>An exemplar to attach to the endpoint summary, or <c>null</c> if no snapshot was produced.</returns>
    IncidentTriggerResult? ProcessPotentialIncident(
        string route,
        string method,
        int statusCode,
        double durationMs,
        string? exceptionType,
        string? exceptionMessage,
        string? stackTrace,
        string? traceId,
        string? spanId,
        long requestTimestampMs,
        IReadOnlyList<CallPathEntry>? spanFrames = null);
}
