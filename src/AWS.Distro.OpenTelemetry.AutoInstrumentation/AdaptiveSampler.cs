// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Encapsulates adaptive sampling logic matching the Python/Java pattern where
/// the sampler owns anomaly detection, rate limiting, trace caching, and capture.
/// The metrics processor calls AdaptSampling(span) and the sampler handles everything.
/// </summary>
internal sealed class AdaptiveSampler
{
    private readonly AnomalyDetector anomalyDetector;
    private Action<Activity>? spanBatcher;

    internal AdaptiveSampler(AdaptiveSamplingConfig config)
    {
        this.anomalyDetector = new AnomalyDetector(config);
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

        this.anomalyDetector.RecordTrace();

        if (this.anomalyDetector.IsAnomaly(span))
        {
            this.anomalyDetector.RecordAnomaly(isSampled);

            if (!isSampled && this.spanBatcher != null && this.anomalyDetector.ShouldCaptureAnomaly(traceId))
            {
                this.spanBatcher(span);
            }
        }
    }

    internal BoostStatistics SnapshotAndResetStatistics()
    {
        return this.anomalyDetector.SnapshotAndResetStatistics();
    }
}
