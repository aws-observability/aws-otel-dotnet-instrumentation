// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal sealed class AdaptiveSamplingConfig
{
    public double Version { get; set; }

    public List<AnomalyCondition>? AnomalyConditions { get; set; }

    public AnomalyCaptureLimit? AnomalyCaptureLimit { get; set; }
}
