// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Per-request capture of FunctionCall span frames for the IncidentSnapshot
/// <c>call_path</c> (spec §5), used for <b>latency</b> incidents (Option A).
/// </summary>
/// <remarks>
/// <para>
/// The frame list lives on the request's server <see cref="Activity" /> via
/// <see cref="Activity.SetCustomProperty" /> — so it is naturally scoped to one request,
/// thread-safe across concurrent child spans (a <see cref="ConcurrentQueue{T}" />), and
/// garbage-collected with the Activity (no shared dictionary, no leak). The server
/// processor creates the buffer on span start; instrumented child spans append their
/// frame on end; the server processor drains it on span end.
/// </para>
/// <para>
/// Exception incidents use the stack-trace-derived call path instead (C1); this span
/// buffer backs the latency path where no exception/stack is available.
/// </para>
/// </remarks>
internal static class CallPathCapture
{
    internal const string PropertyKey = "aws.service_events.call_path_buffer";

    /// <summary>Max frames retained per request before the truncation sentinel is appended.</summary>
    internal const int MaxFrames = 100;

    internal const string TruncatedSentinel = "<call_path_truncated>";

    /// <summary>Create the per-request frame buffer on a server span. Call from OnStart.</summary>
    public static void Begin(Activity serverSpan)
        => serverSpan.SetCustomProperty(PropertyKey, new ConcurrentQueue<CallPathEntry>());

    /// <summary>
    /// Append a frame to the buffer on the nearest server-span ancestor of <paramref name="childSpan" />.
    /// No-op when no buffer is present (e.g. the request was not tracked). Caps at
    /// <see cref="MaxFrames" />, appending a single <see cref="TruncatedSentinel" /> frame on overflow.
    /// </summary>
    public static void Append(Activity childSpan, CallPathEntry frame)
    {
        var queue = FindBuffer(childSpan);
        if (queue is null)
        {
            return;
        }

        if (queue.Count < MaxFrames)
        {
            queue.Enqueue(frame);
        }
        else if (queue.Count == MaxFrames)
        {
            // duration_ns == 0 marks this as a partial (unsampled/truncated) frame per spec §5.
            queue.Enqueue(new CallPathEntry(TruncatedSentinel, null, 0, false, false));
        }
    }

    /// <summary>Drain the buffer off a server span (ordered as captured). Empty when none.</summary>
    public static IReadOnlyList<CallPathEntry> Drain(Activity serverSpan)
    {
        if (serverSpan.GetCustomProperty(PropertyKey) is not ConcurrentQueue<CallPathEntry> queue)
        {
            return Array.Empty<CallPathEntry>();
        }

        return queue.ToArray();
    }

    private static ConcurrentQueue<CallPathEntry>? FindBuffer(Activity span)
    {
        for (var a = span; a is not null; a = a.Parent)
        {
            if (a.GetCustomProperty(PropertyKey) is ConcurrentQueue<CallPathEntry> queue)
            {
                return queue;
            }
        }

        return null;
    }
}
