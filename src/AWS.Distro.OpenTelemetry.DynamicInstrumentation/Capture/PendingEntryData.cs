// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

/// <summary>
/// Data captured at method entry, held until method exit.
/// </summary>
internal sealed class PendingEntryData
{
    public string InstrumentationKey { get; init; } = string.Empty;

    public string LocationHash { get; init; } = string.Empty;

    public long StartTimestamp { get; init; }

    public Dictionary<string, CapturedValue>? Arguments { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public int ThreadId { get; init; }

    public string? ThreadName { get; init; }

    public StackFrameInfo[]? StackTrace { get; init; }
}
