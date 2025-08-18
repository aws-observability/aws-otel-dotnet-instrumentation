// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

namespace OpenTelemetry.Instrumentation.AWS.Implementation;

internal static class AWSSemanticConventions
{
    public const string AttributeAWSServiceName = "aws.service";
    public const string AttributeAWSOperationName = "aws.operation";
    public const string AttributeAWSRegion = "aws.region";
    public const string AttributeAWSRequestId = "aws.requestId";
    public const string AttributeAWSAuthAccessKey = "aws.auth.account.access_key";
    public const string AttributeAWSAuthRegion = "aws.auth.region";

    public const string AttributeAWSDynamoTableArn = "aws.dynamodb.table.arn";
    public const string AttributeAWSDynamoTableName = "aws.table_name";
    public const string AttributeAWSSQSQueueUrl = "aws.queue_url";
    public const string AttributeAWSSQSQueueName = "aws.sqs.queue_name";
    public const string AttributeAWSS3BucketName = "aws.s3.bucket";
    public const string AttributeAWSKinesisStreamArn = "aws.kinesis.stream.arn";
    public const string AttributeAWSKinesisStreamName = "aws.kinesis.stream_name";
    public const string AttributeAWSLambdaFunctionArn = "aws.lambda.function.arn";
    public const string AttributeAWSLambdaFunctionName = "aws.lambda.function.name";
    public const string AttributeAWSLambdaResourceMappingId = "aws.lambda.resource_mapping.id";
    public const string AttributeAWSSecretsManagerSecretArn = "aws.secretsmanager.secret.arn";
    public const string AttributeAWSSNSTopicArn = "aws.sns.topic.arn";
    public const string AttributeAWSStepFunctionsActivityArn = "aws.stepfunctions.activity.arn";
    public const string AttributeAWSStepFunctionsStateMachineArn = "aws.stepfunctions.state_machine.arn";

    // AWS Bedrock service attributes not yet defined in semantic conventions
    public const string AttributeAWSBedrockGuardrailArn = "aws.bedrock.guardrail.arn";
    public const string AttributeAWSBedrockGuardrailId = "aws.bedrock.guardrail.id";
    public const string AttributeAWSBedrockAgentId = "aws.bedrock.agent.id";
    public const string AttributeAWSBedrockKnowledgeBaseId = "aws.bedrock.knowledge_base.id";
    public const string AttributeAWSBedrockDataSourceId = "aws.bedrock.data_source.id";

    // should be global convention for Gen AI attributes
    public const string AttributeGenAiSystem = "gen_ai.system";
    public const string AttributeGenAiModelId = "gen_ai.request.model";
    public const string AttributeGenAiTopP = "gen_ai.request.top_p";
    public const string AttributeGenAiTemperature = "gen_ai.request.temperature";
    public const string AttributeGenAiMaxTokens = "gen_ai.request.max_tokens";
    public const string AttributeGenAiInputTokens = "gen_ai.usage.input_tokens";
    public const string AttributeGenAiOutputTokens = "gen_ai.usage.output_tokens";
    public const string AttributeGenAiFinishReasons = "gen_ai.response.finish_reasons";

    public const string AttributeHttpStatusCode = "http.status_code";
    public const string AttributeHttpResponseContentLength = "http.response_content_length";

    public const string AttributeValueDynamoDb = "dynamodb";

    public const string AttributeValueRPCSystem = "rpc.system";
    public const string AttributeValueRPCService = "rpc.service";
    public const string AttributeValueRPCMethod = "rpc.method";
}
