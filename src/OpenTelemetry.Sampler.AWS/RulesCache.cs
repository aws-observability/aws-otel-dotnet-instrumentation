// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Sampler.AWS;

internal class RulesCache : IDisposable
{
    private const int CacheTTL = 60 * 60; // cache expires 1 hour after the refresh (in sec)

    public const string XrsrTraceStateKey = "xrsr";

    private readonly ReaderWriterLockSlim rwLock;
    private Dictionary<string, string> ruleToHashMap = new();
    private Dictionary<string, string> hashToRuleMap = new();

    public RulesCache(Clock clock, string clientId, Resource resource, Trace.Sampler fallbackSampler)
    {
        this.rwLock = new ReaderWriterLockSlim();
        this.Clock = clock;
        this.ClientId = clientId;
        this.Resource = resource;
        this.FallbackSampler = fallbackSampler;
        this.RuleAppliers = new();
        this.UpdatedAt = this.Clock.Now();
    }

    internal Clock Clock { get; set; }

    internal string ClientId { get; set; }

    internal Resource Resource { get; set; }

    internal Trace.Sampler FallbackSampler { get; set; }

    internal List<SamplingRuleApplier> RuleAppliers { get; set; }

    internal DateTimeOffset UpdatedAt { get; set; }

    public bool Expired()
    {
        this.rwLock.EnterReadLock();
        try
        {
            return this.Clock.Now() > this.UpdatedAt.AddSeconds(CacheTTL);
        }
        finally
        {
            this.rwLock.ExitReadLock();
        }
    }

    public void UpdateRules(List<SamplingRule> newRules)
    {
        // sort the new rules
        newRules.Sort((x, y) => x.CompareTo(y));

        List<SamplingRuleApplier> newRuleAppliers = new();
        foreach (var rule in newRules)
        {
            // If the ruleApplier already exists in the current list of appliers, then we reuse it.
            var ruleApplier = this.RuleAppliers
                .FirstOrDefault(currentApplier => currentApplier.RuleName == rule.RuleName) ??
                new SamplingRuleApplier(this.ClientId, this.Clock, rule, new Statistics());

            // update the rule in the applier in case rule attributes have changed
            ruleApplier.Rule = rule;

            newRuleAppliers.Add(ruleApplier);
        }

        this.rwLock.EnterWriteLock();
        try
        {
            this.RuleAppliers = newRuleAppliers;
            this.UpdatedAt = this.Clock.Now();

            // Rebuild xrsr hash maps
            this.ruleToHashMap = new Dictionary<string, string>();
            this.hashToRuleMap = new Dictionary<string, string>();
            foreach (var applier in this.RuleAppliers)
            {
                var hash = HashRuleName(applier.RuleName);
                this.ruleToHashMap[applier.RuleName] = hash;
                this.hashToRuleMap[hash] = applier.RuleName;
            }
        }
        finally
        {
            this.rwLock.ExitWriteLock();
        }
    }

    public SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        this.rwLock.EnterReadLock();
        List<SamplingRuleApplier> appliers;
        try
        {
            appliers = this.RuleAppliers;
        }
        finally
        {
            this.rwLock.ExitReadLock();
        }

        foreach (var ruleApplier in appliers)
        {
            if (ruleApplier.Matches(samplingParameters, this.Resource))
            {
                return ruleApplier.ShouldSample(in samplingParameters);
            }
        }

        return this.FallbackSampler.ShouldSample(in samplingParameters);
    }

    public List<SamplingStatisticsDocument> Snapshot(DateTimeOffset now)
    {
        List<SamplingStatisticsDocument> snapshots = new();
        foreach (var ruleApplier in this.RuleAppliers)
        {
            snapshots.Add(ruleApplier.Snapshot(now));
        }

        return snapshots;
    }

    public void UpdateTargets(Dictionary<string, SamplingTargetDocument> targets)
    {
        List<SamplingRuleApplier> newRuleAppliers = new();
        foreach (var ruleApplier in this.RuleAppliers)
        {
            targets.TryGetValue(ruleApplier.RuleName, out var target);
            if (target != null)
            {
                newRuleAppliers.Add(ruleApplier.WithTarget(target, this.Clock.Now()));
            }
            else
            {
                // did not get target for this rule. Will be updated in future target poll.
                newRuleAppliers.Add(ruleApplier);
            }
        }

        this.rwLock.EnterWriteLock();
        try
        {
            this.RuleAppliers = newRuleAppliers;
        }
        finally
        {
            this.rwLock.ExitWriteLock();
        }
    }

    public DateTimeOffset NextTargetFetchTime()
    {
        var defaultPollingTime = this.Clock.Now().AddSeconds(AWSXRayRemoteSampler.DefaultTargetInterval.TotalSeconds);

        if (this.RuleAppliers.Count == 0)
        {
            return defaultPollingTime;
        }

        var minPollingTime = this.RuleAppliers.Min(r => r.NextSnapshotTime);

        return minPollingTime < this.Clock.Now() ? defaultPollingTime : minPollingTime;
    }

    public SamplingRuleApplier? GetMatchedRule(in SamplingParameters samplingParameters)
    {
        this.rwLock.EnterReadLock();
        List<SamplingRuleApplier> appliers;
        try
        {
            appliers = this.RuleAppliers;
        }
        finally
        {
            this.rwLock.ExitReadLock();
        }

        foreach (var ruleApplier in appliers)
        {
            if (ruleApplier.Matches(samplingParameters, this.Resource))
            {
                return ruleApplier;
            }
        }

        return null;
    }

    public SamplingRuleApplier? GetRuleApplierByHash(string hash)
    {
        this.rwLock.EnterReadLock();
        try
        {
            if (!this.hashToRuleMap.TryGetValue(hash, out var ruleName))
            {
                return null;
            }

            return this.RuleAppliers.FirstOrDefault(a => a.RuleName == ruleName);
        }
        finally
        {
            this.rwLock.ExitReadLock();
        }
    }

    public string? GetHashForRule(string ruleName)
    {
        this.rwLock.EnterReadLock();
        try
        {
            return this.ruleToHashMap.TryGetValue(ruleName, out var hash) ? hash : null;
        }
        finally
        {
            this.rwLock.ExitReadLock();
        }
    }

    public List<SamplingBoostStatisticsDocument> SnapshotBoostStatistics(string serviceName)
    {
        var docs = new List<SamplingBoostStatisticsDocument>();
        var timestamp = this.Clock.ToDouble(this.Clock.Now());
        foreach (var applier in this.RuleAppliers)
        {
            if (!applier.Rule.HasBoost())
            {
                continue;
            }

            var doc = applier.SnapshotBoostStatistics(this.ClientId, serviceName, timestamp);
            if (doc != null)
            {
                docs.Add(doc);
            }
        }

        return docs;
    }

    public static string HashRuleName(string ruleName)
    {
        using var sha = SHA256.Create();
        byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(ruleName));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hashBytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal DateTimeOffset GetUpdatedAt()
    {
        this.rwLock.EnterReadLock();
        try
        {
            return this.UpdatedAt;
        }
        finally
        {
            this.rwLock.ExitReadLock();
        }
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.rwLock.Dispose();
        }
    }
}
