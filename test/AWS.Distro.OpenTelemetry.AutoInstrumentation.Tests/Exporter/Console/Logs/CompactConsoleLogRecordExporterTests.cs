// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Console.Logs;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Console.Logs;

// The collection doesn't run in parallel, because a test here changes an environment variable.
[Collection("AWS credential providers")]
public class CompactConsoleLogRecordExporterTests
{
    [Fact]
    public void ShouldWriteOneLinePerLogRecord()
    {
        var records = Export(logger =>
        {
            logger.LogInformation("first");
            logger.LogInformation("Processing {Id}", 1);
            logger.LogInformation("Processing {Id}", 2);
        });

        Assert.Equal(
            new[] { "first", "Processing {Id}", "Processing {Id}" },
            records.Select(record => record.GetProperty("body").GetString()));
    }

    [Fact]
    public void ShouldWriteScopeTimestampsAndEmptyTraceContext()
    {
        var before = ToUnixNano(DateTime.UtcNow);
        var record = Export(logger => logger.LogInformation("hello"), category: "MyCategory").Single();
        var after = ToUnixNano(DateTime.UtcNow);

        Assert.Equal("MyCategory", record.GetProperty("scope").GetProperty("name").GetString());
        Assert.InRange(record.GetProperty("timeUnixNano").GetInt64(), before, after);
        Assert.Equal(record.GetProperty("timeUnixNano").GetInt64(), record.GetProperty("observedTimeUnixNano").GetInt64());
        Assert.Equal(string.Empty, record.GetProperty("traceId").GetString());
        Assert.Equal(string.Empty, record.GetProperty("spanId").GetString());
        Assert.Equal(0, record.GetProperty("flags").GetInt32());
        Assert.Equal(0, record.GetProperty("droppedAttributes").GetInt32());
    }

    [Theory]
    [InlineData(LogLevel.Trace, 1, "TRACE")]
    [InlineData(LogLevel.Debug, 5, "DEBUG")]
    [InlineData(LogLevel.Information, 9, "INFO")]
    [InlineData(LogLevel.Warning, 13, "WARN")]
    [InlineData(LogLevel.Error, 17, "ERROR")]
    [InlineData(LogLevel.Critical, 21, "FATAL")]
    public void ShouldMapLogLevelToSeverity(LogLevel level, int expectedNumber, string expectedText)
    {
        var record = Export(logger => logger.Log(level, "hello")).Single();

        Assert.Equal(expectedNumber, record.GetProperty("severityNumber").GetInt32());
        Assert.Equal(expectedText, record.GetProperty("severityText").GetString());
    }

    [Fact]
    public void ShouldPreserveAttributeValueTypes()
    {
        var record = Export(logger => logger.LogInformation(
            "{Int} {Long} {Double} {NaN} {Bool} {Null} {Array} {Text} {Date}",
            1,
            2L,
            1.5,
            double.NaN,
            true,
            null,
            new[] { 1, 2 },
            "text",
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))).Single();

        var attributes = record.GetProperty("attributes");
        Assert.Equal(1, attributes.GetProperty("Int").GetInt32());
        Assert.Equal(2L, attributes.GetProperty("Long").GetInt64());
        Assert.Equal(1.5, attributes.GetProperty("Double").GetDouble());
        Assert.Equal("NaN", attributes.GetProperty("NaN").GetString());
        Assert.True(attributes.GetProperty("Bool").GetBoolean());
        Assert.Equal(JsonValueKind.Null, attributes.GetProperty("Null").ValueKind);
        Assert.Equal(new[] { 1, 2 }, attributes.GetProperty("Array").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal("text", attributes.GetProperty("Text").GetString());
        Assert.Equal("01/02/2026 00:00:00", attributes.GetProperty("Date").GetString());
        Assert.Equal("{Int} {Long} {Double} {NaN} {Bool} {Null} {Array} {Text} {Date}", record.GetProperty("body").GetString());
    }

    [Fact]
    public void ShouldWriteExpectedFields()
    {
        var record = Export(logger => logger.LogInformation("hello")).Single();

        Assert.Equal(
            new[]
            {
                "resource", "scope", "body", "severityNumber", "severityText", "attributes", "droppedAttributes",
                "timeUnixNano", "observedTimeUnixNano", "traceId", "spanId", "flags",
            },
            record.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            new[] { "attributes", "schemaUrl" },
            record.GetProperty("resource").EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            new[] { "name", "version", "schemaUrl" },
            record.GetProperty("scope").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void ShouldFormatValuesWithInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var record = Export(logger => logger.LogInformation(
                "{Date}", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))).Single();

            Assert.Equal("01/02/2026 00:00:00", record.GetProperty("attributes").GetProperty("Date").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void ShouldWriteFloatsAndNonFiniteNumbers()
    {
        var record = Export(logger => logger.LogInformation(
            "{Float} {FloatInfinity} {DoubleInfinity}", 1.5f, float.PositiveInfinity, double.NegativeInfinity)).Single();

        var attributes = record.GetProperty("attributes");
        Assert.Equal(1.5f, attributes.GetProperty("Float").GetSingle());
        Assert.Equal("Infinity", attributes.GetProperty("FloatInfinity").GetString());
        Assert.Equal("-Infinity", attributes.GetProperty("DoubleInfinity").GetString());
    }

    [Theory]
    [InlineData("true", "console")]
    [InlineData(null, null)]
    public void ShouldWriteExportPathOnlyWhenTestFlagIsEnabled(string? flag, string? expectedExportPath)
    {
        var originalFlag = Environment.GetEnvironmentVariable("ADOT_TEST_EXPORT_PATH_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("ADOT_TEST_EXPORT_PATH_ENABLED", flag);

            var record = Export(logger => logger.LogInformation("hello")).Single();

            var exportPath = record.TryGetProperty("exportPath", out var value) ? value.GetString() : null;
            Assert.Equal(expectedExportPath, exportPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADOT_TEST_EXPORT_PATH_ENABLED", originalFlag);
        }
    }

    [Fact]
    public void ShouldWriteResourceAttributes()
    {
        var record = Export(
            logger => logger.LogInformation("hello"),
            options => options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("checkout"))).Single();

        Assert.Equal(
            "checkout",
            record.GetProperty("resource").GetProperty("attributes").GetProperty("service.name").GetString());
    }

    [Fact]
    public void ShouldWriteTraceContextOfCurrentActivity()
    {
        using var activity = new Activity("operation");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        activity.Start();

        var record = Export(logger => logger.LogInformation("hello")).Single();

        Assert.Equal(activity.TraceId.ToString(), record.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToString(), record.GetProperty("spanId").GetString());
        Assert.Equal((int)activity.ActivityTraceFlags, record.GetProperty("flags").GetInt32());
    }

    [Fact]
    public void ShouldWriteExceptionAsAttributes()
    {
        var record = Export(logger =>
        {
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "failed");
            }
        }).Single();

        var attributes = record.GetProperty("attributes");
        Assert.Equal("InvalidOperationException", attributes.GetProperty("exception.type").GetString());
        Assert.Equal("boom", attributes.GetProperty("exception.message").GetString());
        Assert.Contains(nameof(this.ShouldWriteExceptionAsAttributes), attributes.GetProperty("exception.stacktrace").GetString());
    }

    [Fact]
    public void ShouldWriteMinimalJsonWhenSerializationFails()
    {
        var record = Export(logger => logger.LogInformation("{Value}", new ThrowingToString())).Single();

        Assert.Equal("{Value}", record.GetProperty("body").GetString());
        Assert.Equal("serialization_failed", record.GetProperty("error").GetString());
    }

    [Fact]
    public void ShouldKeepOtherRecordsInBatchWhenOneFailsToSerialize()
    {
        var records = Export(
            logger =>
            {
                logger.LogInformation("before");
                logger.LogInformation("{Value}", new ThrowingToString());
                logger.LogInformation("after");
            },
            batch: true);

        Assert.Equal(new[] { "before", "{Value}", "after" }, records.Select(record => record.GetProperty("body").GetString()));
        Assert.Equal(new[] { false, true, false }, records.Select(record => record.TryGetProperty("error", out _)));
    }

    private static long ToUnixNano(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks * 100;

    private static List<JsonElement> Export(
        Action<ILogger> log, Action<OpenTelemetryLoggerOptions>? configure = null, string category = "test", bool batch = false)
    {
        var output = new StringWriter();
        using (var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddOpenTelemetry(options =>
            {
                configure?.Invoke(options);
                var exporter = new CompactConsoleLogRecordExporter(output);
                BaseProcessor<LogRecord> processor = batch
                    ? new BatchLogRecordExportProcessor(exporter)
                    : new SimpleLogRecordExportProcessor(exporter);
                options.AddProcessor(processor);
            })))
        {
            log(loggerFactory.CreateLogger(category));
        }

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("ToString failed");
    }
}
