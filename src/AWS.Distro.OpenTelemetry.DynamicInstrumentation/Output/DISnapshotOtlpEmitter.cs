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
    private const string ScopeVersion = "1.0";

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
    /// <param name="diagnosticsLogger">
    /// Sink for the exporter's own failures. NOT the logger snapshots travel on — that one is the isolated
    /// factory built below, and logging export failures onto it would feed the failing export itself.
    /// </param>
    /// <returns>A configured emitter.</returns>
    public static DISnapshotOtlpEmitter Create(
        string? logsEndpoint,
        InstrumentationRegistry? registry,
        ILogger? diagnosticsLogger = null)
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = false;

                // BEFORE the exporter, and the order is load-bearing: processors run in registration order,
                // and the exporter is itself the last processor in that chain. Registered after it, this
                // would stamp records the exporter had already serialized.
                options.AddProcessor(new SnapshotTraceContextProcessor());

                // No endpoint => no exporter => snapshots are dropped (not buffered). NOT reachable from
                // DynamicInstrumentationConfig.FromEnvironment, which now defaults the endpoint rather than
                // leaving it unset; kept for callers that construct a config directly (tests, and any future
                // in-process host) so a null endpoint degrades to "capture, don't export" instead of throwing
                // inside the Uri constructor.
                if (!string.IsNullOrEmpty(logsEndpoint))
                {
                    // DiOtlpLogExporter rather than AddOtlpExporter: the stock exporter can only ship the
                    // capture tree as one JSON string, because OTel .NET's LogRecord.Body is string-only.
                    //
                    // This also makes the transport OTLP/HTTP-protobuf BY CONSTRUCTION rather than by setting
                    // OtlpExporterOptions.Protocol, so OTEL_EXPORTER_OTLP_PROTOCOL can no longer redirect
                    // snapshots onto gRPC. Java, Python and JS pin it the same way — by choosing the HTTP
                    // exporter type — and expose no protocol knob for snapshots either.
                    options.AddProcessor(new global::OpenTelemetry.BatchLogRecordExportProcessor(
                        new DiOtlpLogExporter(logsEndpoint, ScopeName, ScopeVersion, diagnosticsLogger)));
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

        var logState = new SnapshotLogState(capture, config, level, FormatBody(capture));

        // The formatter returns the already-serialized tree rather than rebuilding it, so the body is
        // serialized exactly once per snapshot however the SDK is configured.
        this.logger.Log(
            LogLevel.Information,
            new EventId(0, EventName),
            logState,
            null,
            static (state, _) => state.BodyJson);
    }

    public void Dispose()
    {
        this.loggerFactory.Dispose();
    }

    private static string FormatBody(PendingCapture capture)
    {
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

        // Exit capture: return value and/or thrown exception both go under the "return" block. Emitting
        // `throwable` ensures a faulted method's snapshot carries the failure, not just a missing return.
        if (capture.ReturnValue != null || capture.Exception != null)
        {
            var exit = new Dictionary<string, object?>();
            if (capture.ReturnValue != null)
            {
                exit["return_value"] = SerializeValue(capture.ReturnValue);
            }

            if (capture.Exception != null)
            {
                exit["throwable"] = SerializeThrowable(capture.Exception);
            }

            captures["return"] = exit;
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
            body["stack"] = SerializeStack(capture.StackTrace);
        }

        return JsonSerializer.Serialize(body);
    }

    private static Dictionary<string, object?> SerializeValueDict(Dictionary<string, CapturedValue> dict) =>
        dict.ToDictionary(kv => kv.Key, kv => (object?)SerializeValue(kv.Value));

    // Shared frame shape for the entry-time stack and a throwable's stacktrace, so the two can't drift.
    // Keys are file_path/function/line_number to match the OTLP snapshot body schema.
    private static Dictionary<string, object?>[] SerializeStack(StackFrameInfo[] frames) =>
        frames.Select(f => new Dictionary<string, object?>
        {
            ["file_path"] = f.FileName,
            ["function"] = f.MethodName,
            ["line_number"] = f.LineNumber,
        }).ToArray();

    // A captured exception: type + message (already truncated at capture time) + its own filtered/capped
    // stack frames, using the same file_path/function/line_number frame keys as the entry-time stack.
    private static Dictionary<string, object?> SerializeThrowable(CapturedValue exception)
    {
        var map = new Dictionary<string, object?>
        {
            ["type"] = exception.Type,
            ["message"] = exception.Value,
        };

        if (exception.Truncated)
        {
            map["truncated"] = true;
        }

        if (exception.StackFrames != null)
        {
            map["stacktrace"] = SerializeStack(exception.StackFrames);
        }

        return map;
    }

    // A captured value is `type` plus EXACTLY ONE of: is_null / not_captured_reason / value(+truncated) /
    // fields / elements(+size). Order matters. A *truncated* collection/object still emits elements/fields
    // WITH size (the size vs count conveys truncation) — not_captured_reason is only for values with no
    // partial data to emit (Depth/Timeout/AlreadyCaptured).
    private static Dictionary<string, object?> SerializeValue(CapturedValue v)
    {
        var map = new Dictionary<string, object?> { ["type"] = v.Type };

        if (v.Type == "null")
        {
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
