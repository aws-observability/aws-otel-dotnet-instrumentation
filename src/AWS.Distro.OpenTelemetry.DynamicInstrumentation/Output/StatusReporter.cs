// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Reports instrumentation status to the CW Agent API every 60 seconds.
/// Status lifecycle: READY → ACTIVE → DISABLED.
/// </summary>
internal sealed class StatusReporter : IDisposable
{
    private const int ReportIntervalMs = 60_000;
    private const int MaxBatchSize = 100;

    private readonly DynamicInstrumentationClient client;
    private readonly InstrumentationRegistry registry;
    private readonly CancellationToken ct;
    private readonly object gate = new();

    // Status-dedup keyed by LocationHash (config identity), NOT InstrumentationKey (type+method): matches
    // the Java/JS reference SDKs so an in-place config change (new LocationHash) or a remove-then-re-add
    // re-reports READY/DISABLED. Cleared per-config on removal via Forget.
    private readonly HashSet<string> reportedReady = new();
    private readonly HashSet<string> reportedDisabled = new();
    private Timer? timer;

    public StatusReporter(DynamicInstrumentationClient client, InstrumentationRegistry registry, CancellationToken ct)
    {
        this.client = client;
        this.registry = registry;
        this.ct = ct;
    }

    public void Start()
    {
        this.timer = new Timer(_ => this.ReportStatuses(), null, ReportIntervalMs, ReportIntervalMs);
    }

    /// <summary>
    /// Report READY for newly-applied configs (hitCount == 0, not yet reported).
    /// Called immediately after applying configs (from the poller thread, under the manager's
    /// configChangeLock), concurrently with the 60s timer's ReportStatuses. The gate serializes all
    /// three status methods so the reportedReady/reportedDisabled sets and the GetAll enumeration are
    /// never mutated by the timer thread mid-iteration.
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

        this.SendBatched(statuses);
    }

    /// <summary>
    /// Report an ERROR for a config that failed to apply.
    /// </summary>
    /// <param name="config">The configuration that failed.</param>
    /// <param name="errorCause">The backend error cause code.</param>
    public void ReportError(InstrumentationConfiguration config, string errorCause)
    {
        // No lock needed: ERROR touches none of the shared state the gate protects — it neither reads/writes
        // the reportedReady/reportedDisabled dedup sets nor enumerates the registry; it just emits one entry
        // built from the passed config. (ReportReadyForNew and ReportStatuses do touch that state and are gated.)
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

        this.SendBatched(statuses);
    }

    /// <summary>
    /// Forget the status-dedup state for a removed configuration (keyed by LocationHash), so if the same
    /// location is re-added later it reports READY/DISABLED again rather than being suppressed for the
    /// process lifetime. Called from the manager when a config goes stale. Mirrors Java's
    /// <c>StatusReporter.forget(locationHash)</c>.
    /// </summary>
    /// <param name="locationHash">The removed configuration's location hash.</param>
    public void Forget(string locationHash)
    {
        lock (this.gate)
        {
            this.reportedReady.Remove(locationHash);
            this.reportedDisabled.Remove(locationHash);
        }
    }

    public void Dispose()
    {
        // Timer.Dispose(WaitHandle) blocks until any in-flight callback completes and signals the handle,
        // so no ReportStatuses can still be running (and touch the about-to-be-disposed HttpClient) after
        // this returns. The parameterless Dispose does NOT wait, which is the race vastin flagged.
        //
        // On the 2s bound: Dispose is always called AFTER the shared CancellationToken is cancelled
        // (DynamicInstrumentationManager.Cleanup cancels, then disposes this reporter). ReportStatuses
        // checks ct at entry and returns before any HTTP send once cancelled, so at Dispose time a callback
        // is either (a) not started — nothing to wait for, or (b) already inside a send that predates the
        // cancel. The 2s wait covers (b)'s tail; it is a fail-safe backstop, not a correctness guarantee —
        // if a truly wedged backend outruns it, we stop waiting rather than hang shutdown, accepting that the
        // callback may finish against a disposing HttpClient (whose own request then faults harmlessly). A
        // longer, backend-coupled bound is deliberately avoided so shutdown can't be held hostage by the network.
        var timerToDispose = this.timer;
        this.timer = null;
        if (timerToDispose != null)
        {
            using var disposed = new ManualResetEvent(false);
            if (timerToDispose.Dispose(disposed))
            {
                disposed.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
    }

    private void ReportStatuses()
    {
        if (this.ct.IsCancellationRequested)
        {
            return;
        }

        // Build the batch under the gate (protects the dedup sets + GetAll enumeration against the poller
        // thread's ReportReadyForNew/ReportError), then release it before the blocking HTTP send so a slow
        // backend can't hold the gate — and thus stall config application — for up to the HttpClient timeout.
        List<StatusEntry> statuses;
        lock (this.gate)
        {
            statuses = new List<StatusEntry>();

            foreach (var reg in this.registry.GetAll())
            {
                var locationHash = reg.Config.LocationHash;
                var hitState = reg.HitState;

                // DISABLED — report once.
                if (hitState.IsDisabled && !this.reportedDisabled.Contains(locationHash))
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

        this.SendBatched(statuses);
    }

    private void SendBatched(List<StatusEntry> statuses)
    {
        if (statuses.Count == 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < statuses.Count; i += MaxBatchSize)
            {
                var batch = statuses.GetRange(i, Math.Min(MaxBatchSize, statuses.Count - i));
                this.client.ReportStatusAsync(batch, this.ct).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Status reporting is best-effort.
        }
    }
}
