// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Logs;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Resources;
using OtlpResource = OpenTelemetry.Proto.Resource.V1.Resource;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Logs;

/// <summary>
/// Creates logs and validates their OTLP content, resource attributes, and instrumentation scope.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsLogRecordExporterTests : AbstractOtlpAwsExporterTest<OtlpAwsLogRecordExporterTests.ExpectedLogRecord>, IDisposable
{
    private readonly DateTime timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<string, AnyValue> expectedLogAttributes;
    private readonly InstrumentationScope expectedScope;
    private readonly Dictionary<string, AnyValue> expectedResourceAttributes;
    private ILoggerFactory? loggerFactory;
    private ILogger? logger;
    private LogExportProcessor? processor;

    public OtlpAwsLogRecordExporterTests()
        : base(new Uri("https://logs.us-west-2.amazonaws.com/v1/logs"), "us-west-2", "logs")
    {
        this.expectedLogAttributes = new Dictionary<string, AnyValue>
        {
            ["test.log.name"] = new() { StringValue = "test" },
            ["test.log.count"] = new() { IntValue = 3 },
            ["test.log.enabled"] = new() { BoolValue = true },
            ["test.scope.name"] = new() { StringValue = "test scope" },
        };
        this.expectedScope = new InstrumentationScope { Name = $"SigV4.Logs.Tests.{Guid.NewGuid()}" };
        this.expectedResourceAttributes = new Dictionary<string, AnyValue>
        {
            ["service.name"] = new() { StringValue = "test-aws-service" },
            ["service.namespace"] = new() { StringValue = "test-namespace" },
            ["service.version"] = new() { StringValue = "1.2.3" },
            ["service.instance.id"] = new() { StringValue = "test-instance" },
            ["deployment.environment.name"] = new() { StringValue = "test" },
            ["test.resource.count"] = new() { IntValue = 3 },
            ["test.resource.enabled"] = new() { BoolValue = true },
        };
        this.Options.Headers += ",x-aws-log-group=test-log-group,x-aws-log-stream=test-log-stream";
    }

    public void Dispose()
    {
        this.loggerFactory?.Dispose();
        this.Transport.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override void ValidateOtlpPayload(byte[] payload, ExpectedLogRecord expectedPayload)
    {
        // Decode the OTLP logs envelope with the existing protobuf reader and common message types.
        var exported = ReadFields(payload);
        var resourceLogs = ReadFields(((ByteString)Assert.Single(exported[1])).ToByteArray());
        var resource = OtlpResource.Parser.ParseFrom((ByteString)Assert.Single(resourceLogs[1]));
        var resourceAttributes = resource.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal(this.expectedResourceAttributes.OrderBy(attribute => attribute.Key), resourceAttributes.OrderBy(attribute => attribute.Key));

        var scopeLogs = ReadFields(((ByteString)Assert.Single(resourceLogs[2])).ToByteArray());
        Assert.Equal(this.expectedScope, InstrumentationScope.Parser.ParseFrom((ByteString)Assert.Single(scopeLogs[1])));
        var logRecord = ReadFields(((ByteString)Assert.Single(scopeLogs[2])).ToByteArray());
        Assert.Equal(new[] { 1, 2, 3, 5, 6, 8, 9, 10, 11 }, logRecord.Keys.OrderBy(field => field));
        Assert.Equal(expectedPayload.TimestampUnixNano, (ulong)Assert.Single(logRecord[1]));
        Assert.Equal(expectedPayload.TimestampUnixNano, (ulong)Assert.Single(logRecord[11]));
        Assert.Equal(17UL, (ulong)Assert.Single(logRecord[2])); // OTLP severity ERROR.
        Assert.Equal("Error", ((ByteString)Assert.Single(logRecord[3])).ToStringUtf8());
        Assert.Equal(new AnyValue { StringValue = expectedPayload.Body }, AnyValue.Parser.ParseFrom((ByteString)Assert.Single(logRecord[5])));
        Assert.Equal((uint)ActivityTraceFlags.Recorded, (uint)Assert.Single(logRecord[8]));
        Assert.Equal(expectedPayload.TraceId, (ByteString)Assert.Single(logRecord[9]));
        Assert.Equal(expectedPayload.SpanId, (ByteString)Assert.Single(logRecord[10]));
        var attributes = logRecord[6].Cast<ByteString>()
            .Select(attribute => KeyValue.Parser.ParseFrom(attribute))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal(this.expectedLogAttributes.OrderBy(attribute => attribute.Key), attributes.OrderBy(attribute => attribute.Key));
        Assert.All(this.Transport.Requests, request =>
        {
            Assert.Equal("test-log-group", request.Headers["x-aws-log-group"]);
            Assert.Equal("test-log-stream", request.Headers["x-aws-log-stream"]);
            Assert.Contains("x-aws-log-group", request.Headers["Authorization"]);
            Assert.Contains("x-aws-log-stream", request.Headers["Authorization"]);
        });
    }

    /// <inheritdoc/>
    protected override ExportResult Export(ExpectedLogRecord expectedPayload)
    {
        // Create the exporter after the test selects compression; reuse the same logger provider.
        if (this.loggerFactory == null)
        {
            var exporter = new OtlpAwsLogRecordExporter(this.Options, this.Authenticator.Object, () => this.Transport);
            this.processor = new LogExportProcessor(exporter, this.timestamp);
            this.loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.SetResourceBuilder(ResourceBuilder.CreateEmpty()
                    .AddService("test-aws-service")
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["service.namespace"] = "test-namespace",
                        ["service.version"] = "1.2.3",
                        ["service.instance.id"] = "test-instance",
                        ["deployment.environment.name"] = "test",
                        ["test.resource.count"] = 3L,
                        ["test.resource.enabled"] = true,
                    }));
                options.AddProcessor(this.processor);
            }));
            this.logger = this.loggerFactory.CreateLogger(this.expectedScope.Name);
        }

        using var activity = new Activity("test log activity")
            .SetParentId("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01")
            .Start();
        expectedPayload.TraceId = ByteString.CopyFrom(Convert.FromHexString(activity.TraceId.ToHexString()));
        expectedPayload.SpanId = ByteString.CopyFrom(Convert.FromHexString(activity.SpanId.ToHexString()));
        using var scope = this.logger!.BeginScope(new Dictionary<string, object> { ["test.scope.name"] = "test scope" });
        this.logger.Log(
            LogLevel.Error,
            default,
            new List<KeyValuePair<string, object?>>
            {
                new("test.log.name", "test"),
                new("test.log.count", 3L),
                new("test.log.enabled", true),
            },
            exception: null,
            formatter: (_, _) => expectedPayload.Body);
        return this.processor!.Result;
    }

    /// <inheritdoc/>
    protected override ExpectedLogRecord CreateExpectedPayload()
        => new((ulong)new DateTimeOffset(this.timestamp).ToUnixTimeMilliseconds() * 1_000_000, $"test log {Guid.NewGuid()}");

    /// <summary>
    /// Reads protobuf fields while preserving repeated values, without generated logs message types.
    /// </summary>
    private static Dictionary<int, List<object>> ReadFields(byte[] payload)
    {
        using var reader = new CodedInputStream(payload);
        var fields = new Dictionary<int, List<object>>();
        uint tag;
        while ((tag = reader.ReadTag()) != 0)
        {
            object value = WireFormat.GetTagWireType(tag) switch
            {
                WireFormat.WireType.Varint => reader.ReadUInt64(),
                WireFormat.WireType.Fixed64 => reader.ReadFixed64(),
                WireFormat.WireType.LengthDelimited => reader.ReadBytes(),
                WireFormat.WireType.Fixed32 => reader.ReadFixed32(),
                _ => throw new InvalidOperationException($"Unexpected protobuf wire type for tag {tag}."),
            };
            var field = WireFormat.GetTagFieldNumber(tag);
            if (!fields.TryGetValue(field, out var values))
            {
                values = new List<object>();
                fields.Add(field, values);
            }

            values.Add(value);
        }

        return fields;
    }

    /// <summary>
    /// Defines the independent expected log content and trace identifiers captured before export.
    /// </summary>
    public sealed record ExpectedLogRecord(ulong TimestampUnixNano, string Body)
    {
        public ByteString TraceId { get; set; } = ByteString.Empty;

        public ByteString SpanId { get; set; } = ByteString.Empty;
    }

    /// <summary>
    /// Exports real SDK log records synchronously and records the export result for the common assertions.
    /// </summary>
    private sealed class LogExportProcessor : BaseExportProcessor<LogRecord>
    {
        private readonly DateTime timestamp;

        public LogExportProcessor(OtlpAwsLogRecordExporter exporter, DateTime timestamp)
            : base(exporter)
        {
            this.timestamp = timestamp;
        }

        public ExportResult Result { get; private set; }

        protected override void OnExport(LogRecord data)
        {
            data.Timestamp = this.timestamp;
            data.ObservedTimestamp = this.timestamp;
            this.Result = this.exporter.Export(new Batch<LogRecord>(data));
        }
    }
}
