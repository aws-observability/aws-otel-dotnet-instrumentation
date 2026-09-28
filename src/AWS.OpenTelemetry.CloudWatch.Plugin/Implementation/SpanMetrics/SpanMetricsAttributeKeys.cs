// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.SpanMetrics;

// TODO: Use generated semantic convention constants once all of these keys are officially available.
internal static class SpanMetricsAttributeKeys
{
    public const string AttributeHttpRequestMethod = "http.request.method";
    public const string AttributeHttpMethod = "http.method";
    public const string AttributeHttpResponseStatusCode = "http.response.status_code";
    public const string AttributeHttpStatusCode = "http.status_code";
    public const string AttributeHttpRoute = "http.route";

    public const string AttributeErrorType = "error.type";

    public const string AttributeRpcSystemName = "rpc.system.name";
    public const string AttributeRpcSystem = "rpc.system";
    public const string AttributeRpcService = "rpc.service";
    public const string AttributeRpcMethod = "rpc.method";

    public const string AttributeDbSystemName = "db.system.name";
    public const string AttributeDbSystem = "db.system";
    public const string AttributeDbOperationName = "db.operation.name";
    public const string AttributeDbOperation = "db.operation";
    public const string AttributeDbCollectionName = "db.collection.name";
    public const string AttributeDbSqlTable = "db.sql.table";
    public const string AttributeDbMongoDbCollection = "db.mongodb.collection";
    public const string AttributeDbCassandraTable = "db.cassandra.table";
    public const string AttributeDbCosmosDbContainer = "db.cosmosdb.container";

    public const string AttributeMessagingSystem = "messaging.system";
    public const string AttributeMessagingOperationName = "messaging.operation.name";
    public const string AttributeMessagingDestinationName = "messaging.destination.name";
    public const string AttributeMessagingDestination = "messaging.destination";
    public const string AttributeMessagingDestinationTemporary = "messaging.destination.temporary";
    public const string AttributeMessagingDestinationAnonymous = "messaging.destination.anonymous";

    // Messaging (https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/)
    public const string AttributeMessagingOperationType = "messaging.operation.type";
    public const string AttributeMessagingConsumerGroupName = "messaging.consumer.group.name";

    // Peer (https://opentelemetry.io/docs/specs/semconv/registry/attributes/server/)
    public const string AttributeServerAddress = "server.address";
    public const string AttributeServerPort = "server.port";
    public const string AttributeNetPeerName = "net.peer.name";
    public const string AttributeNetHostName = "net.host.name";
    public const string AttributeNetPeerPort = "net.peer.port";
    public const string AttributeNetHostPort = "net.host.port";

    // GenAI (https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/)
    public const string AttributeGenAiRequestModel = "gen_ai.request.model";
    public const string AttributeGenAiProviderName = "gen_ai.provider.name";
    public const string AttributeGenAiOperationName = "gen_ai.operation.name";

    // AWS resource identity
    // (https://opentelemetry.io/docs/specs/semconv/registry/attributes/aws/)
    public const string AttributeAwsS3Bucket = "aws.s3.bucket";
    public const string AttributeAwsDynamoDbTableNames = "aws.dynamodb.table_names";
    public const string AttributeAwsLambdaInvokedArn = "aws.lambda.invoked_arn";
    public const string AttributeAwsSnsTopicArn = "aws.sns.topic.arn";
    public const string AttributeAwsSqsQueueUrl = "aws.sqs.queue.url";

    // FaaS (https://opentelemetry.io/docs/specs/semconv/registry/attributes/faas/)
    public const string AttributeFaasInvokedName = "faas.invoked_name";
    public const string AttributeFaasInvokedProvider = "faas.invoked_provider";
    public const string AttributeFaasInvokedRegion = "faas.invoked_region";
    public const string AttributeFaasTrigger = "faas.trigger";
}
