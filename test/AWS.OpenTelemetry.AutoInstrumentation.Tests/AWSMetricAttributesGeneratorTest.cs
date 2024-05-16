using System.Diagnostics;
using Xunit;
using Moq;
using AWS.OpenTelemetry.AutoInstrumentation;
using static OpenTelemetry.Trace.TraceSemanticConventions;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class AWSMetricAttributesGeneratorTest
{
    private readonly ActivitySource testSource = new ActivitySource("Test Source");
    private Activity spanDataMock;
    private AwsMetricAttributeGenerator Generator = new AwsMetricAttributeGenerator();
    private Resource _resource = Resource.Empty;
    private Activity parentSpan;
    private string serviceNameValue = "Service name";
    private string spanNameValue = "Span name";
    private string awsRemoteServiceValue = "AWS remote service";
    private string awsRemoteOperationValue = "AWS remote operation";
    private string awsLocalServiceValue = "AWS local operation";
    private string awsLocalOperationValue = "AWS local operation";
    
    public AWSMetricAttributesGeneratorTest()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = ((ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData)
        };
        ActivitySource.AddActivityListener(listener);
        parentSpan = testSource.StartActivity("test");
        
    }

    [Fact]
    public void testServerSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString()),
            new (AttributeAWSLocalService, AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Server);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testConsumerSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Consumer.ToString()),
            new (AttributeAWSLocalService, AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AwsSpanProcessingUtil.UnknownRemoteOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Consumer);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    // Dotnet seems don't have a default resource, so skip 'testSpanAttributesForEmptyResource'
    
    [Fact]
    public void testProducerSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Producer.ToString()),
            new (AttributeAWSLocalService, AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AwsSpanProcessingUtil.UnknownRemoteOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Producer);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void testClientSpanWithoutAttributes()
    {
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Client.ToString()),
            new (AttributeAWSLocalService, AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AwsSpanProcessingUtil.UnknownRemoteOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Client);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void testInternalSpan()
    {
        // Spans with internal span kind should not produce any attributes.
        spanDataMock = testSource.StartActivity("", ActivityKind.Internal);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(new ActivityTagsCollection(), spanDataMock);
    }

    [Fact]
    public void testLocalRootServerSpan()
    {
        updateResourceWithServiceName();
        parentSpan.Dispose();
        spanDataMock = testSource.StartActivity(spanNameValue, ActivityKind.Server);
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, spanNameValue)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testLocalRootInternalSpan()
    {
        updateResourceWithServiceName();
        parentSpan.Dispose();
        spanDataMock = testSource.StartActivity(spanNameValue, ActivityKind.Internal);
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
        
    [Fact]
    public void testLocalRootClientSpan()
    {
        updateResourceWithServiceName();
        parentSpan.Dispose();
        spanDataMock = testSource.StartActivity(spanNameValue, ActivityKind.Client);
        spanDataMock.SetTag(AttributeAWSRemoteService, awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, awsRemoteOperationValue);
        
        List<KeyValuePair<string, object?>> expectServiceAttiributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation)
        };
        
        List<KeyValuePair<string, object?>> expectDependencyAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Client.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation),
            new (AttributeAWSRemoteService, awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, awsRemoteOperationValue),
        };
        
        ActivityTagsCollection expectServiceAttiributes = new ActivityTagsCollection(expectServiceAttiributesList);
        ActivityTagsCollection expectDependencyAttributes = new ActivityTagsCollection(expectDependencyAttributesList);

        validateAttributesProducedForLocalRootSpanOfKind(expectServiceAttiributes, expectDependencyAttributes,
            spanDataMock);
    }

    [Fact]
    public void testLocalRootConsumerSpan()
    {
        updateResourceWithServiceName();
        parentSpan.Dispose();
        spanDataMock = testSource.StartActivity(spanNameValue, ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeAWSRemoteService, awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, awsRemoteOperationValue);
        
        List<KeyValuePair<string, object?>> expectServiceAttiributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation)
        };
        
        List<KeyValuePair<string, object?>> expectDependencyAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Consumer.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation),
            new (AttributeAWSRemoteService, awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, awsRemoteOperationValue),
        };
        
        ActivityTagsCollection expectServiceAttiributes = new ActivityTagsCollection(expectServiceAttiributesList);
        ActivityTagsCollection expectDependencyAttributes = new ActivityTagsCollection(expectDependencyAttributesList);

        validateAttributesProducedForLocalRootSpanOfKind(expectServiceAttiributes, expectDependencyAttributes,
            spanDataMock);
    }
    
    [Fact]
    public void testLocalRootProducerSpan()
    {
        updateResourceWithServiceName();
        parentSpan.Dispose();
        spanDataMock = testSource.StartActivity(spanNameValue, ActivityKind.Producer);
        spanDataMock.SetTag(AttributeAWSRemoteService, awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, awsRemoteOperationValue);
        
        List<KeyValuePair<string, object?>> expectServiceAttiributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, AwsSpanProcessingUtil.LocalRoot),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation)
        };
        
        List<KeyValuePair<string, object?>> expectDependencyAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Producer.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.InternalOperation),
            new (AttributeAWSRemoteService, awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, awsRemoteOperationValue),
        };
        
        ActivityTagsCollection expectServiceAttiributes = new ActivityTagsCollection(expectServiceAttiributesList);
        ActivityTagsCollection expectDependencyAttributes = new ActivityTagsCollection(expectDependencyAttributesList);

        validateAttributesProducedForLocalRootSpanOfKind(expectServiceAttiributes, expectDependencyAttributes,
            spanDataMock);
    }
    
    [Fact]
    public void testConsumerSpanWithAttributes()
    {
        updateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Consumer.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation),
            new (AttributeAWSRemoteService, AwsSpanProcessingUtil.UnknownRemoteService),
            new (AttributeAWSRemoteOperation, AwsSpanProcessingUtil.UnknownRemoteOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Consumer);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testServerSpanWithAttributes()
    {
        updateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Server);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testServerSpanWithSpanNameAsHttpMethod()
    {
        updateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("GET", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpMethod, "GET");
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testServerSpanWithSpanNameWithHttpTarget()
    {
        updateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, "POST /payment"),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("POST", ActivityKind.Server);
        spanDataMock.SetTag(AttributeHttpMethod, "POST");
        spanDataMock.SetTag(AttributeHttpTarget, "/payment/123");
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testProducerSpanWithAttributes()
    {
        updateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Producer.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, awsLocalOperationValue),
            new (AttributeAWSRemoteService, awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, awsRemoteOperationValue)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Producer);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        spanDataMock.SetTag(AttributeAWSLocalOperation, awsLocalOperationValue);
        spanDataMock.SetTag(AttributeAWSRemoteService, awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, awsRemoteOperationValue);
        
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    [Fact]
    public void testClientSpanWithAttributes()
    {
        updateResourceWithServiceName();
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Client.ToString()),
            new (AttributeAWSLocalService, serviceNameValue),
            new (AttributeAWSLocalOperation, awsLocalOperationValue),
            new (AttributeAWSRemoteService, awsRemoteServiceValue),
            new (AttributeAWSRemoteOperation, awsRemoteOperationValue)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Client);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        spanDataMock.SetTag(AttributeAWSLocalOperation, awsLocalOperationValue);
        spanDataMock.SetTag(AttributeAWSRemoteService, awsRemoteServiceValue);
        spanDataMock.SetTag(AttributeAWSRemoteOperation, awsRemoteOperationValue);
        
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }

    [Fact]
    public void testRemoteAttributesCombinations()
    {
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
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
            { "unknown.operation.key", "TestString" }
        };

        attributesCombination = validateAndRemoveRemoteAttributes(AttributeAWSRemoteService, awsRemoteServiceValue, AttributeAWSRemoteOperation,
            awsRemoteOperationValue, attributesCombination);
        
        attributesCombination = validateAndRemoveRemoteAttributes(AttributeRpcService, "RPC service", AttributeRpcMethod,
            "RPC Method", attributesCombination);
        
        attributesCombination = validateAndRemoveRemoteAttributes(AttributeDbSystem, "DB system", AttributeDbOperation,
            "DB operation", attributesCombination);

        attributesCombination[AttributeDbSystem] = "DB system";
        attributesCombination.Remove(AttributeDbOperation);
        attributesCombination.Remove(AttributeDbStatement);
        
        attributesCombination = validateAndRemoveRemoteAttributes(AttributeDbSystem, "DB system", AttributeDbOperation,
            AwsSpanProcessingUtil.UnknownRemoteOperation, attributesCombination);
        
        // Validate behaviour of various combinations of FAAS attributes, then remove them.
        attributesCombination = validateAndRemoveRemoteAttributes(
            AttributeFaasInvokedName, "FAAS invoked name", AttributeFaasTrigger, "FAAS trigger name",
            attributesCombination);

        // Validate behaviour of various combinations of Messaging attributes, then remove them.
        attributesCombination = validateAndRemoveRemoteAttributes(
            AttributeMessagingSystem, "Messaging system", AttributeMessagingOperation, "Messaging operation",
            attributesCombination);
        
        // Validate behaviour of GraphQL operation type attribute, then remove it.
        attributesCombination[AttributeGraphqlOperationType] = "GraphQL operation type";
        validateExpectedRemoteAttributes(attributesCombination,"graphql", "GraphQL operation type");
        attributesCombination.Remove(AttributeGraphqlOperationType);

        // Validate behaviour of extracting Remote Service from net.peer.name
        attributesCombination[AttributeNetPeerName] = "www.example.com";
        validateExpectedRemoteAttributes(attributesCombination,"www.example.com", AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetPeerName);
        
        // Validate behaviour of extracting Remote Service from net.peer.name and net.peer.port
        attributesCombination[AttributeNetPeerName] = "192.168.0.0";
        attributesCombination[AttributeNetPeerPort] = (long)8081;
        validateExpectedRemoteAttributes(attributesCombination,"192.168.0.0:8081", AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetPeerName);
        attributesCombination.Remove(AttributeNetPeerPort);
        
        // Validate behaviour of extracting Remote Service from net.peer.socket.addr
        attributesCombination[AttributeNetSockPeerAddr] = "www.example.com";
        validateExpectedRemoteAttributes(attributesCombination,"www.example.com", AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetSockPeerAddr);
        
        // Validate behaviour of extracting Remote Service from net.peer.name and net.peer.port
        attributesCombination[AttributeNetSockPeerAddr] = "192.168.0.0";
        attributesCombination[AttributeNetSockPeerPort] = (long)8081;
        validateExpectedRemoteAttributes(attributesCombination,"192.168.0.0:8081", AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeNetSockPeerAddr);
        attributesCombination.Remove(AttributeNetSockPeerPort);
        
        // Validate behavior of Remote Operation from HttpTarget - with 1st api part. Also validates
        // that RemoteService is extracted from HttpUrl.
        attributesCombination[AttributeHttpUrl] = "http://www.example.com/payment/123";
        validateExpectedRemoteAttributes(attributesCombination,"www.example.com:80", "/payment");
        attributesCombination.Remove(AttributeHttpUrl);
        
        // Validate behavior of Remote Operation from HttpTarget - with 1st api part. Also validates
        // that RemoteService is extracted from HttpUrl.
        attributesCombination[AttributeHttpUrl] = "http://www.example.com";
        validateExpectedRemoteAttributes(attributesCombination,"www.example.com:80", "/");
        attributesCombination.Remove(AttributeHttpUrl);
        
        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AttributeHttpUrl] = "http://192.168.1.1";
        validateExpectedRemoteAttributes(attributesCombination,"192.168.1.1:80", "/");
        attributesCombination.Remove(AttributeHttpUrl);
        
        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AttributeHttpUrl] = "";
        validateExpectedRemoteAttributes(attributesCombination,AwsSpanProcessingUtil.UnknownRemoteService, AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeHttpUrl);
        
        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AttributeHttpUrl] = null;
        validateExpectedRemoteAttributes(attributesCombination,AwsSpanProcessingUtil.UnknownRemoteService, AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeHttpUrl);
        
        // Validate behavior of Remote Service from HttpUrl
        attributesCombination[AttributeHttpUrl] = "abc";
        validateExpectedRemoteAttributes(attributesCombination,AwsSpanProcessingUtil.UnknownRemoteService, AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributeHttpUrl);
        
        attributesCombination[AttributePeerService] = "Peer service";
        validateExpectedRemoteAttributes(attributesCombination,"Peer service", AwsSpanProcessingUtil.UnknownRemoteOperation);
        attributesCombination.Remove(AttributePeerService);
        
        validateExpectedRemoteAttributes(attributesCombination,AwsSpanProcessingUtil.UnknownRemoteService, AwsSpanProcessingUtil.UnknownRemoteOperation);
    }
    
    
    // [Fact]
    // // Validate behaviour of various combinations of DB attributes.
    // private void testGetDBStatementRemoteOperation()
    // {
    //     Dictionary<string, object> attributesCombination = new Dictionary<string, object>
    //     {
    //         { AttributeDbSystem, "DB system" },
    //         { AttributeDbStatement, "SELECT DB statement" },
    //         { AttributeDbOperation, null },
    //     };
    //     validateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");
    //     
    //     
    // }

    [Fact]
    public void testPeerServiceDoesOverrideOtherRemoteServices()
    {
        validatePeerServiceDoesOverride(AttributeRpcService);
        validatePeerServiceDoesOverride(AttributeDbSystem);
        validatePeerServiceDoesOverride(AttributeFaasInvokedProvider);
        validatePeerServiceDoesOverride(AttributeMessagingSystem);
        validatePeerServiceDoesOverride(AttributeGraphqlOperationType);
        validatePeerServiceDoesOverride(AttributeNetPeerName);
        validatePeerServiceDoesOverride(AttributeNetSockPeerAddr);
        // Actually testing that peer service overrides "UnknownRemoteService".
        validatePeerServiceDoesOverride("unknown.service.key");
    }

    [Fact]
    public void testPeerServiceDoesNotOverrideAwsRemoteService()
    {
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributePeerService, "Peer service");
        spanDataMock.SetTag(AttributeAWSRemoteService, "TestString");

        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualRemoteService);
        Assert.Equal("TestString", actualRemoteService);
        spanDataMock.Dispose();
    }

    private void validatePeerServiceDoesOverride(string remoteServiceKey)
    {
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributePeerService, "Peer service");
        spanDataMock.SetTag(remoteServiceKey, "TestString");

        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualRemoteService);
        Assert.Equal("Peer service", actualRemoteService);
        spanDataMock.Dispose();
    }
    private Dictionary<string, object> validateAndRemoveRemoteAttributes(string remoteServiceKey, string remoteServiceValue,
        string remoteOperationKey, string remoteOperationValue,
        Dictionary<string, object> attributesCombination)
    {
        attributesCombination[remoteServiceKey] = remoteServiceValue;
        attributesCombination[remoteOperationKey] = remoteOperationValue;
        validateExpectedRemoteAttributes(attributesCombination, remoteServiceValue, remoteOperationValue);
        
        attributesCombination[remoteServiceKey] = remoteServiceValue;
        attributesCombination.Remove(remoteOperationKey);
        validateExpectedRemoteAttributes(attributesCombination, remoteServiceValue, AwsSpanProcessingUtil.UnknownRemoteOperation);

        attributesCombination.Remove(remoteServiceKey);
        attributesCombination[remoteOperationKey] = remoteOperationValue;
        validateExpectedRemoteAttributes(attributesCombination, AwsSpanProcessingUtil.UnknownRemoteService, remoteOperationValue);

        attributesCombination.Remove(remoteOperationKey);
        return attributesCombination;
    }

    private void validateExpectedRemoteAttributes( Dictionary<string, object> attributesCombination, string expectedRemoteService, string expectedRemoteOperation)
    {
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        foreach (var attribute in attributesCombination)
        {
            spanDataMock.SetTag(attribute.Key, attribute.Value);
        }

        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteOperation, out var actualRemoteOperation);
        Assert.Equal(expectedRemoteOperation, actualRemoteOperation);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualRemoteService);
        Assert.Equal(expectedRemoteService, actualRemoteService);
        spanDataMock.Dispose();        
        
        spanDataMock = testSource.StartActivity("test", ActivityKind.Producer);
        foreach (var attribute in attributesCombination)
        {
            spanDataMock.SetTag(attribute.Key, attribute.Value);
        }

        attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteOperation, out actualRemoteOperation);
        Assert.Equal(expectedRemoteOperation, actualRemoteOperation);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out actualRemoteService);
        Assert.Equal(expectedRemoteService, actualRemoteService);
        spanDataMock.Dispose();

    }

    private void validRemoteAttributes()
    {
        
    }
    
    private void validateAttributesProducedForNonLocalRootSpanOfKind(ActivityTagsCollection expectedAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attribteMap =
            Generator.GenerateMetricAttributeMapFromSpan(span, this._resource);
        attribteMap.TryGetValue(IMetricAttributeGenerator.ServiceMetric, out ActivityTagsCollection serviceMetric);
        attribteMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        if (attribteMap.Count > 0)
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

    private void validateAttributesProducedForLocalRootSpanOfKind(ActivityTagsCollection expectServiceAttributes,
        ActivityTagsCollection expectDependencyAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attribteMap =
            Generator.GenerateMetricAttributeMapFromSpan(span, this._resource);
        attribteMap.TryGetValue(IMetricAttributeGenerator.ServiceMetric, out ActivityTagsCollection serviceMetric);
        attribteMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        
        Assert.True(serviceMetric != null);
        Assert.True(serviceMetric.Count == expectServiceAttributes.Count);
        Assert.True(serviceMetric.OrderBy(kvp => kvp.Key).SequenceEqual(expectServiceAttributes.OrderBy(kvp => kvp.Key)));
        
        Assert.True(dependencyMetric != null);
        Assert.True(dependencyMetric.Count == expectDependencyAttributes.Count);
        Assert.True(dependencyMetric.OrderBy(kvp => kvp.Key).SequenceEqual(expectDependencyAttributes.OrderBy(kvp => kvp.Key)));
    }

    private void updateResourceWithServiceName()
    {
        List<KeyValuePair<string, object?>> resourceAttributes = new List<KeyValuePair<string, object?>>
        {
            new (AwsMetricAttributeGenerator.AttributeServiceName, serviceNameValue)
        };
        _resource = new Resource(resourceAttributes);
    }
}
