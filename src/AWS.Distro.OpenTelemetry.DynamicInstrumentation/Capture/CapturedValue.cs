// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

internal sealed class CapturedValue
{
    public string Type { get; init; } = string.Empty;

    public string? Value { get; init; }

    public bool Truncated { get; init; }

    /// <summary>Gets why this value was not fully captured; <see cref="NotCapturedReason.None"/> if it was.</summary>
    public NotCapturedReason NotCapturedReason { get; init; } = NotCapturedReason.None;

    public Dictionary<string, CapturedValue>? Fields { get; init; }

    public CapturedValue[]? Elements { get; init; }

    public int? OriginalSize { get; init; }

    /// <summary>
    /// Gets the internal-frame-filtered, capped stack frames for a captured exception, or null
    /// when this value is not an exception. Preferred over storing the raw StackTrace string.
    /// </summary>
    public StackFrameInfo[]? StackFrames { get; init; }
}
