// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

using Xunit;

public class AwsSpanProcessingUtilTest
{
    private readonly ActivitySource testSource = new ActivitySource("Test Source");
    private readonly string internalOperation = "InternalOperation";
    private readonly string unknownOperation = "UnknownOperation";
    private readonly string defaultPathValue = "/";

    public AwsSpanProcessingUtilTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
    }

    [Fact]
    public void TestGetIngressOperationValidName()
    {
        string validName = "ValidName";
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.DisplayName = validName;
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(validName, actualOperation);
    }

    [Fact]
    public void TestGetIngressOperationWithnotServer()
    {
        string validName = "ValidName";
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.DisplayName = validName;
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(this.internalOperation, actualOperation);
    }

    [Fact]
    public void TestGetIngressOperationHttpMethodNameAndNoFallback()
    {
        string validName = "GET";
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpRequestMethod, validName);
        spanDataMock.DisplayName = validName;
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(this.unknownOperation, actualOperation);
    }

    [Fact]
    public void TestGetIngressOperationEmptyNameAndNoFallback()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.DisplayName = string.Empty;
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(this.unknownOperation, actualOperation);
    }

    [Fact]
    public void TestGetIngressOperationUnknownNameAndNoFallback()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.DisplayName = this.unknownOperation;
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(this.unknownOperation, actualOperation);
    }

    [Fact]
    public void TestGetIngressOperationInvalidNameAndValidTarget()
    {
        string invalidName = string.Empty;
        string validTarget = "/";
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.DisplayName = invalidName;
        spanDataMock.SetTag(AttributeUrlPath, validTarget);
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(validTarget, actualOperation);
    }

    [Fact]
    public void TestGetIngressOperationInvalidNameAndValidTargetAndMethod()
    {
        string invalidName = string.Empty;
        string validTarget = "/";
        string validMethod = "GET";
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.DisplayName = invalidName;
        spanDataMock.SetTag(AttributeHttpRequestMethod, validMethod);
        spanDataMock.SetTag(AttributeUrlPath, validTarget);
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetIngressOperation(spanDataMock);
        Assert.Equal(validMethod + " " + validTarget, actualOperation);
    }

    [Fact]
    public void TestGetEgressOperationUseInternalOperation()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer);
        spanDataMock.DisplayName = string.Empty;
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetEgressOperation(spanDataMock);
        Assert.Equal(this.internalOperation, actualOperation);
    }

    [Fact]
    public void TestGetEgressOperationUseLocalOperation()
    {
        string operation = "TestOperation";
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeAWSLocalOperation, operation);
        spanDataMock.Start();
        string actualOperation = AwsSpanProcessingUtil.GetEgressOperation(spanDataMock);
        Assert.Equal(operation, actualOperation);
    }

    [Fact]
    public void TestExtractAPIPathValueEmptyTarget()
    {
        string invalidTarget = string.Empty;
        string pathValue = AwsSpanProcessingUtil.ExtractAPIPathValue(invalidTarget);
        Assert.Equal(this.defaultPathValue, pathValue);
    }

    [Fact]
    public void TestExtractAPIPathValueNullTarget()
    {
        string invalidTarget = null;
        string pathValue = AwsSpanProcessingUtil.ExtractAPIPathValue(invalidTarget);
        Assert.Equal(this.defaultPathValue, pathValue);
    }

    [Fact]
    public void TestExtractAPIPathValueNoSlash()
    {
        string invalidTarget = "users";
        string pathValue = AwsSpanProcessingUtil.ExtractAPIPathValue(invalidTarget);
        Assert.Equal(this.defaultPathValue, pathValue);
    }

    [Fact]
    public void TestExtractAPIPathValueOnlySlash()
    {
        string invalidTarget = "/";
        string pathValue = AwsSpanProcessingUtil.ExtractAPIPathValue(invalidTarget);
        Assert.Equal(this.defaultPathValue, pathValue);
    }

    [Fact]
    public void TestExtractAPIPathValueOnlySlashAtEnd()
    {
        string invalidTarget = "users/";
        string pathValue = AwsSpanProcessingUtil.ExtractAPIPathValue(invalidTarget);
        Assert.Equal(this.defaultPathValue, pathValue);
    }

    [Fact]
    public void TestExtractAPIPathValidPath()
    {
        string validTarget = "/users/1/pet?query#fragment";
        string pathValue = AwsSpanProcessingUtil.ExtractAPIPathValue(validTarget);
        Assert.Equal("/users", pathValue);
    }

    [Fact]
    public void TestIsKeyPresentKeyPresent()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeUrlPath, "target");
        spanDataMock.Start();
        Assert.True(AwsSpanProcessingUtil.IsKeyPresent(spanDataMock, AttributeUrlPath));
    }

    [Fact]
    public void TestIsKeyPresentKeyAbsent()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.Start();
        Assert.False(AwsSpanProcessingUtil.IsKeyPresent(spanDataMock, AttributeUrlPath));
    }

    [Fact]
    public void TestIsAwsSpanTrue()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.SetTag(AttributeRpcSystem, "aws-api");
        spanDataMock.Start();
        Assert.True(AwsSpanProcessingUtil.IsAwsSDKSpan(spanDataMock));
    }

    [Fact]
    public void TestIsAwsSpanFalse()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        spanDataMock.Start();
        Assert.False(AwsSpanProcessingUtil.IsAwsSDKSpan(spanDataMock));
    }

    [Fact]
    public void TestShouldUseInternalOperationFalse()
    {
        var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server);
        Assert.False(AwsSpanProcessingUtil.ShouldUseInternalOperation(spanDataMock));

        spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer);
        spanDataMock.Start();
        using (var subActivity = this.testSource.StartActivity("test Child"))
        {
            subActivity.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
            subActivity.Start();
            Assert.False(AwsSpanProcessingUtil.ShouldUseInternalOperation(spanDataMock));
        }
    }

    [Fact]
    public void TestShouldGenerateServiceMetricAttributes()
    {
        var spanDataMock = this.testSource.StartActivity("test");
        spanDataMock.Start();
        using (var subActivity = this.testSource.StartActivity("test Child", ActivityKind.Server))
        {
            subActivity.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
            subActivity.Start();
            Assert.True(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(subActivity));
        }

        using (var subActivity = this.testSource.StartActivity("test Child", ActivityKind.Consumer))
        {
            subActivity.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
            subActivity.Start();
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(subActivity));
        }

        using (var subActivity = this.testSource.StartActivity("test Child", ActivityKind.Internal))
        {
            subActivity.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
            subActivity.Start();
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(subActivity));
        }

        using (var subActivity = this.testSource.StartActivity("test Child", ActivityKind.Producer))
        {
            subActivity.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
            subActivity.Start();
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(subActivity));
        }

        using (var subActivity = this.testSource.StartActivity("test Child", ActivityKind.Client))
        {
            subActivity.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
            subActivity.Start();
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(subActivity));
        }
    }

    [Fact]
    public void TestShouldGenerateDependencyMetricAttributes()
    {
        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Server))
        {
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }

        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Internal))
        {
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }

        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer))
        {
            Assert.True(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }

        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Producer))
        {
            Assert.True(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }

        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client))
        {
            Assert.True(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }

        var parentSpan = this.testSource.StartActivity("test Parent");
        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            spanDataMock.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
            spanDataMock.SetTag(AttributeAWSConsumerParentSpanKind, ActivityKind.Consumer.ToString());
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }

        using (var spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer))
        {
            spanDataMock.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
            spanDataMock.SetTag(AttributeAWSConsumerParentSpanKind, ActivityKind.Consumer.ToString());
            Assert.False(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        }
    }

    [Fact]
    public void TestIsLocalRoot()
    {
        using (var spanDataMock = this.testSource.StartActivity("test"))
        {
            Assert.True(AwsSpanProcessingUtil.IsLocalRoot(spanDataMock));
        }

        var parentSpan = this.testSource.StartActivity("test Parent");
        using (var spanDataMock = this.testSource.StartActivity("test"))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            Assert.False(AwsSpanProcessingUtil.IsLocalRoot(spanDataMock));
        }

        using (var spanDataMock = this.testSource.StartActivity("test"))
        {
            spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
            PropertyInfo propertyInfo = typeof(Activity).GetProperty("HasRemoteParent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo setterMethodInfo = propertyInfo.GetSetMethod(true);
            setterMethodInfo.Invoke(spanDataMock, new object[] { true });
            Assert.True(AwsSpanProcessingUtil.IsLocalRoot(spanDataMock));
        }

        parentSpan.Dispose();
        using (var spanDataMock = this.testSource.StartActivity("test"))
        {
            PropertyInfo propertyInfo = typeof(Activity).GetProperty("HasRemoteParent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo setterMethodInfo = propertyInfo.GetSetMethod(true);
            setterMethodInfo.Invoke(spanDataMock, new object[] { true });
            Assert.True(AwsSpanProcessingUtil.IsLocalRoot(spanDataMock));
        }
    }

    [Fact]
    public void TestIsConsumerProcessSpanFalse()
    {
        var spanDataMock = this.testSource.StartActivity("test");
        Assert.False(AwsSpanProcessingUtil.IsConsumerProcessSpan(spanDataMock));
    }

    [Fact]
    public void TestNoMetricAttributesForSqsConsumerSpanAwsSdk()
    {
        ActivitySource awsActivitySource = new ActivitySource("Amazon.AWS.AWSClientInstrumentation");
        var spanDataMock = awsActivitySource.StartActivity("SQS.ReceiveMessage", ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeAWSServiceName, "SQS");
        spanDataMock.Start();
        Assert.False(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(spanDataMock));
        Assert.False(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
    }

    [Fact]
    public void TestMetricAttributesGeneratedForOtherInstrumentationSqsConsumerSpan()
    {
        var spanDataMock = this.testSource.StartActivity("SQS.ReceiveMessage", ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeAWSServiceName, "SQS");
        spanDataMock.Start();
        Assert.True(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(spanDataMock));
        Assert.True(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
    }

    [Fact]
    public void TestNoMetricAttributesForAwsSdkSqsConsumerProcessSpan()
    {
        ActivitySource awsActivitySource = new ActivitySource("Amazon.AWS.AWSClientInstrumentation");
        var spanDataMock = awsActivitySource.StartActivity("SQS.ReceiveMessage", ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeAWSServiceName, "SQS");
        spanDataMock.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
        spanDataMock.Start();
        Assert.False(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(spanDataMock));
        Assert.False(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        spanDataMock.Dispose();

        spanDataMock = awsActivitySource.StartActivity("SQS.ReceiveMessage", ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeAWSServiceName, "SQS");
        spanDataMock.SetTag(AttributeMessagingOperation, MessagingOperationValues.Receive);
        spanDataMock.Start();
        Assert.True(AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(spanDataMock));
        Assert.True(AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(spanDataMock));
        spanDataMock.Dispose();
    }

    [Fact]
    public void TestSqlDialectKeywordsOrder()
    {
        List<string> keywords = AwsSpanProcessingUtil.GetDialectKeywords();
        int prevKeywordLength = int.MaxValue;
        foreach (var keyword in keywords)
        {
            int currKeywordLength = keyword.Length;
            Assert.True(prevKeywordLength >= currKeywordLength);
            prevKeywordLength = currKeywordLength;
        }
    }

    [Fact]
    public void TestSqlDialectKeywordsMaxLength()
    {
        var keywords = AwsSpanProcessingUtil.GetDialectKeywords();
        foreach (var keyword in keywords)
        {
            Assert.True(keyword.Length <= AwsSpanProcessingUtil.MaxKeywordLength);
        }
    }
}
