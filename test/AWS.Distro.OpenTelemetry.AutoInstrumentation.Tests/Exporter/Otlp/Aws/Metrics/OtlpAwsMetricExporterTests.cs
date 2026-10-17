// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Metrics;
using Google.Protobuf;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Resources;
using OtlpResource = OpenTelemetry.Proto.Resource.V1.Resource;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Metrics;

/// <summary>
/// Creates metrics and validates their OTLP content, resource attributes, and instrumentation scope.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsMetricExporterTests : AbstractOtlpAwsExporterTest<OtlpAwsMetricExporterTests.OtlpMetric>, IDisposable
{
    private readonly Dictionary<string, AnyValue> expectedMetricAttributes;
    private readonly InstrumentationScope expectedScope;
    private readonly Dictionary<string, AnyValue> expectedResourceAttributes;
    private readonly OtlpMetric expectedMetric;
    private MeterProvider? provider;
    private BaseExportingMetricReader? reader;
    private Meter? meter;
    private Counter<long>? counter;

    public OtlpAwsMetricExporterTests()
        : base(new Uri("https://monitoring.us-west-2.amazonaws.com/v1/metrics"), "us-west-2", "monitoring")
    {
        this.expectedMetric = new OtlpMetric("test.counter", "Test request count", "{request}", 3);
        this.expectedMetricAttributes = new Dictionary<string, AnyValue>
        {
            ["test.metric.name"] = new() { StringValue = "test" },
            ["test.metric.count"] = new() { IntValue = 3 },
            ["test.metric.enabled"] = new() { BoolValue = true },
        };
        this.expectedScope = new InstrumentationScope { Name = $"SigV4.Metrics.Tests.{Guid.NewGuid()}", Version = "1.2.3" };
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
        this.meter?.Dispose();
        this.provider?.Dispose();
        this.Transport.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override void ValidateOtlpPayload(byte[] payload, OtlpMetric expectedPayload)
    {
        var exported = ReadFields(payload);
        var resourceMetrics = ReadFields(((ByteString)Assert.Single(exported[1])).ToByteArray());
        var resource = OtlpResource.Parser.ParseFrom((ByteString)Assert.Single(resourceMetrics[1]));
        var resourceAttributes = resource.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal(this.expectedResourceAttributes.OrderBy(attribute => attribute.Key), resourceAttributes.OrderBy(attribute => attribute.Key));

        var scopeMetrics = ReadFields(((ByteString)Assert.Single(resourceMetrics[2])).ToByteArray());
        Assert.Equal(this.expectedScope, InstrumentationScope.Parser.ParseFrom((ByteString)Assert.Single(scopeMetrics[1])));
        var metric = ReadFields(((ByteString)Assert.Single(scopeMetrics[2])).ToByteArray());
        Assert.Equal(expectedPayload.Name, ((ByteString)Assert.Single(metric[1])).ToStringUtf8());
        Assert.Equal(expectedPayload.Description, ((ByteString)Assert.Single(metric[2])).ToStringUtf8());
        Assert.Equal(expectedPayload.Unit, ((ByteString)Assert.Single(metric[3])).ToStringUtf8());
        var sum = ReadFields(((ByteString)Assert.Single(metric[7])).ToByteArray());
        Assert.Equal(1UL, (ulong)Assert.Single(sum[2])); // OTLP delta temporality.
        Assert.Equal(1UL, (ulong)Assert.Single(sum[3])); // Monotonic counter.
        var dataPoint = ReadFields(((ByteString)Assert.Single(sum[1])).ToByteArray());
        Assert.Equal(expectedPayload.Value, unchecked((long)(ulong)Assert.Single(dataPoint[6])));
        var timestamp = (ulong)Assert.Single(dataPoint[3]);
        Assert.InRange(timestamp, expectedPayload.EarliestTimestampUnixNano, expectedPayload.LatestTimestampUnixNano);
        Assert.InRange((ulong)Assert.Single(dataPoint[2]), 1UL, timestamp);
        var attributes = dataPoint[7].Cast<ByteString>()
            .Select(attribute => KeyValue.Parser.ParseFrom(attribute))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal(this.expectedMetricAttributes.OrderBy(attribute => attribute.Key), attributes.OrderBy(attribute => attribute.Key));
    }

    /// <inheritdoc/>
    protected override ExportResult Export(OtlpMetric expectedPayload)
    {
        // Create the exporter after the test selects compression; reuse the same meter provider.
        if (this.provider == null)
        {
            var exporter = OtlpAwsMetricExporter.Create(this.Options, this.Authenticator.Object, () => this.Transport);
            this.reader = new BaseExportingMetricReader(exporter)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Delta,
            };
            this.provider = Sdk.CreateMeterProviderBuilder()
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
                .AddMeter(this.expectedScope.Name)
                .AddReader(this.reader)
                .Build();
            this.meter = new Meter(this.expectedScope.Name, this.expectedScope.Version);
            this.counter = this.meter.CreateCounter<long>(expectedPayload.Name, expectedPayload.Unit, expectedPayload.Description);
        }

        expectedPayload.EarliestTimestampUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        this.counter!.Add(
            expectedPayload.Value,
            new KeyValuePair<string, object?>("test.metric.name", "test"),
            new KeyValuePair<string, object?>("test.metric.count", 3L),
            new KeyValuePair<string, object?>("test.metric.enabled", true));
        var succeeded = this.reader!.Collect();
        expectedPayload.LatestTimestampUnixNano = ((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1) * 1_000_000;
        return succeeded ? ExportResult.Success : ExportResult.Failure;
    }

    /// <inheritdoc/>
    protected override OtlpMetric CreateExpectedPayload() => this.expectedMetric with { };

    /// <summary>
    /// Defines the independent expected metric content and collection timestamp bounds.
    /// </summary>
    public sealed record OtlpMetric(string Name, string Description, string Unit, long Value)
    {
        public ulong EarliestTimestampUnixNano { get; set; }

        public ulong LatestTimestampUnixNano { get; set; }
    }
}
