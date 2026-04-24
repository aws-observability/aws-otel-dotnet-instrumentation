// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Console.Logs;

/// <summary>
/// Exports log records as compact JSON to stdout.
/// Produces a single-line JSON object per log record matching the canonical
/// schema shared across all ADOT language implementations. This exporter is
/// used in AWS Lambda environments when OTEL_LOGS_EXPORTER=console.
///
/// If the standardized serialization fails for any reason, falls back to
/// the upstream SDK's ConsoleExporter format to avoid breaking existing infrastructure.
/// </summary>
public class CompactConsoleLogRecordExporter : BaseExporter<LogRecord>
{
    private static readonly JsonWriterOptions CompactWriterOptions = new JsonWriterOptions { Indented = false };
#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory LogFactory = LoggerFactory.Create(builder => builder.AddProvider(new Logging.ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = LogFactory.CreateLogger<CompactConsoleLogRecordExporter>();

    private readonly TextWriter output;
    private Resource? resource;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompactConsoleLogRecordExporter"/> class.
    /// </summary>
    /// <param name="output">Optional TextWriter for output (defaults to Console.Out).</param>
    public CompactConsoleLogRecordExporter(TextWriter? output = null)
    {
        this.output = output ?? System.Console.Out;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        // Lazily resolve resource from the parent provider
        if (this.resource == null && this.ParentProvider is LoggerProvider loggerProvider)
        {
            this.resource = loggerProvider.GetResource() ?? Resource.Empty;
        }

        foreach (var logRecord in batch)
        {
            try
            {
                var json = this.ToCompactJson(logRecord);
                this.output.WriteLine(json);
                this.output.Flush();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to serialize log record with standardized format, writing raw body as fallback");
                this.output.WriteLine(logRecord.Body ?? logRecord.FormattedMessage ?? string.Empty);
                this.output.Flush();
            }
        }

        return ExportResult.Success;
    }

    private static long DateTimeToUnixNano(DateTime timestamp)
    {
        if (timestamp == default)
        {
            return 0;
        }

        var utc = timestamp.ToUniversalTime();
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (utc - epoch).Ticks * 100; // 1 tick = 100 nanoseconds
    }

    private static (int Number, string Text) MapLogLevel(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => (1, "TRACE"),
            LogLevel.Debug => (5, "DEBUG"),
            LogLevel.Information => (9, "INFO"),
            LogLevel.Warning => (13, "WARN"),
            LogLevel.Error => (17, "ERROR"),
            LogLevel.Critical => (21, "FATAL"),
            _ => (0, "UNSPECIFIED"),
        };
    }

    private static void WriteAttributeValue(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case int i:
                writer.WriteNumber(key, i);
                break;
            case long l:
                writer.WriteNumber(key, l);
                break;
            case double d:
                writer.WriteNumber(key, d);
                break;
            case float f:
                writer.WriteNumber(key, f);
                break;
            case bool b:
                writer.WriteBoolean(key, b);
                break;
            case null:
                writer.WriteNull(key);
                break;
            case System.Collections.IEnumerable arr when value is not string:
                writer.WriteStartArray(key);
                foreach (var item in arr)
                {
                    JsonSerializer.Serialize(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteString(key, Convert.ToString(value) ?? string.Empty);
                break;
        }
    }

    private string ToCompactJson(LogRecord logRecord)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, CompactWriterOptions);

        writer.WriteStartObject();

        // resource
        writer.WriteStartObject("resource");
        this.WriteResourceAttributes(writer);
        writer.WriteString("schemaUrl", string.Empty); // Resource schemaUrl not available on LogRecord
        writer.WriteEndObject();

        // scope
        writer.WriteStartObject("scope");
        writer.WriteString("name", logRecord.CategoryName ?? string.Empty);
        writer.WriteString("version", string.Empty); // Not available on .NET LogRecord
        writer.WriteString("schemaUrl", string.Empty); // Not available on .NET LogRecord
        writer.WriteEndObject();

        // body
        var body = logRecord.Body ?? logRecord.FormattedMessage;
        if (body != null)
        {
            writer.WriteString("body", body);
        }
        else
        {
            writer.WriteNull("body");
        }

        // severityNumber + severityText
        var (severityNumber, severityText) = MapLogLevel(logRecord.LogLevel);
        writer.WriteNumber("severityNumber", severityNumber);
        writer.WriteString("severityText", severityText);

        // attributes — preserve value types
        writer.WriteStartObject("attributes");
        if (logRecord.Attributes != null)
        {
            foreach (var attr in logRecord.Attributes)
            {
                WriteAttributeValue(writer, attr.Key, attr.Value);
            }
        }

        writer.WriteEndObject();

        // droppedAttributes — not exposed on .NET LogRecord
        writer.WriteNumber("droppedAttributes", 0);

        // timeUnixNano
        writer.WriteNumber("timeUnixNano", DateTimeToUnixNano(logRecord.Timestamp));

        // observedTimeUnixNano — not exposed on .NET LogRecord
        writer.WriteNumber("observedTimeUnixNano", 0);

        // traceId, spanId, traceFlags
        var traceId = logRecord.TraceId;
        var spanId = logRecord.SpanId;
        bool isValid = traceId != default && spanId != default;

        writer.WriteString("traceId", isValid ? traceId.ToString() : string.Empty);
        writer.WriteString("spanId", isValid ? spanId.ToString() : string.Empty);
        writer.WriteNumber("flags", (int)logRecord.TraceFlags);
        writer.WriteString("exportPath", "console");

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteResourceAttributes(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("attributes");
        if (this.resource != null)
        {
            foreach (var attr in this.resource.Attributes)
            {
                WriteAttributeValue(writer, attr.Key, attr.Value);
            }
        }

        writer.WriteEndObject();
    }
}
