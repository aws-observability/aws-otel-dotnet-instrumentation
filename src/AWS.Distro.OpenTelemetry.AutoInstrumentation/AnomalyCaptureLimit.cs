// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal sealed class AnomalyCaptureLimit
{
    public int AnomalyTracesPerSecond { get; set; } = 1;
}
