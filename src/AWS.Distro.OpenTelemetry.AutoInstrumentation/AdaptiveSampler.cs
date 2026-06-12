// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry.Sampler.AWS;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Encapsulates adaptive sampling logic. Resolves the effective rule via xrsr
/// tracestate lookup, gates on hasBoost(), and delegates per-rule stats to
/// SamplingRuleApplier.
/// </summary>
internal sealed class AdaptiveSampler
{
    private readonly AnomalyDetector anomalyDetector;
    private readonly AWSXRayRemoteSampler sampler;
    private Action<Activity>? spanBatcher;

    internal AdaptiveSampler(AdaptiveSamplingConfig config, AWSXRayRemoteSampler sampler)
    {
        this.anomalyDetector = new AnomalyDetector(config);
        this.sampler = sampler;
    }

    internal AnomalyDetector AnomalyDetector => this.anomalyDetector;

    internal void SetSpanBatcher(Action<Activity> batcher)
    {
        this.spanBatcher = batcher;
    }

    internal void AdaptSampling(Activity span)
    {
        bool isSampled = span.Recorded && span.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded);
        string traceId = span.TraceId.ToString();

        // Resolve effective rule: xrsr hash lookup → local match for root spans → null
        var effectiveApplier = this.ResolveEffectiveApplier(span);

        // Gate on hasBoost() — only count stats for boost-eligible rules
        if (effectiveApplier?.Rule.HasBoost() == true)
        {
            effectiveApplier.CountTrace(traceId);
        }

        bool defaultDisabled = effectiveApplier?.Rule.IsDefaultAnomalyDetectionDisabled() ?? false;
        var match = this.anomalyDetector.GetAnomalyMatch(span, defaultDisabled);
        if (match == null)
        {
            return;
        }

        // Boost stats path — per-rule
        if (match.ForBoost && effectiveApplier?.Rule.HasBoost() == true)
        {
            effectiveApplier.CountAnomalyTrace(isSampled);
        }

        // Capture path
        if (match.ForCapture && !isSampled && this.spanBatcher != null && this.anomalyDetector.ShouldCaptureAnomaly(traceId))
        {
            this.spanBatcher(span);
        }
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

    private SamplingRuleApplier? ResolveEffectiveApplier(Activity span)
    {
        string? xrsrHash = GetTraceStateValue(span.TraceStateString, "xrsr");
        if (xrsrHash != null)
        {
            var applier = this.sampler.GetRuleApplierByHash(xrsrHash);
            if (applier != null)
            {
                return applier;
            }
        }

        bool isRootSpan = span.Parent == null && !span.HasRemoteParent && span.ParentId == null;
        if (isRootSpan)
        {
            var samplingParams = new SamplingParameters(
                default,
                span.TraceId,
                span.DisplayName,
                span.Kind,
                span.TagObjects,
                null);
            return this.sampler.GetMatchedRule(in samplingParams);
        }

        return null;
    }
}
