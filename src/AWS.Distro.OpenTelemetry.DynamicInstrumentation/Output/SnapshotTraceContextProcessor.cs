// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Moves the captured trace context and capture instant from snapshot attributes onto the LogRecord's native
/// TraceId/SpanId/Timestamp fields, which are what the backend correlates on.
/// </summary>
internal sealed class SnapshotTraceContextProcessor : BaseProcessor<LogRecord>
{
    internal const string TraceIdKey = "aws.di.trace_id";
    internal const string SpanIdKey = "aws.di.span_id";
    internal const string TimestampKey = "aws.di.timestamp_ms";

    public override void OnEnd(LogRecord data)
    {
        var attributes = data.Attributes;
        if (attributes == null)
        {
            return;
        }

        string? traceId = null;
        string? spanId = null;
        long? timestampMs = null;

        foreach (var attribute in attributes)
        {
            switch (attribute.Key)
            {
                case TraceIdKey: traceId = attribute.Value as string; break;
                case SpanIdKey: spanId = attribute.Value as string; break;
                case TimestampKey: timestampMs = attribute.Value as long?; break;
                default: break;
            }
        }

        // Both or neither — a TraceId without a SpanId is not a usable correlation target.
        if (traceId != null && spanId != null)
        {
            try
            {
                data.TraceId = ActivityTraceId.CreateFromString(traceId.AsSpan());
                data.SpanId = ActivitySpanId.CreateFromString(spanId.AsSpan());

                // A snapshot only exists because a probe fired, so it is sampled-in.
                data.TraceFlags = ActivityTraceFlags.Recorded;
            }
            catch (ArgumentException)
            {
                // Malformed hex must not drop the snapshot.
            }
        }

        if (timestampMs.HasValue)
        {
            // Otherwise Timestamp is emit time, which trails the probe by up to the batch interval.
            data.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs.Value).UtcDateTime;
        }

        // Drop the carrier attributes so each value appears once.
        if (traceId != null || spanId != null || timestampMs.HasValue)
        {
            var remaining = new List<KeyValuePair<string, object?>>(attributes.Count);
            foreach (var attribute in attributes)
            {
                if (attribute.Key is not (TraceIdKey or SpanIdKey or TimestampKey))
                {
                    remaining.Add(attribute);
                }
            }

            data.Attributes = remaining;
        }
    }
}
