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
