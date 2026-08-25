// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// In-memory snapshot of an IncidentSnapshot record before it's serialized.
/// </summary>
/// <remarks>
/// Field shape mirrors the Python distro's
/// <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/models/incident_telemetry.py"><c>incident_telemetry.py</c></see>.
/// The mapping from this model onto the wire lives in <c>ServiceEventsOtlpEmitter</c>.
/// </remarks>
public sealed record IncidentSnapshot
{
    /// <summary>Gets the unique identifier, e.g. <c>"snap_abc123"</c>.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>
    /// Gets the epoch milliseconds when the incident occurred — the moment the request finished and
    /// the error or latency breach became true.
    /// </summary>
    /// <remarks>
    /// Distinct from the two other times on an incident record, on purpose. The emitted LogRecord's
    /// own <c>time_unix_nano</c> is <i>emit</i> time, which can be up to a flush interval later, and
    /// <see cref="Models.RequestContext.Timestamp" /> is request <i>start</i>. Without this field a
    /// consumer has no top-level answer to "when did this incident happen" — the emit time is late by
    /// the flush interval and request start is early by the request duration. The same value is
    /// carried on the EndpointSummary's <see cref="IncidentExemplar" /> so the two agree.
    /// </remarks>
    public required long Timestamp { get; init; }

    // No Severity here on purpose. The wire format defines no severity attribute on
    // IncidentSnapshot, so it must not be emitted; severity instead travels on the EndpointSummary's
    // IncidentExemplar (see IncidentTriggerResult). Carrying it here as well made it a required
    // field that every construction had to supply and nothing ever read.

    /// <summary>Gets the trigger: one of <c>"exception"</c> or <c>"latency"</c>.</summary>
    public required string TriggerType { get; init; }

    /// <summary>Gets the HTTP operation, e.g. <c>"POST /api/users"</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>Gets the HTTP method portion of <see cref="Operation"/>.</summary>
    public required string Method { get; init; }

    /// <summary>Gets the route portion of <see cref="Operation"/>.</summary>
    public required string Route { get; init; }

    /// <summary>Gets the HTTP response status code.</summary>
    public int StatusCode { get; init; }

    /// <summary>Gets the request duration in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>Gets a value indicating whether the snapshot was captured without full timing data.</summary>
    public bool IsPartial { get; init; }

    /// <summary>Gets the trace correlation id: 32-char hex (no <c>0x</c> prefix). Null = not propagated.</summary>
    public string? TraceId { get; init; }

    /// <summary>Gets the span correlation id: 16-char hex (no <c>0x</c> prefix).</summary>
    public string? SpanId { get; init; }

    /// <summary>Gets the captured exception(s) with stack trace and call path.</summary>
    public IReadOnlyList<ExceptionInfo> ExceptionInfo { get; init; } = Array.Empty<ExceptionInfo>();

    /// <summary>Gets the request context — payload fields gated by capture flag.</summary>
    public RequestContext? RequestContext { get; init; }
}
