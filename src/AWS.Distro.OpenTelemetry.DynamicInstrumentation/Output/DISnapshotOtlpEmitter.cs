// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// Emits DI snapshots as OTLP LogRecords via a dedicated, isolated LoggerProvider.
/// Not shared with the application's logging pipeline.
/// </summary>
internal sealed class DISnapshotOtlpEmitter : IDISnapshotEmitter, IDisposable
{
    private const string EventName = "aws.dynamic_instrumentation.snapshot";
    private const string ScopeName = "aws.dynamic_instrumentation";

    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly InstrumentationRegistry? registry;

    public DISnapshotOtlpEmitter(ILoggerFactory loggerFactory, InstrumentationRegistry? registry = null)
    {
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger(ScopeName);
        this.registry = registry;
    }

    /// <summary>
    /// Creates an emitter with a real OTLP exporter pointing to the configured endpoint.
    /// </summary>
    /// <param name="logsEndpoint">The OTLP logs endpoint; no exporter is added when null/empty.</param>
    /// <param name="registry">The registry used to enrich snapshots with config metadata.</param>
    /// <returns>A configured emitter.</returns>
    public static DISnapshotOtlpEmitter Create(string? logsEndpoint, InstrumentationRegistry? registry)
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = false;

                // Intentional: with no endpoint configured, no exporter is attached, so snapshots are
                // dropped (not buffered) by the no-op logging pipeline. This is the documented operator
                // trap — see "Snapshots require OTEL_AWS_OTLP_LOGS_ENDPOINT" in docs/dynamic-instrumentation.md.
                // A startup warning for the unset case is deferred to the DI hardening pass (PR4); it needs
                // the base agent's diagnostic logger, which is not plumbed into the DI subsystem here.
                if (!string.IsNullOrEmpty(logsEndpoint))
                {
                    options.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(logsEndpoint);

                        // Bound each snapshot export so a wedged endpoint (accepts the connection but never
                        // responds) can't block the drain thread indefinitely and let the capture queue grow
                        // unbounded. Without this the exporter uses the OTel default, which is not guaranteed
                        // to fail fast on a half-open connection. 10s matches the OTLP/HTTP spec default.
                        otlp.TimeoutMilliseconds = 10_000;
                    });
                }
            });
        });

        return new DISnapshotOtlpEmitter(factory, registry);
    }

    public void Emit(PendingCapture capture)
    {
        var reg = this.registry?.Get(capture.InstrumentationKey);
        var config = reg?.Config;

        var level = capture.Type == CaptureType.METHOD ? "method" : "line";

        this.logger.Log(
            LogLevel.Information,
            new EventId(0, EventName),
            new SnapshotLogState(capture, config, level),
            null,
            (state, _) => FormatBody(state));
    }

    public void Dispose()
    {
        this.loggerFactory.Dispose();
    }

    private static string FormatBody(SnapshotLogState state)
    {
        var capture = state.Capture;
        var body = new Dictionary<string, object?>();

        // Captures section.
        var captures = new Dictionary<string, object?>();

        if (capture.Arguments != null)
        {
            captures["entry"] = new Dictionary<string, object?>
            {
                ["arguments"] = SerializeValueDict(capture.Arguments),
            };
        }

        if (capture.ReturnValue != null)
        {
            captures["return"] = new Dictionary<string, object?>
            {
                ["return_value"] = SerializeValue(capture.ReturnValue),
            };
        }

        if (capture.Locals != null && capture.LineNumber > 0)
        {
            captures["lines"] = new Dictionary<string, object?>
            {
                [capture.LineNumber.ToString()] = new Dictionary<string, object?>
                {
                    ["locals"] = SerializeValueDict(capture.Locals),
                },
            };
        }

        body["captures"] = captures;

        // Stack section. Keys are file_path/function/line_number to match the Java/Python OTLP body schema
        // (the CloudWatch backend and cross-SDK consumers expect these exact keys).
        if (capture.StackTrace != null)
        {
            body["stack"] = capture.StackTrace.Select(f => new Dictionary<string, object?>
            {
                ["file_path"] = f.FileName,
                ["function"] = f.MethodName,
                ["line_number"] = f.LineNumber,
            }).ToArray();
        }

        return JsonSerializer.Serialize(body);
    }

    private static Dictionary<string, object?> SerializeValueDict(Dictionary<string, CapturedValue> dict) =>
        dict.ToDictionary(kv => kv.Key, kv => (object?)SerializeValue(kv.Value));

    // Mirrors the Java/Python OTLP body contract: a captured value is `type` plus EXACTLY ONE of
    // is_null / not_captured_reason / value(+truncated,+size) / fields / elements(+size). Without this the
    // body was lossy — objects (fields), collections (elements), nulls, and limit reasons all collapsed to
    // a bare {type,value}, diverging from the sibling SDKs and dropping capture detail.
    private static Dictionary<string, object?> SerializeValue(CapturedValue v)
    {
        var map = new Dictionary<string, object?> { ["type"] = v.Type };

        // Order matches Java's capturedValueToValue exactly-one-of contract. Note a *truncated* collection/
        // object still serializes as elements/fields WITH size (Java's ofCollection(type,elements,length)) —
        // its NotCapturedReason (CollectionSize) is NOT emitted as not_captured_reason; the size vs element
        // count conveys the truncation. not_captured_reason is only for values with NO partial data to emit
        // (Depth/Timeout/AlreadyCaptured), where Fields/Elements are null.
        if (v.Type == "null")
        {
            // ValueSerializer encodes a null as Type == "null"; emit the is_null variant to match siblings.
            map["is_null"] = true;
        }
        else if (v.Fields != null)
        {
            map["fields"] = SerializeValueDict(v.Fields);
            if (v.OriginalSize.HasValue)
            {
                map["size"] = v.OriginalSize.Value;
            }
        }
        else if (v.Elements != null)
        {
            map["elements"] = v.Elements.Select(SerializeValue).ToArray();
            if (v.OriginalSize.HasValue)
            {
                map["size"] = v.OriginalSize.Value;
            }
        }
        else if (v.NotCapturedReason != NotCapturedReason.None)
        {
            map["not_captured_reason"] = ToWireReason(v.NotCapturedReason);
        }
        else if (v.Value != null)
        {
            map["value"] = v.Value;
            if (v.Truncated)
            {
                map["truncated"] = true;
            }
        }

        return map;
    }

    // Backend InstrumentationErrorCause-style wire strings, matching the Java/Python NotCapturedReason.name()
    // values (DEPTH / FIELD_COUNT / COLLECTION_SIZE / TIMEOUT / ALREADY_CAPTURED).
    private static string ToWireReason(NotCapturedReason reason) => reason switch
    {
        NotCapturedReason.Depth => "DEPTH",
        NotCapturedReason.FieldCount => "FIELD_COUNT",
        NotCapturedReason.CollectionSize => "COLLECTION_SIZE",
        NotCapturedReason.Timeout => "TIMEOUT",
        NotCapturedReason.AlreadyCaptured => "ALREADY_CAPTURED",
        _ => reason.ToString(),
    };
}
