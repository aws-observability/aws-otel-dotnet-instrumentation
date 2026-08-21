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
/// <c>is_partial</c> is computed as <c>any(call_path[].duration_ns == 0)</c>; an empty call path is
/// not partial. The two trigger types therefore report it differently, and both are correct.
/// Exception incidents derive their call path from the captured stack trace, which carries no
/// per-frame timing, so every frame has <c>duration_ns == 0</c> and the snapshot reports
/// <c>is_partial: true</c>. Latency incidents use timed span frames, so they report
/// <c>is_partial: false</c> unless the frame buffer overflowed. See <c>BuildExceptionInfo</c>.
/// </para>
/// </remarks>
internal sealed class IncidentSnapshotCollector : CollectorBase, IIncidentSnapshotConfigSink, IIncidentTrigger
{
    /// <summary>
    /// Cap on the emitted <c>exception_message</c>. Generous against real messages, which are
    /// typically well under a few hundred characters; it exists to stop a message that has had a
    /// serialized payload interpolated into it from dominating the record.
    /// </summary>
    internal const int MaxExceptionMessageChars = 4096;

    /// <summary>
    /// Cap on the emitted <c>stack_trace</c>. Sized to preserve realistic traces intact — a typical
    /// ASP.NET Core trace with middleware runs a few KB, a deep async one tens of KB — while bounding
    /// pathological ones. Truncating the tail is the safe direction: the frames that identify the
    /// failure are at the top, and <c>call_path</c> is derived from the untruncated text.
    /// </summary>
    internal const int MaxStackTraceChars = 32768;

    /// <summary>Appended in place of the removed tail, mirroring <see cref="CallPathCapture.TruncatedSentinel" />.</summary>
    internal const string TruncatedSuffix = "<truncated>";

    /// <summary>
    /// The <c>trigger_type</c> values, named because two methods here have to agree on them:
    /// <c>DetermineTriggerType</c> produces the value and <c>DetermineSeverity</c> branches on it, so
    /// a typo in either would silently mis-grade every exception incident's severity rather than
    /// failing.
    /// </summary>
    private const string TriggerTypeException = "exception";

    /// <summary>Companion to <see cref="TriggerTypeException" />; see its remarks.</summary>
    private const string TriggerTypeLatency = "latency";

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

        var operation = HttpOperationResolver.ResolveOperation(method, route);

        var triggerType = this.DetermineTriggerType(statusCode, durationMs, exceptionType, operation);
        if (triggerType is null)
        {
            return null;
        }

        // Keyed on operation + exception type + throw-site method, matching Java and Python. The
        // origin is what keeps two unrelated failures that happen to share an exception type on one
        // route in separate dedup budgets, instead of collapsing them so that one is silently
        // suppressed while the other reports.
        var errorHash = IncidentRateLimiter.GenerateErrorHash(
            operation, exceptionType, ExtractOriginMethod(stackTrace));

        // Batch dedup: one snapshot per error hash per flush cycle. The check and the claim are a
        // single atomic step, which makes this the serialization point for concurrent same-hash
        // requests: exactly one wins the Add and reaches the limiters, so the losers cannot spend
        // limiter budget on a snapshot they were never going to produce.
        //
        // The claim has to be released if a limiter then rejects this request, or the error would be
        // marked "handled this cycle" on behalf of a snapshot that was never emitted, suppressing
        // every later occurrence in the same cycle. Both rejection paths below therefore unclaim.
        // Python takes the same claim-then-release shape, discarding the hash on both of its rejection
        // paths, so this converges with it rather than diverging. Java has no batch dedup at all.
        // The set this claim landed in is captured so the release below targets the same one. Collect()
        // replaces the set on a flush, so without this a claim made just before a flush would be
        // released out of the *new* set — cancelling a different request's legitimate claim.
        HashSet<string> claimedIn;
        lock (this.batchLock)
        {
            claimedIn = this.currentBatchHashes;
            if (!claimedIn.Add(errorHash))
            {
                return null;
            }
        }

        // Per-error dedup, then global rate limit (dedup first so deduped requests
        // don't burn rate-limit slots).
        if (!this.rateLimiter.CheckDeduplication(errorHash))
        {
            this.UnclaimBatchHash(claimedIn, errorHash);
            return null;
        }

        if (!this.rateLimiter.CheckRateLimit())
        {
            this.UnclaimBatchHash(claimedIn, errorHash);
            return null;
        }

        var severity = DetermineSeverity(statusCode, triggerType);
        var snapshotId = "snap_" + Guid.NewGuid().ToString();

        var exceptionInfo = BuildExceptionInfo(exceptionType, exceptionMessage, stackTrace, spanFrames);

        // When the incident *occurred*: the moment the request finished and the error or the latency
        // breach became true — not when the request started. Derived from request start plus duration
        // rather than a fresh clock read, so it stays exactly consistent with the emitted duration_ms
        // and needs no second time source. For a latency incident, whose threshold defaults to five
        // seconds, request start would sit at least that far behind the real event.
        //
        // This is one of three timestamps on an incident, and they deliberately mean different things:
        // this one is the incident time, the LogRecord's own time_unix_nano is *emit* time (up to a
        // flush interval later), and request_context.timestamp is request *start*, which is what the
        // wire format defines it as and what makes it useful next to duration_ms. Java and Python both
        // stamp incident time at this point in their flow.
        var incidentTimestampMs = requestTimestampMs + (long)durationMs;

        var snapshot = new IncidentSnapshot
        {
            SnapshotId = snapshotId,
            Timestamp = incidentTimestampMs,
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

        // Same value on the exemplar, so an EndpointSummary exemplar and the IncidentSnapshot it
        // cross-references agree on when the incident happened.
        return new IncidentTriggerResult(operation, snapshotId, triggerType, severity, incidentTimestampMs);
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
            if (!TryParseFrameName(line, out var name))
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

    /// <summary>
    /// Extract the throw-site method — the innermost frame's <c>Namespace.Class.Method</c> — from a
    /// stack trace, or an empty string when there is no trace or no parseable frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Feeds the dedup key, so it runs on every candidate incident, before the limiter gates. It stops
    /// at the first frame rather than parsing the whole trace, and shares
    /// <see cref="TryParseFrameName" /> with <see cref="ParseStackTrace" /> so the two cannot drift on
    /// what counts as a frame.
    /// </para>
    /// <para>
    /// The source line number is deliberately not part of this, and falls out of the shared parse:
    /// the name is cut at the opening parenthesis, so <c>in file:line N</c> never reaches the key. It
    /// is the least stable part of a frame — any edit above the throw site shifts it — so including it
    /// would make a recurring error re-fire as a brand-new incident after every deploy. Java documents
    /// the same reasoning for the same exclusion.
    /// </para>
    /// </remarks>
    /// <param name="stackTrace">Formatted stack trace, or null.</param>
    /// <returns>The throw-site method name, or an empty string.</returns>
    internal static string ExtractOriginMethod(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return string.Empty;
        }

        foreach (var rawLine in stackTrace!.Split('\n'))
        {
            if (TryParseFrameName(rawLine.Trim(), out var name))
            {
                return name;
            }
        }

        return string.Empty;
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

    /// <summary>
    /// Decide whether one already-trimmed stack-trace line is a frame, and if so extract its
    /// <c>Namespace.Class.Method</c>.
    /// </summary>
    /// <remarks>
    /// The single definition of "what is a frame" for this file. Both the call-path parser and the
    /// dedup-key origin extractor go through it, so a change to the grammar cannot apply to one and
    /// not the other — the divergence that a second private copy would invite.
    /// </remarks>
    /// <param name="trimmedLine">A stack-trace line, already trimmed.</param>
    /// <param name="name">The extracted method name, or empty when this is not a frame.</param>
    /// <returns><c>true</c> when the line yielded a name.</returns>
    private static bool TryParseFrameName(string trimmedLine, out string name)
    {
        name = string.Empty;
        if (!trimmedLine.StartsWith("at ", StringComparison.Ordinal))
        {
            return false;
        }

        // "at Namespace.Class.Method(args) in file:line N" -> "Namespace.Class.Method"
        var afterAt = trimmedLine.Substring(3);
        var paren = afterAt.IndexOf('(');
        var candidate = (paren > 0 ? afterAt.Substring(0, paren) : afterAt).Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        name = candidate;
        return true;
    }

    /// <summary>
    /// Bound a field to <paramref name="maxChars" />, marking the result when the tail was removed so
    /// a consumer can tell a truncated value from a naturally short one.
    /// </summary>
    /// <param name="value">The text to bound; null becomes empty.</param>
    /// <param name="maxChars">Maximum characters retained before the marker.</param>
    /// <returns>The original text, or its first <paramref name="maxChars" /> characters plus the marker.</returns>
    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value!.Length <= maxChars)
        {
            return value ?? string.Empty;
        }

        return string.Concat(value.AsSpan(0, maxChars), TruncatedSuffix);
    }

    private static string DetermineSeverity(int statusCode, string triggerType)
    {
        if (statusCode >= 500 && statusCode <= 503)
        {
            return "critical";
        }

        if (statusCode >= 504 || string.Equals(triggerType, TriggerTypeException, StringComparison.Ordinal))
        {
            return "high";
        }

        return "medium";
    }

    /// <summary>
    /// Build the <c>exception_info</c> entries for a snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place where an exception message and stack trace enter a record that reaches
    /// the wire, and it is worth naming as such. Both are length-bounded here — see
    /// <see cref="MaxExceptionMessageChars" /> and <see cref="MaxStackTraceChars" /> — but they are
    /// <b>not</b> redacted. A redaction policy is still an open deliverable, and when it lands this
    /// method is where it belongs.
    /// </para>
    /// <para>
    /// The bound exists independently of redaction, because size alone can lose data: these two
    /// fields were the only unbounded contributors to an incident record, so a single deep recursion
    /// or long inner-exception chain could push the record past the backend's per-event ceiling and
    /// drop the <i>whole</i> incident rather than just the oversized field. With them capped the
    /// record is bounded by construction — this method always returns exactly one entry,
    /// <c>call_path</c> is capped at <see cref="CallPathCapture.MaxFrames" />, the attribute set is
    /// fixed, and no request payload is ever captured.
    /// </para>
    /// <para>
    /// Specifically not <c>ExceptionCapture.Stash</c>, which is the tempting place to put it. Stash
    /// only sees ServiceEvents' own private capture; a customer who has independently enabled
    /// <c>RecordException</c> on their instrumentation supplies the type, message and stack through
    /// the span's exception event instead, which never passes through Stash.
    /// <c>EndpointActivityProcessor.ReadExceptionDetails</c> resolves between those two sources and
    /// hands the winner here, so this is the only point both paths share.
    /// </para>
    /// </remarks>
    /// <param name="exceptionType">Resolved exception type, or null for a latency incident.</param>
    /// <param name="exceptionMessage">Resolved exception message, if any.</param>
    /// <param name="stackTrace">Resolved stack trace, if any.</param>
    /// <param name="spanFrames">Per-request span frames, used for latency incidents.</param>
    /// <returns>Exactly one entry; never empty.</returns>
    private static IReadOnlyList<ExceptionInfo> BuildExceptionInfo(
        string? exceptionType,
        string? exceptionMessage,
        string? stackTrace,
        IReadOnlyList<CallPathEntry> spanFrames)
    {
        // Exception incident: derive the call_path from the captured stack trace so the real
        // throwing method (e.g. AdoptionController.CalculateAdoptionFee) surfaces as call_path[0].
        // Stack frames carry no per-frame timing, so duration_ns == 0 (→ is_partial: true).
        if (!string.IsNullOrEmpty(exceptionType))
        {
            return new[]
            {
                new ExceptionInfo(
                    ExceptionType: exceptionType!,
                    ExceptionMessage: Truncate(exceptionMessage, MaxExceptionMessageChars),
                    StackTrace: Truncate(stackTrace, MaxStackTraceChars),
                    CallPath: ParseStackTrace(stackTrace)),
            };
        }

        // Latency incident: no exception or stack — use the per-request span frames.
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
    /// Release a batch-dedup claim after a limiter rejected the request, so the error is not treated
    /// as already handled for the rest of this flush cycle.
    /// </summary>
    /// <remarks>
    /// Takes the set the claim was made in rather than reading <c>currentBatchHashes</c>, because a
    /// flush may have replaced it in between; removing from the retired set is then a harmless no-op
    /// on state that is about to be discarded. Still taken under the lock, since concurrent requests
    /// may be adding to that same set.
    /// </remarks>
    /// <param name="claimedIn">The set the claim was added to.</param>
    /// <param name="errorHash">The hash to release.</param>
    private void UnclaimBatchHash(HashSet<string> claimedIn, string errorHash)
    {
        lock (this.batchLock)
        {
            claimedIn.Remove(errorHash);
        }
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
            return TriggerTypeException;
        }

        var thresholdMs = this.config.GetLatencyThresholdMs(operation);
        return durationMs > thresholdMs ? TriggerTypeLatency : null;
    }
}
