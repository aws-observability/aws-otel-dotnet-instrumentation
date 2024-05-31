using System.ComponentModel;
using System.Diagnostics;
using Moq;
using OpenTelemetry.Resources;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class AwsMetricAttributesSpanProcessorTest
{
    private Mock<AwsMetricAttributeGenerator> Generator = new Mock<AwsMetricAttributeGenerator>();
    private Activity span;
    private ActivitySource testSource = new ActivitySource("test");
    private AwsMetricAttributesSpanProcessor attributesSpanProcessor;

    public AwsMetricAttributesSpanProcessorTest()
    {
        attributesSpanProcessor = AwsMetricAttributesSpanProcessor.Create(Generator.Object, Resource.Empty);
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = ((ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData)
        };
        ActivitySource.AddActivityListener(listener);
        span = testSource.StartActivity("test", ActivityKind.Server);
    }

    [Fact]
    public void testOnEndWithoutAttributesOrModification()
    {
        buildSpanAttributes(false);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(false, metricAttributesHelper());
        
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttributes);

        var beforeEndAttributes = span.TagObjects;
        attributesSpanProcessor.OnEnd(span);
        Assert.Equal(0, span.TagObjects.Count());
        Assert.Equal(beforeEndAttributes, span.TagObjects);
    }
    
    [Fact]
    public void testOnEndWithAttributesButWithoutModification()
    {
        buildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(false, metricAttributesHelper());
        
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttributes);

        var beforeEndAttributes = span.TagObjects;
        attributesSpanProcessor.OnEnd(span);
        Assert.Equal(1, span.TagObjects.Count());
        Assert.Equal(beforeEndAttributes, span.TagObjects);
    }
    
    [Fact]
    public void testOnEndWithoutAttributesButWithModification()
    {
        buildSpanAttributes(false);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, metricAttributesHelper());
        ActivityTagsCollection expectMetricAttributes = expectAttributes[IMetricAttributeGenerator.ServiceMetric];

        
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttributes);

        attributesSpanProcessor.OnEnd(span);
        Assert.Equal(expectMetricAttributes.Count(), span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Key, span.TagObjects.FirstOrDefault().Key);
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span.TagObjects.FirstOrDefault().Value);
    }
    
    [Fact]
    public void testOnEndWithAttributesAndModification()
    {
        buildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, metricAttributesHelper());
        ActivityTagsCollection expectMetricAttributes = metricAttributesHelper();

        
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttributes);

        attributesSpanProcessor.OnEnd(span);
        Assert.Equal(expectMetricAttributes.Count() + 1, span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span.GetTagItem(expectMetricAttributes.FirstOrDefault().Key));
        Assert.Equal("original value", span.GetTagItem("original key"));
    }

    [Fact]
    public void testOnEndMultipleSpan()
    {
        buildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, metricAttributesHelper());
        ActivityTagsCollection expectMetricAttributes = metricAttributesHelper();
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttributes);

        Activity span2 = testSource.StartActivity("test2", ActivityKind.Server);
        Dictionary<string, ActivityTagsCollection> expectAttribute2 = buildMetricAttributes(false, metricAttributesHelper(), span2);
        span2.SetTag("original key", "original value");
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span2, Resource.Empty))
            .Returns(expectAttribute2);

        Activity span3 = testSource.StartActivity("test3", ActivityKind.Server);
        Dictionary<string, ActivityTagsCollection> expectAttribute3 = buildMetricAttributes(true, metricAttributesHelper(), span3);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span3, Resource.Empty))
            .Returns(expectAttribute3);
        
        attributesSpanProcessor.OnEnd(span);
        var beforeEndAttributes = span2.TagObjects;
        attributesSpanProcessor.OnEnd(span2);
        attributesSpanProcessor.OnEnd(span3);
        
        Assert.Equal(expectMetricAttributes.Count() + 1, span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span.GetTagItem(expectMetricAttributes.FirstOrDefault().Key));
        Assert.Equal("original value", span.GetTagItem("original key"));
        
        Assert.Equal(beforeEndAttributes, span2.TagObjects);
        
        Assert.Equal(1, expectMetricAttributes.Count());
        Assert.Equal(expectMetricAttributes.Count(), span3.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Key, span3.TagObjects.FirstOrDefault().Key);
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span3.TagObjects.FirstOrDefault().Value);
    }

    [Fact]
    public void testOverridenAttributes()
    {
        span.SetTag("key 1", "old value 1");
        span.SetTag("key 2", "old value 2");
        ActivityTagsCollection metricAttributes = new ActivityTagsCollection([
            new KeyValuePair<string, object?>("key 1", "new value 1"),
            new KeyValuePair<string, object?>("key 3", "new value 3")
        ]);  
        Dictionary<string,ActivityTagsCollection> expectAttribute = buildMetricAttributes(true, metricAttributes);
        
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttribute);
        
        attributesSpanProcessor.OnEnd(span);
        Assert.Equal(3, span.TagObjects.Count());
        Assert.Equal("new value 1", span.GetTagItem("key 1"));
        Assert.Equal("old value 2", span.GetTagItem("key 2"));
        Assert.Equal("new value 3", span.GetTagItem("key 3"));
    }

    [Fact]
    public void testDependencyAttributes()
    {
        span = testSource.StartActivity("test dependency", ActivityKind.Client);
        buildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = buildMetricAttributes(true, metricAttributesHelper(),span);
        ActivityTagsCollection expectMetricAttributes = metricAttributesHelper();
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span, Resource.Empty))
            .Returns(expectAttributes);

        Activity span2 = testSource.StartActivity("test2", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute2 = buildMetricAttributes(false, metricAttributesHelper(), span2);
        span2.SetTag("original key", "original value");
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span2, Resource.Empty))
            .Returns(expectAttribute2);

        Activity span3 = testSource.StartActivity("test3", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute3 = buildMetricAttributes(true, metricAttributesHelper(), span3);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span3, Resource.Empty))
            .Returns(expectAttribute3);
        
        attributesSpanProcessor.OnEnd(span);
        var beforeEndAttributes = span2.TagObjects;
        attributesSpanProcessor.OnEnd(span2);
        attributesSpanProcessor.OnEnd(span3);
        
        Assert.Equal(expectMetricAttributes.Count() + 1, span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span.GetTagItem(expectMetricAttributes.FirstOrDefault().Key));
        Assert.Equal("original value", span.GetTagItem("original key"));
        
        Assert.Equal(beforeEndAttributes, span2.TagObjects);
        
        Assert.Equal(1, expectMetricAttributes.Count());
        Assert.Equal(expectMetricAttributes.Count(), span3.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Key, span3.TagObjects.FirstOrDefault().Key);
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span3.TagObjects.FirstOrDefault().Value);
    }

    [Fact]
    public void testLocalRootSpan()
    {
        span.Dispose();
        Activity localRootSpan1 = testSource.StartActivity("test local root", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute1 = buildMetricAttributes(true, metricAttributesHelper(), localRootSpan1);
        localRootSpan1.SetTag("original key", "original value");
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(localRootSpan1, Resource.Empty))
            .Returns(expectAttribute1);
        attributesSpanProcessor.OnEnd(localRootSpan1);
        Assert.Equal(metricAttributesHelper().Count() + 2, localRootSpan1.TagObjects.Count());
        Assert.Equal(AwsSpanProcessingUtil.LocalRoot,localRootSpan1.GetTagItem(AwsAttributeKeys.AttributeAWSSpanKind));
        Assert.Equal("original value", localRootSpan1.GetTagItem("original key"));
        Assert.Equal(metricAttributesHelper().FirstOrDefault().Value,
            localRootSpan1.GetTagItem(metricAttributesHelper().FirstOrDefault().Key));
        localRootSpan1.Dispose();
        
        Activity localRootSpan2 = testSource.StartActivity("test local root", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute2 = buildMetricAttributes(true, metricAttributesHelper(), localRootSpan2);
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(localRootSpan2, Resource.Empty))
            .Returns(expectAttribute2);
        attributesSpanProcessor.OnEnd(localRootSpan2);
        Assert.Equal(metricAttributesHelper().Count() + 1, localRootSpan2.TagObjects.Count());
        Assert.Equal(AwsSpanProcessingUtil.LocalRoot,localRootSpan2.GetTagItem(AwsAttributeKeys.AttributeAWSSpanKind));
        Assert.Equal(metricAttributesHelper().FirstOrDefault().Value,
            localRootSpan2.GetTagItem(metricAttributesHelper().FirstOrDefault().Key));
        localRootSpan2.Dispose();        
        
        Activity localRootSpan3 = testSource.StartActivity("test local root", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute3 = buildMetricAttributes(false, metricAttributesHelper(), localRootSpan2);
        localRootSpan3.SetTag("original key", "original value");
        Generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(localRootSpan3, Resource.Empty))
            .Returns(expectAttribute3);
        attributesSpanProcessor.OnEnd(localRootSpan3);
        Assert.Equal(2, localRootSpan3.TagObjects.Count());
        Assert.Equal("original value", localRootSpan3.GetTagItem("original key"));
        Assert.Equal(AwsSpanProcessingUtil.LocalRoot,localRootSpan3.GetTagItem(AwsAttributeKeys.AttributeAWSSpanKind));
        localRootSpan3.Dispose();
    }
    
    private void buildSpanAttributes(bool containsAttribute) {
        if (containsAttribute)
        {
            this.span.SetTag("original key", "original value");
        }
    }
    
    private Dictionary<string, ActivityTagsCollection> buildMetricAttributes( bool containsAttribute, ActivityTagsCollection tagsCollection, Activity span = null)
    {
        if (span == null)
        {
            span = this.span;
        }
        Dictionary<string, ActivityTagsCollection> dict = new Dictionary<string, ActivityTagsCollection>();
        if (AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(span))
        {
            dict[IMetricAttributeGenerator.DependencyMetric] = containsAttribute? tagsCollection : new ActivityTagsCollection();
        }
        if (AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(span))
        {
            dict[IMetricAttributeGenerator.ServiceMetric] = containsAttribute? tagsCollection : new ActivityTagsCollection();
        }
        return dict;
    }

    private ActivityTagsCollection metricAttributesHelper()
    {
        return new ActivityTagsCollection([new KeyValuePair<string, object?>("new key", "new value")]);
    }
}
