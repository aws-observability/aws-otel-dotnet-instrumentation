// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// Custom OTLP/HTTP (protobuf) exporter for ServiceEvents LogRecords.
/// </summary>
/// <remarks>
/// <para>
/// Why a custom exporter instead of the stock <c>AddOtlpExporter</c>: OTel .NET's
/// <c>LogRecord.Body</c> is string-only, so the stock exporter cannot emit the
/// nested-object body the other SDKs (Java/Python/JS) produce (spec §1/§2/§5). Our
/// emitter stashes the structured body as a JSON string in a <c>body</c> attribute;
/// this exporter reconstructs it into a proper OTLP <c>AnyValue</c> (kvlist) body,
/// drops the <c>body</c> attribute, sets the top-level <c>event_name</c> field, and
/// pins the InstrumentationScope to <c>serviceevents</c>/<c>1.0</c> — matching the
/// cross-SDK wire format exactly.
/// </para>
/// <para>
/// Why protobuf and not JSON: the CloudWatch OTLP endpoint <b>strips the top-level
/// <c>event_name</c> field from serialized JSON</b> (documented in the spec, and
/// confirmed empirically — a JSON-transported record loses <c>eventName</c> while a
/// protobuf one keeps it). Consumers such as the Application Signals MCP filter
/// incidents on the top-level <c>eventName</c>, so ServiceEvents logs MUST be sent as
/// OTLP/protobuf for those consumers to find them. Java/Python already send protobuf.
/// </para>
/// <para>
/// The protobuf payload is hand-encoded (a minimal writer, below) rather than via
/// <c>Google.Protobuf</c> — the OTLP exporter package no longer carries that dependency,
/// and the ServiceEvents LogsData message shape is small and fixed. Field numbers follow
/// the OTLP proto (logs.proto / common.proto / resource.proto).
/// </para>
/// </remarks>
internal sealed class ServiceEventsOtlpLogExporter : BaseExporter<LogRecord>
{
    // Protobuf wire types.
    private const int WireVarint = 0;
    private const int WireI64 = 1;
    private const int WireLen = 2;
    private const int WireI32 = 5;

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Uri endpoint;

    public ServiceEventsOtlpLogExporter(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        this.endpoint = new Uri(endpoint);
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        // ScopeLogs { scope = 1, log_records = 2 }
        var scopeLogs = new List<byte>();
        var scope = new List<byte>();
        WriteString(scope, 1, ServiceEventsOtlpEmitter.InstrumentationScopeName);
        WriteString(scope, 2, ServiceEventsOtlpEmitter.InstrumentationScopeVersion);
        WriteLenField(scopeLogs, 1, scope);

        var recordCount = 0;
        foreach (var record in batch)
        {
            WriteLenField(scopeLogs, 2, SerializeLogRecord(record));
            recordCount++;
        }

        if (recordCount == 0)
        {
            return ExportResult.Success;
        }

        // ResourceLogs { resource = 1, scope_logs = 2 }
        var resourceLogs = new List<byte>();
        WriteLenField(resourceLogs, 1, EncodeResource(this.ParentProvider?.GetResource()));
        WriteLenField(resourceLogs, 2, scopeLogs);

        // LogsData { resource_logs = 1 }
        var logsData = new List<byte>();
        WriteLenField(logsData, 1, resourceLogs);

        try
        {
            using var content = new ByteArrayContent(logsData.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            using var response = HttpClient.PostAsync(this.endpoint, content).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? ExportResult.Success : ExportResult.Failure;
        }
        catch
        {
            return ExportResult.Failure;
        }
    }

    /// <summary>Encode a flat attribute/primitive value as an OTLP <c>AnyValue</c> message.</summary>
    /// <remarks><c>internal</c> (not <c>private</c>) so the protobuf encoding is unit-testable.</remarks>
    internal static byte[] PrimitiveToAnyValue(object? value)
    {
        var b = new List<byte>();
        switch (value)
        {
            case null:
                WriteString(b, 1, string.Empty);
                break;
            case bool boolean:
                WriteVarintField(b, 2, boolean ? 1UL : 0UL); // bool_value
                break;
            case string s:
                WriteString(b, 1, s); // string_value
                break;
            case int i:
                WriteVarintField(b, 3, (ulong)(long)i); // int_value
                break;
            case long l:
                WriteVarintField(b, 3, (ulong)l);
                break;
            case double d:
                WriteDouble(b, 4, d); // double_value
                break;
            case float f:
                WriteDouble(b, 4, f);
                break;
            default:
                WriteString(b, 1, value.ToString() ?? string.Empty);
                break;
        }

        return b.ToArray();
    }

    /// <summary>Convert a parsed JSON body node into an OTLP <c>AnyValue</c> message (recursive).</summary>
    /// <remarks><c>internal</c> (not <c>private</c>) so the protobuf body encoding is unit-testable.</remarks>
    internal static byte[] JsonNodeToAnyValue(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                // kvlist_value = 6 -> KeyValueList { values = 1 (repeated KeyValue) }
                var kvlist = new List<byte>();
                foreach (var kv in obj)
                {
                    WriteLenField(kvlist, 1, KeyValue(kv.Key, JsonNodeToAnyValue(kv.Value)));
                }

                var kvWrap = new List<byte>();
                WriteLenField(kvWrap, 6, kvlist);
                return kvWrap.ToArray();

            case JsonArray arr:
                // array_value = 5 -> ArrayValue { values = 1 (repeated AnyValue) }
                var array = new List<byte>();
                foreach (var item in arr)
                {
                    WriteLenField(array, 1, JsonNodeToAnyValue(item));
                }

                var arrWrap = new List<byte>();
                WriteLenField(arrWrap, 5, array);
                return arrWrap.ToArray();

            case JsonValue val:
                return JsonValueToAnyValue(val);

            default:
                var empty = new List<byte>();
                WriteString(empty, 1, string.Empty);
                return empty.ToArray();
        }
    }

    /// <summary>Encode one <see cref="LogRecord"/> as an OTLP <c>LogRecord</c> protobuf message.</summary>
    private static byte[] SerializeLogRecord(LogRecord record)
    {
        var timestamp = record.Timestamp.Kind == DateTimeKind.Utc ? record.Timestamp : record.Timestamp.ToUniversalTime();
        var unixNs = (ulong)((timestamp.Ticks - DateTime.UnixEpoch.Ticks) * 100L);

        var b = new List<byte>();
        WriteFixed64(b, 1, unixNs);   // time_unix_nano
        WriteFixed64(b, 11, unixNs);  // observed_time_unix_nano
        WriteVarintField(b, 2, 9);    // severity_number = INFO
        WriteString(b, 3, "Information"); // severity_text

        string? eventName = null;
        byte[]? bodyAny = null;

        if (record.Attributes is not null)
        {
            foreach (var kv in record.Attributes)
            {
                if (kv.Key == "event.name" && kv.Value is string en && !string.IsNullOrEmpty(en))
                {
                    // Surface event.name as the top-level OTLP event_name field (proto field 12).
                    // This is the field CloudWatch preserves over protobuf and that consumers
                    // (e.g. the Application Signals MCP incident query) filter on. Keep the
                    // attribute too — Java/Python carry both.
                    eventName = en;
                }

                if (kv.Key == "body")
                {
                    // Reconstruct the structured body from the JSON string the emitter packed
                    // into this attribute (OTel .NET LogRecord.Body is string-only), then drop
                    // the attribute so it does not also appear as a flat value.
                    if (kv.Value is string bodyJson && !string.IsNullOrEmpty(bodyJson))
                    {
                        try
                        {
                            bodyAny = JsonNodeToAnyValue(JsonNode.Parse(bodyJson));
                        }
                        catch (JsonException)
                        {
                            bodyAny = null;
                        }
                    }

                    continue;
                }

                WriteLenField(b, 6, KeyValue(kv.Key, PrimitiveToAnyValue(kv.Value))); // attributes
            }
        }

        if (bodyAny is not null)
        {
            WriteLenField(b, 5, bodyAny); // body
        }

        if (!string.IsNullOrEmpty(eventName))
        {
            WriteString(b, 12, eventName!); // event_name
        }

        // Trace context (IncidentSnapshot only). OTLP protobuf encodes ids as raw bytes.
        if (record.TraceId != default)
        {
            var traceId = new byte[16];
            record.TraceId.CopyTo(traceId);
            WriteBytes(b, 9, traceId);
        }

        if (record.SpanId != default)
        {
            var spanId = new byte[8];
            record.SpanId.CopyTo(spanId);
            WriteBytes(b, 10, spanId);
        }

        if (record.TraceFlags != ActivityTraceFlags.None)
        {
            WriteFixed32(b, 8, (uint)record.TraceFlags); // flags
        }

        return b.ToArray();
    }

    /// <summary>Encode the OTLP <c>Resource</c> message (attributes only).</summary>
    private static byte[] EncodeResource(global::OpenTelemetry.Resources.Resource? resource)
    {
        var b = new List<byte>();
        if (resource is not null)
        {
            foreach (var kv in resource.Attributes)
            {
                WriteLenField(b, 1, KeyValue(kv.Key, PrimitiveToAnyValue(kv.Value)));
            }
        }

        return b.ToArray();
    }

    // ---- OTLP AnyValue / KeyValue encoders --------------------------------------------------

    /// <summary>Encode a <c>KeyValue { key = 1, value = 2 }</c> message.</summary>
    private static byte[] KeyValue(string key, byte[] anyValue)
    {
        var b = new List<byte>();
        WriteString(b, 1, key);
        WriteLenField(b, 2, anyValue);
        return b.ToArray();
    }

    private static byte[] JsonValueToAnyValue(JsonValue val)
    {
        var b = new List<byte>();
        if (val.TryGetValue<bool>(out var boolean))
        {
            WriteVarintField(b, 2, boolean ? 1UL : 0UL);
        }
        else if (val.TryGetValue<string>(out var s))
        {
            WriteString(b, 1, s);
        }
        else if (val.TryGetValue<long>(out var l))
        {
            WriteVarintField(b, 3, (ulong)l);
        }
        else if (val.TryGetValue<double>(out var d))
        {
            WriteDouble(b, 4, d);
        }
        else
        {
            WriteString(b, 1, val.ToString());
        }

        return b.ToArray();
    }

    // ---- Minimal protobuf wire writer -------------------------------------------------------
    private static void WriteVarint(List<byte> buf, ulong v)
    {
        while (v >= 0x80)
        {
            buf.Add((byte)(v | 0x80));
            v >>= 7;
        }

        buf.Add((byte)v);
    }

    private static void WriteTag(List<byte> buf, int field, int wireType) =>
        WriteVarint(buf, (ulong)((field << 3) | wireType));

    private static void WriteLenField(List<byte> buf, int field, IReadOnlyList<byte> payload)
    {
        WriteTag(buf, field, WireLen);
        WriteVarint(buf, (ulong)payload.Count);
        buf.AddRange(payload);
    }

    private static void WriteString(List<byte> buf, int field, string s) =>
        WriteLenField(buf, field, Encoding.UTF8.GetBytes(s));

    private static void WriteBytes(List<byte> buf, int field, byte[] value) =>
        WriteLenField(buf, field, value);

    private static void WriteVarintField(List<byte> buf, int field, ulong v)
    {
        WriteTag(buf, field, WireVarint);
        WriteVarint(buf, v);
    }

    private static void WriteFixed64(List<byte> buf, int field, ulong v)
    {
        WriteTag(buf, field, WireI64);
        for (var i = 0; i < 8; i++)
        {
            buf.Add((byte)(v & 0xFF));
            v >>= 8;
        }
    }

    private static void WriteFixed32(List<byte> buf, int field, uint v)
    {
        WriteTag(buf, field, WireI32);
        for (var i = 0; i < 4; i++)
        {
            buf.Add((byte)(v & 0xFF));
            v >>= 8;
        }
    }

    private static void WriteDouble(List<byte> buf, int field, double d)
    {
        var bits = BitConverter.DoubleToInt64Bits(d);
        WriteFixed64(buf, field, (ulong)bits);
    }
}
