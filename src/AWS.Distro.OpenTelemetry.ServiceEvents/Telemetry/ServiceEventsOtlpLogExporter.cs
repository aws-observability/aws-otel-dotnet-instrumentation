// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
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
/// nested-object body the other distros (Java, Python, JS) produce. Our
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
    private readonly string? logGroup;
    private readonly string? logStream;

    /// <summary>
    /// Monotonic deadline for exports once shutdown has begun, or <c>long.MaxValue</c> while running
    /// normally. Written by <see cref="OnShutdown" /> and read by <see cref="Export" />, possibly on
    /// the batch processor's thread, so accessed through <see cref="Volatile" />.
    /// </summary>
    private long shutdownDeadlineTicks = long.MaxValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceEventsOtlpLogExporter"/> class.
    /// </summary>
    /// <param name="endpoint">OTLP/HTTP logs endpoint.</param>
    /// <param name="logGroup">
    /// CloudWatch log group, sent as <c>x-aws-log-group</c>. Omitted when null or empty.
    /// </param>
    /// <param name="logStream">
    /// CloudWatch log stream, sent as <c>x-aws-log-stream</c>. Omitted when null or empty.
    /// </param>
    public ServiceEventsOtlpLogExporter(string endpoint, string? logGroup = null, string? logStream = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        this.endpoint = new Uri(endpoint);
        this.logGroup = SanitizeHeaderValue(logGroup);
        this.logStream = SanitizeHeaderValue(logStream);
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
            // Suppress instrumentation for the duration of the export. Without this the HTTP client
            // instrumentation traces our own export POST as if it were a customer dependency call,
            // putting ServiceEvents' self-telemetry into the customer's traces (and, when the export
            // target is reachable through an instrumented path, risking export-triggers-export
            // feedback). This is what the stock OTLP exporters do for the same reason.
            using var suppress = SuppressInstrumentationScope.Begin();

            using var content = new ByteArrayContent(logsData.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

            using var request = new HttpRequestMessage(HttpMethod.Post, this.endpoint) { Content = content };

            // Log group/stream routing metadata, read by the collector or the CloudWatch agent's
            // OTLP logs pipeline to decide where records land. Sent unconditionally, matching the
            // Java distro (ServiceEventsInstrumentation) and the JS distro
            // (src/serviceevents/exporter/otlp-emitter.ts); a collector that does not care simply
            // ignores them.
            if (!string.IsNullOrEmpty(this.logGroup))
            {
                request.Headers.TryAddWithoutValidation("x-aws-log-group", this.logGroup);
            }

            if (!string.IsNullOrEmpty(this.logStream))
            {
                request.Headers.TryAddWithoutValidation("x-aws-log-stream", this.logStream);
            }

            // Bounded by whatever the shutdown budget has left, when shutting down. The client's own
            // 10s timeout is fine while the process is running, but during teardown it is an order of
            // magnitude beyond the window ServiceEvents is allowed, and overrunning that window gets
            // the process terminated mid-flush — losing this batch and everything behind it.
            var remaining = this.RemainingShutdownTime();
            if (remaining == TimeSpan.Zero)
            {
                ServiceEventsEventSource.Log.ExportAbandonedOnShutdown(this.endpoint.ToString(), 0);
                return ExportResult.Failure;
            }

            using var cts = remaining == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(remaining);
            using var response = HttpClient.Send(request, cts?.Token ?? default);
            if (response.IsSuccessStatusCode)
            {
                return ExportResult.Success;
            }

            // A rejection is not an exception, and is the shape a misconfigured endpoint produces.
            // Reported through the same event so a listener does not have to know the difference.
            ServiceEventsEventSource.Log.ExportFailed(
                this.endpoint.ToString(),
                $"HTTP {(int)response.StatusCode} {response.StatusCode}");
            return ExportResult.Failure;
        }
        catch (Exception ex)
        {
            ServiceEventsEventSource.Log.ExportFailed(this.endpoint.ToString(), ex);
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

    /// <summary>
    /// Arm the shutdown deadline from ServiceEvents' own teardown, before the SDK's drain begins, so
    /// that queued and in-flight exports stop attempting once it passes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This — not <see cref="OnShutdown" /> — is what actually bounds the shutdown drain, and the
    /// reason is an ordering problem in the SDK that makes the exporter's own hook useless for it.
    /// <c>BatchExportProcessor.OnShutdown</c> calls <c>worker.Shutdown(timeout)</c> first, which
    /// performs the drain (that is, the <c>Export</c> calls), and only afterwards calls
    /// <c>exporter.Shutdown(remaining)</c>. So by the time <see cref="OnShutdown" /> runs, every export
    /// it was meant to bound has already happened, with the deadline still unset.
    /// </para>
    /// <para>
    /// Nor does the processor's <c>exporterTimeoutMilliseconds</c> help: in OTel .NET 1.16.0 it is
    /// stored, passed to the worker and exposed as a property, but never read — the export is a bare
    /// <c>Exporter.Export(batch)</c> with no timeout and no cancellation. What actually governs the
    /// drain is a hardcoded constant: <c>LoggerProviderSdk.Dispose</c> calls
    /// <c>Processor?.Shutdown(5000)</c> regardless of any configured value.
    /// </para>
    /// <para>
    /// Calling this immediately before disposing the logger factory is therefore the only way to make
    /// the budget real, because it is the last moment we control before the SDK's drain starts.
    /// </para>
    /// <para>
    /// Teardown runs sequentially — collectors flush, then the providers drain over the network — and
    /// the whole sequence has to fit inside the runtime's process-exit allowance. Overrunning it is
    /// worse than giving up: the process is terminated mid-flush, so the batch is lost anyway, having
    /// delayed the host's shutdown to lose it. Under a container orchestrator that can mean a grace
    /// period expiring and a rolling deployment stalling.
    /// </para>
    /// </remarks>
    /// <param name="budget">
    /// Time the drain may take. Zero or negative expires the deadline immediately, which abandons the
    /// remaining batches rather than attempting them outside the window.
    /// </param>
    internal void BeginShutdown(TimeSpan budget)
    {
        var milliseconds = budget <= TimeSpan.Zero ? 0L : (long)budget.TotalMilliseconds;
        this.ArmDeadline(Environment.TickCount64 + milliseconds);
    }

    /// <summary>
    /// Honour a shutdown timeout handed to us by the SDK, tightening the deadline if it is stricter
    /// than the one already armed.
    /// </summary>
    /// <remarks>
    /// Retained for correctness rather than as the mechanism that bounds the drain — see
    /// <see cref="BeginShutdown" /> for why this hook runs too late to do that. The SDK is entitled to
    /// call it, and a budget stricter than our own is worth honouring.
    /// </remarks>
    /// <param name="timeoutMilliseconds">Milliseconds allowed, or <c>Timeout.Infinite</c> for no limit.</param>
    /// <returns>
    /// Always <c>true</c>. There is nothing here that can fail, and reporting failure would only make
    /// the SDK record a shutdown problem the host can do nothing about.
    /// </returns>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        // Timeout.Infinite (-1) is the only negative the SDK permits, and it means "no limit", so it
        // leaves the deadline alone. Everything else arms one -- including 0, which the OTel contract
        // defines as "give up immediately" and which therefore has to expire the deadline rather than
        // skip arming it. An earlier version guarded on `> 0` and so treated 0 as unbounded, the exact
        // opposite of its meaning; BatchExportProcessor.OnShutdown calls exporter.Shutdown(0) verbatim
        // when its own timeout is 0, and Stopwatch.Remaining clamps to 0 whenever the drain has already
        // consumed the whole budget, so that path is reachable rather than theoretical.
        //
        // This hook is not what bounds the shutdown drain -- see BeginShutdown, which is. It remains
        // wired because the SDK is entitled to call it, and honouring a tighter budget than we armed
        // ourselves is correct.
        if (timeoutMilliseconds != Timeout.Infinite)
        {
            this.ArmDeadline(Environment.TickCount64 + timeoutMilliseconds);
        }

        return true;
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

    // Probes long before double, which is correct and not the cause of the integral-double bug:
    // TryGetValue<long> defers to JsonElement.TryGetInt64, which rejects a token carrying a fraction
    // or exponent, so "2000.0" falls through to the double branch on its own. The defect was upstream,
    // where serialization wrote the double 2000.0 as the token "2000" and destroyed the distinction
    // before this code ever saw it. Fixed in ServiceEventsOtlpEmitter.BodyJsonOptions; reordering the
    // probes here would have retyped genuinely integer fields (a duration's Counts and Count) instead.
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

    /// <summary>
    /// Strip anything from a header value that could terminate the header line.
    /// </summary>
    /// <remarks>
    /// These values are attached with <c>TryAddWithoutValidation</c>, which by design skips the
    /// format checks — including the one for CR/LF. They originate in configuration
    /// (<c>LOG_GROUP</c>, and <c>LOG_STREAM</c> which defaults to the service name, itself
    /// resolvable from <c>OTEL_RESOURCE_ATTRIBUTES</c>), so a value carrying a newline could append
    /// headers we never intended to send. Setting an environment variable already implies control of
    /// the process, so this is not a privilege boundary — but a header value that can rewrite the
    /// request is worth closing off where it enters, once, rather than reasoning about it at every
    /// send.
    /// </remarks>
    /// <param name="value">Raw configured value.</param>
    /// <returns>The value with CR, LF and NUL removed; null stays null.</returns>
    private static string? SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0 && value.IndexOf('\0') < 0)
        {
            return value;
        }

        var clean = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is not ('\r' or '\n' or '\0'))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
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

    /// <summary>
    /// Time left for an export attempt: <see cref="Timeout.InfiniteTimeSpan" /> while running normally,
    /// otherwise what remains of the shutdown deadline, floored at zero.
    /// </summary>
    /// <summary>
    /// Move the shutdown deadline earlier, never later.
    /// </summary>
    /// <remarks>
    /// Tighten-only, because two callers arm this and they must not fight. <see cref="BeginShutdown" />
    /// arms it from ServiceEvents' own teardown, then the SDK calls <see cref="OnShutdown" /> with
    /// whatever remains of its own hardcoded grace period -- a larger number. Taking the later of the
    /// two would let the SDK's value undo the budget we set for ourselves.
    /// </remarks>
    /// <param name="deadlineTicks">Candidate deadline, on the <see cref="Environment.TickCount64" /> clock.</param>
    private void ArmDeadline(long deadlineTicks)
    {
        while (true)
        {
            var current = Volatile.Read(ref this.shutdownDeadlineTicks);
            if (current <= deadlineTicks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref this.shutdownDeadlineTicks, deadlineTicks, current) == current)
            {
                return;
            }
        }
    }

    /// <returns>The remaining time, or <see cref="Timeout.InfiniteTimeSpan" />.</returns>
    private TimeSpan RemainingShutdownTime()
    {
        var deadline = Volatile.Read(ref this.shutdownDeadlineTicks);
        if (deadline == long.MaxValue)
        {
            return Timeout.InfiniteTimeSpan;
        }

        var left = deadline - Environment.TickCount64;
        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
    }
}
