// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Stamps each snapshot LogRecord's NATIVE trace context and timestamp from the values captured on the user's
/// thread, replacing whatever the drain thread's ambient context would have supplied.
/// </summary>
/// <remarks>
/// WHY A PROCESSOR AND NOT THE LOG CALL. Snapshots are emitted from the collector's drain thread, long after
/// the probed call returned, so <see cref="Activity.Current"/> there is unrelated to the captured call — or
/// null. The OTLP exporter reads TraceId/SpanId/Timestamp off the LogRecord's own fields, and
/// <c>ILogger.Log</c> offers no way to supply them: the SDK fills them from the ambient Activity at log time.
/// Carrying the real ids in <c>aws.di.trace_id</c>/<c>aws.di.span_id</c> attributes (which this agent also
/// does) does not help, because the backend correlates on the native fields — which were left empty or, worse,
/// pointed at an unrelated trace. Java, Python, and Node all set the native fields.
///
/// A processor is the one place where the record is mutable after the SDK has populated it and before the
/// exporter serializes it, which is why the fix lives here rather than at the call site.
/// </remarks>
internal sealed class DISnapshotTraceContextProcessor : BaseProcessor<LogRecord>
{
    /// <summary>
    /// Overwrites the record's trace context and timestamp with the captured values.
    /// </summary>
    /// <param name="data">The record about to be exported.</param>
    public override void OnEnd(LogRecord data)
    {
        // Only snapshot records carry a SnapshotLogState. Anything else on this provider is left untouched —
        // and this cast is why the state is passed as the log state rather than as pre-flattened attributes.
        if (data.Attributes is not SnapshotLogState state)
        {
            return;
        }

        var capture = state.Capture;

        // The capture instant, measured on the user's thread when the probe fired. Without this the timestamp
        // is when the drain thread happened to export, which is up to a full drain interval later and makes
        // the snapshot appear out of order against the trace it belongs to.
        if (capture.TimestampMs > 0)
        {
            data.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(capture.TimestampMs).UtcDateTime;
        }

        // Both or neither: a span id without its trace id cannot be correlated, and half-set context is
        // harder to diagnose than none. Ids are only present when the probed call ran inside an active trace.
        if (TryParseTraceId(capture.TraceId, out var traceId) &&
            TryParseSpanId(capture.SpanId, out var spanId))
        {
            data.TraceId = traceId;
            data.SpanId = spanId;

            // TraceFlags is deliberately NOT set. The sampling decision of the originating span is not part of
            // what the capture records, so any value here would be invented — and claiming Recorded on an
            // unsampled trace is the kind of thing that quietly changes what a backend retains. If the backend
            // needs it, the flag has to be captured on the user's thread alongside the ids.
        }
    }

    // ActivityTraceId.CreateFromString THROWS on anything that is not exactly 32 lowercase hex characters, and
    // this runs on the drain thread where an exception would cost the whole batch. The ids come from
    // Activity.TraceId.ToHexString() so they are well-formed in practice; this is about not betting the export
    // path on that.
    private static bool TryParseTraceId(string? value, out ActivityTraceId traceId)
    {
        traceId = default;
        if (value == null || value.Length != 32)
        {
            return false;
        }

        try
        {
            traceId = ActivityTraceId.CreateFromString(value.AsSpan());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseSpanId(string? value, out ActivitySpanId spanId)
    {
        spanId = default;
        if (value == null || value.Length != 16)
        {
            return false;
        }

        try
        {
            spanId = ActivitySpanId.CreateFromString(value.AsSpan());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
