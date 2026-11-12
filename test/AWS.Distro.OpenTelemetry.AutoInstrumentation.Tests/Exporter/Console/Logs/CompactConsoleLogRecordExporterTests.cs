// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Console.Logs;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Console.Logs;

public class CompactConsoleLogRecordExporterTests
{
    [Fact]
    public void ShouldWriteExceptionAsAttributes()
    {
        var output = new StringWriter();
        using (var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
            options.AddProcessor(new SimpleLogRecordExportProcessor(new CompactConsoleLogRecordExporter(output))))))
        {
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (InvalidOperationException ex)
            {
                loggerFactory.CreateLogger("test").LogError(ex, "failed");
            }
        }

        var attributes = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("attributes");
        Assert.Equal("InvalidOperationException", attributes.GetProperty("exception.type").GetString());
        Assert.Equal("boom", attributes.GetProperty("exception.message").GetString());
        Assert.Contains(nameof(this.ShouldWriteExceptionAsAttributes), attributes.GetProperty("exception.stacktrace").GetString());
    }
}
