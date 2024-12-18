// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// testSpanNamePropagationBySpanKind in java is included in TestAttributesPropagationBySpanKind Tests
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
#pragma warning disable CS8604 // Possible null reference argument.
public class AttributePropagatingSpanProcessorTest
{
    private Func<Activity, string> spanNameExtractor = AwsSpanProcessingUtil.GetIngressOperation;
    private Resource resource = Resource.Empty;
    private string spanNameKey = "spanName";
    private string testKey1 = "key1";
    private string testKey2 = "key2";
    private ActivitySource activitySource = new ActivitySource("test");
    private AttributePropagatingSpanProcessor attributePropagatingSpanProcessor;

    public AttributePropagatingSpanProcessorTest()
    {
        ReadOnlyCollection<string> attributesKeysToPropagate = new ReadOnlyCollection<string>([this.testKey1, this.testKey2]);
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        this.attributePropagatingSpanProcessor =
            AttributePropagatingSpanProcessor.Create(this.spanNameExtractor, this.spanNameKey, attributesKeysToPropagate);
    }

    [Fact]
    public void TestAttributesPropagationBySpanKindWithAppOnly()
    {
        foreach (ActivityKind activityKind in Enum.GetValues(typeof(ActivityKind)))
        {
            Activity? spanWithAppOnly = this.activitySource.StartActivity("parent", activityKind);
            spanWithAppOnly?.SetTag(this.testKey1, "testValue1");
            this.attributePropagatingSpanProcessor.OnStart(spanWithAppOnly);
            if (activityKind == ActivityKind.Server)
            {
                this.ValidateSpanAttributesInheritance(spanWithAppOnly, "parent", null, null);
            }
            else if (activityKind == ActivityKind.Internal)
            {
                this.ValidateSpanAttributesInheritance(spanWithAppOnly, "InternalOperation", "testValue1", null);
            }
            else
            {
                this.ValidateSpanAttributesInheritance(spanWithAppOnly, "InternalOperation", null, null);
            }

            spanWithAppOnly.Dispose();
        }
    }

    [Fact]
    public void TestAttributesPropagationBySpanKindWithOpOnly()
    {
        foreach (ActivityKind activityKind in Enum.GetValues(typeof(ActivityKind)))
        {
            Activity? spanWithOpOnly = this.activitySource.StartActivity("parent", activityKind);
            spanWithOpOnly?.SetTag(this.testKey2, "testValue2");
            this.attributePropagatingSpanProcessor.OnStart(spanWithOpOnly);
            if (activityKind == ActivityKind.Server)
            {
                this.ValidateSpanAttributesInheritance(spanWithOpOnly, "parent", null, null);
            }
            else if (activityKind == ActivityKind.Internal)
            {
                this.ValidateSpanAttributesInheritance(spanWithOpOnly, "InternalOperation", null, "testValue2");
            }
            else
            {
                this.ValidateSpanAttributesInheritance(spanWithOpOnly, "InternalOperation", null, null);
            }

            spanWithOpOnly.Dispose();
        }
    }

    [Fact]
    public void TestAttributesPropagationBySpanKindWithAppAndOp()
    {
        foreach (ActivityKind activityKind in Enum.GetValues(typeof(ActivityKind)))
        {
            Activity? spanWithAppAndOp = this.activitySource.StartActivity("parent", activityKind);
            spanWithAppAndOp?.SetTag(this.testKey1, "testValue1");
            spanWithAppAndOp?.SetTag(this.testKey2, "testValue2");
            this.attributePropagatingSpanProcessor.OnStart(spanWithAppAndOp);
            if (activityKind == ActivityKind.Server)
            {
                this.ValidateSpanAttributesInheritance(spanWithAppAndOp, "parent", null, null);
            }
            else if (activityKind == ActivityKind.Internal)
            {
                this.ValidateSpanAttributesInheritance(spanWithAppAndOp, "InternalOperation", "testValue1", "testValue2");
            }
            else
            {
                this.ValidateSpanAttributesInheritance(spanWithAppAndOp, "InternalOperation", null, null);
            }

            spanWithAppAndOp.Dispose();
        }
    }

    [Fact]
    public void TestAttributesPropagationWithInternalKinds()
    {
        Activity? grandParentActivity = this.activitySource.StartActivity("grandparent", ActivityKind.Internal);
        grandParentActivity?.SetTag(this.testKey1, "testValue1");
        this.attributePropagatingSpanProcessor.OnStart(grandParentActivity);

        Activity? parentActivity = this.activitySource.StartActivity("parent", ActivityKind.Internal);
        parentActivity?.SetTag(this.testKey2, "testValue2");
        this.attributePropagatingSpanProcessor.OnStart(parentActivity);

        Activity? childActivity = this.activitySource.StartActivity("grandparent", ActivityKind.Client);
        this.attributePropagatingSpanProcessor.OnStart(childActivity);

        Activity? grandChildActivity = this.activitySource.StartActivity("grandparent", ActivityKind.Internal);
        this.attributePropagatingSpanProcessor.OnStart(grandChildActivity);

        Assert.Equal("testValue1", grandParentActivity.GetTagItem(this.testKey1));
        Assert.Null(grandParentActivity.GetTagItem(this.testKey2));

        Assert.Equal("testValue1", parentActivity.GetTagItem(this.testKey1));
        Assert.Equal("testValue2", parentActivity.GetTagItem(this.testKey2));

        Assert.Equal("testValue1", childActivity.GetTagItem(this.testKey1));
        Assert.Equal("testValue2", childActivity.GetTagItem(this.testKey2));

        Assert.Null(grandChildActivity.GetTagItem(this.testKey1));
        Assert.Null(grandChildActivity.GetTagItem(this.testKey2));
    }

    [Fact]
    public void TestOverrideAttributes()
    {
        Activity? parentActivity = this.activitySource.StartActivity("parent", ActivityKind.Server);
        parentActivity?.SetTag(this.testKey1, "testValue1");
        parentActivity?.SetTag(this.testKey2, "testValue2");

        this.CreateNestedSpan(parentActivity, 2);

        Activity? childActivity = this.activitySource.StartActivity("child:1");
        childActivity?.SetTag(this.testKey2, "testValue3");

        Activity transmitActivity2 = this.CreateNestedSpan(childActivity, 2);

        Assert.Equal("testValue3", transmitActivity2.GetTagItem(this.testKey2));
    }

    [Fact]
    public void TestSpanNamePropagationWithRemoteParentSpan()
    {
        this.activitySource.StartActivity("parent");
        Activity? activity = this.activitySource.StartActivity("parent", ActivityKind.Server);
        PropertyInfo? propertyInfo = typeof(Activity).GetProperty("HasRemoteParent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo? setterMethodInfo = propertyInfo?.GetSetMethod(true);
        setterMethodInfo?.Invoke(activity, new object[] { true });
        this.ValidateSpanAttributesInheritance(activity, "parent", null, null);
    }

    [Fact]
    public void TestAwsSdkDescendantSpan()
    {
        Activity? awsSdkActivity = this.activitySource.StartActivity("parent", ActivityKind.Client);
        awsSdkActivity?.SetTag(TraceSemanticConventions.AttributeRpcSystem, "aws-api");
        Assert.Null(awsSdkActivity?.GetTagItem(AwsAttributeKeys.AttributeAWSSdkDescendant));

        Activity childActivity = this.CreateNestedSpan(awsSdkActivity, 1);
        Assert.NotNull(childActivity.GetTagItem(AwsAttributeKeys.AttributeAWSSdkDescendant));
        Assert.Equal("true", childActivity.GetTagItem(AwsAttributeKeys.AttributeAWSSdkDescendant));
    }

    [Fact]
    public void TestConsumerParentSpanKindAttributePropagation()
    {
        this.activitySource.StartActivity("grandparent", ActivityKind.Consumer);
        Activity? parentActivity = this.activitySource.StartActivity("parent", ActivityKind.Internal);
        Activity? childActivity = this.activitySource.StartActivity("child", ActivityKind.Consumer);
        childActivity?.SetTag(
            TraceSemanticConventions.AttributeMessagingOperation,
            TraceSemanticConventions.MessagingOperationValues.Process);

        Assert.Null(parentActivity?.GetTagItem(AwsAttributeKeys.AttributeAWSConsumerParentSpanKind));
        Assert.Null(childActivity?.GetTagItem(AwsAttributeKeys.AttributeAWSConsumerParentSpanKind));
    }

    [Fact]
    public void TestNoConsumerParentSpanKindAttributeWithConsumerProcess()
    {
        this.activitySource.StartActivity("parent", ActivityKind.Server);
        Activity? childActivity = this.activitySource.StartActivity("child", ActivityKind.Consumer);
        childActivity?.SetTag(
            TraceSemanticConventions.AttributeMessagingOperation,
            TraceSemanticConventions.MessagingOperationValues.Process);
        Assert.Null(childActivity?.GetTagItem(AwsAttributeKeys.AttributeAWSConsumerParentSpanKind));
    }

    [Fact]
    public void TestConsumerParentSpanKindAttributeWithConsumerParent()
    {
        Activity? parentActivity = this.activitySource.StartActivity("parent", ActivityKind.Consumer);
        Activity? childActivity = this.activitySource.StartActivity("child", ActivityKind.Consumer);
        this.attributePropagatingSpanProcessor.OnStart(parentActivity);
        this.attributePropagatingSpanProcessor.OnStart(childActivity);
        childActivity.SetTag(
            TraceSemanticConventions.AttributeMessagingOperation,
            TraceSemanticConventions.MessagingOperationValues.Process);
        Assert.Equal(ActivityKind.Consumer.ToString(), childActivity.GetTagItem(AwsAttributeKeys.AttributeAWSConsumerParentSpanKind));
    }

    private Activity CreateNestedSpan(Activity parentSpan, int depth)
    {
        if (depth == 0)
        {
            return parentSpan;
        }

        Activity? childSpan = this.activitySource.StartActivity("child:" + depth);
        this.attributePropagatingSpanProcessor.OnStart(childSpan);

        try
        {
            return this.CreateNestedSpan(childSpan, depth - 1);
        }
        finally
        {
            childSpan.Stop();
        }
    }

    private void ValidateSpanAttributesInheritance(
        Activity parentActivity,
        string? propagatedName,
        string? propagationValue1,
        string? propagatedValue2)
    {
        Activity leafActivity = this.CreateNestedSpan(parentActivity, 10);

        Assert.Equal("child:1", leafActivity.DisplayName);
        if (propagatedName != null)
        {
            Assert.Equal(propagatedName, leafActivity.GetTagItem(this.spanNameKey));
        }
        else
        {
            Assert.Null(leafActivity.GetTagItem(this.spanNameKey));
        }

        if (propagationValue1 != null)
        {
            Assert.Equal(leafActivity.GetTagItem(this.testKey1), propagationValue1);
        }
        else
        {
            Assert.Null(leafActivity.GetTagItem(this.testKey1));
        }

        if (propagatedValue2 != null)
        {
            Assert.Equal(leafActivity.GetTagItem(this.testKey2), propagatedValue2);
        }
        else
        {
            Assert.Null(leafActivity.GetTagItem(this.testKey2));
        }
    }
}
