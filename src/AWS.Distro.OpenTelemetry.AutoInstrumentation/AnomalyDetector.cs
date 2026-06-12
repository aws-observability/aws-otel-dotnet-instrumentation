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

    internal AnomalyDetectionResult? GetAnomalyMatch(Activity span, bool defaultAnomalyDetectionDisabled)
    {
        bool forBoost = false;
        bool forCapture = false;

        var conditions = this.config.AnomalyConditions;
        if (conditions != null && conditions.Count > 0)
        {
            foreach (var condition in conditions)
            {
                if (forBoost && condition.Usage == UsageType.SamplingBoost)
                {
                    continue;
                }

                if (forCapture && condition.Usage == UsageType.AnomalyTraceCapture)
                {
                    continue;
                }

                if (!this.MatchesCondition(condition, span))
                {
                    continue;
                }

                switch (condition.Usage)
                {
                    case UsageType.Both:
                        forBoost = true;
                        forCapture = true;
                        break;
                    case UsageType.SamplingBoost:
                        forBoost = true;
                        break;
                    case UsageType.AnomalyTraceCapture:
                        forCapture = true;
                        break;
                }

                if (forBoost && forCapture)
                {
                    break;
                }
            }
        }
        else if (!defaultAnomalyDetectionDisabled)
        {
            // Default anomaly detection: 5xx or StatusCode.ERROR
            var statusCode = this.GetHttpStatusCode(span);
            if (statusCode != null && statusCode.Value > 499)
            {
                forBoost = true;
                forCapture = true;
            }
            else if (statusCode == null && span.Status == ActivityStatusCode.Error)
            {
                forBoost = true;
                forCapture = true;
            }
        }

        if (!forBoost && !forCapture)
        {
            return null;
        }

        return new AnomalyDetectionResult(forBoost, forCapture);
    }

    internal bool ShouldCaptureAnomaly(string traceId)
    {
        // If trace is already in cache, it was already accepted — capture ALL spans of this trace
        // Matches Python: once a trace is flagged for capture, all its spans are captured
        if (this.traceCache.TryGetValue(traceId, out _))
        {
            return true;
        }

        // Rate-limit new traces
        if (this.rateLimiter != null)
        {
            using var lease = this.rateLimiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                return false;
            }
        }

        // Mark this trace as accepted for capture
        this.traceCache.Set(traceId, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TraceCacheTtl,
            Size = 1,
        });

        return true;
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

        bool isAnomaly = false;

        var errorCodeRegex = condition.ErrorCodeRegex;
        if (errorCodeRegex != null)
        {
            var statusCode = this.GetHttpStatusCode(span);
            if (statusCode != null)
            {
                isAnomaly = this.MatchesErrorCode(errorCodeRegex, statusCode.Value);
            }
        }

        var highLatencyMs = condition.HighLatencyMs;
        if (highLatencyMs.HasValue)
        {
            bool latencyMatch = span.Duration.TotalMilliseconds >= highLatencyMs.Value;

            // If both error code and latency defined, both must agree (matches Python)
            isAnomaly = (errorCodeRegex == null || isAnomaly) && latencyMatch;
        }

        return isAnomaly;
    }

    private bool MatchesErrorCode(string regex, int statusCode)
    {
        try
        {
            string anchored = regex.StartsWith("^") && regex.EndsWith("$") ? regex : $"^(?:{regex})$";
            return Regex.IsMatch(statusCode.ToString(), anchored);
        }
        catch
        {
            return false;
        }
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
