// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Traces;
using Google.Protobuf;
using OpenTelemetry;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using OtlpStatus = OpenTelemetry.Proto.Trace.V1.Status;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Traces;

/// <summary>
/// Creates spans and validates their OTLP content, resource attributes, and instrumentation scope.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsSpanExporterTests : AbstractOtlpAwsExporterTest<OtlpSpan>, IDisposable
{
    private readonly DateTimeOffset startTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly OtlpSpan expectedSpan;
    private readonly InstrumentationScope expectedScope;
    private readonly Dictionary<string, AnyValue> expectedResourceAttributes;
    private BaseExporter<Activity>? exporter;
    private TracerProvider? provider;

    public OtlpAwsSpanExporterTests()
        : base(new Uri("https://xray.us-west-2.amazonaws.com/v1/traces"), "us-west-2", "xray")
    {
        var startTimeUnixNano = (ulong)this.startTime.ToUnixTimeMilliseconds() * 1_000_000;
        this.expectedSpan = new OtlpSpan
        {
            Name = "test span",
            Kind = OtlpSpan.Types.SpanKind.Server,
            TraceId = ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef0123456789abcdef")),
            ParentSpanId = ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef")),
            TraceState = "test=state",
            Flags = (uint)ActivityTraceFlags.Recorded | 0x100,
            StartTimeUnixNano = startTimeUnixNano,
            EndTimeUnixNano = startTimeUnixNano + 1_000_000_000,
            Status = new OtlpStatus { Code = OtlpStatus.Types.StatusCode.Error, Message = "test error" },
            Attributes =
            {
                new KeyValue { Key = "http.request.method", Value = new AnyValue { StringValue = "GET" } },
                new KeyValue { Key = "http.response.status_code", Value = new AnyValue { IntValue = 500 } },
                new KeyValue { Key = "test.span.enabled", Value = new AnyValue { BoolValue = true } },
            },
            Events =
            {
                new OtlpSpan.Types.Event { Name = "test event", TimeUnixNano = startTimeUnixNano + 250_000_000 },
            },
        };
        this.expectedScope = new InstrumentationScope { Name = $"SigV4.Tests.{Guid.NewGuid()}", Version = "1.2.3" };
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
    }

    public void Dispose()
    {
        this.provider?.Dispose();
        this.exporter?.Dispose();
        this.Transport.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override void ValidateOtlpPayload(byte[] payload, OtlpSpan expectedPayload)
    {
        var exported = ExportTraceServiceRequest.Parser.ParseFrom(payload);
        var resourceSpans = Assert.Single(exported.ResourceSpans);
        var resourceAttributes = resourceSpans.Resource.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal(this.expectedResourceAttributes.OrderBy(attribute => attribute.Key), resourceAttributes.OrderBy(attribute => attribute.Key));

        var scopeSpans = Assert.Single(resourceSpans.ScopeSpans);
        Assert.Equal(this.expectedScope, scopeSpans.Scope);
        Assert.Equal(expectedPayload, Assert.Single(scopeSpans.Spans));
    }

    /// <inheritdoc/>
    protected override ExportResult Export(OtlpSpan expectedPayload)
    {
        using var activity = this.CreateSpan(expectedPayload);

        // Create the exporter after the test selects compression; reuse it across exports.
        if (this.exporter == null)
        {
            this.exporter = OtlpAwsSpanExporter.Create(this.Options, this.Authenticator.Object, () => this.Transport);
            this.provider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateEmpty()
                    .AddService("test-aws-service")
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["service.namespace"] = "test-namespace",
                        ["service.version"] = "1.2.3",
                        ["service.instance.id"] = "test-instance",
                        ["deployment.environment.name"] = "test",
                        ["test.resource.count"] = 3L,
                        ["test.resource.enabled"] = true,
                    }))
                .AddProcessor(new SimpleActivityExportProcessor(this.exporter))
                .Build();
        }

        return this.exporter.Export(new Batch<Activity>(activity));
    }

    /// <inheritdoc/>
    protected override OtlpSpan CreateExpectedPayload() => this.expectedSpan.Clone();

    private Activity CreateSpan(OtlpSpan expected)
    {
        using var source = new ActivitySource(this.expectedScope.Name, this.expectedScope.Version);
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var parent = new ActivityContext(
            ActivityTraceId.CreateFromBytes(expected.TraceId.ToByteArray()),
            ActivitySpanId.CreateFromBytes(expected.ParentSpanId.ToByteArray()),
            ActivityTraceFlags.Recorded,
            expected.TraceState);
        var activity = source.StartActivity(expected.Name, ActivityKind.Server, parent, startTime: this.startTime);
        Assert.NotNull(activity);
        expected.SpanId = ByteString.CopyFrom(Convert.FromHexString(activity.SpanId.ToHexString()));
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.response.status_code", 500L);
        activity.SetTag("test.span.enabled", true);
        activity.SetStatus(ActivityStatusCode.Error, "test error");
        activity.AddEvent(new ActivityEvent("test event", this.startTime.AddMilliseconds(250)));
        activity.SetEndTime(this.startTime.AddSeconds(1).UtcDateTime);
        activity.Stop();
        return activity;
    }
}
