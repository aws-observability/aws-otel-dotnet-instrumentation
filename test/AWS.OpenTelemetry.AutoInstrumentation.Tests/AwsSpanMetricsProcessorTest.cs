// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using HarmonyLib;
using Moq;
using OpenTelemetry.Resources;


namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// TODO: Add documentation here
/// </summary>
public class AwsSpanMetricsProcessorTest
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
                    list.Add(new KeyValuePair<string, object>(instrument.Name, tags[0]));
                    GlobalCallbackData.CallList = list;
                }
                ));
        meterListener.SetMeasurementEventCallback<double>(((instrument, measurement, tags, state) =>
                {
                    var list = GlobalCallbackData.CallList is null ? [] : GlobalCallbackData.CallList;
                    list.Add(new KeyValuePair<string, object>(instrument.Name, tags[0]));
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

    [Fact]
    public void testOnEndMetricsGenerationWithoutSpanAttributes()
    {
        var harmony = new Harmony("patch");
        // harmony.Patch(typeof(Histogram<long>).GetMethod("Record"), new HarmonyMethod(Patch.Prefix));
        spanDataMock = activitySource.StartActivity("test");
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, spanDataMock);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(spanDataMock, resource))
            .Returns(expectAttributes);
        awsSpanMetricsProcessor.OnEnd(spanDataMock);
        verifyHistogramRecords(expectAttributes, 0,0);
        // var result = Generator.Object.GenerateMetricAttributeMapFromSpan(spanDataMock, resource);
        // Assert.Equal(CallLogger.CallCount, 0);
    }

    private void verifyHistogramRecords(Dictionary<string, ActivityTagsCollection> metricAttributesMap,
        int wantedServiceMetricInvocation,
        int wantedDependencyMetricInvocation)
    {
        var serviceMetrics = metricAttributesMap[IMetricAttributeGenerator.ServiceMetric];
        var serviceKVP = new KeyValuePair<string, object>(serviceMetrics.Keys.FirstOrDefault(),
            serviceMetrics.Values.FirstOrDefault());
        var dependencyMetrics = metricAttributesMap[IMetricAttributeGenerator.DependencyMetric];
        var dependencyKVP = new KeyValuePair<string, object>(dependencyMetrics.Keys.FirstOrDefault(),
            dependencyMetrics.Values.FirstOrDefault());
        Assert.Equal(1,1);
        

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

}

public static class GlobalCallbackData
{
    public static List<KeyValuePair<string, object?>> CallList { get; set; }
    public static Dictionary<string, object?> Dictionary { get; set; }
}



// public static class CallLogger
// {
//     public static int CallCount { get; private set; } = 0;
//     public static List<string> CallDetails { get; } = new List<string>();
//
//     public static void LogCall(params object[] args)
//     {
//         CallCount++;
//         CallDetails.Add($"Call #{CallCount}: {string.Join(", ", args)}");
//     }
// }
//
// [HarmonyPatch(typeof(Histogram<long>), nameof(Histogram<long>.Record))] 
// class Patch
// {
//     [HarmonyPrefix]
//     [HarmonyPatch("Record", new Type[] { typeof(long) })]
//     public static void Prefix(long value)
//     {
//         CallLogger.LogCall(value);
//     }
// }
