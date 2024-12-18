// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using HarmonyLib;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// There is two test that is not implemented in this Class, comparing with Java:

// 1. testIsRequired()
// Implementation of AwsSpanMetricsProcessor.isStartRequired() and isEndRequired() do not exist

// 2. testsOnEndMetricsGenerationLocalRootServerSpan()
// This test cannot be done here because there is no difference (or cannot set difference) in dotnet for
// a null parent information and a invalid parent information
// Found no way to setup a Activity.Parent to a default/invalid value,
// It either valid (set by passing a parent ID and automatically matching Activity.Parent field)
// or just Null
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8602 // Possible null reference argument.
public class AwsSpanMetricsProcessorTest : IDisposable
{
    private readonly double testLatencyMillis = 150;
    private AwsSpanMetricsProcessor awsSpanMetricsProcessor;
    private Mock<AwsMetricAttributeGenerator> generator = new Mock<AwsMetricAttributeGenerator>();
    private Resource resource = Resource.Empty;
    private MeterProvider provider = Sdk.CreateMeterProviderBuilder().Build();
    private Meter meter = new Meter("test");
    private Histogram<long> errorHistogram;
    private Histogram<long> faultHistogram;
    private Histogram<double> latencyHistogram;
    private ActivitySource activitySource = new ActivitySource("test");

    public AwsSpanMetricsProcessorTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        this.errorHistogram = this.meter.CreateHistogram<long>("error");
        this.faultHistogram = this.meter.CreateHistogram<long>("fault");
        this.latencyHistogram = this.meter.CreateHistogram<double>("latency");
        var meterListener = new MeterListener();
        meterListener.EnableMeasurementEvents(this.errorHistogram);
        meterListener.EnableMeasurementEvents(this.faultHistogram);
        meterListener.EnableMeasurementEvents(this.latencyHistogram);
        this.meter.Tags.AddItem(new KeyValuePair<string, object?>("test", "test"));
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
                {
                    var list = GlobalCallbackData.CallList is null ? new List<KeyValuePair<string, object?>>() : GlobalCallbackData.CallList;
                    list.Add(new KeyValuePair<string, object?>(instrument.Name, new KeyValuePair<long, object?>(measurement, tags[0])));
                    GlobalCallbackData.CallList = list;
                });
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
                {
                    var list = GlobalCallbackData.CallList is null ? new List<KeyValuePair<string, object?>>() : GlobalCallbackData.CallList;
                    list.Add(new KeyValuePair<string, object?>(instrument.Name, new KeyValuePair<double, object?>(measurement, tags[0])));
                    GlobalCallbackData.CallList = list;
                });
        this.awsSpanMetricsProcessor = AwsSpanMetricsProcessor.Create(this.errorHistogram, this.faultHistogram, this.latencyHistogram, this.generator.Object, this.resource, this.provider);
    }

    public void Dispose()
    {
        GlobalCallbackData.Clear();
    }

    [Fact]
    public void TestStartDoesNothingToSpan()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test");
        var parentInfo = spanDataMock.ParentSpanId;
        this.awsSpanMetricsProcessor.OnStart(spanDataMock);
        Assert.Equal(parentInfo, spanDataMock.ParentSpanId);
    }

    [Fact]
    public void TestTearDown()
    {
        Assert.True(this.awsSpanMetricsProcessor.Shutdown());
        Assert.True(this.awsSpanMetricsProcessor.ForceFlush());
    }

    /// <summary>
    /// Tests starting with testOnEndMetricsGeneration are testing the logic in
    /// AwsSpanMetricsProcessor's onEnd method pertaining to metrics generation.
    /// </summary>
    [Fact]
    public void TestOnEndMetricsGenerationWithoutSpanAttributes()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Server);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        spanDataMock.SetEndTime(DateTime.Now);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 1, 0);
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithoutMetricAttributes()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Server);
        this.SetLatency(spanDataMock);
        spanDataMock.SetTag(AttributeHttpResponseStatusCode, 500L);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(false, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 0, 0);
    }

    [Fact]
    public void TestsOnEndMetricsGenerationLocalRootConsumerSpan()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Consumer);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 1, 1);
    }

    [Fact]
    public void TestsOnEndMetricsGenerationLocalRootClientSpan()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Client);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 1, 1);
    }

    [Fact]
    public void TestsOnEndMetricsGenerationLocalRootProducerSpan()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Producer);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 1, 1);
    }

    [Fact]
    public void TestsOnEndMetricsGenerationLocalRootInternalSpan()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Internal);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 1, 0);
    }

    [Fact]
    public void TestsOnEndMetricsGenerationLocalRootProducerSpanWithoutMetricAttributes()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Producer);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(false, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.VerifyHistogramRecords(expectAttributes, 0, 0);
    }

    [Fact]
    public void TestsOnEndMetricsGenerationClientSpan()
    {
        Activity? parentSpan = this.activitySource.StartActivity("test parent");
        using (Activity? spanDataMock = this.activitySource.StartActivity("test Child", ActivityKind.Client))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            spanDataMock.Start();
            this.SetLatency(spanDataMock);
            Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
            this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
                .Returns(expectAttributes);
            this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
            this.VerifyHistogramRecords(expectAttributes, 0, 1);
        }
    }

    [Fact]
    public void TestsOnEndMetricsGenerationProducerSpan()
    {
        Activity? parentSpan = this.activitySource.StartActivity("test parent");
        using (Activity? spanDataMock = this.activitySource.StartActivity("test Child", ActivityKind.Producer))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            spanDataMock.Start();
            this.SetLatency(spanDataMock);
            Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
            this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
                .Returns(expectAttributes);
            this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
            this.VerifyHistogramRecords(expectAttributes, 0, 1);
        }
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithoutEndRequired()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpResponseStatusCode, 500);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        var serviceMetrics = expectAttributes[MetricAttributeGeneratorConstants.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(
            serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedService = new List<KeyValuePair<string, object?>>();
        expectedService.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(1, serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object?>("latency", new KeyValuePair<double, object?>(this.testLatencyMillis, serviceKVP)));

        Assert.True(GlobalCallbackData.CallList.OrderBy(kvp => kvp.Key).SequenceEqual(expectedService.OrderBy(kvp => kvp.Key)));
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithLatency()
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpResponseStatusCode, 200);
        this.SetLatency(spanDataMock, 5.5);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        var serviceMetrics = expectAttributes[MetricAttributeGeneratorConstants.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(
            serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedService = new List<KeyValuePair<string, object?>>();
        expectedService.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object?>("latency", new KeyValuePair<double, object?>(5.5, serviceKVP)));

        Assert.True(GlobalCallbackData.CallList.OrderBy(kvp => kvp.Key).SequenceEqual(expectedService.OrderBy(kvp => kvp.Key)));
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithStatusCodes()
    {
        this.ValidateMetricsGeneratedForHttpStatusCode(null, ExpectedStatusMetric.NEITHER);

        // Valid HTTP status codes
        this.ValidateMetricsGeneratedForHttpStatusCode(200, ExpectedStatusMetric.NEITHER);
        this.ValidateMetricsGeneratedForHttpStatusCode(399, ExpectedStatusMetric.NEITHER);
        this.ValidateMetricsGeneratedForHttpStatusCode(400, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForHttpStatusCode(499, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForHttpStatusCode(500, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForHttpStatusCode(599, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForHttpStatusCode(600, ExpectedStatusMetric.NEITHER);
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithStatusDataError()
    {
        // Empty Status and HTTP with Error Status
        this.ValidateMetricsGeneratedForStatusDataError(null, ExpectedStatusMetric.FAULT);

        // Valid HTTP with Error Status
        this.ValidateMetricsGeneratedForStatusDataError(200, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForStatusDataError(399, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForStatusDataError(400, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForStatusDataError(499, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForStatusDataError(500, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForStatusDataError(599, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForStatusDataError(600, ExpectedStatusMetric.FAULT);
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithAwsStatusCodes()
    {
        // Invalid HTTP status codes
        this.ValidateMetricsGeneratedForAttributeStatusCode(null, ExpectedStatusMetric.NEITHER);

        // Valid HTTP status codes
        this.ValidateMetricsGeneratedForAttributeStatusCode(399, ExpectedStatusMetric.NEITHER);
        this.ValidateMetricsGeneratedForAttributeStatusCode(400, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForAttributeStatusCode(499, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForAttributeStatusCode(500, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForAttributeStatusCode(599, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForAttributeStatusCode(600, ExpectedStatusMetric.NEITHER);
    }

    [Fact]
    public void TestOnEndMetricsGenerationWithStatusDataOk()
    {
        // Empty Status and HTTP with Ok Status
        this.ValidateMetricsGeneratedForStatusDataOk(null, ExpectedStatusMetric.NEITHER);

        // Valid HTTP with Ok Status
        this.ValidateMetricsGeneratedForStatusDataOk(200, ExpectedStatusMetric.NEITHER);
        this.ValidateMetricsGeneratedForStatusDataOk(399, ExpectedStatusMetric.NEITHER);
        this.ValidateMetricsGeneratedForStatusDataOk(400, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForStatusDataOk(499, ExpectedStatusMetric.ERROR);
        this.ValidateMetricsGeneratedForStatusDataOk(500, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForStatusDataOk(599, ExpectedStatusMetric.FAULT);
        this.ValidateMetricsGeneratedForStatusDataOk(600, ExpectedStatusMetric.NEITHER);
    }

    private void ValidateMetricsGeneratedForAttributeStatusCode(
        int? awsStatusCode, ExpectedStatusMetric expectedStatusMetric)
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag("new key", "new value");
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        if (awsStatusCode != null)
        {
            expectAttributes[MetricAttributeGeneratorConstants.ServiceMetric]["new service key"] = "new service value";

            expectAttributes[MetricAttributeGeneratorConstants.ServiceMetric]
                .Add(new KeyValuePair<string, object?>(AttributeHttpResponseStatusCode, awsStatusCode));

            expectAttributes[MetricAttributeGeneratorConstants.DependencyMetric]["new dependency key"] = "new dependency value";

            expectAttributes[MetricAttributeGeneratorConstants.DependencyMetric]
                .Add(new KeyValuePair<string, object?>(AttributeHttpResponseStatusCode, awsStatusCode));
        }

        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);

        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.ValidMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }

    private void ValidateMetricsGeneratedForStatusDataOk(
        int? httpStatusCode, ExpectedStatusMetric expectedStatusMetric)
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag(AttributeHttpResponseStatusCode, httpStatusCode);
        spanDataMock.SetStatus(ActivityStatusCode.Ok);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.ValidMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }

    private void ValidateMetricsGeneratedForStatusDataError(
        int? httpStatusCode, ExpectedStatusMetric expectedStatusMetric)
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag(AttributeHttpResponseStatusCode, httpStatusCode);
        spanDataMock.SetStatus(ActivityStatusCode.Error);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.ValidMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }

    private void ValidateMetricsGeneratedForHttpStatusCode(
        int? httpStatusCode, ExpectedStatusMetric expectedStatusMetric)
    {
        Activity? spanDataMock = this.activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag(AttributeHttpResponseStatusCode, httpStatusCode);
        this.SetLatency(spanDataMock);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, spanDataMock);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource))
            .Returns(expectAttributes);
        this.awsSpanMetricsProcessor.OnEnd(spanDataMock);
        this.ValidMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }

    private void ValidMetrics(
        Dictionary<string, ActivityTagsCollection> metricAttributesMap,
        ExpectedStatusMetric expectedStatusMetric)
    {
        var expectedList = new List<KeyValuePair<string, object?>>();
        var serviceMetrics = metricAttributesMap[MetricAttributeGeneratorConstants.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(
            serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedService = new List<KeyValuePair<string, object?>>();
        var dependencyMetrics = metricAttributesMap[MetricAttributeGeneratorConstants.DependencyMetric];
        var dependencyKVP = new KeyValuePair<string, object>(
            dependencyMetrics.Keys.FirstOrDefault(),
            dependencyMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedDependency = new List<KeyValuePair<string, object?>>();

        switch (expectedStatusMetric)
        {
            case ExpectedStatusMetric.ERROR:
                expectedService.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(1, serviceKVP)));
                expectedService.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, serviceKVP)));
                expectedDependency.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(1, dependencyKVP)));
                expectedDependency.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, dependencyKVP)));
                break;
            case ExpectedStatusMetric.FAULT:
                expectedService.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, serviceKVP)));
                expectedService.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(1, serviceKVP)));
                expectedDependency.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, dependencyKVP)));
                expectedDependency.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(1, dependencyKVP)));
                break;
            case ExpectedStatusMetric.NEITHER:
                expectedService.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, serviceKVP)));
                expectedService.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, serviceKVP)));
                expectedDependency.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, dependencyKVP)));
                expectedDependency.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, dependencyKVP)));
                break;
        }

        expectedList.AddRange(expectedService);
        expectedList.AddRange(expectedDependency);

        var expectDict = new Dictionary<KeyValuePair<string, object?>, int>();
        var actualDict = new Dictionary<KeyValuePair<string, object?>, int>();
        GlobalCallbackData.CallList.RemoveAll(kvp => kvp.Key == "latency");
        if (expectedList.Count > 0)
        {
            expectDict = expectedList.GroupBy(kvp => kvp).ToDictionary(g => g.Key, g => g.Count());
        }

        if (GlobalCallbackData.CallList is not null)
        {
            actualDict = GlobalCallbackData.CallList.GroupBy(kvp => kvp).ToDictionary(g => g.Key, g => g.Count());
        }

        Assert.Equal(expectDict, actualDict);

        GlobalCallbackData.Clear();
    }

    private void VerifyHistogramRecords(
        Dictionary<string, ActivityTagsCollection> metricAttributesMap,
        int wantedServiceMetricInvocation,
        int wantedDependencyMetricInvocation)
    {
        var expectedList = new List<KeyValuePair<string, object?>>();
        if (wantedServiceMetricInvocation > 0)
        {
            var serviceMetrics = metricAttributesMap[MetricAttributeGeneratorConstants.ServiceMetric];
            var serviceKVP = new KeyValuePair<string, object>(
                serviceMetrics.Keys.FirstOrDefault(),
                serviceMetrics.Values.FirstOrDefault());
            List<KeyValuePair<string, object?>> expectedService = new List<KeyValuePair<string, object?>>();
            expectedService.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, serviceKVP)));
            expectedService.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, serviceKVP)));
            expectedService.Add(new KeyValuePair<string, object?>("latency", new KeyValuePair<double, object?>(this.testLatencyMillis, serviceKVP)));
            expectedService = expectedService.SelectMany(item => Enumerable.Repeat(item, wantedServiceMetricInvocation)).ToList();
            expectedList.AddRange(expectedService);
        }

        if (wantedDependencyMetricInvocation > 0)
        {
            var dependencyMetrics = metricAttributesMap[MetricAttributeGeneratorConstants.DependencyMetric];
            var dependencyKVP = new KeyValuePair<string, object>(
                dependencyMetrics.Keys.FirstOrDefault(),
                dependencyMetrics.Values.FirstOrDefault());
            List<KeyValuePair<string, object?>> expectedDependency = new List<KeyValuePair<string, object?>>();
            expectedDependency.Add(new KeyValuePair<string, object?>("error", new KeyValuePair<long, object?>(0, dependencyKVP)));
            expectedDependency.Add(new KeyValuePair<string, object?>("fault", new KeyValuePair<long, object?>(0, dependencyKVP)));
            expectedDependency.Add(new KeyValuePair<string, object?>("latency", new KeyValuePair<double, object?>(this.testLatencyMillis, dependencyKVP)));
            expectedDependency = expectedDependency.SelectMany(item => Enumerable.Repeat(item, wantedDependencyMetricInvocation)).ToList();
            expectedList.AddRange(expectedDependency);
        }

        var expectDict = new Dictionary<KeyValuePair<string, object?>, int>();
        var actualDict = new Dictionary<KeyValuePair<string, object?>, int>();
        if (expectedList.Count > 0)
        {
            expectDict = expectedList.GroupBy(kvp => kvp).ToDictionary(g => g.Key, g => g.Count());
        }

        if (GlobalCallbackData.CallList is not null)
        {
            actualDict = GlobalCallbackData.CallList.GroupBy(kvp => kvp).ToDictionary(g => g.Key, g => g.Count());
        }

        Assert.Equal(expectDict, actualDict);
    }

    private Dictionary<string, ActivityTagsCollection> BuildMetricAttributes(bool containAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attributes = new Dictionary<string, ActivityTagsCollection>();
        if (containAttributes)
        {
            if (AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(span))
            {
                attributes.Add(MetricAttributeGeneratorConstants.DependencyMetric, new ActivityTagsCollection([new KeyValuePair<string, object?>("new dependency key", "new dependency value")]));
            }

            if (AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(span))
            {
                attributes.Add(MetricAttributeGeneratorConstants.ServiceMetric, new ActivityTagsCollection([new KeyValuePair<string, object?>("new service key", "new service value")]));
            }
        }

        return attributes;
    }

    // Configure latency
    private void SetLatency(Activity? spanDataMock, double latency = -1)
    {
        if (latency == -1)
        {
            latency = this.testLatencyMillis;
        }

        PropertyInfo? spanDuration = typeof(Activity).GetProperty("Duration");
        TimeSpan timeSpan = TimeSpan.FromMilliseconds(latency);
        MethodInfo? durationSetMethod = spanDuration?.GetSetMethod(nonPublic: true);
        durationSetMethod?.Invoke(spanDataMock, new object?[] { timeSpan });
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public static class GlobalCallbackData
{
    public static List<KeyValuePair<string, object?>>? CallList { get; set; }

    public static void Clear()
    {
        CallList = null;
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1602:Enumeration items should be documented", Justification = "Tests")]
public enum ExpectedStatusMetric
{
    ERROR = 0,
    FAULT = 1,
    NEITHER = 2,
}
