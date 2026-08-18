// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.SpanMetrics;

// TODO: Use generated semantic convention constants once all of these keys are officially available.
internal static class SpanMetricsAttributeKeys
{
    public const string AttributeServiceName = "service.name";

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
}
