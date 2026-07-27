// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Log state carrying snapshot attributes for the OTLP LogRecord.
/// </summary>
internal sealed class SnapshotLogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> attributes;

    public SnapshotLogState(PendingCapture capture, InstrumentationConfiguration? config, string level)
    {
        this.Capture = capture;
        this.attributes = new List<KeyValuePair<string, object?>>
        {
            new("event.name", "aws.dynamic_instrumentation.snapshot"),
            new("aws.di.snapshot_id", Guid.NewGuid().ToString()),
            new("aws.di.location_hash", capture.LocationHash),
            new("aws.di.instrumentation_level", level),

            // config is null only for an orphaned capture: one enqueued while its config was live, then
            // drained after the config was removed (registry.Get returns null). "PROBE" is the safe default
            // — it is the backend's default instrumentation type and the common case, so an orphaned snapshot
            // is attributed to the more-likely kind rather than dropped or tagged with an error sentinel.
            new("aws.di.instrumentation_type", config?.Type.ToString() ?? "PROBE"),
            new("aws.di.code_unit", config?.CodeUnit ?? string.Empty),
            new("aws.di.class_name", config?.ClassName ?? string.Empty),
            new("aws.di.method_name", config?.MethodName ?? string.Empty),
            new("aws.di.file_path", config?.FilePath ?? string.Empty),
            new("aws.di.line_number", capture.LineNumber),
            new("aws.di.duration_ms", capture.DurationMs),
            new("aws.di.thread_id", capture.ThreadId),
            new("aws.di.thread_name", capture.ThreadName ?? string.Empty),
        };
    }

    public PendingCapture Capture { get; }

    public int Count => this.attributes.Count;

    public KeyValuePair<string, object?> this[int index] => this.attributes[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => this.attributes.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
