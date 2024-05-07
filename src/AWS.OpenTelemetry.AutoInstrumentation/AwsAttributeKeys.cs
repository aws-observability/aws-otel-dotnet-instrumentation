// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.AutoInstrumentation;

// Utility class holding attribute keys with special meaning to AWS components
internal sealed class AwsAttributeKeys
{
    internal static readonly string AttributeAWSSpanKind = "aws.span.kind";
    internal static readonly string AttributeAWSLocalService = "aws.local.service";
    internal static readonly string AttributeAWSLocalOperation = "aws.local.operation";
    internal static readonly string AttributeAWSRemoteService = "aws.remote.service";
    internal static readonly string AttributeAWSRemoteOperation = "aws.remote.operation";
    internal static readonly string AttributeAWSRemoteTarget = "aws.remote.target";
    internal static readonly string AttributeAWSSdkDescendant = "aws.sdk.descendant";
    internal static readonly string AttributeAWSConsumerParentSpanKind = "aws.consumer.parent.span.kind";

    // This was copied over from AWSSemanticConventions from the here:
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AWS/Implementation/AWSSemanticConventions.cs
    // TODO: add any other attributes keys after auto instrumentation.
    internal static readonly string AttributeAWSServiceName = "aws.service";
    internal static readonly string AttributeAWSOperationName = "aws.operation";
    internal static readonly string AttributeAWSRegion = "aws.region";
    internal static readonly string AttributeAWSRequestId = "aws.requestId";

    internal static readonly string AttributeAWSDynamoTableName = "aws.table_name";
    internal static readonly string AttributeAWSSQSQueueUrl = "aws.queue_url";

    internal static readonly string AttributeHttpStatusCode = "http.status_code";
    internal static readonly string AttributeHttpResponseContentLength = "http.response_content_length";

    internal static readonly string AttributeValueDynamoDb = "dynamodb";
}
