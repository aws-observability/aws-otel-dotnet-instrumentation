// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>Emits a single captured snapshot to a downstream sink (OTLP logs).</summary>
internal interface IDISnapshotEmitter
{
    /// <summary>Emits the given capture.</summary>
    /// <param name="capture">The capture to emit.</param>
    void Emit(PendingCapture capture);
}
