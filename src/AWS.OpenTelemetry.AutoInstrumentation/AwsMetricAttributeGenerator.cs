// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsMetricAttributeGenerator generates very specific metric attributes based on low-cardinality
/// span and resource attributes. If such attributes are not present, we fallback to default values.
/// <p>The goal of these particular metric attributes is to get metrics for incoming and outgoing
/// traffic for a service. Namely, <see cref="SpanKind.Server"/> and <see cref="SpanKind.Consumer"/> spans
/// represent "incoming" traffic, {<see cref="SpanKind.Client"/> and <see cref="SpanKind.Producer"/> spans
/// represent "outgoing" traffic, and <see cref="SpanKind.Internal"/> spans are ignored.
/// </summary>
internal sealed class AwsMetricAttributeGenerator : IMetricAttributeGenerator
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddConsole());
    private static readonly ILogger Logger = Factory.CreateLogger<AwsMetricAttributeGenerator>();

    // Special DEPENDENCY attribute value if GRAPHQL_OPERATION_TYPE attribute key is present.
    private static readonly string GraphQL = "graphql";

    // As per
    // https://github.com/open-telemetry/opentelemetry-java/tree/main/sdk-extensions/autoconfigure#opentelemetry-resource
    // If service name is not specified, SDK defaults the service name to unknown_service
    private static readonly string OtelUnknownService = "unknown_service";

    // This is currently not in latest version of the Opentelemetry.SemanticConventions library.
    // although it's available here:
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/4c6474259ccb08a41eb45ea6424243d4d2c707db/src/OpenTelemetry.SemanticConventions/Attributes/ServiceAttributes.cs#L48C25-L48C45
    // TODO: Open an issue to ask about this discrepancy and when will the latest version be released.
    private static readonly string AttributeServiceName = "service.name";

    /// <inheritdoc/>
    public Dictionary<string, ActivityTagsCollection> GenerateMetricAttributeMapFromSpan(Activity span, Resource resource)
    {
        Dictionary<string, ActivityTagsCollection> attributesMap = new Dictionary<string, ActivityTagsCollection>();
        if (ShouldGenerateServiceMetricAttributes(span))
        {
            attributesMap.Add(IMetricAttributeGenerator.ServiceMetric, this.GenerateServiceMetricAttributes(span, resource));
        }

        if (ShouldGenerateDependencyMetricAttributes(span))
        {
            attributesMap.Add(IMetricAttributeGenerator.DependencyMetric, this.GenerateDependencyMetricAttributes(span, resource));
        }

        throw new NotImplementedException();
    }

    private ActivityTagsCollection GenerateServiceMetricAttributes(Activity span, Resource resource)
    {
        ActivityTagsCollection attributes = new ActivityTagsCollection();
        SetService(resource, span, attributes);
        SetIngressOperation(span, attributes);
        SetSpanKindForService(span, attributes);

        return attributes;
    }

    private ActivityTagsCollection GenerateDependencyMetricAttributes(Activity span, Resource resource)
    {
        ActivityTagsCollection attributes = new ActivityTagsCollection();
        SetService(resource, span, attributes);
        SetEgressOperation(span, attributes);
        SetRemoteServiceAndOperation(span, attributes);
        SetRemoteTarget(span, attributes);
        SetSpanKindForDependency(span, attributes);

        return attributes;
    }

#pragma warning disable SA1204 // Static elements should appear before instance elements
    private static void SetRemoteTarget(Activity span, ActivityTagsCollection attributes)
#pragma warning restore SA1204 // Static elements should appear before instance elements
    {
        string? remoteTarget = GetRemoteTarget(span);
        if (remoteTarget != null)
        {
            attributes.Add(AttributeAWSRemoteTarget, remoteTarget);
        }
    }

    /// <summary>
    /// RemoteTarget attribute {@link AwsAttributeKeys#AWS_REMOTE_TARGET} is used to store the resource
    /// name of the remote invokes, such as S3 bucket name, mysql table name, etc. TODO: currently only
    /// support AWS resource name, will be extended to support the general remote targets, such as
    /// ActiveMQ name, etc.
    ///
    /// TODO: This implementation is done mostly to mimic what is in the Java/Python implementation. The reason is because
    /// we currently don't have automatic instrumentation for AWS SDK. This will mostly be updated/rewritten according
    /// to the auto instrumentation implementation.
    /// </summary>
    private static string? GetRemoteTarget(Activity span)
    {
        if (IsKeyPresent(span, AttributeAWSS3Bucket))
        {
            return "::s3:::" + span.GetTagItem(AttributeAWSS3Bucket);
        }

        if (IsKeyPresent(span, AttributeAWSSQSQueueUrl))
        {
            string? arn = SqsUrlParser.GetSqsRemoteTarget((string?)span.GetTagItem(AttributeAWSSQSQueueUrl));

            if (arn != null)
            {
                return arn;
            }
        }

        if (IsKeyPresent(span, AttributeAWSSQSQueueName))
        {
            return "::sqs:::" + span.GetTagItem(AttributeAWSSQSQueueName);
        }

        if (IsKeyPresent(span, AttributeAWSKinesisStreamName))
        {
            return "::kinesis:::stream/" + span.GetTagItem(AttributeAWSKinesisStreamName);
        }

        if (IsKeyPresent(span, AttributeAWSDynamoTableName))
        {
            return "::dynamodb:::table/" + span.GetTagItem(AttributeAWSDynamoTableName);
        }

        return null;
    }

    // Service is always derived from {@link ResourceAttributes#SERVICE_NAME}
    private static void SetService(Resource resource, Activity span, ActivityTagsCollection attributes)
    {
        object service = resource.Attributes.First(attribute => attribute.Key == AttributeServiceName).Value;

        // In practice the service name is never null, but we can be defensive here.
        if (service == null || service.Equals(OtelUnknownService))
        {
            LogUnknownAttribute(AttributeAWSLocalService, span);
            service = UnknownService;
        }

        attributes.Add(AttributeAWSLocalService, service);
    }

    /// <summary>
    /// Ingress operation (i.e. operation for Server and Consumer spans) will be generated from
    /// "http.method + http.target/with the first API path parameter" if the default span name equals
    /// null, UnknownOperation or http.method value.
    /// </summary>
    private static void SetIngressOperation(Activity span, ActivityTagsCollection attributes)
    {
        string operation = GetIngressOperation(span);
        if (operation.Equals(UnknownOperation))
        {
            LogUnknownAttribute(AttributeAWSLocalOperation, span);
        }

        attributes.Add(AttributeAWSLocalOperation, operation);
    }

    /// <summary>
    /// Egress operation(i.e.operation for Client and Producer spans) is always derived from a
    /// special span attribute, {@link AwsAttributeKeys#AWS_LOCAL_OPERATION}. This attribute is
    /// generated with a separate SpanProcessor, {@link AttributePropagatingSpanProcessor}
    /// </summary>
    private static void SetEgressOperation(Activity span, ActivityTagsCollection attributes)
    {
        string? operation = GetEgressOperation(span);
        if (operation == null)
        {
            LogUnknownAttribute(AttributeAWSLocalOperation, span);
            operation = UnknownOperation;
        }

        attributes.Add(AttributeAWSLocalOperation, operation);
    }

    // add `AWS.SDK.` as prefix to indicate the metrics resulted from current span is from AWS SDK
    private static string NormalizeServiceName(Activity span, string serviceName)
    {
        if (IsAwsSDKSpan(span))
        {
            return "AWS.SDK." + serviceName;
        }

        return serviceName;
    }

    /// <summary>
    /// Remote attributes (only for Client and Producer spans) are generated based on low-cardinality
    /// span attributes, in priority order.
    ///
    /// <p>The first priority is the AWS Remote attributes, which are generated from manually
    /// instrumented span attributes, and are clear indications of customer intent. If AWS Remote
    /// attributes are not present, the next highest priority span attribute is Peer Service, which is
    /// also a reliable indicator of customer intent. If this is set, it will override
    /// AWS_REMOTE_SERVICE identified from any other span attribute, other than AWS Remote attributes.
    ///
    /// <p>After this, we look for the following low-cardinality span attributes that can be used to
    /// determine the remote metric attributes:
    ///
    /// <ul>
    ///   <li>RPC
    ///   <li>DB
    ///   <li>FAAS
    ///   <li>Messaging
    ///   <li>GraphQL - Special case, if {@link SemanticAttributes#GRAPHQL_OPERATION_TYPE} is present,
    ///       we use it for RemoteOperation and set RemoteService to {@link #GRAPHQL}.
    /// </ul>
    ///
    /// <p>In each case, these span attributes were selected from the OpenTelemetry trace semantic
    /// convention specifications as they adhere to the three following criteria:
    ///
    /// <ul>
    ///   <li>Attributes are meaningfully indicative of remote service/operation names.
    ///   <li>Attributes are defined in the specification to be low cardinality, usually with a low-
    ///       cardinality list of values.
    ///   <li>Attributes are confirmed to have low-cardinality values, based on code analysis.
    /// </ul>
    ///
    /// if the selected attributes are still producing the UnknownRemoteService or
    /// UnknownRemoteOperation, `net.peer.name`, `net.peer.port`, `net.peer.sock.addr`,
    /// `net.peer.sock.port` and `http.url` will be used to derive the RemoteService. And `http.method`
    /// and `http.url` will be used to derive the RemoteOperation.
    /// </summary>
    private static void SetRemoteServiceAndOperation(Activity span, ActivityTagsCollection attributes)
    {
        string remoteService = UnknownRemoteService;
        string remoteOperation = UnknownRemoteOperation;
        if (IsKeyPresent(span, AttributeAWSRemoteService) || IsKeyPresent(span, AttributeAWSRemoteOperation))
        {
            remoteService = GetRemoteService(span, AttributeAWSRemoteService);
            remoteOperation = GetRemoteOperation(span, AttributeAWSRemoteOperation);
        }
        else if (IsKeyPresent(span, AttributeRpcService) || IsKeyPresent(span, AttributeRpcMethod))
        {
            remoteService = NormalizeServiceName(span, GetRemoteService(span, AttributeRpcService));
            remoteOperation = GetRemoteOperation(span, AttributeRpcMethod);
        }
        else if (IsKeyPresent(span, AttributeDbSystem)
            || IsKeyPresent(span, AttributeDbOperation)
            || IsKeyPresent(span, AttributeDbStatement))
        {
            remoteService = GetRemoteService(span, AttributeDbSystem);
            if (IsKeyPresent(span, AttributeDbOperation))
            {
                remoteOperation = GetRemoteOperation(span, AttributeDbOperation);
            }
            else
            {
                remoteOperation = GetDBStatementRemoteOperation(span, AttributeDbStatement);
            }
        }
        else if (IsKeyPresent(span, AttributeFaasInvokedName) || IsKeyPresent(span, AttributeFaasTrigger))
        {
            remoteService = GetRemoteService(span, AttributeFaasInvokedName);
            remoteOperation = GetRemoteOperation(span, AttributeFaasTrigger);
        }
        else if (IsKeyPresent(span, AttributeMessagingSystem) || IsKeyPresent(span, AttributeMessagingOperation))
        {
            remoteService = GetRemoteService(span, AttributeMessagingSystem);
            remoteOperation = GetRemoteOperation(span, AttributeMessagingOperation);
        }
        else if (IsKeyPresent(span, AttributeGraphqlOperationType))
        {
            remoteService = GraphQL;
            remoteOperation = GetRemoteOperation(span, AttributeGraphqlOperationType);
        }

        // Peer service takes priority as RemoteService over everything but AWS Remote.
        if (IsKeyPresent(span, AttributePeerService) && !IsKeyPresent(span, AttributeAWSRemoteService))
        {
            remoteService = GetRemoteService(span, AttributePeerService);
        }

        // try to derive RemoteService and RemoteOperation from the other related attributes
        if (remoteService.Equals(UnknownRemoteService))
        {
            remoteService = GenerateRemoteService(span);
        }

        if (remoteOperation.Equals(UnknownRemoteOperation))
        {
            remoteOperation = GenerateRemoteOperation(span);
        }

        attributes.Add(AttributeAWSRemoteService, remoteService);
        attributes.Add(AttributeAWSRemoteOperation, remoteOperation);
    }

    // When the remote call operation is undetermined for http use cases,
    // will try to extract the remote operation name from http url string
    private static string GenerateRemoteOperation(Activity span)
    {
        string remoteOperation = UnknownRemoteOperation;
        if (IsKeyPresent(span, AttributeHttpUrl))
        {
            string? httpUrl = (string?)span.GetTagItem(AttributeHttpUrl);
            try
            {
                Uri url;
                if (httpUrl != null)
                {
                    url = new Uri(httpUrl);
                    remoteOperation = ExtractAPIPathValue(url.AbsolutePath);
                }
            }
            catch (UriFormatException)
            {
                Logger.Log(LogLevel.Trace, "invalid http.url attribute: {0}", httpUrl);
            }
        }

        if (IsKeyPresent(span, AttributeHttpMethod))
        {
            string? httpMethod = (string?)span.GetTagItem(AttributeHttpMethod);
            remoteOperation = httpMethod + " " + remoteOperation;
        }

        if (remoteOperation.Equals(UnknownRemoteOperation))
        {
            LogUnknownAttribute(AttributeAWSRemoteOperation, span);
        }

        return remoteOperation;
    }

    private static string GenerateRemoteService(Activity span)
    {
        string remoteService = UnknownRemoteService;
        if (IsKeyPresent(span, AttributeNetPeerName))
        {
            remoteService = GetRemoteService(span, AttributeNetPeerName);
            if (IsKeyPresent(span, AttributeNetPeerPort))
            {
                long? port = (long?)span.GetTagItem(AttributeNetPeerPort);
                remoteService += ":" + port;
            }
        }
        else if (IsKeyPresent(span, AttributeNetSockPeerAddr))
        {
            remoteService = GetRemoteService(span, AttributeNetSockPeerAddr);
            if (IsKeyPresent(span, AttributeNetSockPeerPort))
            {
                long? port = (long?)span.GetTagItem(AttributeNetSockPeerPort);
                remoteService += ":" + port;
            }
        }
        else if (IsKeyPresent(span, AttributeHttpUrl))
        {
            string? httpUrl = (string?)span.GetTagItem(AttributeHttpUrl);
            try
            {
                if (httpUrl != null)
                {
                    Uri url = new Uri(httpUrl);
                    if (!string.IsNullOrEmpty(url.Host))
                    {
                        remoteService = url.Host;
                        if (url.Port != -1)
                        {
                            remoteService += ":" + url.Port;
                        }
                    }
                }
            }
            catch (UriFormatException)
            {
                Logger.Log(LogLevel.Trace, "invalid http.url attribute: {0}", httpUrl);
            }
        }
        else
        {
            LogUnknownAttribute(AttributeAWSRemoteService, span);
        }

        return remoteService;
    }

    // Span kind is needed for differentiating metrics in the EMF exporter
    private static void SetSpanKindForService(Activity span, ActivityTagsCollection attributes)
    {
        string spanKind = span.Kind.GetType().Name;
        if (IsLocalRoot(span))
        {
            spanKind = LocalRoot;
        }

        attributes.Add(AttributeAWSSpanKind, spanKind);
    }

    private static void SetSpanKindForDependency(Activity span, ActivityTagsCollection attributes)
    {
        string spanKind = span.Kind.GetType().Name;
        attributes.Add(AttributeAWSSpanKind, spanKind);
    }

    private static string GetRemoteService(Activity span, string remoteServiceKey)
    {
        string? remoteService = (string?)span.GetTagItem(remoteServiceKey);
        if (remoteService == null)
        {
            remoteService = UnknownRemoteService;
        }

        return remoteService;
    }

    private static string GetRemoteOperation(Activity span, string remoteOperationKey)
    {
        string? remoteOperation = (string?)span.GetTagItem(remoteOperationKey);
        if (remoteOperation == null)
        {
            remoteOperation = UnknownOperation;
        }

        return remoteOperation;
    }

    private static string GetDBStatementRemoteOperation(Activity span, string remoteOperationKey)
    {
        string remoteOperation;
        object? remoteOperationObject = span.GetTagItem(remoteOperationKey);
        if (remoteOperationObject == null)
        {
            remoteOperation = "Unknown";
        }
        else
        {
            remoteOperation = (string)remoteOperationObject;
        }

        // Remove all whitespace and newline characters from the beginning of remote_operation
        // and retrieve the first MAX_KEYWORD_LENGTH characters
        remoteOperation = remoteOperation.TrimStart();
        if (remoteOperation.Length > MaxKeywordLength)
        {
            remoteOperation = remoteOperation.Substring(0, MaxKeywordLength);
        }

        Regex regex = new Regex(SqlDialectPattern);
        Match match = regex.Match(remoteOperation.ToUpper());
        if (match.Success && !string.IsNullOrEmpty(match.Value))
        {
            remoteOperation = match.Value;
        }
        else
        {
            remoteOperation = UnknownRemoteOperation;
        }

        return remoteOperation;
    }

    private static void LogUnknownAttribute(string attributeKey, Activity span)
    {
        string[] logParams = { attributeKey, span.Kind.GetType().Name, span.Context.SpanId.ToString() };
        Logger.Log(LogLevel.Trace, "No valid {0} value found for {1} span {2}", logParams);
    }
}
