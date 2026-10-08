// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Logs;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Resources;
using OtlpLogRecord = OpenTelemetry.Proto.Logs.V1.LogRecord;
using OtlpSeverityNumber = OpenTelemetry.Proto.Logs.V1.SeverityNumber;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Logs;

/// <summary>
/// Creates logs and validates their OTLP content, resource attributes, and instrumentation scope.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsLogExporterTests : AbstractOtlpAwsExporterTest<OtlpLogRecord>, IDisposable
{
    private readonly DateTime timestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly OtlpLogRecord expectedLog;
    private readonly InstrumentationScope expectedScope;
    private readonly Dictionary<string, AnyValue> expectedResourceAttributes;
    private ILoggerFactory? loggerFactory;
    private ILogger? logger;
    private LogExportProcessor? processor;

    public OtlpAwsLogExporterTests()
        : base(new Uri("https://logs.us-west-2.amazonaws.com/v1/logs"), "us-west-2", "logs")
    {
        var timestampUnixNano = (ulong)new DateTimeOffset(this.timestamp).ToUnixTimeMilliseconds() * 1_000_000;
        this.expectedLog = new OtlpLogRecord
        {
            TimeUnixNano = timestampUnixNano,
            ObservedTimeUnixNano = timestampUnixNano,
            SeverityNumber = OtlpSeverityNumber.Error,
            SeverityText = "Error",
            Body = new AnyValue { StringValue = "test log" },
            TraceId = ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef0123456789abcdef")),
            SpanId = ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef")),
            Flags = (uint)ActivityTraceFlags.Recorded,
            Attributes =
            {
                new KeyValue { Key = "test.log.name", Value = new AnyValue { StringValue = "test" } },
                new KeyValue { Key = "test.log.count", Value = new AnyValue { IntValue = 3 } },
                new KeyValue { Key = "test.log.enabled", Value = new AnyValue { BoolValue = true } },
                new KeyValue { Key = "test.scope.name", Value = new AnyValue { StringValue = "test scope" } },
            },
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
    protected override void ValidateOtlpPayload(byte[] payload, OtlpLogRecord expectedPayload)
    {
        var exported = ExportLogsServiceRequest.Parser.ParseFrom(payload);
        var resourceLogs = Assert.Single(exported.ResourceLogs);
        var resourceAttributes = resourceLogs.Resource.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal(this.expectedResourceAttributes.OrderBy(attribute => attribute.Key), resourceAttributes.OrderBy(attribute => attribute.Key));

        var scopeLogs = Assert.Single(resourceLogs.ScopeLogs);
        Assert.Equal(this.expectedScope, scopeLogs.Scope);
        Assert.Equal(expectedPayload, Assert.Single(scopeLogs.LogRecords));
        Assert.All(this.Transport.Requests, request =>
        {
            Assert.Equal("test-log-group", request.Headers["x-aws-log-group"]);
            Assert.Equal("test-log-stream", request.Headers["x-aws-log-stream"]);
            Assert.Contains("x-aws-log-group", request.Headers["Authorization"]);
            Assert.Contains("x-aws-log-stream", request.Headers["Authorization"]);
        });
    }

    /// <inheritdoc/>
    protected override ExportResult Export(OtlpLogRecord expectedPayload)
    {
        // Create the exporter after the test selects compression; reuse the same logger provider.
        if (this.loggerFactory == null)
        {
            var exporter = OtlpAwsLogExporter.Create(this.Options, this.Authenticator.Object, () => this.Transport);
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
            formatter: (_, _) => expectedPayload.Body.StringValue);
        return this.processor!.Result;
    }

    /// <inheritdoc/>
    protected override OtlpLogRecord CreateExpectedPayload() => this.expectedLog.Clone();

    /// <summary>
    /// Exports real SDK log records synchronously and records the export result for the common assertions.
    /// </summary>
    private sealed class LogExportProcessor : BaseExportProcessor<LogRecord>
    {
        private readonly DateTime timestamp;

        public LogExportProcessor(OtlpLogExporter exporter, DateTime timestamp)
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
