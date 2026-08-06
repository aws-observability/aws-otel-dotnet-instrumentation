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

    // Failed configs stay registered with HitCount == 0, which is ReportReadyForNew's criterion. Cleared in
    // Forget.
    private readonly HashSet<string> errored = new();
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
                if (this.reportedReady.Contains(locationHash) || this.errored.Contains(locationHash))
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
        // Under the gate because `errored` is read by ReportReadyForNew and ReportStatuses. Flagged before
        // the send so a failed send still suppresses READY.
        lock (this.gate)
        {
            this.errored.Add(config.LocationHash);
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

        this.SendBatched(statuses);
    }

    /// <summary>
    /// Forget the status-dedup state for a removed configuration, so re-adding the same location later
    /// reports READY/DISABLED again instead of being suppressed for the process lifetime.
    /// </summary>
    /// <param name="locationHash">The removed configuration's location hash.</param>
    public void Forget(string locationHash)
    {
        lock (this.gate)
        {
            this.reportedReady.Remove(locationHash);
            this.reportedDisabled.Remove(locationHash);

            // A re-added config gets a fresh apply attempt, so a past failure stops suppressing READY.
            this.errored.Remove(locationHash);
        }
    }

    public void Dispose()
    {
        // Timer.Dispose(WaitHandle) waits for any in-flight callback to finish, so it can't touch the
        // disposed HttpClient afterward. The 2s bound is a backstop: the token is already cancelled before
        // Dispose, so a callback either hasn't started or is finishing a pre-cancel send — we don't hang
        // shutdown on a wedged backend.
        var timerToDispose = this.timer;
        this.timer = null;
        if (timerToDispose != null)
        {
            var disposed = new ManualResetEvent(false);
            if (timerToDispose.Dispose(disposed))
            {
                // On timeout the CLR still signals this handle when the wedged callback finishes, so
                // disposing it now would throw ObjectDisposedException on the timer thread. Leak it instead
                // (process is shutting down); only dispose once we've observed the signal.
                if (disposed.WaitOne(TimeSpan.FromSeconds(2)))
                {
                    disposed.Dispose();
                }
            }
            else
            {
                disposed.Dispose();
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
