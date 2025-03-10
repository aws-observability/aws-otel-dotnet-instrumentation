// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

namespace OpenTelemetry.Instrumentation.AWS.Implementation;

internal class AWSServiceType
{
    internal const string DynamoDbService = "DynamoDB";
    internal const string SQSService = "SQS";
    internal const string SNSService = "SNS";
    internal const string S3Service = "S3";
    internal const string KinesisService = "Kinesis";
    internal const string LambdaService = "Lambda";
    internal const string SecretsManagerService = "Secrets Manager";
    internal const string StepFunctionsService = "SFN";
    internal const string BedrockService = "Bedrock";
    internal const string BedrockRuntimeService = "Bedrock Runtime";
    internal const string BedrockAgentService = "Bedrock Agent";
    internal const string BedrockAgentRuntimeService = "Bedrock Agent Runtime";

    internal static bool IsDynamoDbService(string service)
        => DynamoDbService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSqsService(string service)
        => SQSService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSnsService(string service)
        => SNSService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsS3Service(string service)
        => S3Service.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsLambdaService(string service)
        => LambdaService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsKinesisService(string service)
        => KinesisService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSecretsManagerService(string service)
        => SecretsManagerService.Equals(service, StringComparison.OrdinalIgnoreCase);
    
    internal static bool IsStepFunctionsService(string service)
        => StepFunctionsService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsBedrockService(string service)
        => BedrockService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsBedrockRuntimeService(string service)
        => BedrockRuntimeService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsBedrockAgentService(string service)
        => BedrockAgentService.Equals(service, StringComparison.OrdinalIgnoreCase);

    internal static bool IsBedrockAgentRuntimeService(string service)
        => BedrockAgentRuntimeService.Equals(service, StringComparison.OrdinalIgnoreCase);
}
