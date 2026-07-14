// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>Thread-safe capture queue plus AsyncLocal entry/exit pairing that DiIntegration callbacks enqueue to and DISnapshotCollector drains.</summary>
// AsyncLocal is correct for sync methods and the synchronous preamble of async methods (before the first await), which is where the OnMethodBegin/OnMethodEnd callbacks run.
internal static class DIDataStore
{
    private static readonly ConcurrentQueue<PendingCapture> Queue = new();
    private static readonly AsyncLocal<Dictionary<string, PendingEntryData>> PendingEntries = new();

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

    /// <summary>Stores entry data (keyed by instrumentation key) when OnMethodBegin fires.</summary>
    public static void RecordEntry(string instrumentationKey, PendingEntryData entry)
    {
        PendingEntries.Value ??= new Dictionary<string, PendingEntryData>();
        PendingEntries.Value[instrumentationKey] = entry;
    }

    /// <summary>Retrieves and removes entry data when OnMethodEnd fires, returning null if there is no matching entry.</summary>
    public static PendingEntryData? RetrieveEntry(string instrumentationKey)
    {
        if (PendingEntries.Value == null)
        {
            return null;
        }

        if (PendingEntries.Value.Remove(instrumentationKey, out var entry))
        {
            return entry;
        }

        return null;
    }
}
