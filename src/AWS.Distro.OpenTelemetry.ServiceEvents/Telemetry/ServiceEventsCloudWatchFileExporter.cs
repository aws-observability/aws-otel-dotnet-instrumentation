// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// File-backed log exporter for the <c>OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE</c>
/// local-testing path. Writes one CloudWatch-faithful NDJSON line per
/// <see cref="LogRecord"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each line is one JSON object shaped as
/// <c>{eventName, timeUnixNano, traceId?, spanId?, flags?, attributes, body, resource}</c>.
/// </para>
/// <para>
/// Trace context (<c>traceId</c>, <c>spanId</c>, <c>flags</c>) is emitted
/// only when the source LogRecord carries a non-empty trace context — in
/// practice this is the IncidentSnapshot signal.
/// </para>
/// </remarks>
internal sealed class ServiceEventsCloudWatchFileExporter : BaseExporter<LogRecord>
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private readonly string fullPath;
    private readonly object writeLock;
    private bool disposed;

    public ServiceEventsCloudWatchFileExporter(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        this.fullPath = Path.GetFullPath(filePath);
        this.writeLock = ServiceEventsCloudWatchMetricFileExporter.GetOrCreateFileLock(this.fullPath);
    }

    /// <inheritdoc />
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        if (this.disposed)
        {
            return ExportResult.Failure;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var record in batch)
        {
            sb.Append(this.SerializeRecord(record)).Append('\n');
        }

        if (sb.Length == 0)
        {
            return ExportResult.Success;
        }

        // Append under the shared per-file lock. The log and metric exporters share
        // this lock so their NDJSON lines never interleave. Open-append-close per
        // flush (rather than a persistent StreamWriter) avoids dual-buffer corruption
        // when both exporters target the same file. OUTPUT_FILE is a dev/test path,
        // so the per-flush open cost is irrelevant.
        lock (this.writeLock)
        {
            try
            {
                File.AppendAllText(this.fullPath, sb.ToString());
                return ExportResult.Success;
            }
            catch
            {
                return ExportResult.Failure;
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        this.disposed = true;
        base.Dispose(disposing);
    }

    /// <summary>
    /// Pull out <c>event.name</c> and <c>body</c> from the attribute list, returning
    /// the rest as a JSON object suitable for the file's <c>attributes</c> field.
    /// </summary>
    private static (string? EventName, JsonNode? Body, JsonObject AttributesJson) ExtractAttributes(
        IReadOnlyList<KeyValuePair<string, object?>>? attributes)
    {
        var attributesJson = new JsonObject();
        string? eventName = null;
        JsonNode? body = null;

        if (attributes is null)
        {
            return (eventName, body, attributesJson);
        }

        foreach (var kv in attributes)
        {
            switch (kv.Key)
            {
                case "event.name":
                    eventName = kv.Value as string;

                    // The CloudWatch workaround keeps event.name as an
                    // attribute too — preserve it in the output.
                    attributesJson[kv.Key] = JsonValue.Create(kv.Value);
                    break;

                case "body":
                    // The emitter serialized the body to a JSON string. Parse it back
                    // so the file's body is structured rather than a string.
                    if (kv.Value is string bodyJson && !string.IsNullOrEmpty(bodyJson))
                    {
                        try
                        {
                            body = JsonNode.Parse(bodyJson);
                        }
                        catch (JsonException)
                        {
                            // Malformed body — fall back to wrapping it as a string field.
                            body = new JsonObject { ["raw"] = bodyJson };
                        }
                    }

                    break;

                default:
                    attributesJson[kv.Key] = JsonValue.Create(kv.Value);
                    break;
            }
        }

        return (eventName, body, attributesJson);
    }

    /// <summary>Convert OTel resource attributes to a JSON object.</summary>
    private static JsonObject ResourceAttributesToJson(global::OpenTelemetry.Resources.Resource? resource)
    {
        var json = new JsonObject();
        if (resource is null)
        {
            return json;
        }

        foreach (var kv in resource.Attributes)
        {
            json[kv.Key] = JsonValue.Create(kv.Value);
        }

        return json;
    }

    /// <summary>Serialize a <see cref="LogRecord"/> to a single NDJSON line per the spec.</summary>
    private string SerializeRecord(LogRecord record)
    {
        var output = new JsonObject();

        // Pull attributes off the record. Our emitter packs `event.name` and `body`
        // (JSON-string) into the attribute list as a workaround for OTel .NET 1.15.0
        // not surfacing structured bodies natively.
        var (eventName, body, attributesJson) = ExtractAttributes(record.Attributes);

        // eventName — required, top-level
        output["eventName"] = eventName ?? "<unknown>";

        // timeUnixNano — convert .NET DateTime → Unix nanoseconds
        var timestamp = record.Timestamp.Kind == DateTimeKind.Utc
            ? record.Timestamp
            : record.Timestamp.ToUniversalTime();
        var unixNs = (timestamp.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        output["timeUnixNano"] = unixNs;

        // Trace context — only emit when set. An unset record has the default
        // (all-zero) TraceId/SpanId, so `!= default` already excludes it. (Do NOT
        // compare against ActivityTraceId.CreateFromString("0…0") — that throws,
        // since W3C disallows an all-zero trace id.)
        if (record.TraceId != default)
        {
            output["traceId"] = record.TraceId.ToHexString();
        }

        if (record.SpanId != default)
        {
            output["spanId"] = record.SpanId.ToHexString();
        }

        if (record.TraceFlags != System.Diagnostics.ActivityTraceFlags.None)
        {
            output["flags"] = (int)record.TraceFlags;
        }

        // Attributes — flat key/value map (everything except event.name and body)
        output["attributes"] = attributesJson;

        // Body — parsed back from the JSON string the emitter packed into the attribute
        // list. Empty object when no body was set (e.g. DeploymentEvent has no body).
        output["body"] = body is null ? new JsonObject() : body;

        // Resource — flat dictionary of resource attributes from the LoggerProvider.
        // OTel .NET 1.15.0 exposes resource via ParentProvider.GetResource(), not on
        // the LogRecord itself.
        output["resource"] = ResourceAttributesToJson(this.ParentProvider?.GetResource());

        return output.ToJsonString(SerializerOptions);
    }
}
