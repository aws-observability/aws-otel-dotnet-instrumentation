// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Reports instrumentation status to the CW Agent API every 60 seconds.
/// Status lifecycle: READY → ACTIVE → DISABLED.
/// </summary>
/// <remarks>
/// Every status is HANDED OFF to a background worker thread and sent from there; no caller of
/// ReportError/ReportReadyForNew ever blocks on HTTP. That matters because the manager calls both from
/// inside its configChangeLock while applying a configuration set: a synchronous send there let a slow or
/// wedged backend stall configuration application for up to the HttpClient timeout (30s), and the poller
/// threads that drive OnConfigurationsChanged pile up behind the same lock. Mirrors the Python agent's
/// BackgroundStatusReporter.
/// </remarks>
internal sealed class StatusReporter : IDisposable
{
    private const int ReportIntervalMs = 60_000;
    private const int MaxBatchSize = 100;

    // Bounded so a wedged backend cannot grow the queue without limit. Generous next to real config counts
    // (one READY per config, plus one ACTIVE per hit config per 60s period).
    private const int MaxQueuedStatuses = 1_000;

    private readonly DynamicInstrumentationClient client;
    private readonly InstrumentationRegistry registry;
    private readonly CancellationToken ct;
    private readonly object gate = new();

    // Hand-off queue between the callers (poller threads, timer) and the sending worker. Bounded: when full,
    // TryAdd fails and the status is dropped rather than blocking the caller — which would reintroduce
    // exactly the stall this queue exists to remove. A dropped READY/DISABLED is rolled back OUT of its dedup
    // set (see Enqueue) so a later pass reports it again; dropping one silently while the dedup set claimed it
    // was reported would hide a live probe from the operator for the rest of the process lifetime.
    private readonly BlockingCollection<StatusEntry> pending = new(MaxQueuedStatuses);

    // Status-dedup keyed by LocationHash (config identity), NOT InstrumentationKey (type+method): matches
    // the Java/JS reference SDKs so an in-place config change (new LocationHash) or a remove-then-re-add
    // re-reports READY/DISABLED. Cleared per-config on removal via Forget.
    private readonly HashSet<string> reportedReady = new();
    private readonly HashSet<string> reportedDisabled = new();

    // READY means "instrumented and waiting to be hit", so it must be driven by what the profiler actually
    // WOVE — not by what is registered. Registration happens for every supported config, including ones whose
    // apply returned TypeNotLoaded (assembly not loaded yet, retried on a later poll) or failed permanently.
    // Reporting READY for those told the operator a probe was live when nothing had been instrumented.
    // MarkApplied is the manager's explicit signal that the apply succeeded; only those report READY.
    private readonly HashSet<string> appliedLocations = new();

    // Locations that have reported ERROR. A failed config stays in the registry (that is how a permanent
    // failure is reported exactly once instead of every poll), so without this the very next
    // ReportReadyForNew pass followed its ERROR with a READY for the same location — the backend's last word
    // on a broken probe was that it was fine. Cleared by Forget, so a removed-and-re-added location is
    // judged afresh.
    private readonly HashSet<string> erroredLocations = new();
    private Timer? timer;
    private Thread? worker;
    private long droppedStatuses;
    private int disposed;

    public StatusReporter(DynamicInstrumentationClient client, InstrumentationRegistry registry, CancellationToken ct)
    {
        this.client = client;
        this.registry = registry;
        this.ct = ct;
    }

    /// <summary>Gets the number of statuses dropped because the hand-off queue was full or closed.</summary>
    internal long DroppedStatusCount => Interlocked.Read(ref this.droppedStatuses);

    public void Start()
    {
        // Worker first: the timer callback and the poller both enqueue, and an entry queued before the worker
        // exists would sit there until the next enqueue happened to be followed by one.
        this.worker = new Thread(this.SendLoop) { IsBackground = true, Name = "DI-StatusReporter" };
        this.worker.Start();

        this.timer = new Timer(_ => this.ReportStatuses(), null, ReportIntervalMs, ReportIntervalMs);
    }

    /// <summary>
    /// Records that a configuration was successfully applied — the profiler wove its target — making it
    /// eligible for READY. Registration alone is not enough; see <see cref="appliedLocations"/>.
    /// </summary>
    /// <param name="config">The configuration whose apply succeeded.</param>
    public void MarkApplied(InstrumentationConfiguration config)
    {
        lock (this.gate)
        {
            this.appliedLocations.Add(config.LocationHash);
        }
    }

    /// <summary>
    /// Report READY for newly-applied configs. Called from the poller thread concurrently with the timer's
    /// ReportStatuses; the gate serializes both so the dedup sets aren't mutated mid-enumeration.
    /// </summary>
    public void ReportReadyForNew()
    {
        List<StatusEntry> statuses;
        lock (this.gate)
        {
            statuses = new List<StatusEntry>();

            foreach (var reg in this.registry.GetAll())
            {
                var locationHash = reg.Config.LocationHash;
                if (this.reportedReady.Contains(locationHash))
                {
                    continue;
                }

                // Registered but not woven (e.g. TypeNotLoaded, retried next poll), or already reported as
                // broken. Neither is READY.
                if (!this.appliedLocations.Contains(locationHash) || this.erroredLocations.Contains(locationHash))
                {
                    continue;
                }

                if (reg.HitState.HitCount == 0)
                {
                    statuses.Add(new StatusEntry
                    {
                        InstrumentationType = reg.Config.Type.ToString(),
                        LocationHash = locationHash,
                        Status = "READY",
                        Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    });
                    this.reportedReady.Add(locationHash);
                }
            }
        }

        this.Enqueue(statuses);
    }

    /// <summary>
    /// Report an ERROR for a config that failed to apply.
    /// </summary>
    /// <param name="config">The configuration that failed.</param>
    /// <param name="errorCause">The backend error cause code.</param>
    public void ReportError(InstrumentationConfiguration config, string errorCause)
    {
        // Under the gate because it records the location as errored, which ReportReadyForNew reads. Cheap and
        // safe: the gate is never held across the HTTP send (the send happens on the worker thread), so this
        // cannot stall config application.
        lock (this.gate)
        {
            // ONLY for a configuration that is in the registry. Forget() is driven by RemoveStale, which yields
            // only REGISTERED configs — so recording an unregistered one (an unsupported target: a ctor or
            // static initializer, refused before Register) would leave a string in this set that no path can
            // ever remove, for every such probe an operator creates over the process lifetime.
            if (this.registry.Get(config.InstrumentationKey) != null)
            {
                this.erroredLocations.Add(config.LocationHash);
            }
        }

        var statuses = new List<StatusEntry>
        {
            new()
            {
                InstrumentationType = config.Type.ToString(),
                LocationHash = config.LocationHash,
                Status = "ERROR",
                ErrorCause = errorCause,
                Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        };

        this.Enqueue(statuses);
    }

    /// <summary>
    /// Forget all status state for a removed configuration, so re-adding the same location later is judged
    /// afresh instead of being suppressed — or condemned by an old ERROR — for the process lifetime.
    /// </summary>
    /// <param name="locationHash">The removed configuration's location hash.</param>
    public void Forget(string locationHash)
    {
        lock (this.gate)
        {
            this.reportedReady.Remove(locationHash);
            this.reportedDisabled.Remove(locationHash);

            // Applied-state and errored-state go too: the removed config's IL stays woven, but its identity
            // is gone. A re-add has to earn READY through a fresh apply (MarkApplied), and must not inherit
            // the previous incarnation's ERROR.
            this.appliedLocations.Remove(locationHash);
            this.erroredLocations.Remove(locationHash);
        }
    }

    public void Dispose()
    {
        // Idempotent: a second Dispose must not CompleteAdding/Dispose the queue twice (both throw).
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
        {
            return;
        }

        // Timer.Dispose(WaitHandle) waits for any in-flight callback to finish, so it can't touch the
        // disposed HttpClient afterward. The 2s bound is a backstop: the token is already cancelled before
        // Dispose, so a callback either hasn't started or is finishing a pre-cancel enqueue — we don't hang
        // shutdown on a wedged backend.
        var timerToDispose = this.timer;
        this.timer = null;
        if (timerToDispose != null)
        {
            var timerCallbackDone = new ManualResetEvent(false);
            if (timerToDispose.Dispose(timerCallbackDone))
            {
                // On timeout the CLR still signals this handle when the wedged callback finishes, so
                // disposing it now would throw ObjectDisposedException on the timer thread. Leak it instead
                // (process is shutting down); only dispose once we've observed the signal.
                if (timerCallbackDone.WaitOne(TimeSpan.FromSeconds(2)))
                {
                    timerCallbackDone.Dispose();
                }
            }
            else
            {
                timerCallbackDone.Dispose();
            }
        }

        // Close the queue AFTER the timer is gone, so nothing enqueues past this point. CompleteAdding is what
        // ends the worker's GetConsumingEnumerable: it drains whatever is already queued and then returns.
        //
        // NOT called a "final flush", deliberately: Shutdown cancels the token BEFORE disposing this, and Send
        // passes that token, so a send attempted during the drain fails with OperationCanceledException and is
        // swallowed. Statuses queued at shutdown are DROPPED, not delivered. Delivering them would mean sending
        // on a separate token and accepting a slower shutdown against a wedged backend.
        this.pending.CompleteAdding();

        var workerToJoin = this.worker;
        this.worker = null;
        if (workerToJoin == null || workerToJoin.Join(TimeSpan.FromSeconds(2)))
        {
            // Only safe once the worker has actually exited. On timeout (wedged send) disposing the
            // collection would throw ObjectDisposedException on the worker thread, so leak it instead —
            // same reasoning as the timer handle above; the process is shutting down.
            this.pending.Dispose();
        }
    }

    /// <summary>
    /// Sends everything currently queued, on the CALLING thread, and returns. Exists for callers that have
    /// not started the worker — unit tests, which need the send to have happened before they assert on it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the worker is running. Two drains of one queue would split batches between threads
    /// nondeterministically, so this is enforced rather than left to a comment.
    /// </exception>
    internal void FlushPending()
    {
        if (this.worker != null)
        {
            throw new InvalidOperationException(
                "FlushPending is for reporters whose worker was never started; the running worker owns the queue.");
        }

        var batch = new List<StatusEntry>(MaxBatchSize);

        while (this.pending.TryTake(out var status))
        {
            batch.Add(status);
            if (batch.Count == MaxBatchSize)
            {
                this.Send(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            this.Send(batch);
        }
    }

    private void ReportStatuses()
    {
        if (this.ct.IsCancellationRequested)
        {
            return;
        }

        // Build the batch under the gate (protects the dedup sets + GetAll enumeration against the poller
        // thread's ReportReadyForNew/ReportError), then release it before enqueuing — the gate is never held
        // across the hand-off, and the send itself happens on the worker thread.
        List<StatusEntry> statuses;
        lock (this.gate)
        {
            statuses = new List<StatusEntry>();

            foreach (var reg in this.registry.GetAll())
            {
                var locationHash = reg.Config.LocationHash;
                var hitState = reg.HitState;

                // DISABLED — report once, and only for something that was actually instrumented. A config
                // stuck at TypeNotLoaded whose ExpiresAt passes is IsDisabled too, and reporting DISABLED for a
                // probe that never reached READY or ACTIVE misrepresents the lifecycle this class documents.
                if (hitState.IsDisabled &&
                    this.appliedLocations.Contains(locationHash) &&
                    !this.reportedDisabled.Contains(locationHash))
                {
                    statuses.Add(new StatusEntry
                    {
                        InstrumentationType = reg.Config.Type.ToString(),
                        LocationHash = locationHash,
                        Status = "DISABLED",
                        Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    });
                    this.reportedDisabled.Add(locationHash);
                    continue;
                }

                // ACTIVE — report every period if hit.
                if (hitState.HitInLastPeriod && !hitState.IsDisabled)
                {
                    statuses.Add(new StatusEntry
                    {
                        InstrumentationType = reg.Config.Type.ToString(),
                        LocationHash = reg.Config.LocationHash,
                        Status = "ACTIVE",
                        Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    });
                    hitState.ResetHitInLastPeriod();
                }
            }
        }

        this.Enqueue(statuses);
    }

    // Hands statuses to the worker. Never blocks: TryAdd on a full bounded collection returns false at once
    // rather than waiting for room, and a caller stalling here would be the very stall this queue removes.
    private void Enqueue(List<StatusEntry> statuses)
    {
        foreach (var status in statuses)
        {
            var accepted = false;
            try
            {
                accepted = this.pending.TryAdd(status);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // CompleteAdding has run (shutting down) or the collection is already disposed. Statuses
                // reported during shutdown are dropped, not an error.
            }

            if (!accepted)
            {
                Interlocked.Increment(ref this.droppedStatuses);
                this.UndoDedup(status);
            }
        }
    }

    // Releases the once-only claim a dropped status made on its dedup set, so a later pass reports it again.
    //
    // The claim has to be made BEFORE the enqueue, not after a successful send: the caller adds to
    // reportedReady/reportedDisabled while holding the gate, and that is what stops two threads from both
    // deciding to report the same location. Recording it only after delivery would open that window and
    // double-report. So the claim is provisional, and this undoes it when the hand-off fails — the probe is
    // woven and capturing, and a status set that swallowed its READY would leave it invisible in the console
    // for the process lifetime with no recovery path (Forget only runs on removal).
    //
    // ERROR is deliberately NOT rolled back: erroredLocations exists to stop a broken probe being reported
    // READY, and a dropped ERROR is no reason to start claiming it is fine. ACTIVE needs nothing — it is
    // re-reported every period it is hit, so a dropped one self-heals on the next pass.
    private void UndoDedup(StatusEntry status)
    {
        lock (this.gate)
        {
            switch (status.Status)
            {
                case "READY":
                    this.reportedReady.Remove(status.LocationHash);
                    break;
                case "DISABLED":
                    this.reportedDisabled.Remove(status.LocationHash);
                    break;
                default:
                    break;
            }
        }
    }

    // The only place a status is actually sent. Blocks on the queue until an entry arrives, coalesces
    // whatever else is already queued into one request, and exits when Dispose calls CompleteAdding.
    private void SendLoop()
    {
        var batch = new List<StatusEntry>(MaxBatchSize);

        try
        {
            foreach (var status in this.pending.GetConsumingEnumerable())
            {
                batch.Add(status);

                // Coalesce: a config change typically reports several statuses at once, and the backend
                // accepts up to MaxBatchSize per request.
                while (batch.Count < MaxBatchSize && this.pending.TryTake(out var next))
                {
                    batch.Add(next);
                }

                this.Send(batch);
                batch.Clear();
            }
        }
        catch (ObjectDisposedException)
        {
            // Raced a Dispose that timed out waiting for this thread; nothing left to send.
        }
    }

    // Safe to reuse the caller's list after this returns: the send is fully awaited here.
    private void Send(List<StatusEntry> batch)
    {
        try
        {
            this.client.ReportStatusAsync(batch, this.ct).GetAwaiter().GetResult();
        }
        catch
        {
            // Status reporting is best-effort.
        }
    }
}
