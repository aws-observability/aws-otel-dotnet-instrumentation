// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Turns the native profiler's per-probe weave verdicts into per-configuration ERROR statuses.
/// </summary>
// THE GAP THIS CLOSES. Applying a line probe reports READY as soon as the MANAGED resolution succeeds, because
// that is the only thing knowable at the time: the native rewriter runs later, on a ReJIT thread, when the
// target method is next invoked. So a probe the rewriter declines was reported live and never corrected — the
// operator sees READY on a probe that cannot fire, forever.
//
// That is a measured failure mode, not a theoretical one: before the callback-AssemblyRef fix, a module
// carrying only line-level probes had eleven probes skipped by the rewriter while every one of them reported
// READY. The fix removed that particular cause. This removes the CLASS of it — any future rewriter refusal now
// surfaces instead of being swallowed.
//
// WHY A SEPARATE CLASS. The manager could hold this logic, but it is stateful (what has already been reported)
// and its inputs are four collaborators, which together make it the part most worth testing in isolation. Kept
// as constructor-injected delegates so the whole thing runs without a native profiler.
internal sealed class LineProbeWeaveReporter
{
    private readonly Func<IReadOnlyList<(int ProbeId, LineProbeWeaveOutcome Outcome)>> readVerdicts;
    private readonly Func<int, string?> resolveInstrumentationKey;
    private readonly Func<string, InstrumentationConfiguration?> resolveConfiguration;
    private readonly Action<InstrumentationConfiguration, string> reportError;

    // Guards both sets. Report runs on the status-reporting timer; Forget runs on whichever poller thread
    // applied a configuration change. They touch the same state, so neither can go unguarded.
    private readonly object gate = new();

    // Probe ids whose verdict has already been examined. Keyed by probe id rather than by configuration
    // because the native log keeps reporting the same verdict on every poll — it is a state, not an event.
    private readonly HashSet<int> examinedProbeIds = new();

    // Configurations already reported. SEPARATE from the probe-id set, and both are needed: a config
    // capturing three locals owns three probes, and if all three fail (a whole-method Import/Export failure
    // fails every one) the operator must get ONE error, not three copies of the same news.
    private readonly HashSet<string> reportedLocationHashes = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LineProbeWeaveReporter"/> class.
    /// </summary>
    /// <param name="readVerdicts">Reads the native profiler's verdicts, e.g. <c>LineProbeTranslator.GetWeaveResults</c>.</param>
    /// <param name="resolveInstrumentationKey">Maps a probe id to its owning configuration key, or null.</param>
    /// <param name="resolveConfiguration">Maps an instrumentation key to the live configuration, or null.</param>
    /// <param name="reportError">Reports (configuration, backend error cause).</param>
    public LineProbeWeaveReporter(
        Func<IReadOnlyList<(int ProbeId, LineProbeWeaveOutcome Outcome)>> readVerdicts,
        Func<int, string?> resolveInstrumentationKey,
        Func<string, InstrumentationConfiguration?> resolveConfiguration,
        Action<InstrumentationConfiguration, string> reportError)
    {
        this.readVerdicts = readVerdicts;
        this.resolveInstrumentationKey = resolveInstrumentationKey;
        this.resolveConfiguration = resolveConfiguration;
        this.reportError = reportError;
    }

    /// <summary>
    /// Gets the number of configurations reported so far. For tests and diagnostics.
    /// </summary>
    internal int ReportedConfigurationCount
    {
        get
        {
            lock (this.gate)
            {
                return this.reportedLocationHashes.Count;
            }
        }
    }

    /// <summary>
    /// Reads the current verdicts and reports an ERROR for each newly-failed configuration.
    /// </summary>
    /// <returns>The number of configurations reported by this pass.</returns>
    public int Report()
    {
        var verdicts = this.readVerdicts();
        if (verdicts.Count == 0)
        {
            return 0;
        }

        // Collected under the gate, then reported OUTSIDE it. reportError reaches StatusReporter, which takes
        // its own lock and enqueues; holding this gate across that call would nest two locks for no reason and
        // put a second ordering constraint on a path that has one already.
        List<InstrumentationConfiguration>? toReport = null;
        List<string>? causes = null;

        lock (this.gate)
        {
            foreach (var (probeId, outcome) in verdicts)
            {
                // PENDING is the normal state for a probe on a method nobody has called, and it is NOT
                // recorded as examined: the verdict is still to come, and marking it now would make the real
                // verdict — whenever it arrives — invisible.
                if (!outcome.IsWeaveFailure())
                {
                    // WOVEN is examined, though. It is terminal, and recording it keeps the pass from
                    // re-resolving every healthy probe's key on every period.
                    if (outcome == LineProbeWeaveOutcome.Woven)
                    {
                        this.examinedProbeIds.Add(probeId);
                    }

                    continue;
                }

                if (!this.examinedProbeIds.Add(probeId))
                {
                    continue;
                }

                var key = this.resolveInstrumentationKey(probeId);
                if (key == null)
                {
                    // The configuration was removed after the verdict was recorded. Nothing to report: the
                    // operator deleted the probe, and telling them it failed would be noise about something
                    // that no longer exists.
                    continue;
                }

                var config = this.resolveConfiguration(key);
                if (config == null)
                {
                    continue;
                }

                if (!this.reportedLocationHashes.Add(config.LocationHash))
                {
                    continue;
                }

                toReport ??= new List<InstrumentationConfiguration>();
                causes ??= new List<string>();
                toReport.Add(config);
                causes.Add(outcome.MapErrorCause());
            }
        }

        if (toReport == null || causes == null)
        {
            return 0;
        }

        for (var i = 0; i < toReport.Count; i++)
        {
            this.reportError(toReport[i], causes[i]);
        }

        return toReport.Count;
    }

    /// <summary>
    /// Drops the reported-state for a configuration that has been removed or edited in place.
    /// </summary>
    /// <param name="locationHash">The configuration identity being retired.</param>
    /// <param name="probeIds">The probe ids that configuration owned.</param>
    // MIRRORS StatusReporter.Forget, and for the same reason: without it, re-adding the same probe after
    // fixing the underlying problem would be suppressed as already-reported for the rest of the process
    // lifetime, and the operator would never see it recover.
    public void Forget(string locationHash, IEnumerable<int> probeIds)
    {
        lock (this.gate)
        {
            this.reportedLocationHashes.Remove(locationHash);
            foreach (var probeId in probeIds)
            {
                this.examinedProbeIds.Remove(probeId);
            }
        }
    }
}
