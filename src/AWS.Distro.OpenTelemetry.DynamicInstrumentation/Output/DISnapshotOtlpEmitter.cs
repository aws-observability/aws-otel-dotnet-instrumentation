// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
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

                // BEFORE AddOtlpExporter, and the order is load-bearing: processors run in registration
                // order, and the exporter is itself the last processor in that chain. Registered after it,
                // this would stamp records the exporter had already serialized.
                options.AddProcessor(new SnapshotTraceContextProcessor());

                // No endpoint => no exporter => snapshots are dropped (not buffered). NOT reachable from
                // DynamicInstrumentationConfig.FromEnvironment, which now defaults the endpoint rather than
                // leaving it unset; kept for callers that construct a config directly (tests, and any future
                // in-process host) so a null endpoint degrades to "capture, don't export" instead of throwing
                // inside the Uri constructor.
                if (!string.IsNullOrEmpty(logsEndpoint))
                {
                    options.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(logsEndpoint);

                        // PINNED, and deliberately NOT read from OTEL_EXPORTER_OTLP_PROTOCOL or
                        // OTEL_EXPORTER_OTLP_LOGS_PROTOCOL.
                        //
                        // The other DI SDKs pin the transport by CHOOSING THE HTTP EXPORTER TYPE, so none of
                        // them exposes a protocol knob for snapshots: Java builds
                        // OtlpHttpLogRecordExporter.builder().setEndpoint(...) in
                        // DynamicInstrumentationManager, Python imports OTLPLogExporter from
                        // opentelemetry.exporter.otlp.proto.http._log_exporter in _snapshot_otlp_emitter.py,
                        // and JS imports it from @opentelemetry/exporter-logs-otlp-http. Reading the variable
                        // here would make .NET the only SDK where a generic distro-wide setting — set for
                        // traces and metrics — silently redirects snapshots onto a transport the snapshot
                        // consumer does not accept.
                        //
                        // Setting it explicitly is required regardless, because unlike those three we get the
                        // protocol from an options object rather than from the exporter type, and the SDK's own
                        // default is OTLP/gRPC. Measured with no protocol set, exporting to
                        // http://127.0.0.1:PORT/v1/logs put "PRI * HTTP/2.0" (the HTTP/2 preface for gRPC) on
                        // the wire against the HTTP endpoint the docs tell operators to configure, and every
                        // snapshot was silently lost.
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;

                        // Bound each export so a wedged endpoint can't block the drain thread and grow the
                        // queue unboundedly. 10s matches the OTLP/HTTP spec default.
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
