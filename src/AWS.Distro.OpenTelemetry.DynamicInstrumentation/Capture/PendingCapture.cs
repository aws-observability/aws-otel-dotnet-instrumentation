// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

internal sealed class PendingCapture
{
    public CaptureType Type { get; init; }

    public string InstrumentationKey { get; init; } = string.Empty;

    public string LocationHash { get; init; } = string.Empty;

    public long TimestampMs { get; init; }

    public long DurationMs { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public int ThreadId { get; init; }

    public string? ThreadName { get; init; }

    public Dictionary<string, CapturedValue>? Arguments { get; init; }

    public CapturedValue? ReturnValue { get; init; }

    public CapturedValue? Exception { get; init; }

    public Dictionary<string, CapturedValue>? Locals { get; init; }

    public int LineNumber { get; init; }

    public StackFrameInfo[]? StackTrace { get; init; }
}
