// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Span processor that invokes adaptive sampling logic on span end.
/// Registers as a processor on the TracerProvider so it receives all ended spans.
/// Delegates to AdaptiveSampler for anomaly detection, boost stats, and capture.
/// </summary>
internal sealed class AdaptiveSamplingSpanProcessor : BaseProcessor<Activity>
{
    private readonly AdaptiveSampler adaptiveSampler;

    internal AdaptiveSamplingSpanProcessor(AdaptiveSampler adaptiveSampler)
    {
        this.adaptiveSampler = adaptiveSampler;
    }

    public override void OnEnd(Activity activity)
    {
        this.adaptiveSampler.AdaptSampling(activity);
    }
}
