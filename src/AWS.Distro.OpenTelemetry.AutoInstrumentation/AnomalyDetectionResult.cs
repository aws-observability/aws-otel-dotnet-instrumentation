// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal sealed class AnomalyDetectionResult
{
    internal AnomalyDetectionResult(bool forBoost, bool forCapture)
    {
        this.ForBoost = forBoost;
        this.ForCapture = forCapture;
    }

    internal bool ForBoost { get; }

    internal bool ForCapture { get; }
}
