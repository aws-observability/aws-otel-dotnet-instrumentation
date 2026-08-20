// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Production <see cref="ILineProbeSink"/>: turns a woven line-probe hit into a snapshot on the
/// <see cref="DIDataStore"/> queue, which <c>DISnapshotCollector</c> drains to OTLP.
/// </summary>
// This is the line-level counterpart of DiIntegrationHelper's capture path, and it exists because the
// injected IL carries only an OPAQUE probeId — not a type, method, line, or local name. Everything else has
// to be recovered from a registration made at apply time, which is what `probes` holds. Without this class
// the whole line-level stack is inert: the callbacks fire and drop the hit on the floor.
internal sealed class LineProbeSink : ILineProbeSink
{
    // Probe ids are handed to the native rewriter and baked into customer IL, so they must be unique for the
    // process lifetime and must NEVER be recycled: the woven `ldc.i4 <probeId>` in an already-rewritten method
    // survives removal (there is no un-weave), so reusing an id would make a stale probe report as a live one.
    // Monotonic allocation makes a stale hit resolve to nothing instead.
    //
    // STATIC for exactly that reason. Shutdown()/Initialize() constructs a NEW sink (and a failed Initialize
    // reaches Cleanup too), so an instance counter restarts at 1 while methods woven in the previous lifetime
    // still carry ids 1..N — and those stale hits would then resolve against the new sink's registrations: a
    // different config, local name, and line. Process-wide is the only scope that matches the IL's lifetime.
    private static int nextProbeId;

    // The hit decision made by the budget-owning probe of the CURRENT line hit, so the sibling probes at the
    // same offset reuse it instead of each charging the budget. Per-thread because the N callbacks run
    // consecutively on whichever customer thread executed the line.
    [ThreadStatic]
    private static (string? Key, bool Allowed) lineHitDecision;

    private readonly InstrumentationRegistry registry;
    private readonly ConcurrentDictionary<int, LineProbeRegistration> probes = new();

    // InstrumentationKey -> every probeId applied for it, so a removed config can unregister all of them.
    // A LIST, not a single id: multi-local capture applies one probe per captured local at the same line, so
    // a config owns N ids. Keeping only the last would leave the earlier probes registered forever — still
    // firing, still enqueuing snapshots, after the operator deleted the config.
    private readonly ConcurrentDictionary<string, List<int>> probeIdsByKey = new();

    public LineProbeSink(InstrumentationRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Gets the number of currently-registered probes.</summary>
    internal int Count => this.probes.Count;

    /// <inheritdoc />
    // Runs on the customer's thread at an arbitrary interior IL offset, so it MUST NOT throw into user code.
    // DiLineIntegrationHelper wraps the call in a catch-all as well, but ILineProbeSink states the no-throw
    // contract as this type's own, and the body below calls three things that can throw (serialization, stack
    // capture, enqueue). Holding the contract here rather than relying on the caller is the difference between
    // a documented invariant and an accidental one.
    public void OnLineProbeHit(int probeId, bool hasValue, object? value)
    {
        try
        {
            this.OnLineProbeHitCore(probeId, hasValue, value);
        }
        catch
        {
            // A capture is never worth a customer-visible exception.
        }
    }

    /// <inheritdoc />
    // FAILS CLOSED. Returning false suppresses the capture; returning true on an unknown probe would run the
    // capture path (and its allocation) for a probe we cannot attribute.
    public bool ShouldCapture(int probeId) =>
        this.probes.TryGetValue(probeId, out var probe) && this.registry.TryHit(probe.InstrumentationKey);

    /// <summary>
    /// Allocates the next probe id. Ids are never reused — see the note on <c>nextProbeId</c>.
    /// </summary>
    /// <returns>A process-unique probe id.</returns>
    internal int AllocateProbeId() => Interlocked.Increment(ref nextProbeId);

    /// <summary>
    /// Records what a probe id means, so a later hit can be attributed to a configuration.
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL.</param>
    /// <param name="config">The configuration this probe was applied for.</param>
    /// <param name="location">The resolved location, carrying the captured local's source name.</param>
    /// <param name="gated">
    /// True when the emitted IL calls <c>ShouldCapture</c> before capturing. This decides WHICH callback
    /// performs the rate-limit check, and getting it wrong double-counts every hit against MaxHits.
    /// </param>
    internal void Register(int probeId, InstrumentationConfiguration config, LineProbeLocation location, bool gated)
    {
        // AddOrUpdate under a lock on the list: two locals of one config register from the same apply loop
        // today, but the registry is reachable from the poller's threads and a plain Add would tear.
        var ids = this.probeIdsByKey.GetOrAdd(config.InstrumentationKey, _ => new List<int>());
        bool chargesHit;
        lock (ids)
        {
            if (!ids.Contains(probeId))
            {
                ids.Add(probeId);
            }

            // The FIRST probe registered for a config owns the hit budget; see OnLineProbeHit. Decided here,
            // under the same lock that orders the list, so two locals registering concurrently cannot both
            // claim ownership.
            chargesHit = ids.Count > 0 && ids[0] == probeId;
        }

        this.probes[probeId] = new LineProbeRegistration(
            config.InstrumentationKey, location.LocalName, gated, chargesHit);
    }

    /// <summary>
    /// Resolves a probe id back to the configuration it was applied for.
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL.</param>
    /// <param name="instrumentationKey">The owning configuration's key, when the probe is registered.</param>
    /// <returns>True when the probe id is registered.</returns>
    // Exists so a weave verdict read back from the native profiler — which knows nothing but the opaque id —
    // can be attributed to a configuration and reported. Returning false is the expected answer for a probe
    // whose config has since been removed, NOT an error: the native log is forgotten on removal, but a verdict
    // can still be in flight in a poll that started before it.
    internal bool TryGetInstrumentationKey(int probeId, out string instrumentationKey)
    {
        if (this.probes.TryGetValue(probeId, out var probe))
        {
            instrumentationKey = probe.InstrumentationKey;
            return true;
        }

        instrumentationKey = string.Empty;
        return false;
    }

    /// <summary>
    /// Forgets every probe applied for a configuration so their still-woven IL becomes a no-op.
    /// </summary>
    /// <param name="instrumentationKey">The configuration key being removed.</param>
    /// <param name="probeIds">The probe ids that were unregistered; empty when none were found.</param>
    /// <returns>True when at least one registration was found and removed.</returns>
    // A LOGICAL uninstrument, exactly as for function-level: the injected `call` cannot be removed from a
    // method whose IL was already rewritten, so dropping the registration is what stops captures. The
    // callback still runs on every execution and now resolves nothing, which is the cheap short-circuit.
    //
    // Returns ALL ids because a multi-local config owns one probe per captured local, and every one of them
    // needs the native RemoveLineProbe call plus its registration dropped.
    internal bool Unregister(string instrumentationKey, out IReadOnlyList<int> probeIds)
    {
        if (!this.probeIdsByKey.TryRemove(instrumentationKey, out var ids))
        {
            probeIds = Array.Empty<int>();
            return false;
        }

        int[] snapshot;
        lock (ids)
        {
            snapshot = ids.ToArray();
        }

        foreach (var id in snapshot)
        {
            this.probes.TryRemove(id, out _);
        }

        probeIds = snapshot;
        return snapshot.Length > 0;
    }

    private void OnLineProbeHitCore(int probeId, bool hasValue, object? value)
    {
        if (!this.probes.TryGetValue(probeId, out var probe))
        {
            // Unknown or already-removed probe: the woven IL outlives the registration, so this is the
            // expected steady state after a removal, not an error.
            return;
        }

        // ONE CHARGE PER LINE HIT, NOT PER PROBE. Multi-local capture applies N probes at ONE IL offset, so a
        // single execution of the line invokes this N times, back to back on the same thread. Charging each
        // one spent the config's budget N times faster and — worse — tore the result: with MaxHits=1 and two
        // locals, the line's only snapshot contained the first local and the second was never captured at
        // all, with nothing marking the omission.
        //
        // The first-registered probe owns the charge; the rest reuse its decision. The decision is
        // [ThreadStatic] because those N callbacks run consecutively on the executing thread with no
        // interleaving, while two customer threads hitting the same line must decide independently.
        //
        // A gated probe is exempt: its emitted IL already consumed the hit via ShouldCapture, so charging here
        // would double-count.
        if (!probe.Gated)
        {
            bool allowed;
            if (probe.ChargesHit)
            {
                allowed = this.registry.TryHit(probe.InstrumentationKey);
                lineHitDecision = (probe.InstrumentationKey, allowed);
            }
            else if (lineHitDecision.Key == probe.InstrumentationKey)
            {
                allowed = lineHitDecision.Allowed;
            }
            else
            {
                // No decision from this line hit — e.g. the owning probe was removed, or this thread has not
                // run the owner yet. Fall back to charging, so removal can never lift the rate limit.
                allowed = this.registry.TryHit(probe.InstrumentationKey);
            }

            if (!allowed)
            {
                return;
            }
        }

        var reg = this.registry.Get(probe.InstrumentationKey);
        if (reg == null)
        {
            return;
        }

        var limits = reg.Config.Capture;

        // hasValue is deliberately separate from a null check on `value`: a captured local that IS null must
        // stay distinguishable from a probe that captured nothing at all.
        Dictionary<string, CapturedValue>? locals = null;
        if (hasValue)
        {
            locals = new Dictionary<string, CapturedValue>(1)
            {
                [probe.LocalName ?? "local"] = ValueSerializer.Serialize(value, limits),
            };
        }

        string? traceId = null, spanId = null;
        var activity = Activity.Current;
        if (activity != null)
        {
            traceId = activity.TraceId.ToHexString();
            spanId = activity.SpanId.ToHexString();
        }

        StackFrameInfo[]? stackTrace = null;
        if (limits.CaptureStackTrace)
        {
            stackTrace = FunctionLevel.DiIntegrationHelper.CaptureStackTrace(limits.MaxStackFrames);
        }

        DIDataStore.Enqueue(new PendingCapture
        {
            Type = CaptureType.LINE,
            InstrumentationKey = probe.InstrumentationKey,

            // IDENTITY FROM THE LIVE CONFIG, NOT THE APPLY-TIME SNAPSHOT. A line-level InstrumentationKey is
            // Type.Method:Line, so editing a probe in place (different captured locals, different MaxHits)
            // produces the SAME key with a NEW LocationHash. Reading the snapshot attributed those snapshots
            // to the config the operator had already deleted. `reg` is the live registry entry, which
            // Register() replaced when the edit arrived — and it is already the source of `limits` above, so
            // taking identity from anywhere else was the inconsistency.
            LocationHash = reg.Config.LocationHash,
            LineNumber = reg.Config.LineNumber,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),

            // No duration: a line probe is a point observation, not an interval. Emitting 0 rather than a
            // synthetic value keeps the snapshot honest.
            DurationMs = 0,
            TraceId = traceId,
            SpanId = spanId,
            ThreadId = Environment.CurrentManagedThreadId,
            ThreadName = Thread.CurrentThread.Name ?? $"Thread-{Environment.CurrentManagedThreadId}",
            Locals = locals,
            StackTrace = stackTrace,
        });
    }

    /// <summary>
    /// What a probe id means. Captured at apply time because the injected IL carries only the id.
    /// </summary>
    // Deliberately holds NO LocationHash or LineNumber: those are config IDENTITY, they change when a probe is
    // edited in place (same key, new hash), and a snapshot of them attributed captures to a deleted config.
    // Identity is read from the live registry entry in OnLineProbeHit. What is safe to snapshot is what the
    // INJECTED IL fixed at weave time and cannot change afterwards: which local this id reads, and whether the
    // emitted sequence already consulted ShouldCapture.
    private sealed record LineProbeRegistration(
        string InstrumentationKey,
        string? LocalName,
        bool Gated,
        bool ChargesHit);
}
