// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

// Utility class holding attribute keys with special meaning to AWS components
internal sealed class AwsAttributeKeys
{
    internal static readonly string AttributeAWSSpanKind = "aws.span.kind";
    internal static readonly string AttributeAWSLocalService = "aws.local.service";
    internal static readonly string AttributeAWSLocalOperation = "aws.local.operation";
    internal static readonly string AttributeAWSRemoteDBUser = "aws.remote.db.user";
    internal static readonly string AttributeAWSRemoteService = "aws.remote.service";
    internal static readonly string AttributeAWSRemoteOperation = "aws.remote.operation";

    internal static readonly string AttributeAWSRemoteResourceIdentifier = "aws.remote.resource.identifier";
    internal static readonly string AttributeAWSRemoteResourceType = "aws.remote.resource.type";
    internal static readonly string AttributeAWSSdkDescendant = "aws.sdk.descendant";
    internal static readonly string AttributeAWSConsumerParentSpanKind = "aws.consumer.parent.span.kind";

    // This was copied over from AWSSemanticConventions from the here:
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AWS/Implementation/AWSSemanticConventions.cs
    // TODO: add any other attributes keys after auto instrumentation.
    internal static readonly string AttributeAWSServiceName = "aws.service";
    internal static readonly string AttributeAWSOperationName = "aws.operation";
    internal static readonly string AttributeAWSRegion = "aws.region";
    internal static readonly string AttributeAWSRequestId = "aws.requestId";
    internal static readonly string AttributeAWSTraceFlagSampled = "aws.trace.flag.sampled";

    // The below semantic names were copied over from various sources.
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/4c6474259ccb08a41eb45ea6424243d4d2c707db/src/OpenTelemetry.Instrumentation.AWS/Implementation/AWSSemanticConventions.cs
    // This is a link for the attributes in the AWS instrumentation package. The others were copied from semcov packages
    // of Java and Python.
    // TODO: Update the attributes below after auto instrumentation to avoid processing errors. These also need
    // to be moved to the Opentelemetry.SemanticConventions to have a single source of truth.

    // internal static readonly string AttributeAWSSQSQueueUrl = "aws.sqs.queue_url";
    internal static readonly string AttributeAWSSQSQueueName = "aws.sqs.queue_name";
    internal static readonly string AttributeAWSKinesisStreamName = "aws.kinesis.stream_name";

    // This attribute is being used here:
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/4c6474259ccb08a41eb45ea6424243d4d2c707db/src/OpenTelemetry.Instrumentation.AWS/Implementation/AWSSemanticConventions.cs#L13
    // However, the one in Opentelemetry.SemanticConventions
    // public const string AttributeAwsDynamodbTableNames = "aws.dynamodb.table_names"
    // Going to use the below one because of the manual instrumentation.
    // TODO: update/remove attribute according to auto instrumentation.
    internal static readonly string AttributeAWSDynamoTableName = "aws.table_name";
    internal static readonly string AttributeAWSSQSQueueUrl = "aws.queue_url";

    internal static readonly string AttributeAWSS3Bucket = "aws.s3.bucket";

    internal static readonly string AttributeAWSBedrockGuardrailId = "aws.bedrock.guardrail.id";
    internal static readonly string AttributeAWSBedrockAgentId = "aws.bedrock.agent.id";
    internal static readonly string AttributeAWSBedrockKnowledgeBaseId = "aws.bedrock.knowledge_base.id";
    internal static readonly string AttributeAWSBedrockDataSourceId = "aws.bedrock.data_source.id";

    internal static readonly string AttributeHttpResponseContentLength = "http.response_content_length";

    internal static readonly string AttributeValueDynamoDb = "dynamodb";
}
