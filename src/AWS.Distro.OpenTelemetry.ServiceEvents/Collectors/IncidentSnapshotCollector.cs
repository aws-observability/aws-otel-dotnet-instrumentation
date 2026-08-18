// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Captures an <see cref="IncidentSnapshot" /> when a request errors (5xx / unhandled
/// exception) or breaches its latency threshold, then emits the pending snapshots on each
/// flush. Ports the algorithm from the Python distro's
/// <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/collectors/incident_snapshot_collector.py"><c>incident_snapshot_collector.py</c></see>,
/// with the Java distro's concurrency model (see <see cref="IncidentRateLimiter" />).
/// </summary>
/// <remarks>
/// <para>
/// Trigger path (<see cref="ProcessPotentialIncident" />) runs on the request thread via
/// the incident trigger processor. To bound volume it applies, in order:
/// <list type="number">
/// <item><description><b>Batch dedup</b> — one snapshot per error hash per flush cycle.</description></item>
/// <item><description><b>Per-error dedup</b> — at most <c>maxSameError</c> per error hash per minute.</description></item>
/// <item><description><b>Rate limit</b> — at most <c>maxPerMinute</c> globally per minute.</description></item>
/// </list>
/// Dedup runs before the rate limit so deduplicated requests don't consume rate slots
/// (matching the Python distro's deliberate ordering).
/// </para>
/// <para>
/// <c>is_partial</c> is computed as <c>any(call_path[].duration_ns == 0)</c>;
/// an empty call path is not partial. v1 emits no call-path frames, so snapshots report
/// <c>is_partial: false</c>.
/// </para>
/// </remarks>
internal sealed class IncidentSnapshotCollector : CollectorBase, IIncidentSnapshotConfigSink, IIncidentTrigger
{
    private readonly ServiceEventsOtlpEmitter emitter;
    private readonly ServiceEventsConfig config;
    private readonly IncidentRateLimiter rateLimiter;

    private readonly object batchLock = new();
    private readonly ConcurrentQueue<IncidentSnapshot> pending = new();

    private HashSet<string> currentBatchHashes = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentSnapshotCollector"/> class.
    /// </summary>
    /// <param name="flushIntervalMs">Flush cadence in milliseconds.</param>
    /// <param name="emitter">OTLP emitter used to emit each snapshot.</param>
    /// <param name="config">ServiceEvents config (latency thresholds, rate-limit settings).</param>
    public IncidentSnapshotCollector(
        int flushIntervalMs,
        ServiceEventsOtlpEmitter emitter,
        ServiceEventsConfig config)
        : base(flushIntervalMs, "IncidentSnapshotCollector")
    {
        this.emitter = emitter;
        this.config = config;
        this.rateLimiter = new IncidentRateLimiter(
            config.IncidentSnapshotMaxPerMinute,
            config.IncidentSnapshotMaxSameError);
    }

    /// <summary>
    /// Evaluate a completed request for an incident trigger and, if it passes dedup +
    /// rate limiting, enqueue a snapshot. Returns an exemplar (for attaching to the
    /// endpoint summary) when a snapshot was created, otherwise <c>null</c>.
    /// </summary>
    /// <param name="route">Route template, e.g. <c>"/users/{id}"</c>.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="statusCode">HTTP response status code.</param>
    /// <param name="durationMs">Request duration in milliseconds.</param>
    /// <param name="exceptionType">Exception type name, or null if none captured.</param>
    /// <param name="exceptionMessage">Exception message, or null.</param>
    /// <param name="stackTrace">Formatted stack trace, or null.</param>
    /// <param name="traceId">32-hex trace id (no <c>0x</c>), or null.</param>
    /// <param name="spanId">16-hex span id (no <c>0x</c>), or null.</param>
    /// <param name="requestTimestampMs">Epoch ms when the request started.</param>
    /// <param name="spanFrames">
    /// Per-request call-path frames captured from instrumented child spans, used for latency
    /// incidents where there is no stack trace to derive a path from. Empty when the request was not
    /// tracked or no incident trigger was wired.
    /// </param>
    /// <returns>An exemplar when a snapshot was created, otherwise <c>null</c>.</returns>
    public IncidentTriggerResult? ProcessPotentialIncident(
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
        IReadOnlyList<CallPathEntry>? spanFrames = null)
    {
        spanFrames ??= Array.Empty<CallPathEntry>();

        var operation = $"{method} {route}";

        var triggerType = this.DetermineTriggerType(statusCode, durationMs, exceptionType, operation);
        if (triggerType is null)
        {
            return null;
        }

        var errorHash = IncidentRateLimiter.GenerateErrorHash(operation, exceptionType);

        // Batch dedup: one snapshot per error hash per flush cycle. Checked without claiming the hash
        // yet — claiming here would mark an error as "handled this cycle" even when a limiter below
        // then drops it, suppressing every later occurrence in the same cycle on behalf of a snapshot
        // that was never emitted.
        lock (this.batchLock)
        {
            if (this.currentBatchHashes.Contains(errorHash))
            {
                return null;
            }
        }

        // Per-error dedup, then global rate limit (dedup first so deduped requests
        // don't burn rate-limit slots).
        if (!this.rateLimiter.CheckDeduplication(errorHash))
        {
            return null;
        }

        if (!this.rateLimiter.CheckRateLimit())
        {
            return null;
        }

        // Every gate passed, so this request is producing a snapshot: claim the hash for this cycle.
        // Re-checked under the lock because the Contains above released it — two requests with the
        // same hash can both reach here, and only one may proceed.
        lock (this.batchLock)
        {
            if (!this.currentBatchHashes.Add(errorHash))
            {
                return null;
            }
        }

        var severity = DetermineSeverity(statusCode, triggerType);
        var snapshotId = "snap_" + Guid.NewGuid().ToString();

        var exceptionInfo = BuildExceptionInfo(exceptionType, exceptionMessage, stackTrace, spanFrames);

        var snapshot = new IncidentSnapshot
        {
            SnapshotId = snapshotId,
            Timestamp = requestTimestampMs,
            TriggerType = triggerType,
            Operation = operation,
            Method = method,
            Route = route,
            StatusCode = statusCode,
            DurationMs = durationMs,

            // is_partial = any(call_path[].duration_ns == 0); an empty call path is NOT partial
            // Exception incidents use stack-derived frames (no timing → true);
            // latency incidents use timed span frames (false unless truncated).
            IsPartial = exceptionInfo.Any(e => e.CallPath.Any(c => c.DurationNs == 0)),
            TraceId = traceId,
            SpanId = spanId,
            ExceptionInfo = exceptionInfo,
            RequestContext = new RequestContext
            {
                Type = "http",
                Timestamp = requestTimestampMs,
                StatusCode = statusCode,
            },
        };

        this.pending.Enqueue(snapshot);

        return new IncidentTriggerResult(operation, snapshotId, triggerType, severity, requestTimestampMs);
    }

    /// <inheritdoc />
    public void UpdateIncidentConfig(int maxPerMinute, int maxSameError)
        => this.rateLimiter.UpdateConfig(maxPerMinute, maxSameError);

    /// <summary>
    /// Parse a .NET stack trace into ordered <c>call_path</c> frames (innermost/throw frame first).
    /// Each <c>at Namespace.Class.Method(args) in file:line N</c> line yields a frame whose
    /// <c>function_name</c> is <c>Namespace.Class.Method</c> and whose caller is the next frame up.
    /// Consecutive identical frames are collapsed (see below). Frames carry no timing
    /// (<c>duration_ns == 0</c>); the innermost frame is marked <c>error</c>.
    /// </summary>
    /// <remarks><c>internal</c> (not <c>private</c>) so the frame-dedup behaviour is unit-testable.</remarks>
    internal static IReadOnlyList<CallPathEntry> ParseStackTrace(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return Array.Empty<CallPathEntry>();
        }

        var names = new List<string>();
        foreach (var rawLine in stackTrace!.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            // "at Namespace.Class.Method(args) in file:line N" -> "Namespace.Class.Method"
            var afterAt = line.Substring(3);
            var paren = afterAt.IndexOf('(');
            var name = (paren > 0 ? afterAt.Substring(0, paren) : afterAt).Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // Collapse consecutive identical frames. .NET re-lists the same method for inner
            // exceptions (the `--->` / `--- End of inner exception ---` block) and ASP.NET prints
            // repeated `…Logged|` wrapper frames; without this the call_path would contain a frame
            // whose caller is itself, which is malformed in the adjacency-list model the wire format uses.
            if (names.Count > 0 && string.Equals(names[names.Count - 1], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (names.Count >= CallPathCapture.MaxFrames)
            {
                names.Add(CallPathCapture.TruncatedSentinel);
                break;
            }

            names.Add(name);
        }

        var frames = new CallPathEntry[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            frames[i] = new CallPathEntry(
                FunctionName: names[i],
                CallerFunctionName: i + 1 < names.Count ? names[i + 1] : null,
                DurationNs: 0,
                Error: i == 0,
                IsAsync: false);
        }

        return frames;
    }

    /// <summary>Test seam: force a flush cycle (drain pending + reset batch dedup).</summary>
    internal void Flush() => this.Collect();

    /// <inheritdoc />
    protected override void Collect()
    {
        // Start a fresh batch-dedup window for the next interval.
        lock (this.batchLock)
        {
            this.currentBatchHashes = new HashSet<string>(StringComparer.Ordinal);
        }

        // Drain and emit everything captured this window.
        while (this.pending.TryDequeue(out var snapshot))
        {
            this.emitter.EmitIncidentSnapshot(snapshot);
        }
    }

    private static string DetermineSeverity(int statusCode, string triggerType)
    {
        if (statusCode >= 500 && statusCode <= 503)
        {
            return "critical";
        }

        if (statusCode >= 504 || string.Equals(triggerType, "exception", StringComparison.Ordinal))
        {
            return "high";
        }

        return "medium";
    }

    private static IReadOnlyList<ExceptionInfo> BuildExceptionInfo(
        string? exceptionType,
        string? exceptionMessage,
        string? stackTrace,
        IReadOnlyList<CallPathEntry> spanFrames)
    {
        // Exception incident (C1): derive the call_path from the captured stack trace so the real
        // throwing method (e.g. AdoptionController.CalculateAdoptionFee) surfaces as call_path[0].
        // Stack frames carry no per-frame timing, so duration_ns == 0 (→ is_partial: true).
        if (!string.IsNullOrEmpty(exceptionType))
        {
            return new[]
            {
                new ExceptionInfo(
                    ExceptionType: exceptionType!,
                    ExceptionMessage: exceptionMessage ?? string.Empty,
                    StackTrace: stackTrace ?? string.Empty,
                    CallPath: ParseStackTrace(stackTrace)),
            };
        }

        // Latency incident (Option A): no exception/stack — use the per-request span frames.
        // Matches Java's latency shape: one exception_info entry with empty exception fields and a
        // call_path.
        //
        // Emitted even when no frames were captured, rather than returning an empty list. The entry
        // frame appended by EndpointActivityProcessor normally guarantees at least one, but that is a
        // guarantee held in another file, and consumers — including our own contract suite — treat
        // exception_info as always present on an IncidentSnapshot. An entry with an empty call_path
        // is a truthful "incident with no path captured"; omitting the field entirely would instead
        // look like a malformed record.
        return new[]
        {
            new ExceptionInfo(
                ExceptionType: string.Empty,
                ExceptionMessage: string.Empty,
                StackTrace: string.Empty,
                CallPath: spanFrames),
        };
    }

    /// <summary>
    /// Decide the trigger type: <c>"exception"</c> for a 5xx or captured
    /// exception, <c>"latency"</c> when duration exceeds the per-operation threshold,
    /// or <c>null</c> when there is no trigger.
    /// </summary>
    private string? DetermineTriggerType(int statusCode, double durationMs, string? exceptionType, string operation)
    {
        if (statusCode >= 500 || !string.IsNullOrEmpty(exceptionType))
        {
            return "exception";
        }

        var thresholdMs = this.config.GetLatencyThresholdMs(operation);
        return durationMs > thresholdMs ? "latency" : null;
    }
}
