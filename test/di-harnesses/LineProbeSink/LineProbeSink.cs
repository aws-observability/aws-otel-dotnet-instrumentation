// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace LineProbeSinkNs
{
    // A PLAIN managed static in a normal application assembly. This is the entire point of Q2:
    // prove the injected line-probe `call` binds to a plain static that is NOT a profiler-owned
    // type (no CallTargetState / CallTargetReturn), unlike the Phase-1 CallTarget begin/end invoker.
    public static class LineProbeSink
    {
        // Observable marker. Incremented once per probe fire.
        public static int FireCount;

        // Records the probeId(s) the injected IL passed us, to prove the ldc.i4 <probeId> arg
        // actually arrived (i.e. the call resolved with the right signature, not by luck).
        public static int LastProbeId = -1;

        // EVERY distinct probeId seen. P5 needs this to tell "N probes in one method all landed"
        // from "only the first landed and the rest were silently dropped" — FireCount alone cannot
        // distinguish those, since one surviving probe in a hot loop produces a huge fire count.
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> SeenProbeIdsMap = new();

        public static System.Collections.Generic.ICollection<int> SeenProbeIds => SeenProbeIdsMap.Keys;

        public static void Probe(int probeId)
        {
            System.Threading.Interlocked.Increment(ref FireCount);
            LastProbeId = probeId;
            SeenProbeIdsMap[probeId] = 0;
        }

        // ---- ASYNC SPIKE (DECISION B) ----
        // The captured HOISTED LOCAL value, boxed, arrives here. The whole point of the async spike:
        // prove the injected `ldarg.0; ldfld <hoistedField>; box; call CaptureLocal` reads the real
        // value of a local that was hoisted onto the async state machine and survived an `await`.
        public static object? LastCapturedValue = null;

        public static void CaptureLocal(int probeId, object value)
        {
            System.Threading.Interlocked.Increment(ref FireCount);
            LastProbeId = probeId;
            LastCapturedValue = value;
        }

        // ---------------------------------------------------------------------------------------
        // BOX-GATE SPIKE surface (DECISION A). Two extra plain statics the GATED emission targets:
        //   ldc.i4 probeId; call ShouldCapture; brfalse SKIP;
        //   ldc.i4 probeId; ldloc <val>; box System.Int32; call Capture; SKIP:
        // ShouldCapture mimics MaxHits: true for the first MaxHits calls, false afterwards (a simple
        // counter). The whole point is that when it returns FALSE the injected branch skips PAST the
        // box + Capture, so no value-type -> heap boxing happens on the discarded path.
        // ---------------------------------------------------------------------------------------

        // How many hits are allowed before ShouldCapture starts returning false (mimics MaxHits).
        public static int MaxHits = 3;

        // Number of times ShouldCapture has been invoked (i.e. how many times the GATE ran).
        public static int ShouldCaptureCalls = 0;

        // Number of times Capture actually fired (i.e. how many times we got PAST the gate + boxed).
        public static int CaptureCount = 0;

        // Last boxed value Capture received; kept as object so we can prove the box arrived and
        // unbox it back to assert the exact value materialized.
        public static object? LastGatedValue = null;

        // Last probeId Capture received (proves the constant ldc.i4 <probeId> flowed as arg0).
        public static int LastCaptureProbeId = -1;

        // Cheap gate. Returns true only for the first MaxHits calls, then false. No allocation.
        public static bool ShouldCapture(int probeId)
        {
            int n = System.Threading.Interlocked.Increment(ref ShouldCaptureCalls);
            return n <= MaxHits;
        }

        // The capture callback. `value` arrives already BOXED (the injected IL did `box System.Int32`),
        // so merely being called means the allocation already happened on the caller's thread. When the
        // gate skips this call, that allocation is avoided entirely.
        public static void Capture(int probeId, object value)
        {
            System.Threading.Interlocked.Increment(ref CaptureCount);
            LastCaptureProbeId = probeId;
            LastGatedValue = value;
        }

        // Reset the box-gate counters between measurement phases.
        public static void ResetGate()
        {
            ShouldCaptureCalls = 0;
            CaptureCount = 0;
            LastGatedValue = null;
            LastCaptureProbeId = -1;
        }
    }
}
