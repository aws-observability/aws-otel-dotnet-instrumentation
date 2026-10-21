// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Xray.Udp;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpExporterUtilsTest
{
    [Fact]
    public void WriteTraceDataSerializesBatchWithResolvedOtlpExporter()
    {
        using var source = new ActivitySource($"Xray.Udp.Tests.{Guid.NewGuid()}");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("test span");
        Assert.NotNull(activity);
        activity.Stop();
        var resource = ResourceBuilder.CreateEmpty().AddService("test-udp-service").Build();

        // Same buffer size XrayUdpExporter uses.
        byte[] buffer = new byte[65527];
        int length = OtlpExporterUtils.WriteTraceData(ref buffer, 0, resource, new Batch<Activity>(activity));

        Assert.True(length > 0);
        var exported = ExportTraceServiceRequest.Parser.ParseFrom(buffer, 0, length);
        var resourceSpans = Assert.Single(exported.ResourceSpans);
        Assert.Contains(resourceSpans.Resource.Attributes, attribute => attribute.Key == "service.name" && attribute.Value.StringValue == "test-udp-service");
        Assert.Equal("test span", Assert.Single(Assert.Single(resourceSpans.ScopeSpans).Spans).Name);
    }

    [Fact]
    public void WriteLogsDataSerializesBatchWithResolvedOtlpExporter()
    {
        // LogRecord has no public constructor, so capture a real batch inside an exporter.
        var exporter = new WriteLogsDataExporter();
        using (var loggerFactory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter)))))
        {
            loggerFactory.CreateLogger("test").LogInformation("test log message");
        }

        Assert.True(exporter.Length > 0);
        Assert.Contains("test log message", Encoding.UTF8.GetString(exporter.Buffer, 0, exporter.Length));
    }

    private sealed class WriteLogsDataExporter : BaseExporter<LogRecord>
    {
        // Same buffer size SigV4OtlpLogExporter uses.
        public byte[] Buffer { get; private set; } = new byte[750000];

        public int Length { get; private set; }

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            byte[] buffer = this.Buffer;
            this.Length = OtlpExporterUtils.WriteLogsData(ref buffer, 0, Resource.Empty, batch);
            this.Buffer = buffer;
            return ExportResult.Success;
        }
    }
}
