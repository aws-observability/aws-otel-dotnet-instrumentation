// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;
using OpenTelemetry;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Xray.Udp;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpExporterUtilsTest
{
    [Fact]
    public void ExportRejectsOversizedBatchWithoutAffectingNextBatch()
    {
        using var activity = new Activity("oversized span").Start();
        activity.SetTag("large.attribute", new string('x', 100_000));
        activity.Stop();
        using var exporter = new XrayUdpExporter(Resource.Empty);

        Assert.Equal(ExportResult.Failure, exporter.Export(new Batch<Activity>(activity)));

        using var nextActivity = new Activity("following span").Start();
        nextActivity.Stop();
        byte[] buffer = new byte[65527];
        int length = OtlpExporterUtils.WriteTraceData(ref buffer, 0, Resource.Empty, new Batch<Activity>(nextActivity));
        var exported = ExportTraceServiceRequest.Parser.ParseFrom(buffer, 0, length);
        var span = Assert.Single(Assert.Single(Assert.Single(exported.ResourceSpans).ScopeSpans).Spans);

        Assert.Equal("following span", span.Name);
    }

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
}
