using System.Diagnostics;
using System.Reflection;
using Xunit;
using Moq;
using AWS.OpenTelemetry.AutoInstrumentation;
using static OpenTelemetry.Trace.TraceSemanticConventions;
using OpenTelemetry.Resources;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsMetricAttributeGenerator;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;
// There are 5 tests in this class cannot be done in dotnet:

// 1. testHttpStatusAttributeNotAwsSdk
// 2. testHttpStatusAttributeStatusAlreadyPresent
// 3. testHttpStatusAttributeGetStatusCodeException
// 4. testHttpStatusAttributeStatusCodeException
// 5. testHttpStatusAttributeNoStatusCodeException
// Throwable related logic is not implemented or not supported in dotnet
public class AwsMetricAttributesGeneratorTest
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
    
    public AwsMetricAttributesGeneratorTest()
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
    
    [Fact]
    public void testSpanAttributesForEmptyResource()
    {
        _resource = Resource.Empty;
        List<KeyValuePair<string, object?>> expectAttributesList = new List<KeyValuePair<string, object?>>
        {
            new (AttributeAWSSpanKind, ActivityKind.Server.ToString()),
            new (AttributeAWSLocalService, AwsSpanProcessingUtil.UnknownService),
            new (AttributeAWSLocalOperation, AwsSpanProcessingUtil.UnknownOperation),
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity("", ActivityKind.Server);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
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
            new (AttributeAWSLocalOperation, spanNameValue)
        };
        ActivityTagsCollection expectedAttributes = new ActivityTagsCollection(expectAttributesList);
        spanDataMock = testSource.StartActivity(spanNameValue, ActivityKind.Server);
        spanDataMock.SetParentId(parentSpan.TraceId, parentSpan.SpanId);
        validateAttributesProducedForNonLocalRootSpanOfKind(expectedAttributes, spanDataMock);
    }
    
    // Equal to testServerSpanWithNullSpanName, dotnet do not allow null name, test empty instead
    [Fact]
    public void testServerSpanWithEmptySpanName()
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

    [Fact]
    public void testDBClientSpanWithRemoteResourceAttributes()
    {
        // Validate behaviour of DB_NAME, SERVER_ADDRESS and SERVER_PORT exist
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, (long)3306 },
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", false);
        
        // Validate behaviour of DB_NAME with '|' char, SERVER_ADDRESS and SERVER_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name|special" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, (long)3306 },
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name^|special|abc.com|3306", false);
                
        // Validate behaviour of DB_NAME with '^' char, SERVER_ADDRESS and SERVER_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name^special" },
            { AttributeServerAddress, "abc.com" },
            { AttributeServerPort, (long)3306 },
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name^^special|abc.com|3306", false);
                        
        // Validate behaviour of DB_NAME, SERVER_ADDRESS exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerAddress, "abc.com" },
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com", false);
        
        // Validate behaviour of SERVER_ADDRESS exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeServerAddress, "abc.com" },
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "abc.com", false);
        
        // Validate behaviour of SERVER_PORT exist
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeServerPort, (long)3306);

        Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource)
            .TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();
        
        // Validate behaviour of DB_NAME, NET_PEER_NAME and NET_PEER_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeNetPeerName, "abc.com"},
            { AttributeNetPeerPort, (long)3306}
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", false);

        // Validate behaviour of DB_NAME, NET_PEER_NAME exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeNetPeerName, "abc.com"},
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com", false);

        // Validate behaviour of NET_PEER_NAME exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeNetPeerName, "abc.com"},
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "abc.com", false);

        // Validate behaviour of NET_PEER_PORT exist
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeServerPort, (long)3306);

        Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource)
            .TryGetValue(IMetricAttributeGenerator.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();
        
        // Validate behaviour of DB_NAME, SERVER_SOCKET_ADDRESS and SERVER_SOCKET_PORT exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerSocketAddress, "abc.com"},
            { AttributeServerSocketPort, (long)3306}
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com|3306", false);
        
        // Validate behaviour of DB_NAME, SERVER_SOCKET_ADDRESS exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeServerSocketAddress, "abc.com"},
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|abc.com", false);
        
        // Validate behaviour of SERVER_SOCKET_PORT exist
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeServerSocketPort, (long)3306);

        Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource)
            .TryGetValue(IMetricAttributeGenerator.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();
        
        // Validate behaviour of only DB_NAME exist
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeDbName, "db_name");

        Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource)
            .TryGetValue(IMetricAttributeGenerator.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();
        
        // Validate behaviour of DB_NAME and DB_CONNECTION_STRING exist
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbName, "db_name" },
            { AttributeDbConnectionString, "mysql://test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com:3306/petclinic"},
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "db_name|test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com|3306", false);

        // Validate behaviour of DB_CONNECTION_STRING
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbConnectionString, "mysql://test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com:3306/petclinic"},
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "test-apm.cluster-cnrw3s3ddo7n.us-east-1.rds.amazonaws.com|3306", false);

        // Validate behaviour of DB_CONNECTION_STRING exist without port
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "mysql" },
            { AttributeDbConnectionString, "http://dbserver"},
        };
        validateRemoteResourceAttributes(attributesCombination, "DB::Connection", "dbserver|80", false);

        // Validate behaviour of DB_NAME and invalid DB_CONNECTION_STRING exist
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeDbSystem, "mysql");
        spanDataMock.SetTag(AttributeDbName, "db_name");
        spanDataMock.SetTag(AttributeDbConnectionString, "hsqldb:mem:");

        Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource)
            .TryGetValue(IMetricAttributeGenerator.DependencyMetric, out dependencyMetric);
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceType));
        Assert.False(dependencyMetric.ContainsKey(AttributeAWSRemoteResourceIdentifier));
        spanDataMock.Dispose();
    }
    
    [Fact]
    // Validate behaviour of various combinations of DB attributes.
    public void testGetDBStatementRemoteOperation()
    {
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT DB statement" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");
        
        // Case 2: More than 1 valid keywords match, we want to pick the longest match

        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "DROP VIEW DB statement" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "DROP VIEW");        
        
        // Case 3: More than 1 valid keywords match, but the other keywords is not
        // at the start of the SpanAttributes.DB_STATEMENT. We want to only pick start match
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT data FROM domains" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");        
        
        // Case 4: Have valid keywords，but it is not at the start of SpanAttributes.DB_STATEMENT
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "invalid SELECT DB statement" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", AwsSpanProcessingUtil.UnknownRemoteOperation);        
        
        // Case 5: Have valid keywords, match the longest word
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "UUID" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "UUID");
        
        // Case 6: Have valid keywords, match with first word
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "FROM SELECT *" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "FROM");
        
        // Case 7: Have valid keyword, match with first word
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT FROM *" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");
        
        // Case 8: Have valid keywords, match with upper case
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "seLeCt *" },
            { AttributeDbOperation, null },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "SELECT");
        
        // Case 9: Both DB_OPERATION and DB_STATEMENT are set but the former takes precedence
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeDbSystem, "DB system" },
            { AttributeDbStatement, "SELECT FROM *" },
            { AttributeDbOperation, "DB operation" },
        };
        validateExpectedRemoteAttributes(attributesCombination, "DB system", "DB operation");
        
    }

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

    [Fact]
    public void testSdkClientSpanWithRemoteResourceAttributes()
    {
        Dictionary<string, object> attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSS3Bucket, "aws_s3_bucket_name" },
        };
        validateRemoteResourceAttributes(attributesCombination, "AWS::S3::Bucket", "aws_s3_bucket_name");
        
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSSQSQueueName, "aws_queue_name" },
        };
        validateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "aws_queue_name");
        attributesCombination[AttributeAWSSQSQueueUrl] = "https://sqs.us-east-2.amazonaws.com/123456789012/Queue";
        validateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "aws_queue_name");

        attributesCombination[AttributeAWSSQSQueueUrl] = "invalidUrl";
        validateRemoteResourceAttributes(attributesCombination, "AWS::SQS::Queue", "aws_queue_name");
        
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSKinesisStreamName, "aws_stream_name" },
        };
        validateRemoteResourceAttributes(attributesCombination, "AWS::Kinesis::Stream", "aws_stream_name");
        
        attributesCombination = new Dictionary<string, object>
        {
            { AttributeAWSDynamoTableName, "aws_table_name" },
        };
        validateRemoteResourceAttributes(attributesCombination, "AWS::DynamoDB::Table", "aws_table_name");
    }
    
    private void validateRemoteResourceAttributes(Dictionary<string, object> attributesCombination,String type, String identifier, bool isAwsServiceTest = true)
    {
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        foreach (var attribute in attributesCombination)
        {
            spanDataMock.SetTag(attribute.Key, attribute.Value);
        }
        if (isAwsServiceTest)
        {
            spanDataMock.SetTag(AttributeRpcSystem, "aws-api");

        }
        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteResourceType, out var actualAWSRemoteResourceType);
        dependencyMetric.TryGetValue(AttributeAWSRemoteResourceIdentifier, out var actualAWSRemoteResourceIdentifier);
        Assert.Equal(type, actualAWSRemoteResourceType);
        Assert.Equal(identifier, actualAWSRemoteResourceIdentifier);
        spanDataMock.Dispose();
    }

    [Fact]
    public void testNormalizeRemoteServiceName_NoNormalization()
    {
        string serviceName = "non aws service";
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeRpcService, serviceName);
        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualServiceName);
        Assert.Equal(serviceName, actualServiceName);
    }

    [Fact]
    public void testNormalizeRemoteServiceName_AwsSdk()
    {
        // AWS SDK V2
        testAwsSdkServiceNormalization("AmazonDynamoDBv2", "AWS::DynamoDB");
        testAwsSdkServiceNormalization("AmazonKinesis", "AWS::Kinesis");
        testAwsSdkServiceNormalization("Amazon S3", "AWS::S3");
        testAwsSdkServiceNormalization("AmazonSQS", "AWS::SQS");
        
        // AWS SDK V1
        testAwsSdkServiceNormalization("DynamoDb", "AWS::DynamoDB");
        testAwsSdkServiceNormalization("Kinesis", "AWS::Kinesis");
        testAwsSdkServiceNormalization("S3", "AWS::S3");
        testAwsSdkServiceNormalization("Sqs", "AWS::SQS");
    }

    [Fact]
    public void testNoMetricWhenConsumerProcessWithConsumerParent()
    {
        spanDataMock = testSource.StartActivity("test", ActivityKind.Consumer);
        Activity childSpan = testSource.StartActivity("test", ActivityKind.Consumer);
        childSpan.SetParentId(spanDataMock.TraceId, spanDataMock.SpanId);
        childSpan.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
        childSpan.SetTag(AttributeAWSConsumerParentSpanKind, ActivityKind.Consumer.ToString());
        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(childSpan, _resource);
        Assert.Equal(0, attributeMap.Count);
    }
    
    [Fact]
    public void testBothMetricsWhenLocalRootConsumerProcess()
    {
        parentSpan.Dispose();
        spanDataMock = testSource.StartActivity("test", ActivityKind.Consumer);
        spanDataMock.SetTag(AttributeMessagingOperation, MessagingOperationValues.Process);
        spanDataMock.SetTag(AttributeAWSConsumerParentSpanKind, ActivityKind.Consumer.ToString());
        spanDataMock.Start();
        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        Assert.Equal(2, attributeMap.Count);
    }

    private void testAwsSdkServiceNormalization(String serviceName, String expectedRemoteService)
    {
        spanDataMock = testSource.StartActivity("test", ActivityKind.Client);
        spanDataMock.SetTag(AttributeRpcSystem, "aws-api");
        spanDataMock.SetTag(AttributeRpcService, serviceName);
        var attributeMap = Generator.GenerateMetricAttributeMapFromSpan(spanDataMock, _resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        dependencyMetric.TryGetValue(AttributeAWSRemoteService, out var actualServiceName);
        Assert.Equal(expectedRemoteService, actualServiceName);
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
    
    private void validateAttributesProducedForNonLocalRootSpanOfKind(ActivityTagsCollection expectedAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attributeMap =
            Generator.GenerateMetricAttributeMapFromSpan(span, this._resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.ServiceMetric, out ActivityTagsCollection serviceMetric);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
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

    private void validateAttributesProducedForLocalRootSpanOfKind(ActivityTagsCollection expectServiceAttributes,
        ActivityTagsCollection expectDependencyAttributes, Activity span)
    {
        Dictionary<string, ActivityTagsCollection> attributeMap =
            Generator.GenerateMetricAttributeMapFromSpan(span, this._resource);
        attributeMap.TryGetValue(IMetricAttributeGenerator.ServiceMetric, out ActivityTagsCollection serviceMetric);
        attributeMap.TryGetValue(IMetricAttributeGenerator.DependencyMetric, out ActivityTagsCollection dependencyMetric);
        
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
