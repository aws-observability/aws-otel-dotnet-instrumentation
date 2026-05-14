// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal sealed class AnomalyDetector
{
    internal static readonly string AdaptiveSamplingConfiguredAttribute = "aws.xray.adaptive_sampling_configured";

    private const int TraceCacheMaxSize = 100_000;
    private static readonly TimeSpan TraceCacheTtl = TimeSpan.FromSeconds(600);

    private readonly AdaptiveSamplingConfig config;
    private readonly TokenBucketRateLimiter? rateLimiter;
    private readonly MemoryCache traceCache;
    private readonly BoostStatistics stats = new();

    internal AnomalyDetector(AdaptiveSamplingConfig config)
    {
        this.config = config;

        this.traceCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = TraceCacheMaxSize,
        });

        if (config.AnomalyCaptureLimit != null)
        {
            this.rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = config.AnomalyCaptureLimit.AnomalyTracesPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = config.AnomalyCaptureLimit.AnomalyTracesPerSecond,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }
    }

    internal bool IsAnomaly(Activity span)
    {
        var conditions = this.config.AnomalyConditions;
        if (conditions == null || conditions.Count == 0)
        {
            return false;
        }

        return conditions.Any(c => this.MatchesCondition(c, span));
    }

    internal bool ShouldCaptureAnomaly(string traceId)
    {
        if (this.traceCache.TryGetValue(traceId, out _))
        {
            return false;
        }

        if (this.rateLimiter != null)
        {
            using var lease = this.rateLimiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                return false;
            }
        }

        this.traceCache.Set(traceId, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TraceCacheTtl,
            Size = 1,
        });

        return true;
    }

    internal void RecordTrace()
    {
        this.stats.IncrementTotal();
    }

    internal void RecordAnomaly(bool isSampled)
    {
        this.stats.IncrementAnomaly();
        if (isSampled)
        {
            this.stats.IncrementSampledAnomaly();
        }
    }

    internal BoostStatistics SnapshotAndResetStatistics()
    {
        return this.stats.SnapshotAndReset();
    }

    private bool MatchesCondition(AnomalyCondition condition, Activity span)
    {
        if (condition.Usage == UsageType.Neither)
        {
            return false;
        }

        if (condition.Operations != null && condition.Operations.Count > 0)
        {
            var operation = span.GetTagItem(AwsAttributeKeys.AttributeAWSLocalOperation) as string;
            if (operation == null || !condition.Operations.Contains(operation))
            {
                return false;
            }
        }

        bool hasAnyCriteria = false;

        if (condition.ErrorCodeRegex != null)
        {
            hasAnyCriteria = true;
            if (!this.MatchesErrorCode(condition.ErrorCodeRegex, span))
            {
                return false;
            }
        }

        if (condition.HighLatencyMs.HasValue)
        {
            hasAnyCriteria = true;
            if (!this.MatchesHighLatency(condition.HighLatencyMs.Value, span))
            {
                return false;
            }
        }

        return hasAnyCriteria;
    }

    private bool MatchesErrorCode(string regex, Activity span)
    {
        var statusCode = this.GetHttpStatusCode(span);
        if (statusCode == null)
        {
            return false;
        }

        try
        {
            string anchored = regex.StartsWith("^") && regex.EndsWith("$") ? regex : $"^(?:{regex})$";
            return Regex.IsMatch(statusCode.Value.ToString(), anchored);
        }
        catch
        {
            return false;
        }
    }

    private bool MatchesHighLatency(int thresholdMs, Activity span)
    {
        return span.Duration.TotalMilliseconds > thresholdMs;
    }

    private int? GetHttpStatusCode(Activity span)
    {
        var code = span.GetTagItem("http.response.status_code") ?? span.GetTagItem("http.status_code");
        if (code == null)
        {
            return null;
        }

        if (code is int intCode)
        {
            return intCode;
        }

        if (int.TryParse(code.ToString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }
}
