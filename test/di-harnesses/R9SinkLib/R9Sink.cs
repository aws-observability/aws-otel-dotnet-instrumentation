// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace R9SinkNs
{
    // Plain managed statics targeted by the injected line-probe IL. R9 needs PER-PROBE fire counts
    // (LineProbeSink only records "seen" as a set), because the R9 question is about DROPPED and
    // DOUBLE-FIRED hits on a co-located survivor while another probe is removed under load.
    public static class R9Sink
    {
        // Per-probeId fire counts. ConcurrentDictionary because the target is driven from N user
        // threads concurrently — this is the "under load" part.
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> Counts = new();

        public static long TotalFires;

        // Per-probe captured value, so we can assert the survivor still reads the CORRECT local
        // (a survivor that fires but reads garbage would otherwise pass a count-only assertion).
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> LastValue = new();

        // Any capture whose value did not match the expected local value. Must stay 0 — this is the
        // "re-ReJIT excluding one probe corrupted the survivor's ldloc slot" detector.
        public static long BadValues;

        public static void Probe(int probeId)
        {
            Counts.AddOrUpdate(probeId, 1L, static (_, v) => v + 1);
            System.Threading.Interlocked.Increment(ref TotalFires);
        }

        // Local-capture callback (LINE_EMIT_LOCAL_CAPTURE, mode 3). `value` arrives boxed.
        public static void CaptureLocal(int probeId, object value)
        {
            Counts.AddOrUpdate(probeId, 1L, static (_, v) => v + 1);
            System.Threading.Interlocked.Increment(ref TotalFires);
            LastValue[probeId] = value;
        }

        // Gate for LINE_EMIT_GATED_BOX (mode 1). Always true here: R9 is about removal correctness,
        // not rate limiting, and a gate that declines would mask dropped fires. Counted so we can
        // prove the gated probe's gate genuinely ran (mixed-mode proof, gap 2).
        public static long GateCalls;

        public static bool ShouldCapture(int probeId)
        {
            System.Threading.Interlocked.Increment(ref GateCalls);
            return true;
        }

        // Capture target for the box modes (1 and 2).
        public static void Capture(int probeId, object value)
        {
            Counts.AddOrUpdate(probeId, 1L, static (_, v) => v + 1);
            System.Threading.Interlocked.Increment(ref TotalFires);
            LastValue[probeId] = value;
        }

        public static long Get(int probeId) => Counts.TryGetValue(probeId, out var v) ? v : 0;

        public static void Reset()
        {
            Counts.Clear();
            LastValue.Clear();
            System.Threading.Interlocked.Exchange(ref TotalFires, 0);
            System.Threading.Interlocked.Exchange(ref BadValues, 0);
            System.Threading.Interlocked.Exchange(ref GateCalls, 0);
        }
    }
}
