// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using Moq;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8602 // Possible null reference argument.
public class AwsMetricAttributesSpanProcessorTest
{
    private Mock<AwsMetricAttributeGenerator> generator = new Mock<AwsMetricAttributeGenerator>();
    private Activity? span;
    private ActivitySource testSource = new ActivitySource("test");
    private AwsMetricAttributesSpanProcessor attributesSpanProcessor;

    public AwsMetricAttributesSpanProcessorTest()
    {
        this.attributesSpanProcessor = AwsMetricAttributesSpanProcessor.Create(this.generator.Object, Resource.Empty);
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        this.span = this.testSource.StartActivity("test", ActivityKind.Server);
    }

    [Fact]
    public void TestOnEndWithoutAttributesOrModification()
    {
        this.BuildSpanAttributes(false);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(false, this.MetricAttributesHelper());

        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttributes);

        var beforeEndAttributes = this.span.TagObjects;
        this.attributesSpanProcessor.OnEnd(this.span);
        Assert.Empty(this.span.TagObjects);
        Assert.Equal(beforeEndAttributes, this.span.TagObjects);
    }

    [Fact]
    public void TestOnEndWithAttributesButWithoutModification()
    {
        this.BuildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(false, this.MetricAttributesHelper());

        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttributes);

        var beforeEndAttributes = this.span.TagObjects;
        this.attributesSpanProcessor.OnEnd(this.span);
        Assert.Single(this.span.TagObjects);
        Assert.Equal(beforeEndAttributes, this.span.TagObjects);
    }

    [Fact]
    public void TestOnEndWithoutAttributesButWithModification()
    {
        this.BuildSpanAttributes(false);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, this.MetricAttributesHelper());
        ActivityTagsCollection expectMetricAttributes = expectAttributes[MetricAttributeGeneratorConstants.ServiceMetric];

        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttributes);

        this.attributesSpanProcessor.OnEnd(this.span);
        Assert.Equal(expectMetricAttributes.Count(), this.span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Key, this.span.TagObjects.FirstOrDefault().Key);
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, this.span.TagObjects.FirstOrDefault().Value);
    }

    [Fact]
    public void TestOnEndWithAttributesAndModification()
    {
        this.BuildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, this.MetricAttributesHelper());
        ActivityTagsCollection expectMetricAttributes = this.MetricAttributesHelper();

        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttributes);

        this.attributesSpanProcessor.OnEnd(this.span);
        Assert.Equal(expectMetricAttributes.Count() + 1, this.span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, this.span.GetTagItem(expectMetricAttributes.FirstOrDefault().Key));
        Assert.Equal("original value", this.span.GetTagItem("original key"));
    }

    [Fact]
    public void TestOnEndMultipleSpan()
    {
        this.BuildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, this.MetricAttributesHelper());
        ActivityTagsCollection expectMetricAttributes = this.MetricAttributesHelper();
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttributes);

        Activity? span2 = this.testSource.StartActivity("test2", ActivityKind.Server);
        Dictionary<string, ActivityTagsCollection> expectAttribute2 = this.BuildMetricAttributes(false, this.MetricAttributesHelper(), span2);
        span2.SetTag("original key", "original value");
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span2, Resource.Empty))
            .Returns(expectAttribute2);

        Activity? span3 = this.testSource.StartActivity("test3", ActivityKind.Server);
        Dictionary<string, ActivityTagsCollection> expectAttribute3 = this.BuildMetricAttributes(true, this.MetricAttributesHelper(), span3);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span3, Resource.Empty))
            .Returns(expectAttribute3);

        this.attributesSpanProcessor.OnEnd(this.span);
        var beforeEndAttributes = span2.TagObjects;
        this.attributesSpanProcessor.OnEnd(span2);
        this.attributesSpanProcessor.OnEnd(span3);

        Assert.Equal(expectMetricAttributes.Count() + 1, this.span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, this.span.GetTagItem(expectMetricAttributes.FirstOrDefault().Key));
        Assert.Equal("original value", this.span.GetTagItem("original key"));

        Assert.Equal(beforeEndAttributes, span2.TagObjects);

        Assert.Single(expectMetricAttributes);
        Assert.Equal(expectMetricAttributes.Count(), span3.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Key, span3.TagObjects.FirstOrDefault().Key);
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span3.TagObjects.FirstOrDefault().Value);
    }

    [Fact]
    public void TestOverridenAttributes()
    {
        this.span.SetTag("key 1", "old value 1");
        this.span.SetTag("key 2", "old value 2");
        ActivityTagsCollection metricAttributes = new ActivityTagsCollection([
            new KeyValuePair<string, object?>("key 1", "new value 1"),
            new KeyValuePair<string, object?>("key 3", "new value 3")
        ]);
        Dictionary<string, ActivityTagsCollection> expectAttribute = this.BuildMetricAttributes(true, metricAttributes);

        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttribute);

        this.attributesSpanProcessor.OnEnd(this.span);
        Assert.Equal(3, this.span.TagObjects.Count());
        Assert.Equal("new value 1", this.span.GetTagItem("key 1"));
        Assert.Equal("old value 2", this.span.GetTagItem("key 2"));
        Assert.Equal("new value 3", this.span.GetTagItem("key 3"));
    }

    [Fact]
    public void TestDependencyAttributes()
    {
        this.span = this.testSource.StartActivity("test dependency", ActivityKind.Client);
        this.BuildSpanAttributes(true);
        Dictionary<string, ActivityTagsCollection> expectAttributes = this.BuildMetricAttributes(true, this.MetricAttributesHelper(), this.span);
        ActivityTagsCollection expectMetricAttributes = this.MetricAttributesHelper();
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(this.span, Resource.Empty))
            .Returns(expectAttributes);

        Activity? span2 = this.testSource.StartActivity("test2", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute2 = this.BuildMetricAttributes(false, this.MetricAttributesHelper(), span2);
        span2.SetTag("original key", "original value");
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span2, Resource.Empty))
            .Returns(expectAttribute2);

        Activity? span3 = this.testSource.StartActivity("test3", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute3 = this.BuildMetricAttributes(true, this.MetricAttributesHelper(), span3);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(span3, Resource.Empty))
            .Returns(expectAttribute3);

        this.attributesSpanProcessor.OnEnd(this.span);
        var beforeEndAttributes = span2.TagObjects;
        this.attributesSpanProcessor.OnEnd(span2);
        this.attributesSpanProcessor.OnEnd(span3);

        Assert.Equal(expectMetricAttributes.Count() + 1, this.span.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, this.span.GetTagItem(expectMetricAttributes.FirstOrDefault().Key));
        Assert.Equal("original value", this.span.GetTagItem("original key"));

        Assert.Equal(beforeEndAttributes, span2.TagObjects);

        Assert.Single(expectMetricAttributes);
        Assert.Equal(expectMetricAttributes.Count(), span3.TagObjects.Count());
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Key, span3.TagObjects.FirstOrDefault().Key);
        Assert.Equal(expectMetricAttributes.FirstOrDefault().Value, span3.TagObjects.FirstOrDefault().Value);
    }

    [Fact]
    public void TestLocalRootSpan()
    {
        this.span.Dispose();
        Activity? localRootSpan1 = this.testSource.StartActivity("test local root", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute1 = this.BuildMetricAttributes(true, this.MetricAttributesHelper(), localRootSpan1);
        localRootSpan1.SetTag("original key", "original value");
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(localRootSpan1, Resource.Empty))
            .Returns(expectAttribute1);
        this.attributesSpanProcessor.OnEnd(localRootSpan1);
        Assert.Equal(this.MetricAttributesHelper().Count() + 2, localRootSpan1.TagObjects.Count());
        Assert.Equal(AwsSpanProcessingUtil.LocalRoot, localRootSpan1.GetTagItem(AwsAttributeKeys.AttributeAWSSpanKind));
        Assert.Equal("original value", localRootSpan1.GetTagItem("original key"));
        Assert.Equal(
            this.MetricAttributesHelper().FirstOrDefault().Value,
            localRootSpan1.GetTagItem(this.MetricAttributesHelper().FirstOrDefault().Key));
        localRootSpan1.Dispose();

        Activity? localRootSpan2 = this.testSource.StartActivity("test local root", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute2 = this.BuildMetricAttributes(true, this.MetricAttributesHelper(), localRootSpan2);
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(localRootSpan2, Resource.Empty))
            .Returns(expectAttribute2);
        this.attributesSpanProcessor.OnEnd(localRootSpan2);
        Assert.Equal(this.MetricAttributesHelper().Count() + 1, localRootSpan2.TagObjects.Count());
        Assert.Equal(AwsSpanProcessingUtil.LocalRoot, localRootSpan2.GetTagItem(AwsAttributeKeys.AttributeAWSSpanKind));
        Assert.Equal(
            this.MetricAttributesHelper().FirstOrDefault().Value,
            localRootSpan2.GetTagItem(this.MetricAttributesHelper().FirstOrDefault().Key));
        localRootSpan2.Dispose();

        Activity? localRootSpan3 = this.testSource.StartActivity("test local root", ActivityKind.Client);
        Dictionary<string, ActivityTagsCollection> expectAttribute3 = this.BuildMetricAttributes(false, this.MetricAttributesHelper(), localRootSpan2);
        localRootSpan3.SetTag("original key", "original value");
        this.generator.Setup(g => g.GenerateMetricAttributeMapFromSpan(localRootSpan3, Resource.Empty))
            .Returns(expectAttribute3);
        this.attributesSpanProcessor.OnEnd(localRootSpan3);
        Assert.Equal(2, localRootSpan3.TagObjects.Count());
        Assert.Equal("original value", localRootSpan3.GetTagItem("original key"));
        Assert.Equal(AwsSpanProcessingUtil.LocalRoot, localRootSpan3.GetTagItem(AwsAttributeKeys.AttributeAWSSpanKind));
        localRootSpan3.Dispose();
    }

    private void BuildSpanAttributes(bool containsAttribute)
    {
        if (containsAttribute)
        {
            this.span?.SetTag("original key", "original value");
        }
    }

    private Dictionary<string, ActivityTagsCollection> BuildMetricAttributes(bool containsAttribute, ActivityTagsCollection tagsCollection, Activity? span = null)
    {
        if (span == null && this.span != null)
        {
            span = this.span;
        }

        Dictionary<string, ActivityTagsCollection> dict = new Dictionary<string, ActivityTagsCollection>();
        if (AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(span))
        {
            dict[MetricAttributeGeneratorConstants.DependencyMetric] = containsAttribute ? tagsCollection : new ActivityTagsCollection();
        }

        if (AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(span))
        {
            dict[MetricAttributeGeneratorConstants.ServiceMetric] = containsAttribute ? tagsCollection : new ActivityTagsCollection();
        }

        return dict;
    }

    private ActivityTagsCollection MetricAttributesHelper()
    {
        return new ActivityTagsCollection([new KeyValuePair<string, object?>("new key", "new value")]);
    }
}
