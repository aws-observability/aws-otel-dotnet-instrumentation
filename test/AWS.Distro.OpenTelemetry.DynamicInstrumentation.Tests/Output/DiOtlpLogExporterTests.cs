// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;
using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

/// <summary>
/// Wire-level tests for <see cref="DiOtlpLogExporter"/>: the bytes that reach the socket are decoded back
/// into OTLP messages and asserted on.
/// </summary>
// WHY WIRE-LEVEL. The whole reason this exporter exists is a body shape the stock OTLP exporter cannot
// produce, and that shape is only observable in the encoded payload. Asserting on the emitter's JSON string
// would pass identically with AddOtlpExporter, which is the bug this replaces.
public class DiOtlpLogExporterTests
{
    private const string ScopeName = "aws.dynamic_instrumentation";
    private const string EventName = "aws.dynamic_instrumentation.snapshot";

    [Fact]
    public void Export_SnapshotBody_IsAKvListNotAString()
    {
        using var server = new OtlpTestServer();
        EmitOneSnapshot(server.Endpoint, new PendingCapture
        {
            Type = CaptureType.METHOD,
            InstrumentationKey = "MyApp.OrderService.Process",
            LocationHash = "loc-abc",
            Arguments = new Dictionary<string, CapturedValue>
            {
                ["orderId"] = new CapturedValue { Type = "System.String", Value = "ORD-123" },
            },
        });

        var record = server.SingleRecord();

        // The parity fix: a walkable tree (captures.entry.arguments.orderId.value), not one opaque string.
        var body = record.Body.Should().BeOfType<Dictionary<string, object?>>().Subject;
        var arguments = Walk(body, "captures", "entry", "arguments");
        var orderId = arguments["orderId"].Should().BeOfType<Dictionary<string, object?>>().Subject;
        orderId["value"].Should().Be("ORD-123");
        orderId["type"].Should().Be("System.String");
    }

    [Fact]
    public void Export_BodyCarrierAttribute_IsDroppedFromAttributes()
    {
        using var server = new OtlpTestServer();
        EmitOneSnapshot(server.Endpoint, NewCapture());

        var record = server.SingleRecord();

        // The emitter smuggles the tree through a `body` attribute because LogRecord.Body is string-only.
        // Leaving it there would ship the whole snapshot twice, once structured and once as a string.
        record.Attributes.Should().NotContainKey("body");
        record.Attributes.Should().ContainKey("aws.di.location_hash");
    }

    [Fact]
    public void Export_EventName_IsSetOnTheTopLevelField_NotOnlyAsAnAttribute()
    {
        using var server = new OtlpTestServer();
        EmitOneSnapshot(server.Endpoint, NewCapture());

        var record = server.SingleRecord();

        // Consumers filter incidents on the top-level event_name field, which is why this must be protobuf:
        // the CloudWatch OTLP endpoint strips that field from OTLP/JSON. The attribute is kept too.
        record.EventName.Should().Be(EventName);
        record.Attributes["event.name"].Should().Be(EventName);
    }

    [Fact]
    public void Export_StampsTheDiInstrumentationScope()
    {
        using var server = new OtlpTestServer();
        EmitOneSnapshot(server.Endpoint, NewCapture());

        server.Payloads.Should().HaveCount(1);
        var (scopeName, scopeVersion, _) = OtlpLogsDataDecoder.Decode(server.Payloads[0]);
        scopeName.Should().Be(ScopeName);
        scopeVersion.Should().Be("1.0");
    }

    [Fact]
    public void Export_TraceContext_TravelsAsRawBytes()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string spanId = "00f067aa0ba902b7";

        using var server = new OtlpTestServer();
        var capture = NewCapture();
        EmitOneSnapshot(server.Endpoint, new PendingCapture
        {
            Type = CaptureType.METHOD,
            InstrumentationKey = capture.InstrumentationKey,
            LocationHash = capture.LocationHash,
            TraceId = traceId,
            SpanId = spanId,
        });

        var record = server.SingleRecord();
        Convert.ToHexString(record.TraceId!).ToLowerInvariant().Should().Be(traceId);
        Convert.ToHexString(record.SpanId!).ToLowerInvariant().Should().Be(spanId);
    }

    [Fact]
    public void Export_RetriesATransientFailure_AndKeepsTheSnapshot()
    {
        // The batch processor DROPS a batch on ExportResult.Failure, so without a retry a single 503 from a
        // restarting collector loses the snapshot outright.
        using var server = new OtlpTestServer(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        EmitOneSnapshot(server.Endpoint, NewCapture());

        server.RequestCount.Should().Be(2, "the 503 must be retried");
        var record = server.SingleRecord();
        record.EventName.Should().Be(EventName, "the retried payload is the same snapshot, not a truncated one");
    }

    [Fact]
    public void Export_DoesNotRetryAPermanentFailure()
    {
        // 400 means the payload is wrong; resending it wastes the export thread and cannot succeed.
        using var server = new OtlpTestServer(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest);
        EmitOneSnapshot(server.Endpoint, NewCapture());

        server.RequestCount.Should().Be(1);
    }

    [Fact]
    public void Export_GivesUpAfterThreeAttempts_AndSaysSo()
    {
        var logger = new RecordingLogger();
        using var server = new OtlpTestServer(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);

        EmitOneSnapshot(server.Endpoint, NewCapture(), logger);

        server.RequestCount.Should().Be(3, "retries are bounded; a wedged endpoint must not be retried forever");

        // A dropped snapshot is indistinguishable from a probe that never fired, so the drop must be visible.
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("dropping").And.Contain("503");
    }

    [Fact]
    public void PrimitiveToAnyValue_EncodesEachScalarType()
    {
        AnyValueDecoder.Decode(DiOtlpLogExporter.PrimitiveToAnyValue("hello")).Should().Be("hello");
        AnyValueDecoder.Decode(DiOtlpLogExporter.PrimitiveToAnyValue(true)).Should().Be(true);
        AnyValueDecoder.Decode(DiOtlpLogExporter.PrimitiveToAnyValue(42)).Should().Be(42L);
        AnyValueDecoder.Decode(DiOtlpLogExporter.PrimitiveToAnyValue(9_000_000_000L)).Should().Be(9_000_000_000L);
        AnyValueDecoder.Decode(DiOtlpLogExporter.PrimitiveToAnyValue(1.5d)).Should().Be(1.5d);
    }

    [Fact]
    public void JsonNodeToAnyValue_RoundTripsTheSnapshotBodyShape()
    {
        // The shapes a snapshot body actually contains: nested maps (captures.lines.<n>.locals), an array of
        // frames, an array of collection elements, and the integer line_number/size fields.
        const string bodyJson = """
        {
          "captures": {
            "lines": { "221": { "locals": { "note": { "type": "System.String", "value": "hi" } } } },
            "entry": {
              "arguments": {
                "items": {
                  "type": "System.Collections.Generic.List`1",
                  "elements": [ { "type": "System.Int32", "value": "1" } ],
                  "size": 100
                }
              }
            }
          },
          "stack": [ { "file_path": "Program.cs", "function": "MyApp.Run", "line_number": 221 } ]
        }
        """;

        var decoded = AnyValueDecoder.Decode(DiOtlpLogExporter.JsonNodeToAnyValue(JsonNode.Parse(bodyJson)));
        var body = decoded.Should().BeOfType<Dictionary<string, object?>>().Subject;

        Walk(body, "captures", "lines", "221", "locals", "note")["value"].Should().Be("hi");

        var items = Walk(body, "captures", "entry", "arguments", "items");
        items["size"].Should().Be(100L, "size is an integer field, not a string");
        var elements = items["elements"].Should().BeOfType<List<object?>>().Subject;
        elements.Should().HaveCount(1);

        var stack = body["stack"].Should().BeOfType<List<object?>>().Subject;
        var frame = stack[0].Should().BeOfType<Dictionary<string, object?>>().Subject;
        frame["file_path"].Should().Be("Program.cs");
        frame["line_number"].Should().Be(221L);
    }

    private static PendingCapture NewCapture() => new()
    {
        Type = CaptureType.METHOD,
        InstrumentationKey = "MyApp.OrderService.Process",
        LocationHash = "loc-abc",
    };

    private static void EmitOneSnapshot(string endpoint, PendingCapture capture, ILogger? logger = null)
    {
        var emitter = DISnapshotOtlpEmitter.Create(endpoint, null, logger);
        emitter.Emit(capture);

        // Disposing flushes the batch, which is what performs the export.
        emitter.Dispose();
    }

    private static Dictionary<string, object?> Walk(Dictionary<string, object?> root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            current.Should().ContainKey(key);
            current = current[key].Should().BeOfType<Dictionary<string, object?>>().Subject;
        }

        return current;
    }
}

// A real HTTP endpoint rather than a raw socket: these tests need the exporter's whole send path, including
// the status codes that drive the retry decision. Responses are scripted so a transient failure can be
// distinguished from a permanent one.
internal sealed class OtlpTestServer : IDisposable
{
    private readonly HttpListener listener;
    private readonly Queue<HttpStatusCode> responses;
    private readonly List<byte[]> payloads = new();
    private readonly Thread worker;
    private readonly object gate = new();

    public OtlpTestServer(params HttpStatusCode[] scriptedResponses)
    {
        this.responses = new Queue<HttpStatusCode>(
            scriptedResponses.Length == 0 ? new[] { HttpStatusCode.OK } : scriptedResponses);

        var port = FreePort();
        this.Endpoint = $"http://127.0.0.1:{port}/v1/logs";
        this.listener = new HttpListener();
        this.listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        this.listener.Start();

        this.worker = new Thread(this.Serve) { IsBackground = true };
        this.worker.Start();
    }

    public string Endpoint { get; }

    public List<byte[]> Payloads
    {
        get
        {
            lock (this.gate)
            {
                return new List<byte[]>(this.payloads);
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (this.gate)
            {
                return this.payloads.Count;
            }
        }
    }

    // The single LogRecord of the LAST request: a retried export resends the same batch, and it is the
    // payload that finally landed that a consumer would see.
    public DecodedLogRecord SingleRecord()
    {
        var payloads = this.Payloads;
        payloads.Should().NotBeEmpty("the exporter must have sent something");
        var (_, _, records) = OtlpLogsDataDecoder.Decode(payloads[^1]);
        return records.Should().ContainSingle().Subject;
    }

    public void Dispose()
    {
        this.listener.Stop();
        this.listener.Close();
        this.worker.Join(TimeSpan.FromSeconds(5));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private void Serve()
    {
        try
        {
            while (this.listener.IsListening)
            {
                var context = this.listener.GetContext();

                using var buffer = new MemoryStream();
                context.Request.InputStream.CopyTo(buffer);

                HttpStatusCode status;
                lock (this.gate)
                {
                    this.payloads.Add(buffer.ToArray());
                    status = this.responses.Count > 1 ? this.responses.Dequeue() : this.responses.Peek();
                }

                context.Response.StatusCode = (int)status;
                context.Response.ContentLength64 = 0;
                context.Response.Close();
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // Listener stopped in Dispose; assertions run on what was already recorded.
        }
    }
}

internal sealed record DecodedLogRecord(
    Dictionary<string, object?> Attributes,
    object? Body,
    string? EventName,
    byte[]? TraceId,
    byte[]? SpanId);

/// <summary>
/// Minimal reader for the OTLP <c>LogsData</c> message the exporter writes (logs.proto): unwraps
/// ResourceLogs/ScopeLogs and decodes each LogRecord's attributes, body, event_name and trace context.
/// </summary>
internal static class OtlpLogsDataDecoder
{
    public static (string? ScopeName, string? ScopeVersion, List<DecodedLogRecord> Records) Decode(byte[] logsData)
    {
        // LogsData { resource_logs = 1 } -> ResourceLogs { scope_logs = 2 }
        var resourceLogs = FirstLenField(logsData, 0, logsData.Length, 1);
        var scopeLogs = FirstLenField(resourceLogs, 0, resourceLogs.Length, 2);

        string? scopeName = null;
        string? scopeVersion = null;
        var records = new List<DecodedLogRecord>();

        var pos = 0;
        while (pos < scopeLogs.Length)
        {
            var (field, wire) = ProtoReader.ReadTag(scopeLogs, ref pos);
            if (wire != 2)
            {
                ProtoReader.SkipField(scopeLogs, ref pos, wire);
                continue;
            }

            var payload = ProtoReader.ReadLengthDelimited(scopeLogs, ref pos);
            if (field == 1)
            {
                // InstrumentationScope { name = 1, version = 2 }
                (scopeName, scopeVersion) = DecodeScope(payload);
            }
            else if (field == 2)
            {
                records.Add(DecodeLogRecord(payload));
            }
        }

        return (scopeName, scopeVersion, records);
    }

    private static (string? Name, string? Version) DecodeScope(byte[] scope)
    {
        string? name = null;
        string? version = null;
        var pos = 0;
        while (pos < scope.Length)
        {
            var (field, wire) = ProtoReader.ReadTag(scope, ref pos);
            if (wire != 2)
            {
                ProtoReader.SkipField(scope, ref pos, wire);
                continue;
            }

            var value = Encoding.UTF8.GetString(ProtoReader.ReadLengthDelimited(scope, ref pos));
            if (field == 1)
            {
                name = value;
            }
            else if (field == 2)
            {
                version = value;
            }
        }

        return (name, version);
    }

    private static DecodedLogRecord DecodeLogRecord(byte[] record)
    {
        var attributes = new Dictionary<string, object?>();
        object? body = null;
        string? eventName = null;
        byte[]? traceId = null;
        byte[]? spanId = null;

        var pos = 0;
        while (pos < record.Length)
        {
            var (field, wire) = ProtoReader.ReadTag(record, ref pos);
            if (wire != 2)
            {
                ProtoReader.SkipField(record, ref pos, wire);
                continue;
            }

            var payload = ProtoReader.ReadLengthDelimited(record, ref pos);
            switch (field)
            {
                case 5: // body (AnyValue)
                    body = AnyValueDecoder.Decode(payload);
                    break;
                case 6: // attributes (KeyValue)
                    AnyValueDecoder.ReadKeyValueInto(payload, attributes);
                    break;
                case 9:
                    traceId = payload;
                    break;
                case 10:
                    spanId = payload;
                    break;
                case 12:
                    eventName = Encoding.UTF8.GetString(payload);
                    break;
            }
        }

        return new DecodedLogRecord(attributes, body, eventName, traceId, spanId);
    }

    private static byte[] FirstLenField(byte[] buf, int start, int end, int wanted)
    {
        var pos = start;
        while (pos < end)
        {
            var (field, wire) = ProtoReader.ReadTag(buf, ref pos);
            if (wire == 2)
            {
                var payload = ProtoReader.ReadLengthDelimited(buf, ref pos);
                if (field == wanted)
                {
                    return payload;
                }
            }
            else
            {
                ProtoReader.SkipField(buf, ref pos, wire);
            }
        }

        throw new InvalidOperationException($"field {wanted} not present");
    }
}

/// <summary>
/// Minimal OTLP <c>AnyValue</c> reader (common.proto): string_value=1, bool_value=2, int_value=3,
/// double_value=4, array_value=5, kvlist_value=6.
/// </summary>
internal static class AnyValueDecoder
{
    public static object? Decode(byte[] anyValue)
    {
        var pos = 0;
        return ReadAnyValue(anyValue, ref pos, anyValue.Length);
    }

    public static void ReadKeyValueInto(byte[] keyValue, Dictionary<string, object?> map)
    {
        var pos = 0;
        ReadKeyValue(keyValue, ref pos, keyValue.Length, map);
    }

    private static object? ReadAnyValue(byte[] buf, ref int pos, int end)
    {
        // AnyValue is a oneof — take the first (only) field present.
        while (pos < end)
        {
            var (field, wire) = ProtoReader.ReadTag(buf, ref pos);
            switch (field)
            {
                case 1:
                    return Encoding.UTF8.GetString(ProtoReader.ReadLengthDelimited(buf, ref pos));
                case 2:
                    return ProtoReader.ReadVarint(buf, ref pos) != 0;
                case 3:
                    return (long)ProtoReader.ReadVarint(buf, ref pos);
                case 4:
                    return BitConverter.Int64BitsToDouble(ProtoReader.ReadFixed64(buf, ref pos));
                case 5:
                    return ReadArrayValue(buf, ref pos);
                case 6:
                    return ReadKeyValueList(buf, ref pos);
                default:
                    ProtoReader.SkipField(buf, ref pos, wire);
                    break;
            }
        }

        return null;
    }

    private static List<object?> ReadArrayValue(byte[] buf, ref int pos)
    {
        var len = (int)ProtoReader.ReadVarint(buf, ref pos);
        var end = pos + len;
        var list = new List<object?>();
        while (pos < end)
        {
            var (field, wire) = ProtoReader.ReadTag(buf, ref pos);
            if (field == 1 && wire == 2)
            {
                var itemLen = (int)ProtoReader.ReadVarint(buf, ref pos);
                var itemEnd = pos + itemLen;
                list.Add(ReadAnyValue(buf, ref pos, itemEnd));
                pos = itemEnd;
            }
            else
            {
                ProtoReader.SkipField(buf, ref pos, wire);
            }
        }

        return list;
    }

    private static Dictionary<string, object?> ReadKeyValueList(byte[] buf, ref int pos)
    {
        var len = (int)ProtoReader.ReadVarint(buf, ref pos);
        var end = pos + len;
        var map = new Dictionary<string, object?>();
        while (pos < end)
        {
            var (field, wire) = ProtoReader.ReadTag(buf, ref pos);
            if (field == 1 && wire == 2)
            {
                var kvLen = (int)ProtoReader.ReadVarint(buf, ref pos);
                var kvEnd = pos + kvLen;
                ReadKeyValue(buf, ref pos, kvEnd, map);
                pos = kvEnd;
            }
            else
            {
                ProtoReader.SkipField(buf, ref pos, wire);
            }
        }

        return map;
    }

    private static void ReadKeyValue(byte[] buf, ref int pos, int end, Dictionary<string, object?> map)
    {
        string? key = null;
        object? value = null;
        while (pos < end)
        {
            var (field, wire) = ProtoReader.ReadTag(buf, ref pos);
            if (field == 1 && wire == 2)
            {
                key = Encoding.UTF8.GetString(ProtoReader.ReadLengthDelimited(buf, ref pos));
            }
            else if (field == 2 && wire == 2)
            {
                var valLen = (int)ProtoReader.ReadVarint(buf, ref pos);
                var valEnd = pos + valLen;
                value = ReadAnyValue(buf, ref pos, valEnd);
                pos = valEnd;
            }
            else
            {
                ProtoReader.SkipField(buf, ref pos, wire);
            }
        }

        if (key is not null)
        {
            map[key] = value;
        }
    }
}

internal static class ProtoReader
{
    public static (int Field, int Wire) ReadTag(byte[] buf, ref int pos)
    {
        var tag = ReadVarint(buf, ref pos);
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    public static byte[] ReadLengthDelimited(byte[] buf, ref int pos)
    {
        var len = (int)ReadVarint(buf, ref pos);
        var payload = new byte[len];
        Array.Copy(buf, pos, payload, 0, len);
        pos += len;
        return payload;
    }

    public static ulong ReadVarint(byte[] buf, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    public static long ReadFixed64(byte[] buf, ref int pos)
    {
        long v = 0;
        for (var i = 0; i < 8; i++)
        {
            v |= (long)buf[pos++] << (8 * i);
        }

        return v;
    }

    public static void SkipField(byte[] buf, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0:
                ReadVarint(buf, ref pos);
                break;
            case 1:
                pos += 8;
                break;
            case 2:
                pos += (int)ReadVarint(buf, ref pos);
                break;
            case 5:
                pos += 4;
                break;
            default:
                throw new InvalidOperationException($"Unsupported wire type {wire}");
        }
    }
}

internal sealed class RecordingLogger : ILogger
{
    public List<string> Warnings { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            this.Warnings.Add(formatter(state, exception));
        }
    }
}
