// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Sampler.AWS;

/// <summary>
/// Remote sampler that gets sampling configuration from AWS X-Ray.
/// </summary>
public sealed class AWSXRayRemoteSampler : Trace.Sampler, IDisposable
{
    internal static readonly TimeSpan DefaultTargetInterval = TimeSpan.FromSeconds(10);

    private static readonly Random Random = new();
    private bool isFallBackEventToWriteSwitch = true;

    [SuppressMessage("Performance", "CA5394: Do not use insecure randomness", Justification = "Secure random is not required for jitters.")]
    internal AWSXRayRemoteSampler(Resource resource, TimeSpan pollingInterval, string endpoint, Clock clock)
    {
        this.Resource = resource;
        this.PollingInterval = pollingInterval;
        this.Endpoint = endpoint;
        this.Clock = clock;
        this.ClientId = GenerateClientId();
        this.Client = new AWSXRaySamplerClient(this.Endpoint);
        this.FallbackSampler = new FallbackSampler(this.Clock);
        this.RulesCache = new RulesCache(this.Clock, this.ClientId, this.Resource, this.FallbackSampler);

        // upto 5 seconds of jitter for rule polling
        this.RulePollerJitter = TimeSpan.FromMilliseconds(Random.Next(1, 5000));

        // upto 100 milliseconds of jitter for target polling
        this.TargetPollerJitter = TimeSpan.FromMilliseconds(Random.Next(1, 100));

        // execute the first update right away and schedule subsequent update later.
        this.RulePollerTimer = new Timer(this.GetAndUpdateRules, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        // set up the target poller to go off once after the default interval. We will update the timer later.
        this.TargetPollerTimer = new Timer(this.GetAndUpdateTargets, null, DefaultTargetInterval, Timeout.InfiniteTimeSpan);
    }

    internal TimeSpan RulePollerJitter { get; set; }

    internal TimeSpan TargetPollerJitter { get; set; }

    internal Clock Clock { get; set; }

    internal string ClientId { get; set; }

    internal Resource Resource { get; set; }

    internal string Endpoint { get; set; }

    internal AWSXRaySamplerClient Client { get; set; }

    internal RulesCache RulesCache { get; set; }

    internal Timer RulePollerTimer { get; set; }

    internal Timer TargetPollerTimer { get; set; }

    internal TimeSpan PollingInterval { get; set; }

    internal Trace.Sampler FallbackSampler { get; set; }

    /// <summary>
    /// Initializes a <see cref="AWSXRayRemoteSamplerBuilder"/> for the sampler.
    /// </summary>
    /// <param name="resource">an instance of <see cref="Resources.Resource"/>
    /// to identify the service attributes for sampling. This resource should
    /// be the same as what the OpenTelemetry SDK is configured with.</param>
    /// <returns>an instance of <see cref="AWSXRayRemoteSamplerBuilder"/>.</returns>
    public static AWSXRayRemoteSamplerBuilder Builder(Resource resource)
    {
        return new AWSXRayRemoteSamplerBuilder(resource);
    }

    /// <inheritdoc/>
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        SamplingResult result;
        string? matchedRuleName = null;

        if (this.RulesCache.Expired())
        {
            if (this.isFallBackEventToWriteSwitch)
            {
                this.isFallBackEventToWriteSwitch = false;
                AWSSamplerEventSource.Log.InfoUsingFallbackSampler();
            }

            result = this.FallbackSampler.ShouldSample(in samplingParameters);
        }
        else
        {
            this.isFallBackEventToWriteSwitch = true;
            var matched = this.RulesCache.GetMatchedRule(in samplingParameters);
            if (matched != null)
            {
                result = matched.ShouldSample(in samplingParameters);
                matchedRuleName = matched.RuleName;
            }
            else
            {
                result = this.FallbackSampler.ShouldSample(in samplingParameters);
            }
        }

        var parentContext = samplingParameters.ParentContext;
        string? upstreamXrsr = GetTraceStateValue(parentContext.TraceState, RulesCache.XrsrTraceStateKey);

        string? hashedRuleName = null;
        if (upstreamXrsr != null)
        {
            hashedRuleName = upstreamXrsr;
        }
        else if (parentContext != default)
        {
            hashedRuleName = null;
        }
        else if (matchedRuleName != null)
        {
            hashedRuleName = this.RulesCache.GetHashForRule(matchedRuleName);
        }

        string? traceStateString = result.TraceStateString;
        if (hashedRuleName != null)
        {
            traceStateString = SetTraceStateValue(traceStateString, RulesCache.XrsrTraceStateKey, hashedRuleName);
        }

        return new SamplingResult(result.Decision, result.Attributes, traceStateString);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal SamplingRuleApplier? GetMatchedRule(in SamplingParameters samplingParameters)
    {
        return this.RulesCache.GetMatchedRule(in samplingParameters);
    }

    internal SamplingRuleApplier? GetRuleApplierByHash(string hash)
    {
        return this.RulesCache.GetRuleApplierByHash(hash);
    }

    [SuppressMessage(
        "Usage",
        "CA5394: Do not use insecure randomness",
        Justification = "using insecure random is fine here since clientId doesn't need to be secure.")]
    private static string GenerateClientId()
    {
        char[] hex = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f'];
        var clientIdChars = new char[24];
        for (var i = 0; i < clientIdChars.Length; i++)
        {
            clientIdChars[i] = hex[Random.Next(hex.Length)];
        }

        return new string(clientIdChars);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.RulePollerTimer?.Dispose();
            this.Client?.Dispose();
            this.RulesCache?.Dispose();
        }
    }

    private async void GetAndUpdateRules(object? state)
    {
        var rules = await this.Client.GetSamplingRules().ConfigureAwait(false);

        this.RulesCache.UpdateRules(rules);

        // schedule the next rule poll.
        this.RulePollerTimer.Change(this.PollingInterval.Add(this.RulePollerJitter), Timeout.InfiniteTimeSpan);
    }

    private static string? GetTraceStateValue(string? traceState, string key)
    {
        if (string.IsNullOrEmpty(traceState))
        {
            return null;
        }

        foreach (var entry in traceState!.Split(','))
        {
            var parts = entry.Trim().Split('=');
            if (parts.Length == 2 && parts[0] == key)
            {
                return parts[1];
            }
        }

        return null;
    }

    private static string SetTraceStateValue(string? traceState, string key, string value)
    {
        var newEntry = $"{key}={value}";
        if (string.IsNullOrEmpty(traceState))
        {
            return newEntry;
        }

        var entries = traceState!.Split(',')
            .Where(e => !e.Trim().StartsWith($"{key}=", StringComparison.Ordinal))
            .ToList();
        entries.Insert(0, newEntry);
        return string.Join(",", entries);
    }

    private async void GetAndUpdateTargets(object? state)
    {
        var statistics = this.RulesCache.Snapshot(this.Clock.Now());

        var serviceName = (string?)this.Resource.Attributes.FirstOrDefault(kvp =>
            kvp.Key.Equals("service.name", StringComparison.Ordinal)).Value ?? string.Empty;
        var boostStats = this.RulesCache.SnapshotBoostStatistics(serviceName);

        var request = new GetSamplingTargetsRequest(statistics, boostStats.Count > 0 ? boostStats : null);
        var response = await this.Client.GetSamplingTargets(request).ConfigureAwait(false);
        if (response != null)
        {
            Dictionary<string, SamplingTargetDocument> targets = [];
            foreach (var target in response.SamplingTargetDocuments)
            {
                if (target.RuleName != null)
                {
                    targets[target.RuleName] = target;
                }
            }

            this.RulesCache.UpdateTargets(targets);

            if (response.LastRuleModification > 0)
            {
                var lastRuleModificationTime = this.Clock.ToDateTime(response.LastRuleModification);

                if (lastRuleModificationTime > this.RulesCache.GetUpdatedAt())
                {
                    // rules have been updated. fetch the new ones right away.
                    this.RulePollerTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }
        }

        // schedule next target poll
        var nextTargetFetchTime = this.RulesCache.NextTargetFetchTime();
        var nextTargetFetchInterval = nextTargetFetchTime.Subtract(this.Clock.Now());
        if (nextTargetFetchInterval < TimeSpan.Zero)
        {
            nextTargetFetchInterval = DefaultTargetInterval;
        }

        this.TargetPollerTimer.Change(nextTargetFetchInterval.Add(this.TargetPollerJitter), Timeout.InfiniteTimeSpan);
    }
}
