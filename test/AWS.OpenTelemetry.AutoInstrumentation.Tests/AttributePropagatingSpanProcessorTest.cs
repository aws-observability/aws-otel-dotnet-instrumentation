using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Moq;
using OpenTelemetry.Resources;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class AttributePropagatingSpanProcessorTest
{
    private Func<Activity, string> spanNameExtractor = AwsSpanProcessingUtil.GetIngressOperation;
    private AwsSpanMetricsProcessor awsSpanMetricsProcessor;
    private Mock<AwsMetricAttributeGenerator> Generator = new Mock<AwsMetricAttributeGenerator>();
    private Resource resource = Resource.Empty;
    private string spanNameKey = "spanName";
    private string testKey1 = "key1";
    private string testKey2 = "key2";
    private TracerProviderBuilder tracerProviderBuilder = Sdk.CreateTracerProviderBuilder();
    private Tracer tracer;
    private ActivitySource activitySource = new ActivitySource("test");
    private AttributePropagatingSpanProcessor attributePropagatingSpanProcessor;

    public AttributePropagatingSpanProcessorTest()
    {
        ReadOnlyCollection<string> attributesKeysToPropagate = new ReadOnlyCollection<string>([testKey1, testKey2]);
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = ((ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData)
        };
        ActivitySource.AddActivityListener(listener);
        attributePropagatingSpanProcessor=
            AttributePropagatingSpanProcessor.Create(spanNameExtractor, spanNameKey, attributesKeysToPropagate);
        tracerProviderBuilder.AddProcessor(attributePropagatingSpanProcessor);
        tracerProviderBuilder.AddSource(["test"]);
        var sdkTracerProvider = tracerProviderBuilder.Build();
        tracer = sdkTracerProvider.GetTracer("awsxray");
    }

    [Fact]
    public void testAttributesPropagationBySpanKind()
    {
        // foreach (var kind in Enum.GetValues(typeof(ActivityKind)))
        // {
        //     TelemetrySpan activityWithAppOnly = tracer.StartSpan()
        // }
        SpanAttributes spanAttributes = new SpanAttributes([new KeyValuePair<string, object?>(testKey1, "testValue1")]);
        TelemetrySpan spanWithAppOnly = tracer.StartSpan("parent", SpanKind.Server, initialAttributes: spanAttributes);
        validateSpanAttributesInheritance(spanWithAppOnly, "parent", null, null);
    }

    private TelemetrySpan createNestedSpan(TelemetrySpan parentSpan, int depth)
    {
        if (depth == 0)
        {
            return parentSpan;
        }

        TelemetrySpan childSpan = tracer.StartSpan("child:" + depth, SpanKind.Server, parentContext: parentSpan.Context);
        
        FieldInfo fieldInfo = typeof(TelemetrySpan).GetField(
            "Activity",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Activity childActivity = (Activity)fieldInfo.GetValue(childSpan);
        attributePropagatingSpanProcessor.OnStart(childActivity);
        

        try
        {
            return createNestedSpan(childSpan, depth - 1);
        }
        finally
        {
            childSpan.End();
        }
    }

    private void validateSpanAttributesInheritance(
        TelemetrySpan parentSpan,
        string? propagatedName,
        string? propagationValue1,
        string? propagatedValue2)
    {
        TelemetrySpan leafSpan = createNestedSpan(parentSpan, 10);

        Assert.True(leafSpan.ParentSpanId != default);
        FieldInfo fieldInfo = typeof(TelemetrySpan).GetField(
            "Activity",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Activity leafActivity = (Activity)fieldInfo.GetValue(leafSpan);

        Assert.Equal(leafActivity.DisplayName, "child:1");
        if (propagatedName != null)
        {
            Assert.Equal(propagatedName, leafActivity.GetTagItem(spanNameKey));
        }
        else
        {
            Assert.Null(leafActivity.GetTagItem(spanNameKey));
        }

        if (propagationValue1 != null)
        {
            Assert.Equal(leafActivity.GetTagItem(testKey1), propagationValue1);
        }
        else
        {
            Assert.Null(leafActivity.GetTagItem(testKey1));
        }

        if (propagatedValue2 != null)
        {
            Assert.Equal(leafActivity.GetTagItem(testKey2), propagatedValue2);
        }
        else
        {
            Assert.Null(leafActivity.GetTagItem(testKey2));
        }
    }
}
