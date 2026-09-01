// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

/// <summary>
/// OTLP/HTTP (protobuf) exporter for DI snapshot LogRecords, emitting the capture tree as a
/// structured (kvlist) OTLP body rather than an opaque JSON string.
/// </summary>
/// <remarks>
/// <para>
/// Why not the stock <c>AddOtlpExporter</c>: OTel .NET types <c>LogRecord.Body</c> as
/// <see cref="string"/> and the SDK has no <c>AnyValue</c> equivalent (upstream issue #5724, milestone
/// Future), so the stock exporter can only ship the whole capture tree as one string. Java, Python and
/// JS all emit a nested body and consumers walk it (<c>captures.entry.arguments.*</c>); hand-encoding
/// the payload is the only way to reach that shape without taking a protobuf dependency.
/// </para>
/// <para>
/// Why protobuf and not JSON: the CloudWatch OTLP endpoint strips the top-level <c>event_name</c> field
/// from OTLP/JSON, and consumers filter on it, so snapshots must travel as protobuf. The encoder below
/// is a minimal hand-rolled writer — the OTLP exporter package no longer carries
/// <c>Google.Protobuf</c>, and the LogsData shape used here is small and fixed. Field numbers follow
/// logs.proto / common.proto / resource.proto.
/// </para>
/// <para>
/// Deliberately a DI-owned copy of a technique the ServiceEvents exporter also uses. The two features
/// ship on separate timelines, so neither may gate the other; do not merge them into a shared type.
/// </para>
/// </remarks>
internal sealed class DiOtlpLogExporter : BaseExporter<LogRecord>
{
    // Protobuf wire types.
    private const int WireVarint = 0;
    private const int WireI64 = 1;
    private const int WireLen = 2;
    private const int WireI32 = 5;

    // The batch processor DROPS a batch that returns ExportResult.Failure, so a transient 503 or a
    // restarting collector would silently lose snapshots without this.
    private const int MaxAttempts = 3;

    // Short because the endpoint is a local CloudWatch Agent or collector, not a remote service:
    // 500ms then 1s rides out a restart without stalling the export thread for tens of seconds.
    private const int InitialRetryDelayMs = 500;

    // CloudWatch Logs rejects a single event over 256 KB. Logged rather than dropped: truncation belongs
    // to the capture limits, and a silent drop here would look identical to a probe that never fired.
    private const int RecordSizeWarnBytes = 256 * 1024;

    // The OTLP spec's retryable HTTP set. Anything else is treated as permanent.
    private static readonly int[] RetryableStatusCodes = { 408, 429, 502, 503, 504 };

    // 10s is the OTLP/HTTP spec default, and it bounds each attempt so a wedged endpoint stalls this
    // exporter's thread rather than letting the batch processor's buffer grow without limit.
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Uri endpoint;
    private readonly string scopeName;
    private readonly string scopeVersion;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiOtlpLogExporter"/> class.
    /// </summary>
    /// <param name="endpoint">OTLP/HTTP logs endpoint.</param>
    /// <param name="scopeName">InstrumentationScope name stamped on every exported batch.</param>
    /// <param name="scopeVersion">InstrumentationScope version stamped on every exported batch.</param>
    /// <param name="logger">Diagnostics sink for export failures; silent when null.</param>
    public DiOtlpLogExporter(string endpoint, string scopeName, string scopeVersion, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(scopeName);
        ArgumentException.ThrowIfNullOrEmpty(scopeVersion);
        this.endpoint = new Uri(endpoint);
        this.scopeName = scopeName;
        this.scopeVersion = scopeVersion;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        // ScopeLogs { scope = 1, log_records = 2 }
        var scopeLogs = new List<byte>();
        var scope = new List<byte>();
        WriteString(scope, 1, this.scopeName);
        WriteString(scope, 2, this.scopeVersion);
        WriteLenField(scopeLogs, 1, scope);

        var recordCount = 0;
        foreach (var record in batch)
        {
            var encoded = this.SerializeLogRecord(record);
            WriteLenField(scopeLogs, 2, encoded);
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

        return this.Send(logsData.ToArray(), recordCount);
    }

    /// <summary>Encode a flat attribute/primitive value as an OTLP <c>AnyValue</c> message.</summary>
    /// <remarks><c>internal</c> (not <c>private</c>) so the protobuf encoding is unit-testable.</remarks>
    internal static byte[] PrimitiveToAnyValue(object? value)
    {
        var b = new List<byte>();
        switch (value)
        {
            // Should be an unset AnyValue rather than "" — kept for now so both hand-rolled exporters
            // in this repo encode null identically and one fix can change both.
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

    // A captured value's `value` is always a string (CapturedValue.Value is string?), so the long/double
    // probes below only ever see the body's own numbers: line_number and size.
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

    private static bool IsRetryable(int statusCode) => Array.IndexOf(RetryableStatusCodes, statusCode) >= 0;

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

    // POST the payload, retrying transient failures. Runs on the batch processor's own thread, so the
    // backoff delays only our export — never a customer thread and never the snapshot drain loop.
    private ExportResult Send(byte[] payload, int recordCount)
    {
        // Without this, HTTP client instrumentation traces our own export POST as if it were a customer
        // dependency call, and an instrumented export path could feed itself.
        using var suppress = SuppressInstrumentationScope.Begin();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // A request and its content cannot be re-sent, so every attempt builds its own.
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var request = new HttpRequestMessage(HttpMethod.Post, this.endpoint) { Content = content };
                using var response = HttpClient.Send(request);

                if (response.IsSuccessStatusCode)
                {
                    this.logger.LogDebug(
                        "DI snapshot export succeeded: {RecordCount} record(s) to {Endpoint}.",
                        recordCount,
                        this.endpoint);
                    return ExportResult.Success;
                }

                var status = (int)response.StatusCode;
                if (!IsRetryable(status) || attempt == MaxAttempts)
                {
                    this.logger.LogWarning(
                        "DI snapshot export to {Endpoint} failed with HTTP {Status} after {Attempts} attempt(s); dropping {RecordCount} record(s).",
                        this.endpoint,
                        status,
                        attempt,
                        recordCount);
                    return ExportResult.Failure;
                }

                this.logger.LogDebug(
                    "DI snapshot export to {Endpoint} got HTTP {Status}; retrying (attempt {Attempt} of {MaxAttempts}).",
                    this.endpoint,
                    status,
                    attempt,
                    MaxAttempts);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt == MaxAttempts)
                {
                    this.logger.LogWarning(
                        ex,
                        "DI snapshot export to {Endpoint} failed after {Attempts} attempt(s); dropping {RecordCount} record(s).",
                        this.endpoint,
                        attempt,
                        recordCount);
                    return ExportResult.Failure;
                }

                this.logger.LogDebug(
                    ex,
                    "DI snapshot export to {Endpoint} failed; retrying (attempt {Attempt} of {MaxAttempts}).",
                    this.endpoint,
                    attempt,
                    MaxAttempts);
            }
            catch (Exception ex)
            {
                // Anything else is permanent (bad URI, serialization defect). Never let it escape into the
                // batch processor's thread.
                this.logger.LogWarning(
                    ex,
                    "DI snapshot export to {Endpoint} failed unrecoverably; dropping {RecordCount} record(s).",
                    this.endpoint,
                    recordCount);
                return ExportResult.Failure;
            }

            Thread.Sleep(InitialRetryDelayMs * (int)Math.Pow(2, attempt - 1));
        }

        return ExportResult.Failure;
    }

    /// <summary>Encode one <see cref="LogRecord"/> as an OTLP <c>LogRecord</c> protobuf message.</summary>
    private byte[] SerializeLogRecord(LogRecord record)
    {
        var timestamp = record.Timestamp.Kind == DateTimeKind.Utc ? record.Timestamp : record.Timestamp.ToUniversalTime();
        var unixNs = (ulong)((timestamp.Ticks - DateTime.UnixEpoch.Ticks) * 100L);

        var b = new List<byte>();
        WriteFixed64(b, 1, unixNs);   // time_unix_nano
        WriteFixed64(b, 11, unixNs);  // observed_time_unix_nano

        // DI emits snapshots at a single severity; there is no per-snapshot level to carry.
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
                    // Surface event.name as the top-level OTLP event_name field (proto field 12): the field
                    // CloudWatch preserves over protobuf and that consumers filter on. Keep the attribute
                    // too — Java/Python carry both.
                    eventName = en;
                }

                if (kv.Key == "body")
                {
                    // Reconstruct the structured body from the JSON string the emitter packed into this
                    // attribute, then drop the attribute so the tree does not also appear as a flat value.
                    if (kv.Value is string bodyJson && !string.IsNullOrEmpty(bodyJson))
                    {
                        try
                        {
                            bodyAny = JsonNodeToAnyValue(JsonNode.Parse(bodyJson));
                        }
                        catch (JsonException ex)
                        {
                            this.logger.LogWarning(ex, "DI snapshot body was not valid JSON; exporting the record without a body.");
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

        // Trace context. OTLP protobuf encodes ids as raw bytes.
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

        if (b.Count > RecordSizeWarnBytes)
        {
            this.logger.LogWarning(
                "A DI snapshot serialized to {Bytes} bytes, above the {Limit}-byte CloudWatch Logs event limit, and may be rejected downstream.",
                b.Count,
                RecordSizeWarnBytes);
        }

        return b.ToArray();
    }
}
