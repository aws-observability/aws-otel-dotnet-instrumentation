// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Aggregates per-endpoint HTTP metrics and, on each flush, emits the
/// <c>EndpointSummary</c> LogRecord (spec §3) and the <c>EndpointErrorMetrics</c>
/// Sum metric (spec §7). Ports the Python SDK's <c>EndpointMetricCollector</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hot path (<see cref="RecordRequest" />): one request per call, on the
/// customer's request thread — lock-free except for the per-endpoint histogram
/// lock inside <see cref="EndpointAggregation" />.
/// </para>
/// <para>
/// Slow path (<see cref="Collect" />): every flush interval, atomically swap the
/// aggregation map and emit one summary + error-metric set per endpoint.
/// </para>
/// <para>
/// When <see cref="suppressEndpointSummary" /> is set (Application Signals is on),
/// the <c>EndpointSummary</c> LogRecord is skipped — App Signals already carries
/// equivalent per-endpoint data — but the collector keeps running so its latency
/// histogram can feed IncidentSnapshot's threshold triggers (M4). The
/// <c>EndpointErrorMetrics</c> per-exception breakdown still emits (App Signals
/// doesn't carry it).
/// </para>
/// </remarks>
internal sealed class EndpointMetricCollector : CollectorBase, IEndpointRecorder
{
    private readonly ServiceEventsOtlpEmitter emitter;
    private readonly string serviceName;
    private readonly string environment;
    private readonly bool suppressEndpointSummary;

    private ConcurrentDictionary<string, EndpointAggregation> aggregations = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="EndpointMetricCollector"/> class.</summary>
    /// <param name="flushIntervalMs">Flush cadence in milliseconds.</param>
    /// <param name="emitter">OTLP emitter for summaries and error metrics.</param>
    /// <param name="serviceName">Service name dimension for error metrics.</param>
    /// <param name="environment">Deployment environment dimension for error metrics.</param>
    /// <param name="suppressEndpointSummary">When true, skip emitting EndpointSummary (App Signals bundling).</param>
    public EndpointMetricCollector(
        int flushIntervalMs,
        ServiceEventsOtlpEmitter emitter,
        string serviceName,
        string environment,
        bool suppressEndpointSummary)
        : base(flushIntervalMs, "EndpointMetricCollector")
    {
        this.emitter = emitter;
        this.serviceName = serviceName;
        this.environment = environment;
        this.suppressEndpointSummary = suppressEndpointSummary;
    }

    /// <summary>
    /// Record one completed HTTP request. Hot path — called once per request.
    /// </summary>
    /// <param name="route">Route template, e.g. <c>"/users/{id}"</c>.</param>
    /// <param name="method">HTTP method, e.g. <c>"GET"</c>.</param>
    /// <param name="statusCode">HTTP response status code.</param>
    /// <param name="durationNs">Request duration in nanoseconds.</param>
    /// <param name="errorType">Exception type when the request errored, else null.</param>
    /// <param name="functionName">Function where the error occurred, else null.</param>
    public void RecordRequest(
        string route,
        string method,
        int statusCode,
        long durationNs,
        string? errorType = null,
        string? functionName = null)
    {
        var operation = $"{method} {route}";
        var agg = this.aggregations.GetOrAdd(operation, _ => new EndpointAggregation(route, method));

        agg.RecordDuration(durationNs);

        if (statusCode >= 500)
        {
            agg.IncrementFaults();
        }
        else if (statusCode >= 400)
        {
            agg.IncrementErrors();
        }

        // exception_breakdown + the count metric are fault-only: 5xx with a captured exception
        // type (spec §3/§7). A 4xx increments request.errors but produces no breakdown entry, and
        // a 5xx without an exception type produces none either (no synthetic UnknownError).
        if (statusCode >= 500 && !string.IsNullOrEmpty(errorType))
        {
            agg.RecordError(
                failureType: statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                exceptionType: errorType!,
                functionName: string.IsNullOrEmpty(functionName) ? "unknown" : functionName!);
        }
    }

    /// <summary>
    /// Attach an incident exemplar to an operation's window. Called by the
    /// IncidentSnapshot collector (M4) when it produces a snapshot.
    /// </summary>
    public void RecordIncidentExemplar(string operation, string snapshotId, string triggerType, string severity, long timestamp)
    {
        if (this.aggregations.TryGetValue(operation, out var agg))
        {
            agg.AddIncidentExemplar(snapshotId, triggerType, severity, timestamp);
        }
    }

    /// <inheritdoc />
    protected override void Collect()
    {
        // Atomic swap-and-reset: drain everything accumulated this window.
        var swapped = Interlocked.Exchange(ref this.aggregations, new ConcurrentDictionary<string, EndpointAggregation>(StringComparer.Ordinal));

        if (swapped.IsEmpty)
        {
            return;
        }

        foreach (var agg in swapped.Values)
        {
            if (agg.Count == 0)
            {
                continue;
            }

            var exemplars = agg.GetExemplars();
            var summary = new EndpointMetricEvent
            {
                Operation = agg.Operation,
                Method = agg.Method,
                Route = agg.Route,
                Count = agg.Count,
                Faults = agg.Faults,
                Errors = agg.Errors,
                IncidentCount = exemplars.Count,
                Duration = agg.BuildDurationMetrics(),
                ExceptionBreakdown = agg.BuildErrorBreakdown(),
                IncidentsExemplar = exemplars,
            };

            if (!this.suppressEndpointSummary)
            {
                this.emitter.EmitEndpointSummary(summary);
            }

            var errorMetrics = this.BuildErrorMetrics(summary);
            if (errorMetrics.Count > 0)
            {
                this.emitter.EmitEndpointErrorMetrics(errorMetrics);
            }
        }
    }

    /// <summary>
    /// Derive the per-<c>(operation, exception)</c> Sum-metric data points from
    /// an endpoint summary's error breakdown (spec §7). Counts for the same
    /// exception type are summed across failure types.
    /// </summary>
    private List<EndpointErrorMetric> BuildErrorMetrics(EndpointMetricEvent summary)
    {
        var byException = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var entry in summary.ExceptionBreakdown)
        {
            foreach (var ex in entry.Exceptions)
            {
                byException.TryGetValue(ex.ExceptionType, out var current);
                byException[ex.ExceptionType] = current + entry.Count;
            }
        }

        var metrics = new List<EndpointErrorMetric>(byException.Count);
        foreach (var (exceptionType, count) in byException)
        {
            if (count > 0)
            {
                metrics.Add(new EndpointErrorMetric(
                    ServiceName: this.serviceName,
                    Environment: this.environment,
                    Operation: summary.Operation,
                    Exception: exceptionType,
                    Count: count));
            }
        }

        return metrics;
    }
}
