// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Decides whether a given FunctionCall should be recorded, per the
/// <c>OTEL_AWS_SERVICE_EVENTS_SAMPLING_MODE</c> contract.
/// </summary>
/// <remarks>
/// <para>Three modes (an earlier <c>adaptive</c> mode is no longer accepted):</para>
/// <list type="bullet">
/// <item><description><c>always</c> (default) — record every call.</description></item>
/// <item><description><c>never</c> — record nothing.</description></item>
/// <item><description><c>auto</c> — per-function 3-tier downsample on the function's
/// cumulative call count (tier1 100%, tier2 every <c>TIER2_RATE</c>, tier3 every
/// <c>TIER3_RATE</c>).</description></item>
/// </list>
/// <para>Thread-safe: the per-function counter is a <see cref="ConcurrentDictionary{TKey,TValue}" />.</para>
/// </remarks>
internal sealed class FunctionCallSampler
{
    private readonly string mode;
    private readonly int tier1Threshold;
    private readonly int tier2Threshold;
    private readonly int tier2Rate;
    private readonly int tier3Rate;

    private readonly ConcurrentDictionary<string, long> callCounters = new();

    public FunctionCallSampler(ServiceEventsConfig config)
    {
        this.mode = config.SamplingMode;
        this.tier1Threshold = config.SampleTier1Threshold;
        this.tier2Threshold = config.SampleTier2Threshold;

        // Guard against divide-by-zero from a misconfigured rate (% by 0 throws).
        this.tier2Rate = config.SampleTier2Rate <= 0 ? 1 : config.SampleTier2Rate;
        this.tier3Rate = config.SampleTier3Rate <= 0 ? 1 : config.SampleTier3Rate;
    }

    /// <summary>
    /// Decide whether a call should be recorded.
    /// </summary>
    /// <param name="functionName">Derived function name — the auto-mode counter key.</param>
    /// <returns><c>true</c> when the call should be recorded.</returns>
    public bool ShouldSample(string functionName)
    {
        switch (this.mode)
        {
            case "never":
                return false;
            case "auto":
                return this.SampleAuto(functionName);
            default:
                // "always" (the default) and any unrecognized value → record every call.
                return true;
        }
    }

    // The counter map persists for process lifetime and is never pruned, unlike IncidentRateLimiter's
    // per-error map (guarded at 1000 entries and discarded every window). That difference is
    // deliberate: tiered sampling is defined on cumulative call counts, so resetting would restart
    // every function in tier 1 and re-sample everything. Cardinality is bounded instead by the
    // PackagesToInstrument allowlist — nothing outside it reaches here, and the allowlist is a
    // configured, finite set of Activity source names. An app that generates unbounded distinct
    // operation names *within* an allowlisted source would grow this map; if that ever shows up,
    // the guard belongs here rather than in the allowlist.
    private bool SampleAuto(string functionName)
    {
        var total = this.callCounters.AddOrUpdate(functionName, 1, (_, v) => v + 1);

        if (total <= this.tier1Threshold)
        {
            return true;
        }

        if (total <= this.tier2Threshold)
        {
            return total % this.tier2Rate == 0;
        }

        return total % this.tier3Rate == 0;
    }
}
