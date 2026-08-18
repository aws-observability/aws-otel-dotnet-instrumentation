// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// In-memory snapshot of an EndpointSummary record before it's serialized
/// by <c>ServiceEventsOtlpEmitter</c>. Each instance corresponds to one
/// operation (<c>method + route</c>) flushed at the end of a collection
/// window.
/// </summary>
/// <remarks>
/// Field shape mirrors the Python distro's
/// <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/models/endpoint_telemetry.py"><c>endpoint_telemetry.py</c></see>.
/// The mapping from this model onto the wire lives in <c>ServiceEventsOtlpEmitter</c>.
/// </remarks>
public sealed record EndpointMetricEvent
{
    /// <summary>Gets operation key, e.g. <c>"POST /investigation/trigger_error"</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>Gets the HTTP method portion of <see cref="Operation"/>.</summary>
    public required string Method { get; init; }

    /// <summary>Gets route template portion of <see cref="Operation"/>.</summary>
    public required string Route { get; init; }

    /// <summary>Gets total request count in the window.</summary>
    public long Count { get; init; }

    /// <summary>Gets 5xx fault count in the window.</summary>
    public long Faults { get; init; }

    /// <summary>Gets 4xx error count in the window.</summary>
    public long Errors { get; init; }

    /// <summary>Gets number of incidents triggered for this operation in the window.</summary>
    public long IncidentCount { get; init; }

    /// <summary>Gets latency histogram for the window.</summary>
    public DurationMetrics Duration { get; init; } = DurationMetrics.Empty;

    /// <summary>Gets aggregated error breakdown by failure type.</summary>
    public IReadOnlyList<ErrorBreakdownEntry> ExceptionBreakdown { get; init; } = Array.Empty<ErrorBreakdownEntry>();

    /// <summary>Gets pointers to incident snapshots produced this window.</summary>
    public IReadOnlyList<IncidentExemplar> IncidentsExemplar { get; init; } = Array.Empty<IncidentExemplar>();
}
