// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>Thread-safe capture queue plus AsyncLocal entry/exit pairing that DiIntegration callbacks enqueue to and DISnapshotCollector drains.</summary>
// AsyncLocal is correct for sync methods and the synchronous preamble of async methods (before the first await), which is where the OnMethodBegin/OnMethodEnd callbacks run.
internal static class DIDataStore
{
    private static readonly ConcurrentQueue<PendingCapture> Queue = new();
    private static readonly AsyncLocal<Dictionary<long, PendingEntryData>> PendingEntries = new();
    private static long callIdSeed;

    public static int Count => Queue.Count;

    public static void Enqueue(PendingCapture capture) => Queue.Enqueue(capture);

    public static List<PendingCapture> Drain()
    {
        var list = new List<PendingCapture>();
        while (Queue.TryDequeue(out var item))
        {
            list.Add(item);
        }

        return list;
    }

    public static void Clear()
    {
        while (Queue.TryDequeue(out _))
        {
        }

        PendingEntries.Value?.Clear();
    }

    /// <summary>
    /// Records entry data when OnMethodBegin fires and returns a unique call id to pair with
    /// OnMethodEnd. Keyed per-call (not per instrumentation key) so recursive/reentrant calls on the
    /// same method each keep their own entry — the innermost End no longer overwrites the outer one.
    /// </summary>
    public static long RecordEntry(PendingEntryData entry)
    {
        var callId = Interlocked.Increment(ref callIdSeed);
        PendingEntries.Value ??= new Dictionary<long, PendingEntryData>();
        PendingEntries.Value[callId] = entry;
        return callId;
    }

    /// <summary>Retrieves and removes the entry for a call id when OnMethodEnd fires; null if absent.</summary>
    public static PendingEntryData? RetrieveEntry(long callId)
    {
        if (PendingEntries.Value == null)
        {
            return null;
        }

        return PendingEntries.Value.Remove(callId, out var entry) ? entry : null;
    }
}
