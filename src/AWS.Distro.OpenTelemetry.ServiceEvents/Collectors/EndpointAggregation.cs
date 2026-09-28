// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Mutable per-endpoint aggregation state — the value type stored in the
/// <c>EndpointMetricCollector</c>'s <c>ConcurrentDictionary</c>, one instance
/// per <c>(method, route)</c> operation.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency model (ported from Java's <c>EndpointAggregation</c>, adapted to
/// .NET primitives — Python uses one global lock because the GIL serializes
/// threads, which does not translate):
/// </para>
/// <list type="bullet">
/// <item><description>Counters (<c>count</c>, <c>faults</c>, <c>errors</c>, <c>sumDurationNs</c>) use <see cref="Interlocked" /> — lock-free.</description></item>
/// <item><description>The SEH histogram is the one multi-step structure; it is guarded by a per-aggregation lock so concurrent requests to <i>this</i> endpoint serialize on the histogram only, never across endpoints.</description></item>
/// <item><description>The error breakdown is a nested <see cref="ConcurrentDictionary{TKey, TValue}" /> (failure type → error key → bucket).</description></item>
/// <item><description>Incident exemplars are a lock-protected list, capped per trigger type.</description></item>
/// </list>
/// </remarks>
internal sealed class EndpointAggregation
{
    private const int MaxExemplarsPerTrigger = 10;

    private readonly object histogramLock = new();
    private readonly SehHistogram histogram = new(maxBuckets: 100);

    // failure type (e.g. "500") -> error key (e.g. "TypeError:fn") -> bucket
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ErrorBucket>> errorBreakdown = new();

    private readonly object exemplarsLock = new();
    private readonly List<IncidentExemplar> exemplars = new();

    private long count;
    private long faults;
    private long errors;
    private long sumDurationNs;
    private string? operation;

    /// <summary>Initializes a new instance of the <see cref="EndpointAggregation"/> class for an operation.</summary>
    public EndpointAggregation(string route, string method)
    {
        this.Route = route;
        this.Method = method;
    }

    /// <summary>Gets route template, e.g. <c>"/users/{id}"</c>.</summary>
    public string Route { get; }

    /// <summary>Gets the HTTP method, e.g. <c>"GET"</c>.</summary>
    public string Method { get; }

    /// <summary>Gets or sets operation key. Defaults to <c>"method route"</c> when not explicitly set.</summary>
    public string Operation
    {
        get => Volatile.Read(ref this.operation) ?? $"{this.Method} {this.Route}";
        set => Volatile.Write(ref this.operation, value);
    }

    /// <summary>Gets total request count.</summary>
    public long Count => Interlocked.Read(ref this.count);

    /// <summary>Gets 5xx fault count.</summary>
    public long Faults => Interlocked.Read(ref this.faults);

    /// <summary>Gets 4xx error count.</summary>
    public long Errors => Interlocked.Read(ref this.errors);

    /// <summary>Gets sum of all request durations in nanoseconds.</summary>
    public long SumDurationNs => Interlocked.Read(ref this.sumDurationNs);

    /// <summary>Record one request's duration (nanoseconds): bumps count, sum, and the histogram.</summary>
    public void RecordDuration(long durationNs)
    {
        Interlocked.Increment(ref this.count);
        Interlocked.Add(ref this.sumDurationNs, durationNs);

        lock (this.histogramLock)
        {
            this.histogram.Record(durationNs);
        }
    }

    /// <summary>Increment the 5xx fault counter.</summary>
    public void IncrementFaults() => Interlocked.Increment(ref this.faults);

    /// <summary>Increment the 4xx error counter.</summary>
    public void IncrementErrors() => Interlocked.Increment(ref this.errors);

    /// <summary>
    /// Record one error occurrence into the nested breakdown: failure type
    /// (status code) → <c>(exceptionType, functionName)</c> → count.
    /// </summary>
    public void RecordError(string failureType, string exceptionType, string functionName)
    {
        var errorKey = $"{exceptionType}:{functionName}";
        var byKey = this.errorBreakdown.GetOrAdd(failureType, _ => new ConcurrentDictionary<string, ErrorBucket>());
        var bucket = byKey.GetOrAdd(errorKey, _ => new ErrorBucket(exceptionType, functionName));
        bucket.Increment();
    }

    /// <summary>
    /// Attach an incident exemplar (pointer to a snapshot). Capped at
    /// <see cref="MaxExemplarsPerTrigger" /> per trigger type to bound payload size.
    /// </summary>
    public void AddIncidentExemplar(string snapshotId, string triggerType, string severity, long timestamp)
    {
        lock (this.exemplarsLock)
        {
            var perTrigger = 0;
            foreach (var ex in this.exemplars)
            {
                if (string.Equals(ex.TriggerType, triggerType, StringComparison.Ordinal))
                {
                    perTrigger++;
                }
            }

            if (perTrigger < MaxExemplarsPerTrigger)
            {
                this.exemplars.Add(new IncidentExemplar(snapshotId, triggerType, severity, timestamp));
            }
        }
    }

    /// <summary>
    /// Build the latency <see cref="DurationMetrics" /> from the histogram, in
    /// microseconds (the spec's <c>duration</c> unit). Read at flush time.
    /// </summary>
    public DurationMetrics BuildDurationMetrics()
    {
        lock (this.histogramLock)
        {
            if (this.histogram.IsEmpty)
            {
                return DurationMetrics.Empty;
            }

            var (values, counts) = this.histogram.GetValuesAndCounts();
            var (min, max, _, _) = this.histogram.GetStatistics();

            // Histogram stores nanoseconds; the spec's duration unit is microseconds.
            var valuesUs = new double[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                valuesUs[i] = values[i] / 1000.0;
            }

            var countsInt = new long[counts.Count];
            for (var i = 0; i < counts.Count; i++)
            {
                countsInt[i] = (long)counts[i];
            }

            return new DurationMetrics(
                Values: valuesUs,
                Counts: countsInt,
                Max: max / 1000.0,
                Min: min / 1000.0,
                Count: this.Count,
                Sum: this.SumDurationNs / 1000.0);
        }
    }

    /// <summary>Flatten the nested error breakdown into the wire-shaped list. Read at flush time.</summary>
    public IReadOnlyList<ErrorBreakdownEntry> BuildErrorBreakdown()
    {
        var result = new List<ErrorBreakdownEntry>();
        foreach (var (failureType, byKey) in this.errorBreakdown)
        {
            foreach (var bucket in byKey.Values)
            {
                var c = bucket.Count;
                if (c > 0)
                {
                    result.Add(new ErrorBreakdownEntry(
                        FailureType: failureType,
                        Count: c,
                        Exceptions: new[] { new ErrorDetail(bucket.ExceptionType, bucket.FunctionName) }));
                }
            }
        }

        return result;
    }

    /// <summary>Snapshot the incident exemplars accumulated this window. Read at flush time.</summary>
    public IReadOnlyList<IncidentExemplar> GetExemplars()
    {
        lock (this.exemplarsLock)
        {
            return this.exemplars.ToArray();
        }
    }

    /// <summary>One error bucket — an exception type/function pair with a count.</summary>
    private sealed class ErrorBucket
    {
        private long count;

        public ErrorBucket(string exceptionType, string functionName)
        {
            this.ExceptionType = exceptionType;
            this.FunctionName = functionName;
        }

        public string ExceptionType { get; }

        public string FunctionName { get; }

        public long Count => Interlocked.Read(ref this.count);

        public void Increment() => Interlocked.Increment(ref this.count);
    }
}
