// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

public class DISnapshotOtlpEmitterTests
{
    [Fact]
    public void Emit_ProducesLogWithCorrectEventName()
    {
        var logs = new List<string>();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs)));
        var emitter = new DISnapshotOtlpEmitter(factory);

        var capture = new PendingCapture
        {
            Type = CaptureType.METHOD,
            InstrumentationKey = "MyApp.Svc.Run",
            LocationHash = "aabb001",
            TimestampMs = 1000,
            DurationMs = 5,
            ThreadId = 1,
            ThreadName = "main",
            Arguments = new Dictionary<string, CapturedValue>
            {
                ["orderId"] = new CapturedValue { Type = "System.String", Value = "ORD-123" }
            },
            ReturnValue = new CapturedValue { Type = "System.Int32", Value = "42" }
        };

        emitter.Emit(capture);

        logs.Should().HaveCount(1);
        logs[0].Should().Contain("ORD-123");
        logs[0].Should().Contain("42");
    }

    [Fact]
    public void Emit_IncludesStackTraceInBody()
    {
        var logs = new List<string>();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs)));
        var emitter = new DISnapshotOtlpEmitter(factory);

        var capture = new PendingCapture
        {
            Type = CaptureType.METHOD,
            InstrumentationKey = "MyApp.Svc.Run",
            LocationHash = "hash1",
            StackTrace = new[]
            {
                new StackFrameInfo("OrderService.cs", "MyApp.OrderService.Process", 42),
                new StackFrameInfo("Controller.cs", "MyApp.Controller.Handle", 10)
            }
        };

        emitter.Emit(capture);

        logs[0].Should().Contain("OrderService.cs");
        logs[0].Should().Contain("MyApp.OrderService.Process");
    }

    [Fact]
    public void Emit_HandlesNullArguments()
    {
        var logs = new List<string>();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs)));
        var emitter = new DISnapshotOtlpEmitter(factory);

        var capture = new PendingCapture
        {
            Type = CaptureType.METHOD,
            InstrumentationKey = "key",
            LocationHash = "hash",
            Arguments = null,
            ReturnValue = null
        };

        var act = () => emitter.Emit(capture);
        act.Should().NotThrow();
        logs.Should().HaveCount(1);

        // Assert on the body SHAPE, not just that something was logged: with no args/return/exception the
        // body must be well-formed JSON with an empty captures object and no phantom entry/return blocks.
        using var doc = System.Text.Json.JsonDocument.Parse(logs[0]);
        var captures = doc.RootElement.GetProperty("captures");
        captures.TryGetProperty("entry", out _).Should().BeFalse("no arguments => no entry block");
        captures.TryGetProperty("return", out _).Should().BeFalse("no return/exception => no return block");
    }

    [Fact]
    public void Emit_LineLevelCapture_IncludesLineNumber()
    {
        var logs = new List<string>();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs)));
        var emitter = new DISnapshotOtlpEmitter(factory);

        var capture = new PendingCapture
        {
            Type = CaptureType.LINE,
            InstrumentationKey = "MyApp.Svc.Run:42",
            LocationHash = "hash1",
            LineNumber = 42,
            Locals = new Dictionary<string, CapturedValue>
            {
                ["total"] = new CapturedValue { Type = "System.Double", Value = "99.99" }
            }
        };

        emitter.Emit(capture);

        logs[0].Should().Contain("42");
        logs[0].Should().Contain("99.99");
    }
}

// Exercises the REAL OpenTelemetry logging export pipeline (not a fake ILogger): the emitter's records
// flow through a genuine LoggerFactory + OpenTelemetry logging provider to an in-memory LogRecord exporter,
// so we assert on the actual exported LogRecord.Attributes and LogRecord.Body — the same objects an OTLP
// exporter would serialize. This closes the gap where prior tests only proved our JSON string was built,
// never that it survived the export path, and pins cross-SDK (Java/Python) body/attribute parity.
public class DISnapshotOtlpEmitterExportTests
{
    private static (DISnapshotOtlpEmitter Emitter, List<LogRecord> Exported, ILoggerFactory Factory) CreateRealPipeline()
    {
        var exported = new List<LogRecord>();
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;

                // Same order as DISnapshotOtlpEmitter.Create: the processor runs before the exporter.
                options.AddProcessor(new SnapshotTraceContextProcessor());
                options.AddInMemoryExporter(exported);
            });
        });
        return (new DISnapshotOtlpEmitter(factory), exported, factory);
    }

    private static string? AttrString(LogRecord record, string key)
    {
        var attr = record.Attributes?.FirstOrDefault(kv => kv.Key == key);
        return attr?.Value?.ToString();
    }

    [Fact]
    public void Export_SetsSnapshotAttributes_MatchingSiblingSchema()
    {
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.OrderService.Process",
                LocationHash = "loc-abc",
                DurationMs = 7,
                ThreadId = 3,
                ThreadName = "worker",
            });
        }

        exported.Should().HaveCount(1);
        var record = exported[0];

        // event.name + the aws.di.* attribute keys shared with Java/Python.
        AttrString(record, "event.name").Should().Be("aws.dynamic_instrumentation.snapshot");
        AttrString(record, "aws.di.location_hash").Should().Be("loc-abc");
        AttrString(record, "aws.di.instrumentation_level").Should().Be("method");
        AttrString(record, "aws.di.instrumentation_type").Should().Be("PROBE");
        record.Attributes.Should().Contain(kv => kv.Key == "aws.di.snapshot_id");
        record.Attributes.Should().Contain(kv => kv.Key == "aws.di.duration_ms");
    }

    [Fact]
    public void Export_StackFrames_UseSiblingBodyKeys()
    {
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                StackTrace = new[] { new StackFrameInfo("Order.cs", "MyApp.Svc.Run", 12) },
            });
        }

        var body = exported.Single().Body ?? string.Empty;

        // The parity break this fix closes: keys must be file_path/function/line_number (Java/Python),
        // NOT the old file/method/line.
        body.Should().Contain("file_path").And.Contain("function").And.Contain("line_number");
        body.Should().NotContain("\"file\":").And.NotContain("\"method\":");
    }

    [Fact]
    public void Export_CapturedValueVariants_MatchExactlyOneOfContract()
    {
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                Arguments = new Dictionary<string, CapturedValue>
                {
                    // object → fields
                    ["obj"] = new CapturedValue
                    {
                        Type = "MyApp.Order",
                        Fields = new Dictionary<string, CapturedValue>
                        {
                            ["id"] = new CapturedValue { Type = "System.String", Value = "ORD-1" },
                        },
                    },

                    // truncated collection → elements + size (NOT not_captured_reason — matches Java's
                    // ofCollection: partial data is emitted with size conveying the real length).
                    ["list"] = new CapturedValue
                    {
                        Type = "System.Collections.Generic.List`1",
                        Elements = new[] { new CapturedValue { Type = "System.Int32", Value = "1" } },
                        OriginalSize = 100,
                        NotCapturedReason = NotCapturedReason.CollectionSize,
                    },

                    // value with NO partial data + a limit reason → not_captured_reason (Depth).
                    ["deep"] = new CapturedValue
                    {
                        Type = "MyApp.Nested",
                        NotCapturedReason = NotCapturedReason.Depth,
                    },

                    // null → is_null
                    ["missing"] = new CapturedValue { Type = "null", Value = "null" },
                },
            });
        }

        var body = exported.Single().Body ?? string.Empty;

        body.Should().Contain("fields", "objects serialize as fields");
        body.Should().Contain("elements", "collections serialize as elements");
        body.Should().Contain("\"size\":100", "a truncated collection carries its real size");
        body.Should().Contain("is_null", "a null value serializes as is_null");
        body.Should().Contain("DEPTH", "a value with no partial data carries its not_captured_reason wire string");
        body.Should().Contain("not_captured_reason");
        // A collection with partial elements must NOT also emit not_captured_reason (exactly-one-of).
        body.Should().NotContain("COLLECTION_SIZE");
    }

    [Fact]
    public void Export_FieldCountTruncatedObject_CarriesSizeOnTheWire()
    {
        // The `fields` branch is evaluated before `not_captured_reason`, so size is the truncation signal.
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                Arguments = new Dictionary<string, CapturedValue>
                {
                    ["obj"] = new CapturedValue
                    {
                        Type = "MyApp.Order",
                        Fields = new Dictionary<string, CapturedValue>
                        {
                            ["id"] = new CapturedValue { Type = "System.String", Value = "ORD-1" },
                        },
                        OriginalSize = 50,
                        NotCapturedReason = NotCapturedReason.FieldCount,
                    },
                },
            });
        }

        var body = exported.Single().Body ?? string.Empty;

        body.Should().Contain("fields", "partial field data is still emitted");
        body.Should().Contain("\"size\":50", "a field-count-truncated object carries its real member count");

        // Exactly-one-of: partial data present means the reason is NOT also emitted (same contract the
        // truncated-collection case follows).
        body.Should().NotContain("FIELD_COUNT");
    }

    [Fact]
    public void Export_FaultedMethod_EmitsThrowableInBody()
    {
        // A probe on a method that threw must carry the exception in the snapshot body (type + message +
        // its own stack frames), not just a missing return. Regression guard: the emitter previously
        // populated PendingCapture.Exception at capture time but never read it, so faulted-method snapshots
        // silently dropped the failure — a parity break vs Java/Python.
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                Exception = new CapturedValue
                {
                    Type = "System.InvalidOperationException",
                    Value = "order already shipped",
                    StackFrames = new[] { new StackFrameInfo("OrderService.cs", "MyApp.OrderService.Ship", 88) },
                },
            });
        }

        var body = exported.Single().Body ?? string.Empty;
        body.Should().Contain("throwable");
        body.Should().Contain("System.InvalidOperationException");
        body.Should().Contain("order already shipped");
        // Exception frames use the same sibling body keys as the entry stack.
        body.Should().Contain("MyApp.OrderService.Ship").And.Contain("file_path");
    }

    [Fact]
    public void Export_TraceContext_SetOnNativeLogRecordFields_FromCapturedIds()
    {
        // The native fields are what the backend correlates on; the aws.di.* attributes are only carriers.
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                TraceId = "0af7651916cd43dd8448eb211c80319c",
                SpanId = "b7ad6b7169203331",
            });
        }

        var record = exported.Single();
        record.TraceId.ToHexString().Should().Be("0af7651916cd43dd8448eb211c80319c");
        record.SpanId.ToHexString().Should().Be("b7ad6b7169203331");

        // A snapshot only exists because a probe fired, so the record is sampled-in by definition.
        record.TraceFlags.Should().Be(ActivityTraceFlags.Recorded);

        // The attributes were only carriers; the values now live on the native fields exactly once.
        record.Attributes.Should().NotContain(kv => kv.Key == "aws.di.trace_id");
        record.Attributes.Should().NotContain(kv => kv.Key == "aws.di.span_id");
    }

    [Fact]
    public void Export_NoTraceContext_LeavesNativeTraceFieldsDefault()
    {
        // Untraced call: the native fields stay default.
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                TraceId = null,
                SpanId = null,
            });
        }

        var record = exported.Single();
        record.TraceId.Should().Be(default(ActivityTraceId));
        record.SpanId.Should().Be(default(ActivitySpanId));
        record.Attributes.Should().NotContain(kv => kv.Key == "aws.di.trace_id");
        record.Attributes.Should().NotContain(kv => kv.Key == "aws.di.span_id");
    }

    [Fact]
    public void Export_CaptureTimestamp_SetOnNativeTimestamp_NotEmitTime()
    {
        // Timestamp must carry the capture instant, not the drain thread's emit time.
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                TimestampMs = 1_785_000_000_000,
            });
        }

        var record = exported.Single();
        record.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_785_000_000_000).UtcDateTime);
        record.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        record.Attributes.Should().NotContain(kv => kv.Key == "aws.di.timestamp_ms");
    }

    [Fact]
    public void Export_ReturnAndException_BothPresent_CoexistWithoutClobber()
    {
        // Defensive: if a capture ever carries BOTH a return value and an exception, the exit block must
        // contain both keys — neither overwrites the other. (In practice a faulted method has a null return,
        // but the emitter must not silently drop one if both are set.)
        var (emitter, exported, factory) = CreateRealPipeline();
        using (factory)
        {
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "h",
                ReturnValue = new CapturedValue { Type = "System.Int32", Value = "7" },
                Exception = new CapturedValue { Type = "System.Exception", Value = "boom" },
            });
        }

        var body = exported.Single().Body ?? string.Empty;
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var ret = doc.RootElement.GetProperty("captures").GetProperty("return");
        ret.TryGetProperty("return_value", out _).Should().BeTrue("return value must survive alongside throwable");
        ret.TryGetProperty("throwable", out _).Should().BeTrue("throwable must survive alongside return_value");
    }
}

// Simple test logger that captures formatted messages
internal class TestLoggerProvider : ILoggerProvider
{
    private readonly List<string> _logs;
    public TestLoggerProvider(List<string> logs) => _logs = logs;
    public ILogger CreateLogger(string categoryName) => new TestLogger(_logs);
    public void Dispose() { }
}

internal class TestLogger : ILogger
{
    private readonly List<string> _logs;
    public TestLogger(List<string> logs) => _logs = logs;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _logs.Add(formatter(state, exception));
    }
}
