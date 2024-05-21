// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using HarmonyLib;
using OpenTelemetry.Resources;


namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// TODO: Add documentation here
/// </summary>
public class AwsSpanMetricsProcessorTest
{
    private AwsSpanMetricsProcessor awsSpanMetricsProcessor;
    private AwsMetricAttributeGenerator Generator = new AwsMetricAttributeGenerator();
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
        awsSpanMetricsProcessor = AwsSpanMetricsProcessor.Create(errorHistogram, faultHistogram, latencyHistogram, Generator, resource);
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
        spanDataMock = activitySource.StartActivity("test");
        Dictionary<string, ActivityTagsCollection> expectAttributes = new Dictionary<string, ActivityTagsCollection>();
        var generateMethod = Generator.GetType().GetMethod(nameof(Generator.GenerateMetricAttributeMapFromSpan));
        var harmony = new HarmonyLib.Harmony("generateMetricAttribute.Patch");
        var prefixMethod = typeof(AwsSpanMetricsProcessorTest).GetMethod(nameof(GenerateMetricAttributeMapFromSpan_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
        harmony.Patch(generateMethod, new HarmonyMethod(prefixMethod));
        var result = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, resource);
        Assert.NotEmpty(result); 
    }
    private static bool GenerateMetricAttributeMapFromSpan_Prefix(Activity span, Resource resource, ref Dictionary<string, ActivityTagsCollection> __result)
    {
        
        __result = new Dictionary<string, ActivityTagsCollection>
        {
            { "key", new ActivityTagsCollection(new Dictionary<string, object> { { "tagKey", "tagValue" } }) }
        };
        return false;
    }
}

