// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.RegionalResourceArnParser;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.SqsUrlParser;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsMetricAttributeGenerator generates very specific metric attributes based on low-cardinality
/// span and resource attributes. If such attributes are not present, we fallback to default values.
/// <p>The goal of these particular metric attributes is to get metrics for incoming and outgoing
/// traffic for a service. Namely, <see cref="SpanKind.Server"/> and <see cref="SpanKind.Consumer"/> spans
/// represent "incoming" traffic, {<see cref="SpanKind.Client"/> and <see cref="SpanKind.Producer"/> spans
/// represent "outgoing" traffic, and <see cref="SpanKind.Internal"/> spans are ignored.
/// </summary>
internal class AwsMetricAttributeGenerator : IMetricAttributeGenerator
{
    // This is currently not in latest version of the Opentelemetry.SemanticConventions library.
    // although it's available here:
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/4c6474259ccb08a41eb45ea6424243d4d2c707db/src/OpenTelemetry.SemanticConventions/Attributes/ServiceAttributes.cs#L48C25-L48C45
    // TODO: Open an issue to ask about this discrepancy and when will the latest version be released.
    public static readonly string AttributeServiceName = "service.name";
    public static readonly string AttributeServerAddress = "server.address";
    public static readonly string AttributeServerPort = "server.port";

    // This is not mentioned in upstream but java have that part
    public static readonly string AttributeServerSocketPort = "server.socket.port";
    public static readonly string AttributeDBUser = "db.user";

#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = Factory.CreateLogger<AwsMetricAttributeGenerator>();

    // Normalized remote service names for supported AWS services
    private static readonly string NormalizedDynamoDBServiceName = "AWS::DynamoDB";
    private static readonly string NormalizedKinesisServiceName = "AWS::Kinesis";
    private static readonly string NormalizedLambdaServiceName = "AWS::Lambda";
    private static readonly string NormalizedS3ServiceName = "AWS::S3";
    private static readonly string NormalizedSecretsManagerServiceName = "AWS::SecretsManager";
    private static readonly string NormalizedSNSServiceName = "AWS::SNS";
    private static readonly string NormalizedSQSServiceName = "AWS::SQS";
    private static readonly string NormalizedStepFunctionsName = "AWS::StepFunctions";
    private static readonly string NormalizedBedrockServiceName = "AWS::Bedrock";
    private static readonly string NormalizedBedrockRuntimeServiceName = "AWS::BedrockRuntime";
    private static readonly string DbConnectionResourceType = "DB::Connection";

    // Constants for Lambda operations
    private static readonly string LambdaInvokeOperation = "Invoke";

    // Special DEPENDENCY attribute value if GRAPHQL_OPERATION_TYPE attribute key is present.
    private static readonly string GraphQL = "graphql";

    /// <inheritdoc/>
    public virtual Dictionary<string, ActivityTagsCollection> GenerateMetricAttributeMapFromSpan(Activity span, Resource resource)
    {
        Dictionary<string, ActivityTagsCollection> attributesMap = new Dictionary<string, ActivityTagsCollection>();
        if (ShouldGenerateServiceMetricAttributes(span))
        {
            attributesMap.Add(MetricAttributeGeneratorConstants.ServiceMetric, this.GenerateServiceMetricAttributes(span, resource));
        }

        if (ShouldGenerateDependencyMetricAttributes(span))
        {
            attributesMap.Add(MetricAttributeGeneratorConstants.DependencyMetric, this.GenerateDependencyMetricAttributes(span, resource));
        }

        return attributesMap;
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
        SetRemoteEnvironment(span, attributes);
        SetRemoteServiceAndOperation(span, attributes);
        bool isRemoteResourceIdentifierPresent = SetRemoteResourceTypeAndIdentifier(span, attributes);
        if (isRemoteResourceIdentifierPresent)
        {
            bool isAccountIdAndRegionPresent = SetRemoteResourceAccountIdAndRegion(span, attributes);
            if (!isAccountIdAndRegionPresent)
            {
                SetRemoteResourceAccessKeyAndRegion(span, attributes);
            }
        }

        SetSpanKindForDependency(span, attributes);
        SetRemoteDbUser(span, attributes);

        return attributes;
    }

    /// Service is always derived from <see cref="AttributeServiceName"/>
#pragma warning disable SA1204 // Static elements should appear before instance elements
    private static void SetService(Resource resource, Activity span, ActivityTagsCollection attributes)
#pragma warning restore SA1204 // Static elements should appear before instance elements
    {
        string? service = (string?)resource.Attributes.FirstOrDefault(attribute => attribute.Key == AttributeAWSLocalService).Value;

        // In practice the service name is never null, but we can be defensive here.
        if (service == null)
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
    /// special span attribute, <see cref="AttributeAWSLocalOperation"/>. This attribute is
    /// generated with a separate SpanProcessor, <see cref="AttributePropagatingSpanProcessor"/>
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
    ///   <li>GraphQL - Special case, if <see cref="AttributeGraphqlOperationType"/> is present,
    ///       we use it for RemoteOperation and set RemoteService to <see cref="GraphQL"/>.
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
            remoteService = NormalizeRemoteServiceName(span, GetRemoteService(span, AttributeRpcService));
            remoteOperation = GetRemoteOperation(span, AttributeRpcMethod);
        }

        // TODO workaround for AWS SDK span
        else if (IsKeyPresent(span, AttributeAWSServiceName) || IsKeyPresent(span, AttributeAWSOperationName))
        {
            remoteService = NormalizeRemoteServiceName(span, GetRemoteService(span, AttributeAWSServiceName));
            remoteOperation = GetRemoteOperation(span, AttributeAWSOperationName);
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
        if (IsKeyPresent(span, AttributeUrlFull))
        {
            string? httpUrl = (string?)span.GetTagItem(AttributeUrlFull);
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

        if (IsKeyPresent(span, AttributeHttpRequestMethod))
        {
            string? httpMethod = (string?)span.GetTagItem(AttributeHttpRequestMethod);
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
        else if (IsKeyPresent(span, AttributeUrlFull))
        {
            string? httpUrl = (string?)span.GetTagItem(AttributeUrlFull);
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

    /// <summary>
    /// If the span is an AWS SDK span, normalize the name to align with
    /// <a href="https://docs.aws.amazon.com/cloudcontrolapi/latest/userguide/supported-resources.html">AWS
    /// Cloud Control resource format</a> as much as possible, with special attention to services we
    /// can detect remote resource information for. Long term, we would like to normalize service name
    /// in the upstream.
    /// </summary>
    private static string NormalizeRemoteServiceName(Activity span, string serviceName)
    {
        if (IsAwsSDKSpan(span))
        {
            switch (serviceName)
            {
                case "AmazonDynamoDBv2": // AWS SDK v1
                case "DynamoDb": // AWS SDK v2
                    return NormalizedDynamoDBServiceName;
                case "AmazonKinesis": // AWS SDK v1
                case "Kinesis": // AWS SDK v2
                    return NormalizedKinesisServiceName;
                case "Amazon S3": // AWS SDK v1
                case "S3": // AWS SDK v2
                    return NormalizedS3ServiceName;
                case "Secrets Manager":
                    return NormalizedSecretsManagerServiceName;
                case "SNS":
                    return NormalizedSNSServiceName;
                case "AmazonSQS": // AWS SDK v1
                case "Sqs": // AWS SDK v2
                    return NormalizedSQSServiceName;
                case "SFN":
                    return NormalizedStepFunctionsName;
                case "Bedrock":
                case "Bedrock Agent":
                case "Bedrock Agent Runtime":
                    return NormalizedBedrockServiceName;
                case "Bedrock Runtime":
                    return NormalizedBedrockRuntimeServiceName;
                case "Lambda":
                    if (IsLambdaInvokeOperation(span))
                    {
                        string? lambdaFunctionName = (string?)span.GetTagItem(AttributeAWSLambdaFunctionName);

                        // if Lambda function name is not present, use UnknownRemoteService
                        // This is intentional - we want to clearly indicate when the Lambda function name
                        // is missing rather than falling back to a generic service name
                        return lambdaFunctionName != null
                            ? lambdaFunctionName
                            : UnknownRemoteService;
                    }
                    else
                    {
                        return NormalizedLambdaServiceName;
                    }

                default:
                    return "AWS::" + serviceName;
            }
        }

        return serviceName;
    }

    // This function is used to check for AWS specific attributes and set the RemoteResourceType
    // and RemoteResourceIdentifier accordingly. Right now, this sets it for DDB, S3, Kinesis,
    // and SQS (using QueueName or QueueURL). Returns true if remote resource type and identifier
    // are successfully set, false otherwise.
    private static bool SetRemoteResourceTypeAndIdentifier(Activity span, ActivityTagsCollection attributes)
    {
        string? remoteResourceType = null;
        string? remoteResourceIdentifier = null;
        string? cloudformationPrimaryIdentifier = null;
        if (IsAwsSDKSpan(span))
        {
            if (IsKeyPresent(span, AttributeAWSDynamoTableName))
            {
                remoteResourceType = NormalizedDynamoDBServiceName + "::Table";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSDynamoTableName));
            }
            else if (IsKeyPresent(span, AttributeAWSDynamoTableArn))
            {
                remoteResourceType = NormalizedDynamoDBServiceName + "::Table";
                remoteResourceIdentifier = EscapeDelimiters(RegionalResourceArnParser.ExtractDynamoDbTableNameFromArn((string?)span.GetTagItem(AttributeAWSDynamoTableArn)));
            }
            else if (IsKeyPresent(span, AttributeAWSKinesisStreamName))
            {
                remoteResourceType = NormalizedKinesisServiceName + "::Stream";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSKinesisStreamName));
            }
            else if (IsKeyPresent(span, AttributeAWSKinesisStreamArn))
            {
                remoteResourceType = NormalizedKinesisServiceName + "::Stream";
                remoteResourceIdentifier = EscapeDelimiters(RegionalResourceArnParser.ExtractKinesisStreamNameFromArn((string?)span.GetTagItem(AttributeAWSKinesisStreamArn)));
            }
            else if (IsKeyPresent(span, AttributeAWSLambdaFunctionName))
            {
                // For non-invoke Lambda operations, treat Lambda as a resource.
                // see NormalizeRemoteServiceName for more information.
                if (!IsLambdaInvokeOperation(span))
                {
                    remoteResourceType = NormalizedLambdaServiceName + "::Function";
                    remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSLambdaFunctionName));
                    cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSLambdaFunctionArn));
                }
            }
            else if (IsKeyPresent(span, AttributeAWSLambdaResourceMappingId))
            {
                remoteResourceType = NormalizedLambdaServiceName + "::EventSourceMapping";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSLambdaResourceMappingId));
            }
            else if (IsKeyPresent(span, AttributeAWSS3Bucket))
            {
                remoteResourceType = NormalizedS3ServiceName + "::Bucket";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSS3Bucket));
            }
            else if (IsKeyPresent(span, AttributeAWSSecretsManagerSecretArn))
            {
                remoteResourceType = NormalizedSecretsManagerServiceName + "::Secret";
                remoteResourceIdentifier = EscapeDelimiters(RegionalResourceArnParser.ExtractResourceNameFromArn((string?)span.GetTagItem(AttributeAWSSecretsManagerSecretArn)));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSSecretsManagerSecretArn));
            }
            else if (IsKeyPresent(span, AttributeAWSSNSTopicArn))
            {
                remoteResourceType = NormalizedSNSServiceName + "::Topic";
                remoteResourceIdentifier = EscapeDelimiters(RegionalResourceArnParser.ExtractResourceNameFromArn((string?)span.GetTagItem(AttributeAWSSNSTopicArn)));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSSNSTopicArn));
            }
            else if (IsKeyPresent(span, AttributeAWSSQSQueueName))
            {
                remoteResourceType = NormalizedSQSServiceName + "::Queue";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSSQSQueueName));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSSQSQueueUrl));
            }
            else if (IsKeyPresent(span, AttributeAWSSQSQueueUrl))
            {
                remoteResourceType = NormalizedSQSServiceName + "::Queue";
                remoteResourceIdentifier = EscapeDelimiters(GetQueueName((string?)span.GetTagItem(AttributeAWSSQSQueueUrl)));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSSQSQueueUrl));
            }
            else if (IsKeyPresent(span, AttributeAWSStepFunctionsActivityArn))
            {
                remoteResourceType = NormalizedStepFunctionsName + "::Activity";
                remoteResourceIdentifier =
                    EscapeDelimiters(
                        RegionalResourceArnParser.ExtractResourceNameFromArn(
                            (string?)span.GetTagItem(AttributeAWSStepFunctionsActivityArn)));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSStepFunctionsActivityArn));
            }
            else if (IsKeyPresent(span, AttributeAWSStepFunctionsStateMachineArn))
            {
                remoteResourceType = NormalizedStepFunctionsName + "::StateMachine";
                remoteResourceIdentifier = EscapeDelimiters(RegionalResourceArnParser.ExtractResourceNameFromArn((string?)span.GetTagItem(AttributeAWSStepFunctionsStateMachineArn)));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSStepFunctionsStateMachineArn));
            }
            else if (IsKeyPresent(span, AttributeAWSBedrockGuardrailId))
            {
                remoteResourceType = NormalizedBedrockServiceName + "::Guardrail";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSBedrockGuardrailId));
                cloudformationPrimaryIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSBedrockGuardrailArn));
            }
            else if (IsKeyPresent(span, AttributeGenAiModelId))
            {
                remoteResourceType = NormalizedBedrockServiceName + "::Model";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeGenAiModelId));
            }
            else if (IsKeyPresent(span, AttributeAWSBedrockAgentId))
            {
                remoteResourceType = NormalizedBedrockServiceName + "::Agent";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSBedrockAgentId));
            }
            else if (IsKeyPresent(span, AttributeAWSBedrockDataSourceId))
            {
                remoteResourceType = NormalizedBedrockServiceName + "::DataSource";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSBedrockDataSourceId));
                cloudformationPrimaryIdentifier =
                    EscapeDelimiters((string?)span.GetTagItem(AttributeAWSBedrockKnowledgeBaseId))
                    + "|"
                    + remoteResourceIdentifier;
            }
            else if (IsKeyPresent(span, AttributeAWSBedrockKnowledgeBaseId))
            {
                remoteResourceType = NormalizedBedrockServiceName + "::KnowledgeBase";
                remoteResourceIdentifier = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSBedrockKnowledgeBaseId));
            }
        }
        else if (IsDBSpan(span))
        {
            remoteResourceType = DbConnectionResourceType;
            remoteResourceIdentifier = GetDbConnection(span);
        }

        if (cloudformationPrimaryIdentifier == null)
        {
            cloudformationPrimaryIdentifier = remoteResourceIdentifier;
        }

        if (remoteResourceType != null && remoteResourceIdentifier != null && cloudformationPrimaryIdentifier != null)
        {
            attributes.Add(AttributeAWSRemoteResourceType, remoteResourceType);
            attributes.Add(AttributeAWSRemoteResourceIdentifier, remoteResourceIdentifier);
            attributes.Add(AttributeAWSCloudformationPrimaryIdentifier, cloudformationPrimaryIdentifier);
            return true;
        }

        return false;
    }

    // Extracts and sets the remote resource account ID and region from either an SQS queue URL or various AWS ARN attributes.
    private static bool SetRemoteResourceAccountIdAndRegion(Activity span, ActivityTagsCollection attributes)
    {
        string[] arnAttributes = new[]
        {
            AttributeAWSDynamoTableArn,
            AttributeAWSKinesisStreamArn,
            AttributeAWSSNSTopicArn,
            AttributeAWSSecretsManagerSecretArn,
            AttributeAWSStepFunctionsActivityArn,
            AttributeAWSStepFunctionsStateMachineArn,
            AttributeAWSBedrockGuardrailArn,
            AttributeAWSLambdaFunctionArn,
        };

        string? remoteResourceAccountId = null;
        string? remoteResourceRegion = null;

        if (IsKeyPresent(span, AttributeAWSSQSQueueUrl))
        {
            string? url = EscapeDelimiters((string?)span.GetTagItem(AttributeAWSSQSQueueUrl));
            remoteResourceAccountId = SqsUrlParser.GetAccountId(url);
            remoteResourceRegion = SqsUrlParser.GetRegion(url);
        }
        else
        {
            foreach (var attributeKey in arnAttributes)
            {
                if (IsKeyPresent(span, attributeKey))
                {
                    string? arn = (string?)span.GetTagItem(attributeKey);
                    remoteResourceAccountId = RegionalResourceArnParser.GetAccountId(arn);
                    remoteResourceRegion = RegionalResourceArnParser.GetRegion(arn);
                    break;
                }
            }
        }

        if (remoteResourceAccountId != null && remoteResourceRegion != null)
        {
            attributes.Add(AttributeAWSRemoteResourceAccountId, remoteResourceAccountId);
            attributes.Add(AttributeAWSRemoteResourceRegion, remoteResourceRegion);
            return true;
        }

        return false;
    }

    // Extracts and sets the remote resource account access key id and region from STS credentials.
    private static void SetRemoteResourceAccessKeyAndRegion(Activity span, ActivityTagsCollection attributes)
    {
        if (IsKeyPresent(span, AttributeAWSAuthAccessKey))
        {
            string? remoteResourceAccessKey = (string?)span.GetTagItem(AttributeAWSAuthAccessKey);
            attributes.Add(AttributeAWSRemoteResourceAccessKey, remoteResourceAccessKey);
        }

        if (IsKeyPresent(span, AttributeAWSAuthRegion))
        {
            string? remoteResourceRegion = (string?)span.GetTagItem(AttributeAWSAuthRegion);
            attributes.Add(AttributeAWSRemoteResourceRegion, remoteResourceRegion);
        }
    }

    private static void SetRemoteDbUser(Activity span, ActivityTagsCollection attributes)
    {
        if (IsDBSpan(span) && IsKeyPresent(span, AttributeDBUser))
        {
            attributes.Add(AttributeAWSRemoteDBUser, (string?)span.GetTagItem(AttributeDBUser));
        }
    }

    // Remote environment is used to identify the environment of downstream services.
    // Currently only set to "lambda:default" for Lambda Invoke operations when aws-api system is detected.
    private static void SetRemoteEnvironment(Activity span, ActivityTagsCollection attributes)
    {
        // We want to treat downstream Lambdas as a service rather than a resource because
        // Application Signals topology map gets disconnected due to conflicting Lambda Entity definitions
        // Additional context can be found in https://github.com/aws-observability/aws-otel-python-instrumentation/pull/319
        if (IsLambdaInvokeOperation(span))
        {
            var remoteEnvironment = Environment.GetEnvironmentVariable(Plugin.LambdaApplicationSignalsRemoteEnvironment);
            if (string.IsNullOrEmpty(remoteEnvironment))
            {
                remoteEnvironment = "default";
            }

            attributes.Add(AttributeAWSRemoteEnvironment, "lambda:" + remoteEnvironment.Trim());
        }
    }

    // Span kind is needed for differentiating metrics in the EMF exporter
    private static void SetSpanKindForService(Activity span, ActivityTagsCollection attributes)
    {
        string spanKind = span.Kind.ToString().ToUpper();
        if (IsLocalRoot(span))
        {
            spanKind = LocalRoot;
        }

        attributes.Add(AttributeAWSSpanKind, spanKind);
    }

    private static void SetSpanKindForDependency(Activity span, ActivityTagsCollection attributes)
    {
        string spanKind = span.Kind.ToString().ToUpper();
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
            remoteOperation = UnknownRemoteOperation;
        }

        return remoteOperation;
    }

    /// This function extracts the DBStatement from the remote operation attribute value by trying to match it
    /// SqlDialectPattern which is a list of SQL Dialect keywords (SELECT, DROP, etc). For more details about
    /// those keywords, check <see cref="SqlDialectPattern"/>
    private static string GetDBStatementRemoteOperation(Activity span, string remoteOperationKey)
    {
        string remoteOperation;
        object? remoteOperationObject = span.GetTagItem(remoteOperationKey);
        if (remoteOperationObject == null)
        {
            remoteOperation = UnknownRemoteOperation;
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

    private static string? GetDbConnection(Activity span)
    {
        var dbName = span.GetTagItem(AttributeDbName);
        string? dbConnection = null;

        if (IsKeyPresent(span, AttributeServerAddress))
        {
            var serverAddress = span.GetTagItem(AttributeServerAddress);
            var serverPort = span.GetTagItem(AttributeServerPort);
            dbConnection = BuildDbConnection(serverAddress?.ToString(), serverPort == null ? null : Convert.ToInt64(serverPort));
        }
        else if (IsKeyPresent(span, AttributeNetPeerName))
        {
            var networkPeerAddress = span.GetTagItem(AttributeNetPeerName);
            var networkPeerPort = span.GetTagItem(AttributeNetPeerPort);
            dbConnection = BuildDbConnection(networkPeerAddress?.ToString(), networkPeerPort == null ? null : Convert.ToInt64(networkPeerPort));
        }
        else if (IsKeyPresent(span, AttributeServerSocketAddress))
        {
            var serverSocketAddress = span.GetTagItem(AttributeServerSocketAddress);
            var serverSocketPort = span.GetTagItem(AttributeServerSocketPort);
            dbConnection = BuildDbConnection(serverSocketAddress?.ToString(), serverSocketPort == null ? null : Convert.ToInt64(serverSocketPort));
        }
        else if (IsKeyPresent(span, AttributeDbConnectionString))
        {
            var connectionString = span.GetTagItem(AttributeDbConnectionString);
            dbConnection = BuildDbConnection(connectionString?.ToString());
        }

        // return empty resource identifier if db server is not found
        if (dbConnection != null && dbName != null)
        {
            return EscapeDelimiters(dbName.ToString()) + "|" + dbConnection;
        }

        return dbConnection;
    }

    private static string BuildDbConnection(string? address, long? port)
    {
        return EscapeDelimiters(address) + (port != null ? "|" + port : string.Empty);
    }

    private static string? BuildDbConnection(string? connectionString)
    {
        if (connectionString == null)
        {
            Console.WriteLine("Invalid DB Connection String");
            return null;
        }

        Uri uri;
        string address;
        int port;
        try
        {
            uri = new Uri(connectionString);
            address = uri.Host;
            port = uri.Port;
        }
        catch (UriFormatException)
        {
            Console.WriteLine("Invalid DB Connection String");
            return null;
        }

        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        return EscapeDelimiters(address) + (port != -1 ? "|" + port.ToString() : string.Empty);
    }

    private static string? EscapeDelimiters(string? input)
    {
        if (input == null)
        {
            return null;
        }

        return input.Replace("^", "^^").Replace("|", "^|");
    }

    // Check if the span represents a Lambda Invoke operation.
    private static bool IsLambdaInvokeOperation(Activity span)
    {
        if (!IsAwsSDKSpan(span))
        {
            return false;
        }

        string rpcService = GetRemoteService(span, AttributeRpcService);
        string rpcMethod = GetRemoteOperation(span, AttributeRpcMethod);
        return rpcService.Equals("Lambda") && rpcMethod.Equals(LambdaInvokeOperation);
    }
}
