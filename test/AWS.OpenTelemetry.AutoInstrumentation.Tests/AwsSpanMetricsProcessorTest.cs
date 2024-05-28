// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using HarmonyLib;
using Moq;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;



namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

// There is two test that is not implemented in this Class, comparing with Java:

// 1. testIsRequired()
// Implementation of AwsSpanMetricsProcessor.isStartRequired() and isEndRequired() do not exist

// 2. testsOnEndMetricsGenerationLocalRootServerSpan()
// This test cannot be done here because there is no difference (or cannot set difference) in dotnet for
// a null parent information and a invalid parent information
// Found no way to setup a Activity.Parent to a default/invalid value,
// It either valid (set by passing a parent ID and automatically matching Activity.Parent field)
// or just Null




public class AwsSpanMetricsProcessorTest: IDisposable
{
    public static int count;
    private AwsSpanMetricsProcessor awsSpanMetricsProcessor;
    private Mock<AwsMetricAttributeGenerator> Generator = new Mock<AwsMetricAttributeGenerator>();
    private Resource resource = Resource.Empty;
    private Meter meter = new Meter("test");
    private Histogram<long> errorHistogram;
    private Histogram<long> faultHistogram;
    private Histogram<double> latencyHistogram;
    private ActivitySource activitySource = new ActivitySource("test");
    private Activity spanDataMock;
    private readonly double testLatencyMillis = 150;

    public void Dispose()
    {
        GlobalCallbackData.Clear();
    }

    public AwsSpanMetricsProcessorTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = ((ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData)
        };
        ActivitySource.AddActivityListener(listener);
        
        errorHistogram = meter.CreateHistogram<long>("error");
        faultHistogram = meter.CreateHistogram<long>("fault");
        latencyHistogram = meter.CreateHistogram<double>("latency");
        var meterListener = new MeterListener();
        meterListener.EnableMeasurementEvents(errorHistogram);
        meterListener.EnableMeasurementEvents(faultHistogram);
        meterListener.EnableMeasurementEvents(latencyHistogram);
        meter.Tags.AddItem(new KeyValuePair<string, object?>("test", "test"));
        meterListener.SetMeasurementEventCallback<long>(((instrument, measurement, tags, state) =>
                {
                    var list = GlobalCallbackData.CallList is null ? [] : GlobalCallbackData.CallList;
                    list.Add(new KeyValuePair<string, object>(instrument.Name, new KeyValuePair<long, object>(measurement, tags[0])));
                    GlobalCallbackData.CallList = list;
                }
                ));
        meterListener.SetMeasurementEventCallback<double>(((instrument, measurement, tags, state) =>
                {
                    var list = GlobalCallbackData.CallList is null ? [] : GlobalCallbackData.CallList;
                    list.Add(new KeyValuePair<string, object>(instrument.Name, new KeyValuePair<double, object>(measurement, tags[0])));
                    GlobalCallbackData.CallList = list;
                }
            ));
        awsSpanMetricsProcessor = AwsSpanMetricsProcessor.Create(errorHistogram, faultHistogram, latencyHistogram, Generator.Object, resource);
    }

    [Fact]
    public void testStartDoesNothingToSpan()
    {
        spanDataMock = activitySource.StartActivity("test");
        var parentInfo = spanDataMock.ParentSpanId;
        awsSpanMetricsProcessor.OnStart(spanDataMock);
        Assert.Equal(parentInfo, spanDataMock.ParentSpanId);
    }

    [Fact]
    public void testTearDown()
    {
        Assert.True(awsSpanMetricsProcessor.Shutdown());
        Assert.True(awsSpanMetricsProcessor.ForceFlush());
    }
    
    /**
     * Tests starting with testOnEndMetricsGeneration are testing the logic in
     * AwsSpanMetricsProcessor's onEnd method pertaining to metrics generation.
     */
    [Fact]
    public void testOnEndMetricsGenerationWithoutSpanAttributes()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Server);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        spanDataMock.SetEndTime(DateTime.Now);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 1,0);
    }

    [Fact]
    public void testOnEndMetricsGenerationWithoutMetricAttributes()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Server);
        setLatency();
        spanDataMock.SetTag(AwsAttributeKeys.AttributeHttpStatusCode, (long)500);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(false, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 0,0);
    }

    [Fact]
    public void testsOnEndMetricsGenerationLocalRootConsumerSpan()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Consumer);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 1,1);
    }

    [Fact]
    public void testsOnEndMetricsGenerationLocalRootClientSpan()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Client);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 1,1);
    }
    
    [Fact]
    public void testsOnEndMetricsGenerationLocalRootProducerSpan()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Producer);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 1,1);
    }
    
    [Fact]
    public void testsOnEndMetricsGenerationLocalRootInternalSpan()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Internal);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 1,0);
    }
    
    [Fact]
    public void testsOnEndMetricsGenerationLocalRootProducerSpanWithoutMetricAttributes()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Producer);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(false, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 0,0);
    }
    
    [Fact]
    public void testsOnEndMetricsGenerationClientSpan()
    {
        Activity parentSpan = activitySource.StartActivity("test parent");
        using (spanDataMock = activitySource.StartActivity("test Child", ActivityKind.Client))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            spanDataMock.Start();
            setLatency();
            Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
            Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
                .Returns(expectAttributes);
            awsSpanMetricsProcessor.OnEnd(spanDataMock);
            verifyHistogramRecords(expectAttributes, 0,1);
        }
    }
    
    [Fact]
    public void testsOnEndMetricsGenerationProducerSpan()
    {
        Activity parentSpan = activitySource.StartActivity("test parent");
        using (spanDataMock = activitySource.StartActivity("test Child", ActivityKind.Producer))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            spanDataMock.Start();
            setLatency();
            Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
            Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
                .Returns(expectAttributes);
            awsSpanMetricsProcessor.OnEnd(spanDataMock);
            verifyHistogramRecords(expectAttributes, 0,1);
        }
    }
    
    [Fact]
    public void testOnEndMetricsGenerationWithoutEndRequired()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpStatusCode, (long)500);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        var serviceMetrics = expectAttributes[IMetricAttributeGenerator.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedService = [];
        expectedService.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(1,serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object>("latency", new KeyValuePair<double,object>(testLatencyMillis,serviceKVP)));
        
        Assert.True(GlobalCallbackData.CallList.OrderBy(kvp => kvp.Key).SequenceEqual(expectedService.OrderBy(kvp => kvp.Key)));
    }

    [Fact]
    public void testOnEndMetricsGenerationWithLatency()
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpStatusCode, (long)200);
        setLatency(5.5);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        var serviceMetrics = expectAttributes[IMetricAttributeGenerator.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedService = [];
        expectedService.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,serviceKVP)));
        expectedService.Add(new KeyValuePair<string, object>("latency", new KeyValuePair<double,object>(5.5,serviceKVP)));
        
        Assert.True(GlobalCallbackData.CallList.OrderBy(kvp => kvp.Key).SequenceEqual(expectedService.OrderBy(kvp => kvp.Key)));
    }

    [Fact]
    public void testOnEndMetricsGenerationWithStatusCodes()
    {
        validateMetricsGeneratedForHttpStatusCode(null, ExpectedStatusMetric.NEITHER);

        // Valid HTTP status codes
        validateMetricsGeneratedForHttpStatusCode(200L, ExpectedStatusMetric.NEITHER);
        validateMetricsGeneratedForHttpStatusCode(399L, ExpectedStatusMetric.NEITHER);
        validateMetricsGeneratedForHttpStatusCode(400L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForHttpStatusCode(499L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForHttpStatusCode(500L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForHttpStatusCode(599L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForHttpStatusCode(600L, ExpectedStatusMetric.NEITHER);
    }
    
    [Fact]
    public void testOnEndMetricsGenerationWithStatusDataError() {
        // Empty Status and HTTP with Error Status
        validateMetricsGeneratedForStatusDataError(null, ExpectedStatusMetric.FAULT);

        // Valid HTTP with Error Status
        validateMetricsGeneratedForStatusDataError(200L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForStatusDataError(399L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForStatusDataError(400L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForStatusDataError(499L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForStatusDataError(500L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForStatusDataError(599L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForStatusDataError(600L, ExpectedStatusMetric.FAULT);
    }
    
    [Fact]
    public void testOnEndMetricsGenerationWithAwsStatusCodes() {
        // Invalid HTTP status codes
        validateMetricsGeneratedForAttributeStatusCode(null, ExpectedStatusMetric.NEITHER);

        // Valid HTTP status codes
        validateMetricsGeneratedForAttributeStatusCode(399L, ExpectedStatusMetric.NEITHER);
        validateMetricsGeneratedForAttributeStatusCode(400L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForAttributeStatusCode(499L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForAttributeStatusCode(500L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForAttributeStatusCode(599L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForAttributeStatusCode(600L, ExpectedStatusMetric.NEITHER);
    }
    
    [Fact]
    public void testOnEndMetricsGenerationWithStatusDataOk() {
        // Empty Status and HTTP with Ok Status
        validateMetricsGeneratedForStatusDataOk(null, ExpectedStatusMetric.NEITHER);

        // Valid HTTP with Ok Status
        validateMetricsGeneratedForStatusDataOk(200L, ExpectedStatusMetric.NEITHER);
        validateMetricsGeneratedForStatusDataOk(399L, ExpectedStatusMetric.NEITHER);
        validateMetricsGeneratedForStatusDataOk(400L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForStatusDataOk(499L, ExpectedStatusMetric.ERROR);
        validateMetricsGeneratedForStatusDataOk(500L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForStatusDataOk(599L, ExpectedStatusMetric.FAULT);
        validateMetricsGeneratedForStatusDataOk(600L, ExpectedStatusMetric.NEITHER);
    }

    private void validateMetricsGeneratedForAttributeStatusCode(
        long? awsStatusCode, ExpectedStatusMetric expectedStatusMetric)
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag("new key", "new value");
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        if (awsStatusCode != null)
        {
            expectAttributes[IMetricAttributeGenerator.ServiceMetric]["new service key"] = "new service value";

            expectAttributes[IMetricAttributeGenerator.ServiceMetric]
                .Add(new KeyValuePair<string, object?>(AttributeHttpStatusCode, awsStatusCode));

            expectAttributes[IMetricAttributeGenerator.DependencyMetric]["new dependency key"] = "new dependency value";
            
            expectAttributes[IMetricAttributeGenerator.DependencyMetric]
                .Add(new KeyValuePair<string, object?>(AttributeHttpStatusCode, awsStatusCode));
        }
        
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        validMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }
    
    private void validateMetricsGeneratedForStatusDataOk(
        long? httpStatusCode, ExpectedStatusMetric expectedStatusMetric) {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag(AttributeHttpStatusCode, httpStatusCode);
        spanDataMock.SetStatus(ActivityStatusCode.Ok);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        validMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }
    
    
    private void validateMetricsGeneratedForStatusDataError(
        long? httpStatusCode, ExpectedStatusMetric expectedStatusMetric) {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag(AttributeHttpStatusCode, httpStatusCode);
        spanDataMock.SetStatus(ActivityStatusCode.Error);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        validMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }

    private void validateMetricsGeneratedForHttpStatusCode(
        long? httpStatusCode, ExpectedStatusMetric expectedStatusMetric)
    {
        spanDataMock = activitySource.StartActivity("test", ActivityKind.Producer);
        spanDataMock.SetTag(AttributeHttpStatusCode, httpStatusCode);
        setLatency();
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        validMetrics(expectAttributes, expectedStatusMetric);
        spanDataMock.Dispose();
    }

    private void validMetrics(Dictionary<string, ActivityTagsCollection> metricAttributesMap,
        ExpectedStatusMetric expectedStatusMetric)
    {
        var expectedList = new List<KeyValuePair<string, object?>>();
        var serviceMetrics = metricAttributesMap[IMetricAttributeGenerator.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedService = [];
        var dependencyMetrics = metricAttributesMap[IMetricAttributeGenerator.DependencyMetric];
        var dependencyKVP = new KeyValuePair<string, object>(dependencyMetrics.Keys.FirstOrDefault(),
            dependencyMetrics.Values.FirstOrDefault());
        List<KeyValuePair<string, object?>> expectedDependency = [];

        switch (expectedStatusMetric)
        {
            case ExpectedStatusMetric.ERROR:
                expectedService.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(1,serviceKVP)));
                expectedService.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,serviceKVP)));    
                expectedDependency.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(1,dependencyKVP)));
                expectedDependency.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,dependencyKVP)));
                break;
            case ExpectedStatusMetric.FAULT:
                expectedService.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,serviceKVP)));
                expectedService.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(1,serviceKVP)));    
                expectedDependency.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,dependencyKVP)));
                expectedDependency.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(1,dependencyKVP)));
                break;
            case ExpectedStatusMetric.NEITHER:
                expectedService.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,serviceKVP)));
                expectedService.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,serviceKVP)));    
                expectedDependency.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,dependencyKVP)));
                expectedDependency.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,dependencyKVP)));
                break;
        }

        expectedList.AddRange(expectedService);
        expectedList.AddRange(expectedDependency);
        
        var expectDict = new Dictionary<KeyValuePair<string, object>, int>();
        var actualDict = new Dictionary<KeyValuePair<string, object>, int>();
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

    private void verifyHistogramRecords(Dictionary<string, ActivityTagsCollection> metricAttributesMap,
        int wantedServiceMetricInvocation,
        int wantedDependencyMetricInvocation)
    {
        var expectedList = new List<KeyValuePair<string, object?>>();
        if (wantedServiceMetricInvocation > 0)
        {
            var serviceMetrics = metricAttributesMap[IMetricAttributeGenerator.ServiceMetric];
            var serviceKVP = new KeyValuePair<string, object>(serviceMetrics.Keys.FirstOrDefault(),
                serviceMetrics.Values.FirstOrDefault());
            List<KeyValuePair<string, object?>> expectedService = [];
            expectedService.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,serviceKVP)));
            expectedService.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,serviceKVP)));
            expectedService.Add(new KeyValuePair<string, object>("latency", new KeyValuePair<double,object>(testLatencyMillis,serviceKVP)));
            expectedService = expectedService.SelectMany(item => Enumerable.Repeat(item, wantedServiceMetricInvocation)).ToList();
            expectedList.AddRange(expectedService);
        }
        
        if (wantedDependencyMetricInvocation > 0)
        {
            var dependencyMetrics = metricAttributesMap[IMetricAttributeGenerator.DependencyMetric];
            var dependencyKVP = new KeyValuePair<string, object>(dependencyMetrics.Keys.FirstOrDefault(),
                dependencyMetrics.Values.FirstOrDefault());
            List<KeyValuePair<string, object?>> expectedDependency = [];
            expectedDependency.Add(new KeyValuePair<string, object>("error", new KeyValuePair<long,object>(0,dependencyKVP)));
            expectedDependency.Add(new KeyValuePair<string, object>("fault", new KeyValuePair<long,object>(0,dependencyKVP)));
            expectedDependency.Add(new KeyValuePair<string, object>("latency", new KeyValuePair<double,object>(testLatencyMillis,dependencyKVP)));
            expectedDependency = expectedDependency.SelectMany(item => Enumerable.Repeat(item, wantedDependencyMetricInvocation)).ToList();
            expectedList.AddRange(expectedDependency);
        }

        var expectDict = new Dictionary<KeyValuePair<string, object>, int>();
        var actualDict = new Dictionary<KeyValuePair<string, object>, int>();
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

    private Dictionary<string, ActivityTagsCollection> buildMetricAttributes(bool containAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attributes = new Dictionary<string, ActivityTagsCollection>();
        if (containAttributes)
        {
            if (AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(span))
            {
                attributes.Add(IMetricAttributeGenerator.DependencyMetric, new ActivityTagsCollection([new KeyValuePair<string, object?>("new dependency key", "new dependency value")]));
            }
            
            if (AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(span))
            {
                attributes.Add(IMetricAttributeGenerator.ServiceMetric, new ActivityTagsCollection([new KeyValuePair<string, object?>("new service key", "new service value")]));
            }
        }
        return attributes;
    }

    // Configure latency
    private void setLatency(double latency = -1)
    {
        if (latency == -1)
        {
            latency = testLatencyMillis;
        }
        PropertyInfo spanDuration = typeof(Activity).GetProperty("Duration");
        TimeSpan timeSpan = TimeSpan.FromMilliseconds(latency);
        MethodInfo durationSetMethod = spanDuration?.GetSetMethod(nonPublic: true);
        durationSetMethod.Invoke(spanDataMock, new object?[] { timeSpan });
    }

}

public static class GlobalCallbackData
{
    public static List<KeyValuePair<string, object?>> CallList { get; set; }

    public static void Clear()
    {
        CallList = null;
    }
}
public enum ExpectedStatusMetric {
    ERROR = 0,
    FAULT = 1,
    NEITHER = 2
}

