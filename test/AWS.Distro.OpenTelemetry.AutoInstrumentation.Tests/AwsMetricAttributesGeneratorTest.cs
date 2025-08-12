// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using AWS.Distro.OpenTelemetry.AutoInstrumentation;
using Moq;
using OpenTelemetry.Resources;
using Xunit;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsMetricAttributeGenerator;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// There are 5 tests in this class cannot be done in dotnet:

// 1. testHttpStatusAttributeNotAwsSdk
// 2. testHttpStatusAttributeStatusAlreadyPresent
// 3. testHttpStatusAttributeGetStatusCodeException
// 4. testHttpStatusAttributeStatusCodeException
// 5. testHttpStatusAttributeNoStatusCodeException
// Throwable related logic is not implemented or not supported in dotnet
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS8604 // Possible null reference argument.
public class AwsMetricAttributesGeneratorTest
{
    private readonly ActivitySource testSource = new ActivitySource("Test Source");
    private AutoInstrumentation.AwsMetricAttributeGenerator generator = new AutoInstrumentation.AwsMetricAttributeGenerator();
    private Resource resource = Resource.Empty;
    private Activity? parentSpan;
    private string serviceNameValue = "Service name";
    private string spanNameValue = "Span name";
    private string awsRemoteServiceValue = "AWS remote service";
    private string awsRemoteOperationValue = "AWS remote operation";
    private string awsLocalOperationValue = "AWS local operation";
    private string awsRemoteResourceRegion = "us-east-1";
    private string awsRemoteResourceAccessKey = "Test access key";

    public AwsMetricAttributesGeneratorTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        this.parentSpan = this.testSource.StartActivity("test");
    }

    [Fact]
    public void TestServerSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString().ToUpper()),
            new (AttributeAWSLocalService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Server);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestConsumerSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Consumer.ToString().ToUpper()),
            new (AttributeAWSLocalService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Consumer);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestSpanAttributesForEmptyResource()
    {
        this.resource = Resource.Empty;
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString().ToUpper()),
            new (AttributeAWSLocalService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Server);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestProducerSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Producer.ToString().ToUpper()),
            new (AttributeAWSLocalService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Producer);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestClientSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Client.ToString().ToUpper()),
            new (AttributeAWSLocalService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Client);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestInternalSpan()
    {
        // Spans with internal span kind should not produce any attributes.
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Internal);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(new ActivityTagsCollection(), spanDataMock);
    }

    [Fact]
    public void TestLocalRootServerSpan()
    {
        this.UpdateResourceWithServiceName();
        this.parentSpan?.Dispose();
        Activity? spanDataMock = this.testSource.StartActivity(this.spanNameValue, ActivityKind.Server);
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AutoInstrumentation.AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, this.spanNameValue),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestLocalRootInternalSpan()
    {
        this.UpdateResourceWithServiceName();
        this.parentSpan?.Dispose();
        Activity? spanDataMock = this.testSource.StartActivity(this.spanNameValue, ActivityKind.Internal);
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AutoInstrumentation.AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestLocalRootClientSpan()
    {
        this.UpdateResourceWithServiceName();
        this.parentSpan?.Dispose();
        Activity? spanDataMock = this.testSource.StartActivity(this.spanNameValue, ActivityKind.Client);
        spanDataMock.SetTag(AttributeAWSRemoteService, this.awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, this.awsRemoteOperationValue);

        List<KeyValuePair<string, object?>> expectServiceAttiributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AutoInstrumentation.AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
        };

        List<KeyValuePair<string, object?>> expectDependencyAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Client.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
            new (AttributeAWSRemoteService, this.awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, this.awsRemoteOperationValue),
        };

        ActivityTagsCollection expectServiceAttiributes = new ActivityTagsCollection(expectServiceAttiributesList);
        ActivityTagsCollection expectDependencyAttributes = new ActivityTagsCollection(expectDependencyAttributesList);

        this.ValidateAttributesProducedForLocalRootSpanOfKind(expectServiceAttiributes, expectDependencyAttributes, spanDataMock);
    }

    [Fact]
    public void TestLocalRootConsumerSpan()
    {
        this.UpdateResourceWithServiceName();
        this.parentSpan?.Dispose();
        Activity? spanDataMock = this.testSource.StartActivity(this.spanNameValue, ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeAWSRemoteService, this.awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, this.awsRemoteOperationValue);

        List<KeyValuePair<string, object?>> expectServiceAttiributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AutoInstrumentation.AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
        };

        List<KeyValuePair<string, object?>> expectDependencyAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Consumer.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
            new (AttributeAWSRemoteService, this.awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, this.awsRemoteOperationValue),
        };

        ActivityTagsCollection expectServiceAttiributes = new ActivityTagsCollection(expectServiceAttiributesList);
        ActivityTagsCollection expectDependencyAttributes = new ActivityTagsCollection(expectDependencyAttributesList);

        this.ValidateAttributesProducedForLocalRootSpanOfKind(expectServiceAttiributes, expectDependencyAttributes, spanDataMock);
    }

    [Fact]
    public void TestLocalRootProducerSpan()
    {
        this.UpdateResourceWithServiceName();
        this.parentSpan?.Dispose();
        Activity? spanDataMock = this.testSource.StartActivity(this.spanNameValue, ActivityKind.Producer);
        spanDataMock.SetTag(AttributeAWSRemoteService, this.awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, this.awsRemoteOperationValue);

        List<KeyValuePair<string, object?>> expectServiceAttiributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AutoInstrumentation.AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
        };

        List<KeyValuePair<string, object?>> expectDependencyAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Producer.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.InternalOperation),
            new (AttributeAWSRemoteService, this.awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, this.awsRemoteOperationValue),
        };

        ActivityTagsCollection expectServiceAttiributes = new ActivityTagsCollection(expectServiceAttiributesList);
        ActivityTagsCollection expectDependencyAttributes = new ActivityTagsCollection(expectDependencyAttributesList);

        this.ValidateAttributesProducedForLocalRootSpanOfKind(expectServiceAttiributes, expectDependencyAttributes, spanDataMock);
    }

    [Fact]
    public void TestConsumerSpanWithAttributes()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Consumer.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Consumer);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestServerSpanWithAttributes()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, this.spanNameValue),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(this.spanNameValue, ActivityKind.Server);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    // Equal to testServerSpanWithNullSpanName, dotnet do not allow null name, test empty instead
    [Fact]
    public void TestServerSpanWithEmptySpanName()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Server);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestServerSpanWithSpanNameAsHttpMethod()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, AutoInstrumentation.AwsSpanProcessingUtil.UnknownOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity("GET", ActivityKind.Server);
        spanDataMock.SetTag(AutoInstrumentation.AwsSpanProcessingUtil.AttributeHttpRequestMethod, "GET");
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        var service = AutoInstrumentation.AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(spanDataMock);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestServerSpanWithSpanNameWithHttpTarget()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, "POST /payment"),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity("POST", ActivityKind.Server);
        spanDataMock.SetTag(AutoInstrumentation.AwsSpanProcessingUtil.AttributeHttpRequestMethod, "POST");
        spanDataMock.SetTag(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlPath, "/payment/123");
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestProducerSpanWithAttributes()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Producer.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, this.awsLocalOperationValue),
            new (AttributeAWSRemoteService, this.awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, this.awsRemoteOperationValue),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Producer);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        spanDataMock.SetTag(AttributeAWSLocalOperation, this.awsLocalOperationValue);
        spanDataMock.SetTag(AttributeAWSRemoteService, this.awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, this.awsRemoteOperationValue);

        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestClientSpanWithAttributes()
    {
        this.UpdateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Client.ToString().ToUpper()),
            new (AttributeAWSLocalService, this.serviceNameValue),
            new (AttributeAWSLocalOperation, this.awsLocalOperationValue),
            new (AttributeAWSRemoteService, this.awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, this.awsRemoteOperationValue),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        Activity? spanDataMock = this.testSource.StartActivity(string.Empty, ActivityKind.Client);
        spanDataMock.SetParentId(this.parentSpan.TraceId, this.parentSpan.SpanId);
        spanDataMock.SetTag(AttributeAWSLocalOperation, this.awsLocalOperationValue);
        spanDataMock.SetTag(AttributeAWSRemoteService, this.awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, this.awsRemoteOperationValue);

        this.ValidateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void TestRemoteAttributesCombinations()
    {
        Dictionary<string, object?> attributesCombination = new Dictionary<string, object?>
        {
            { AttributeAWSRemoteService, "TestString" },
            { AttributeAWSRemoteOperation, "TestString" },
            { AttributeRpcService, "TestString" },
            { AttributeRpcMethod, "TestString" },
            { AttributeDbSystem, "TestString" },
            { AttributeDbOperation, "TestString" },
            { AttributeDbStatement, "TestString" },
            { AttributeFaasInvokedProvider, "TestString" },
            { AttributeFaasInvokedName, "TestString" },
            { AttributeMessagingSystem, "TestString" },
            { AttributeMessagingOperation, "TestString" },
            { AttributeGraphqlOperationType, "TestString" },

            // Do not set dummy value for PEER_SERVICE, since it has special behaviour.
            // Two unused attributes to show that we will not make use of unrecognized attributes
            { "unknown.service.key", "TestString" },
            { "unknown.operation.key", "TestString" },
        };

        attributesCombination = this.ValidateAndRemoveRemoteAttributes(AttributeAWSRemoteService, this.awsRemoteServiceValue, AttributeAWSRemoteOperation, this.awsRemoteOperationValue, attributesCombination);

        attributesCombination = this.ValidateAndRemoveRemoteAttributes(AttributeRpcService, "RPC service", AttributeRpcMethod, "RPC Method", attributesCombination);

        attributesCombination = this.ValidateAndRemoveRemoteAttributes(AttributeDbSystem, "DB system", AttributeDbOperation, "DB operation", attributesCombination);

        attributesCombination[AttributeDbSystem] = "DB system";
        attributesCombination.Remove(AttributeDbOperation);
        attributesCombination.Remove(AttributeDbStatement);

        attributesCombination = this.ValidateAndRemoveRemoteAttributes(AttributeDbSystem, "DB system", AttributeDbOperation, UnknownRemoteOperation, attributesCombination);

        // Validate behaviour of various combinations of FAAS attributes, then remove them.
        attributesCombination = this.ValidateAndRemoveRemoteAttributes(AttributeFaasInvokedName, "FAAS invoked name", AttributeFaasTrigger, "FAAS trigger name", attributesCombination);

        // Validate behaviour of various combinations of Messaging attributes, then remove them.
        attributesCombination = this.ValidateAndRemoveRemoteAttributes(AttributeMessagingSystem, "Messaging system", AttributeMessagingOperation, "Messaging operation", attributesCombination);

        // Validate behaviour of GraphQL operation type attribute, then remove it.
        attributesCombination[AttributeGraphqlOperationType] = "GraphQL operation type";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "graphql", "GraphQL operation type");
        attributesCombination.Remove(AttributeGraphqlOperationType);

        // Validate behaviour of extracting Remote Service from net.peer.name
        attributesCombination[AttributeNetPeerName] = "www.example.com";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "www.example.com", AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetPeerName);

        // Validate behaviour of extracting Remote Service from net.peer.name and net.peer.port
        attributesCombination[AttributeNetPeerName] = "192.168.0.0";
        attributesCombination[AttributeNetPeerPort] = 8081L;
        this.ValidateExpectedRemoteAttributes(attributesCombination, "192.168.0.0:8081", AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetPeerName);
        attributesCombination.Remove(AttributeNetPeerPort);

        // Validate behaviour of extracting Remote Service from net.peer.socket.addr
        attributesCombination[AttributeNetSockPeerAddr] = "www.example.com";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "www.example.com", AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetSockPeerAddr);

        // Validate behaviour of extracting Remote Service from net.peer.name and net.peer.port
        attributesCombination[AttributeNetSockPeerAddr] = "192.168.0.0";
        attributesCombination[AttributeNetSockPeerPort] = 8081L;
        this.ValidateExpectedRemoteAttributes(attributesCombination, "192.168.0.0:8081", AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetSockPeerAddr);
        attributesCombination.Remove(AttributeNetSockPeerPort);

        // Validate behavior of Remote Operation from HttpTarget - with 1st api part. Also validates
        // that RemoteService is extracted from HttpUrl.
        attributesCombination[AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull] = "http://www.example.com/payment/123";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "www.example.com:80", "/payment");
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);

        // Validate behavior of Remote Operation from HttpTarget - with 1st api part. Also validates
        // that RemoteService is extracted from HttpUrl.
        attributesCombination[AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull] = "http://www.example.com";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "www.example.com:80", "/");
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);

        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull] = "http://192.168.1.1";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "192.168.1.1:80", "/");
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);

        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull] = string.Empty;
        this.ValidateExpectedRemoteAttributes(attributesCombination, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);

        // Validate behavior of Remote Service from HttpUrl
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);
        this.ValidateExpectedRemoteAttributes(attributesCombination, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);

        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull] = "abc";
        this.ValidateExpectedRemoteAttributes(attributesCombination, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AutoInstrumentation.AwsSpanProcessingUtil.AttributeUrlFull);

        attributesCombination[AttributePeerService] = "Peer service";
        this.ValidateExpectedRemoteAttributes(attributesCombination, "Peer service", AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributePeerService);

        this.ValidateExpectedRemoteAttributes(attributesCombination, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);
    }

    [Fact]
    public void TestDBClientSpanWithRemoteResourceAttributes()
    {
        // Validate behaviour of DB_NAME, SERVER_ADDRESS and SERVER_PORT exist
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, 3306L },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate BuildDbConnection string when server port is string
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, "3306" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate BuildDbConnection string when server port is int32
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, 3306L },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate behaviour of DB_NAME with '|' char, SERVER_ADDRESS and SERVER_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name|special" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, 3306L },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name^|special|abc.com|3306", "db_name^|special|abc.com|3306", false);

        // Validate behaviour of DB_NAME with '^' char, SERVER_ADDRESS and SERVER_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name^special" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, 3306L },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name^^special|abc.com|3306", "db_name^^special|abc.com|3306", false);

        // Validate behaviour of DB_NAME, SERVER_ADDRESS exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerAddress, "abc.com" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com", "db_name|abc.com", false);

        // Validate behaviour of SERVER_ADDRESS exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeServerAddress, "abc.com" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "abc.com", "abc.com", false);

        // Validate behaviour of SERVER_PORT exist
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeServerPort, 3306L);

        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();

        // Validate behaviour of DB_NAME, NET_PEER_NAME and NET_PEER_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeNetPeerName, "abc.com" },
            { AttributeNetPeerPort, 3306L },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate BuildDbConnection string when AttributeNetPeerPort is int32
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeNetPeerName, "abc.com" },
            { AttributeNetPeerPort, 3306 },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate BuildDbConnection string when AttributeNetPeerPort is string
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeNetPeerName, "abc.com" },
            { AttributeNetPeerPort, "3306" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate behaviour of DB_NAME, NET_PEER_NAME exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeNetPeerName, "abc.com" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com", "db_name|abc.com", false);

        // Validate behaviour of NET_PEER_NAME exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeNetPeerName, "abc.com" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "abc.com", "abc.com", false);

        // Validate behaviour of NET_PEER_PORT exist
        spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeServerPort, 3306L);

        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();

        // Validate behaviour of DB_NAME, SERVER_SOCKET_ADDRESS and SERVER_SOCKET_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerSocketAddress, "abc.com" },
            { AttributeServerSocketPort, 3306L },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate BuildDbConnection string when AttributeServerSocketPort is int32
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerSocketAddress, "abc.com" },
            { AttributeServerSocketPort, 3306 },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate BuildDbConnection string when AttributeServerSocketPort is string
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerSocketAddress, "abc.com" },
            { AttributeServerSocketPort, "3306" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", "db_name|abc.com|3306", false);

        // Validate behaviour of DB_NAME, SERVER_SOCKET_ADDRESS exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerSocketAddress, "abc.com" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com", "db_name|abc.com", false);

        // Validate behaviour of SERVER_SOCKET_PORT exist
        spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeServerSocketPort, 3306L);

        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();

        // Validate behaviour of only DB_NAME exist
        spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeDbName, "db_name");

        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();

        // Validate behaviour of DB_NAME and DB_CONNECTION_STRING exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeDbConnectionString, "mysql://test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com:3306/petclinic" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com|3306", "db_name|test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com|3306", false);

        // Validate behaviour of DB_CONNECTION_STRING
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbConnectionString, "mysql://test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com:3306/petclinic" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com|3306", "test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com|3306", false);

        // Validate behaviour of DB_CONNECTION_STRING exist without port
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbConnectionString, "http://dbserver" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "DB::Connection", "dbserver|80", "dbserver|80", false);

        // Validate behaviour of DB_NAME and invalid DB_CONNECTION_STRING exist
        spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeDbName, "db_name");
        spanDataMock.SetTag(AttributeDbConnectionString, "hsqldb:mem:");

        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();
    }

    [Fact]

    // Validate behaviour of various combinations of DB attributes.
    public void TestGetDBStatementRemoteOperation()
    {
        Dictionary<string, object?> attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT DB statement" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");

        // Case 2: More than 1 valid keywords match, we want to pick the longest match
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "DROP VIEW DB statement" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "DROP VIEW");

        // Case 3: More than 1 valid keywords match, but the other keywords is not
        // at the start of the SpanAttributes.DB_STATEMENT. We want to only pick start match
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT data FROM domains" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");

        // Case 4: Have valid keywords，but it is not at the start of SpanAttributes.DB_STATEMENT
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "invalid SELECT DB statement" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);

        // Case 5: Have valid keywords, match the longest word
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "UUID" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "UUID");

        // Case 6: Have valid keywords, match with first word
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "FROM SELECT *" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "FROM");

        // Case 7: Have valid keyword, match with first word
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT FROM *" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");

        // Case 8: Have valid keywords, match with upper case
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "seLeCt *" },
            { AttributeDbOperation, null },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");

        // Case 9: Both DB_OPERATION and DB_STATEMENT are set but the former takes precedence
        attributesCombination = new Dictionary<string, object?>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT FROM *" },
            { AttributeDbOperation, "DB operation" },
        };
        this.ValidateExpectedRemoteAttributes(attributesCombination, "DB system", "DB operation");
    }

    [Fact]
    public void TestPeerServiceDoesOverrideOtherRemoteServices()
    {
        this.ValidatePeerServiceDoesOverride(AttributeRpcService);
        this.ValidatePeerServiceDoesOverride(AttributeDbSystem);
        this.ValidatePeerServiceDoesOverride(AttributeFaasInvokedProvider);
        this.ValidatePeerServiceDoesOverride(AttributeMessagingSystem);
        this.ValidatePeerServiceDoesOverride(AttributeGraphqlOperationType);
        this.ValidatePeerServiceDoesOverride(AttributeNetPeerName);
        this.ValidatePeerServiceDoesOverride(AttributeNetSockPeerAddr);

        // Actually testing that peer service overrides "UnknownRemoteService".
        this.ValidatePeerServiceDoesOverride("unknown.service.key");
    }

    [Fact]
    public void TestPeerServiceDoesNotOverrideAwsRemoteService()
    {
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributePeerService, "Peer service");
        spanDataMock.SetTag(AttributeAWSRemoteService, "TestString");

        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualRemoteService);
        Assert.Equal("TestString", actualRemoteService);
        spanDataMock.Dispose();
    }

    [Fact]
    public void TestSdkClientSpanWithRemoteResourceAttributes()
    {
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSS3Bucket, "aws_s3_bucket_name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::S3::Bucket", "aws_s3_bucket_name", "aws_s3_bucket_name", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        // when QueueName and QueueUrl are both available, QueueName is used as resource identifier. QueueUrl is always used as the CFN primary identifier.
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSQSQueueName, "aws_queue_name" },
            { AttributeAWSSQSQueueUrl, "https://sqs.us-east-2.amazonaws.com/123456789012/Queue" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "aws_queue_name", "https://sqs.us-east-2.amazonaws.com/123456789012/Queue", region: "us-east-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSQSQueueName, "aws_queue_name" },
            { AttributeAWSSQSQueueUrl,  "invalidUrl" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "aws_queue_name", "invalidUrl", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        // Validate SQS behavior when QueueName isn't available
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSQSQueueUrl, "https://sqs.us-east-2.amazonaws.com/123456789012/MyQueue" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "MyQueue", "https://sqs.us-east-2.amazonaws.com/123456789012/MyQueue", region: "us-east-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSKinesisStreamName, "aws_stream_name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Kinesis::Stream", "aws_stream_name", "aws_stream_name", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSKinesisStreamArn, "arn:aws:kinesis:us-west-2:123456789012:stream/aws_stream_name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Kinesis::Stream", "aws_stream_name", "aws_stream_name", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSDynamoTableArn, "arn:aws:dynamodb:us-west-2:123456789012:table/aws_table_name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "aws_table_name", "aws_table_name", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSDynamoTableName, "aws_table_name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "aws_table_name", "aws_table_name", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        // validate behavior of AttributeAWSDynamoTableName with special chars('|', '^')
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSDynamoTableName, "aws_table|name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "aws_table^|name", "aws_table^|name", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSDynamoTableName, "aws_table^name" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "aws_table^^name", "aws_table^^name", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSLambdaFunctionName, "aws_lambda_function_name" },
            { AttributeAWSLambdaFunctionArn, "arn:aws:lambda:us-west-2:123456789012:function:aws_lambda_function_arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Lambda::Function", "aws_lambda_function_name", "arn:aws:lambda:us-west-2:123456789012:function:aws_lambda_function_arn", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSLambdaFunctionName, "aws_lambda_function_^name" },
            { AttributeAWSLambdaFunctionArn, "arn:aws:lambda:us-west-2:123456789012:function:aws_lambda_function_^arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Lambda::Function", "aws_lambda_function_^^name", "arn:aws:lambda:us-west-2:123456789012:function:aws_lambda_function_^^arn", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSLambdaResourceMappingId, "aws_event_source_mapping_id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Lambda::EventSourceMapping", "aws_event_source_mapping_id", "aws_event_source_mapping_id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSLambdaResourceMappingId, "aws_event_source_mapping_^id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Lambda::EventSourceMapping", "aws_event_source_mapping_^^id", "aws_event_source_mapping_^^id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSecretsManagerSecretArn, "arn:aws:secretsmanager:us-west-2:123456789012:secret:aws_secret_arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SecretsManager::Secret", "aws_secret_arn", "arn:aws:secretsmanager:us-west-2:123456789012:secret:aws_secret_arn", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSecretsManagerSecretArn, "arn:aws:secretsmanager:us-west-2:123456789012:secret:aws_secret_^arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SecretsManager::Secret", "aws_secret_^^arn", "arn:aws:secretsmanager:us-west-2:123456789012:secret:aws_secret_^^arn", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "arn:aws:sns:us-west-2:012345678901:aws_topic_arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SNS::Topic", "aws_topic_arn", "arn:aws:sns:us-west-2:012345678901:aws_topic_arn", region: "us-west-2", accountId: "012345678901");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "arn:aws:sns:us-west-2:012345678901:aws_topic_^arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SNS::Topic", "aws_topic_^^arn", "arn:aws:sns:us-west-2:012345678901:aws_topic_^^arn", region: "us-west-2", accountId: "012345678901");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSStepFunctionsActivityArn, "arn:aws:states:us-west-2:012345678901:activity:aws_activity_arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::StepFunctions::Activity", "aws_activity_arn", "arn:aws:states:us-west-2:012345678901:activity:aws_activity_arn", region: "us-west-2", accountId: "012345678901");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSStepFunctionsActivityArn, "arn:aws:states:us-west-2:012345678901:activity:aws_activity_^arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::StepFunctions::Activity", "aws_activity_^^arn", "arn:aws:states:us-west-2:012345678901:activity:aws_activity_^^arn", region: "us-west-2", accountId: "012345678901");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSStepFunctionsStateMachineArn, "arn:aws:states:us-west-2:012345678901:stateMachine:aws_state_machine_arn" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::StepFunctions::StateMachine", "aws_state_machine_arn", "arn:aws:states:us-west-2:012345678901:stateMachine:aws_state_machine_arn", region: "us-west-2", accountId: "012345678901");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSStepFunctionsStateMachineArn, "arn:aws:states:us-west-2:012345678901:stateMachine:aws_state_machine_^arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::StepFunctions::StateMachine", "aws_state_machine_^^arn", "arn:aws:states:us-west-2:012345678901:stateMachine:aws_state_machine_^^arn", region: "us-west-2", accountId: "012345678901");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockGuardrailId, "aws_guardrail_id" },
            { AttributeAWSBedrockGuardrailArn, "arn:aws:bedrock:us-west-2:123456789012:guardrail/aws_guardrail_arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::Guardrail", "aws_guardrail_id", "arn:aws:bedrock:us-west-2:123456789012:guardrail/aws_guardrail_arn", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockGuardrailId, "aws_guardrail_^id" },
            { AttributeAWSBedrockGuardrailArn, "arn:aws:bedrock:us-west-2:123456789012:guardrail/aws_guardrail_^arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::Guardrail", "aws_guardrail_^^id", "arn:aws:bedrock:us-west-2:123456789012:guardrail/aws_guardrail_^^arn", region: "us-west-2", accountId: "123456789012");

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeGenAiModelId, "gen_ai_model_id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::Model", "gen_ai_model_id", "gen_ai_model_id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeGenAiModelId, "gen_ai_model_^id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::Model", "gen_ai_model_^^id", "gen_ai_model_^^id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockAgentId, "aws_agent_id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::Agent", "aws_agent_id", "aws_agent_id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockAgentId, "aws_agent_^id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::Agent", "aws_agent_^^id", "aws_agent_^^id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockKnowledgeBaseId, "aws_knowledge_base_id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::KnowledgeBase", "aws_knowledge_base_id", "aws_knowledge_base_id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockKnowledgeBaseId, "aws_knowledge_base_^id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::KnowledgeBase", "aws_knowledge_base_^^id", "aws_knowledge_base_^^id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockDataSourceId, "aws_data_source_id" },
            { AttributeAWSBedrockKnowledgeBaseId, "aws_knowledge_base_id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::DataSource", "aws_data_source_id", "aws_knowledge_base_id|aws_data_source_id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSBedrockDataSourceId, "aws_data_source_^id" },
            { AttributeAWSBedrockKnowledgeBaseId, "aws_knowledge_base_^id" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Bedrock::DataSource", "aws_data_source_^^id", "aws_knowledge_base_^^id|aws_data_source_^^id", region: this.awsRemoteResourceRegion, accountAccessKey: this.awsRemoteResourceAccessKey);

        // Cross account support
        // Both account access key and account id are not available
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSS3Bucket, "aws_s3_bucket_name" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::S3::Bucket", "aws_s3_bucket_name", "aws_s3_bucket_name");

        // Account access key is not available
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "arn:aws:sns:us-west-2:012345678901:aws_topic_arn" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SNS::Topic", "aws_topic_arn", "arn:aws:sns:us-west-2:012345678901:aws_topic_arn", region: "us-west-2", accountId: "012345678901");

        // Arn with invalid account id
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "arn:aws:sns:us-west-2:invalid_account_id:aws_topic_arn" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, null, null, null);

        // Arn with invalid region
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "arn:aws:sns:invalid_region:012345678901:aws_topic_arn" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SNS::Topic", "aws_topic_arn", "arn:aws:sns:invalid_region:012345678901:aws_topic_arn", region: "invalid_region", accountId: "012345678901");

        // Invalid arn and no account access key
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "invalid_arn" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, null, null, null);

        // Invalid arn but account access key is available
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSNSTopicArn, "invalid_arn" },
            { AttributeAWSAuthAccessKey, this.awsRemoteResourceAccessKey },
            { AttributeAWSAuthRegion, this.awsRemoteResourceRegion },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, null, null, null);
    }

    [Fact]
    public void TestCloudformationPrimaryIdentifierFallbackToRemoteResourceIdentifier()
    {
        // Test case 1: S3 Bucket (no ARN available, should use bucket name for both)
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
        {
            { AttributeRpcService, "S3" },
            { AttributeAWSS3Bucket, "my-test-bucket" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::S3::Bucket", "my-test-bucket", "my-test-bucket");

        // Test S3 Bucket with speicial characters
        attributesCombination[AttributeAWSS3Bucket] = "my-test|bucket^name";
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::S3::Bucket", "my-test^|bucket^^name", "my-test^|bucket^^name");

        // Test case 2: SQS Queue (no ARN, should use queue name for both)
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeRpcService, "SQS" },
            { AttributeAWSSQSQueueName, "my-test-queue" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "my-test-queue", "my-test-queue");

        // Test SQS Queue with special characters
        attributesCombination[AttributeAWSSQSQueueName] = "my^queue|name";
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "my^^queue^|name", "my^^queue^|name");

        // Test case 3: DynamoDB Table (no ARN, should use table name for both)
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeRpcService, "DynamoDB" },
            { AttributeAWSDynamoTableName, "my-test-table" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "my-test-table", "my-test-table");

        // Test DynamoDB Table with special characters
        attributesCombination[AttributeAWSDynamoTableName] = "my|table^name";
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "my^|table^^name", "my^|table^^name");

        // Test case 4: Kinesis Stream (no ARN, should use stream name for both)
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeRpcService, "Kinesis" },
            { AttributeAWSKinesisStreamName, "my-test-stream" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Kinesis::Stream", "my-test-stream", "my-test-stream");

        // Test Kinesis Stream with special characters
        attributesCombination[AttributeAWSKinesisStreamName] = "my|stream^name";
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Kinesis::Stream", "my^|stream^^name", "my^|stream^^name");

        // Test case 5: Lambda Function (non-invoke operation, no ARN)
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeRpcService, "Lambda" },
            { AttributeRpcMethod, "GetFunction" },
            { AttributeAWSLambdaFunctionName, "my-test-function" },
        };
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Lambda::Function", "my-test-function", "my-test-function");

        // Test Lambda Function with special characters
        attributesCombination[AttributeAWSLambdaFunctionName] = "my|lambda^function";
        this.ValidateRemoteResourceAttributes(attributesCombination, "AWS::Lambda::Function", "my^|lambda^^function", "my^|lambda^^function");
    }

    [Fact]
    public void TestSetRemoteEnvironment()
    {
        // Test 1: Setting remote environment when all relevant attributes are present
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeRpcSystem, "aws-api");
        spanDataMock.SetTag(AttributeRpcService, "Lambda");
        spanDataMock.SetTag(AttributeRpcMethod, "Invoke");
        spanDataMock.SetTag(AttributeAWSLambdaFunctionName, "testFunction");

        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteEnvironment, out var remoteEnvironment);
        Assert.Equal(remoteEnvironment, "lambda:default");

        // Test 2: NOT setting remote environment when rpc.system is missing
        spanDataMock.SetTag(AttributeRpcSystem, null);
        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteEnvironment, out remoteEnvironment);
        Assert.Null(remoteEnvironment);
        spanDataMock.SetTag(AttributeRpcSystem, "aws-api");

        // Test 3: NOT setting remote environment when rpc.method is missing
        spanDataMock.SetTag(AttributeRpcMethod, null);
        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteEnvironment, out remoteEnvironment);
        Assert.Null(remoteEnvironment);
        spanDataMock.SetTag(AttributeRpcMethod, "Invoke");

        // Test 4: setting remote environment to lambda:default when FunctionName is missing
        spanDataMock.SetTag(AttributeAWSLambdaFunctionName, null);
        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteEnvironment, out remoteEnvironment);
        Assert.Equal(remoteEnvironment, "lambda:default");

        // Test 5: NOT setting remote environment for non-Lambda services
        spanDataMock.SetTag(AttributeRpcService, "S3");
        spanDataMock.SetTag(AttributeRpcMethod, "GetObject");
        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteEnvironment, out remoteEnvironment);
        Assert.Null(remoteEnvironment);

        // Test 6: NOT setting remote environment for Lambda non-Invoke operations
        spanDataMock.SetTag(AttributeRpcService, "Lambda");
        spanDataMock.SetTag(AttributeRpcMethod, "GetFunction");
        this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource)
            .TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteEnvironment, out remoteEnvironment);
        Assert.Null(remoteEnvironment);

        spanDataMock.Dispose();
    }

    [Fact]
    public void TestNormalizeRemoteServiceName_NoNormalization()
    {
        string serviceName = "non aws service";
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeRpcService, serviceName);
        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualServiceName);
        Assert.Equal(serviceName, actualServiceName);
    }

    [Fact]
    public void TestNormalizeRemoteServiceName_AwsSdk()
    {
        this.TestAwsSdkServiceNormalization("Bedrock Runtime", "AWS::BedrockRuntime");
        this.TestAwsSdkServiceNormalization("Bedrock", "AWS::Bedrock");
        this.TestAwsSdkServiceNormalization("Bedrock Agent", "AWS::Bedrock");
        this.TestAwsSdkServiceNormalization("Bedrock Agent Runtime", "AWS::Bedrock");

        // AWS SDK V2
        this.TestAwsSdkServiceNormalization("AmazonDynamoDBv2", "AWS::DynamoDB");
        this.TestAwsSdkServiceNormalization("AmazonKinesis", "AWS::Kinesis");
        this.TestAwsSdkServiceNormalization("Amazon S3", "AWS::S3");
        this.TestAwsSdkServiceNormalization("AmazonSQS", "AWS::SQS");

        // AWS SDK V1
        this.TestAwsSdkServiceNormalization("DynamoDb", "AWS::DynamoDB");
        this.TestAwsSdkServiceNormalization("Kinesis", "AWS::Kinesis");
        this.TestAwsSdkServiceNormalization("S3", "AWS::S3");
        this.TestAwsSdkServiceNormalization("Sqs", "AWS::SQS");

        // Lambda: non-Invoke operations
        this.TestAwsSdkServiceNormalization("Lambda", "AWS::Lambda");

        // Lambda: Invoke with function name
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeRpcSystem, "aws-api");
        spanDataMock.SetTag(AttributeRpcService, "Lambda");

        spanDataMock.SetTag(AttributeRpcMethod, "Invoke");
        spanDataMock.SetTag(AttributeAWSLambdaFunctionName, "testFunction");

        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualServiceName);
        Assert.Equal("testFunction", actualServiceName);

        // Lambda: Invoke without function name - should fall back to UnknownRemoteService
        spanDataMock.SetTag(AttributeAWSLambdaFunctionName, null);
        attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out actualServiceName);
        Assert.Equal(AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService, actualServiceName);

        spanDataMock.Dispose();
    }

    [Fact]
    public void TestNoMetricWhenConsumerProcessWithConsumerParent()
    {
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer);
        Activity? childSpan = this.testSource.StartActivity("test", ActivityKind.Consumer);
        childSpan.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
        childSpan.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
        childSpan.SetTag(AttributeAWSConsumerParentSpanKind, ActivityKind.Consumer.ToString());
        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(childSpan, this.resource);
        Assert.Empty(attributeMap);
    }

    [Fact]
    public void TestBothMetricsWhenLocalRootConsumerProcess()
    {
        this.parentSpan?.Dispose();
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
        spanDataMock.SetTag(AttributeAWSConsumerParentSpanKind, ActivityKind.Consumer.ToString().ToUpper());
        spanDataMock.Start();
        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        Assert.Equal(2, attributeMap.Count);
    }

    private void TestAwsSdkServiceNormalization(string serviceName, string expectedRemoteService)
    {
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeRpcSystem, "aws-api");
        spanDataMock.SetTag(AttributeRpcService, serviceName);

        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualServiceName);
        Assert.Equal(expectedRemoteService, actualServiceName);
        spanDataMock.Dispose();
    }

    private void ValidatePeerServiceDoesOverride(string remoteServiceKey)
    {
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributePeerService, "Peer service");
        spanDataMock.SetTag(remoteServiceKey, "TestString");

        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualRemoteService);
        Assert.Equal("Peer service", actualRemoteService);
        spanDataMock.Dispose();
    }

    private Dictionary<string, object?> ValidateAndRemoveRemoteAttributes(string remoteServiceKey, string remoteServiceValue, string remoteOperationKey, string remoteOperationValue, Dictionary<string, object?> attributesCombination)
    {
        attributesCombination[remoteServiceKey] = remoteServiceValue;
        attributesCombination[remoteOperationKey] = remoteOperationValue;
        this.ValidateExpectedRemoteAttributes(attributesCombination, remoteServiceValue, remoteOperationValue);

        attributesCombination[remoteServiceKey] = remoteServiceValue;
        attributesCombination.Remove(remoteOperationKey);
        this.ValidateExpectedRemoteAttributes(attributesCombination, remoteServiceValue, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteOperation);

        attributesCombination.Remove(remoteServiceKey);
        attributesCombination[remoteOperationKey] = remoteOperationValue;
        this.ValidateExpectedRemoteAttributes(attributesCombination, AutoInstrumentation.AwsSpanProcessingUtil.UnknownRemoteService, remoteOperationValue);

        attributesCombination.Remove(remoteOperationKey);
        return attributesCombination;
    }

    private void ValidateExpectedRemoteAttributes(Dictionary<string, object?> attributesCombination, string expectedRemoteService, string expectedRemoteOperation)
    {
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        foreach (var attribute in attributesCombination)
        {
            spanDataMock.SetTag(attribute.Key, attribute.Value);
        }

        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteOperation, out var actualRemoteOperation);
        Assert.Equal(expectedRemoteOperation, actualRemoteOperation);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualRemoteService);
        Assert.Equal(expectedRemoteService, actualRemoteService);
        spanDataMock.Dispose();

        spanDataMock = this.testSource.StartActivity("test", ActivityKind.Producer);
        foreach (var attribute in attributesCombination)
        {
            spanDataMock.SetTag(attribute.Key, attribute.Value);
        }

        attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteOperation, out actualRemoteOperation);
        Assert.Equal(expectedRemoteOperation, actualRemoteOperation);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out actualRemoteService);
        Assert.Equal(expectedRemoteService, actualRemoteService);
        spanDataMock.Dispose();
    }

    private void ValidateAttributesProducedForNonLocalRootSpanOfKind(ActivityTagsCollection expectedAttributes, Activity? span)
    {
        Dictionary<string, ActivityTagsCollection> attributeMap =
            this.generator.GenerateMetricAttributeMapFromSpan(span, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.ServiceMetric, out ActivityTagsCollection? serviceMetric);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        if (attributeMap.Count > 0)
        {
            switch (span.Kind)
            {
                case ActivityKind.Producer:
                case ActivityKind.Client:
                case ActivityKind.Consumer:
                    Assert.True(serviceMetric == null);
                    Assert.True(dependencyMetric != null);
                    Assert.True(dependencyMetric.Count == expectedAttributes.Count);
                    Assert.True(dependencyMetric.OrderBy(kvp => kvp.Key).SequenceEqual(expectedAttributes.OrderBy(kvp => kvp.Key)));
                    break;
                default:
                    Assert.True(dependencyMetric == null);
                    Assert.True(serviceMetric != null);
                    Assert.True(serviceMetric.Count == expectedAttributes.Count);
                    Assert.True(serviceMetric.OrderBy(kvp => kvp.Key).SequenceEqual(expectedAttributes.OrderBy(kvp => kvp.Key)));
                    break;
            }
        }
    }

    private void ValidateAttributesProducedForLocalRootSpanOfKind(ActivityTagsCollection expectServiceAttributes, ActivityTagsCollection expectDependencyAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attributeMap =
            this.generator.GenerateMetricAttributeMapFromSpan(span, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.ServiceMetric, out ActivityTagsCollection? serviceMetric);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);

        Assert.True(serviceMetric != null);
        Assert.True(serviceMetric.Count == expectServiceAttributes.Count);
        Assert.True(serviceMetric.OrderBy(kvp => kvp.Key).SequenceEqual(expectServiceAttributes.OrderBy(kvp => kvp.Key)));

        Assert.True(dependencyMetric != null);
        Assert.True(dependencyMetric.Count == expectDependencyAttributes.Count);
        Assert.True(dependencyMetric.OrderBy(kvp => kvp.Key).SequenceEqual(expectDependencyAttributes.OrderBy(kvp => kvp.Key)));
    }

    private void UpdateResourceWithServiceName()
    {
        List<KeyValuePair<string, object>> resourceAttributes = new List<KeyValuePair<string, object>>
        {
            new (AutoInstrumentation.AwsMetricAttributeGenerator.AttributeServiceName, this.serviceNameValue),
            new (AwsAttributeKeys.AttributeAWSLocalService, this.serviceNameValue),
        };
        this.resource = new Resource(resourceAttributes);
    }

    private void ValidateRemoteResourceAttributes(Dictionary<string, object> attributesCombination, string type, string identifier, string cfnIdentifier, bool isAwsServiceTest = true, string? region = null, string? accountId = null, string? accountAccessKey = null)
    {
        Activity? spanDataMock = this.testSource.StartActivity("test", ActivityKind.Client);
        foreach (var attribute in attributesCombination)
        {
            spanDataMock.SetTag(attribute.Key, attribute.Value);
        }

        if (isAwsServiceTest)
        {
            spanDataMock.SetTag(AttributeRpcSystem, "aws-api");
        }

        var attributeMap = this.generator.GenerateMetricAttributeMapFromSpan(spanDataMock, this.resource);
        attributeMap.TryGetValue(MetricAttributeGeneratorConstants.DependencyMetric, out ActivityTagsCollection? dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteResourceType, out var actualAWSRemoteResourceType);
        dependencyMetric.TryGetValue(AttributeAWSRemoteResourceIdentifier, out var actualAWSRemoteResourceIdentifier);
        dependencyMetric.TryGetValue(AttributeAWSCloudformationPrimaryIdentifier, out var actualAWSCloudformationPrimaryIdentifier);
        Assert.Equal(type, actualAWSRemoteResourceType);
        Assert.Equal(identifier, actualAWSRemoteResourceIdentifier);
        Assert.Equal(cfnIdentifier, actualAWSCloudformationPrimaryIdentifier);

        // Cross account support
        if (region != null)
        {
            dependencyMetric.TryGetValue(AttributeAWSRemoteResourceRegion, out var actualAWSRemoteResourceRegion);
            Assert.Equal(region, actualAWSRemoteResourceRegion);
        }
        else
        {
            Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceRegion));
        }

        if (accountId != null)
        {
            dependencyMetric.TryGetValue(AttributeAWSRemoteResourceAccountId, out var actualAWSRemoteResourceAccountId);
            Assert.Equal(accountId, actualAWSRemoteResourceAccountId);
            Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceAccessKey));
        }
        else
        {
            Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceAccountId));
        }

        if (accountAccessKey != null)
        {
            dependencyMetric.TryGetValue(AttributeAWSRemoteResourceAccessKey, out var actualAWSRemoteResourceAccountAccessKey);
            Assert.Equal(accountAccessKey, actualAWSRemoteResourceAccountAccessKey);
            Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceAccountId));
        }
        else
        {
            Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceAccessKey));
        }

        spanDataMock.Dispose();
    }
}
