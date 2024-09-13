using Amazon.Bedrock;
using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.Kinesis;
using Amazon.S3;
using Amazon.SQS;
using TestSimpleApp.AWSSDK.Core;

var builder = WebApplication.CreateBuilder(args);


builder.Logging.AddConsole();

builder.Services
    .AddEndpointsApiExplorer()
    .AddSwaggerGen()
    .AddSingleton<IAmazonDynamoDB>(provider => new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = "http://localstack:4566" }))
    .AddSingleton<IAmazonS3>(provider => new AmazonS3Client(new AmazonS3Config { ServiceURL = "http://localstack:4566", ForcePathStyle = true }))
    .AddSingleton<IAmazonSQS>(provider => new AmazonSQSClient(new AmazonSQSConfig { ServiceURL = "http://localstack:4566" }))
    .AddSingleton<IAmazonKinesis>(provider => new AmazonKinesisClient(new AmazonKinesisConfig { ServiceURL = "http://localstack:4566" }))
    // bedrock services are not supported by localstack, so we mock the API responses on the same server
    .AddSingleton<IAmazonBedrock>(provider => new AmazonBedrockClient(new AmazonBedrockConfig { ServiceURL = "http://localhost:8080" }))
    .AddSingleton<IAmazonBedrockRuntime>(provider => new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { ServiceURL = "http://localhost:8080" }))
    .AddSingleton<IAmazonBedrockAgent>(provider => new AmazonBedrockAgentClient(new AmazonBedrockAgentConfig { ServiceURL = "http://localhost:8080" }))
    .AddSingleton<IAmazonBedrockAgentRuntime>(provider => new AmazonBedrockAgentRuntimeClient(new AmazonBedrockAgentRuntimeConfig { ServiceURL = "http://localhost:8080" }))
    // fault client
    .AddKeyedSingleton<IAmazonDynamoDB>("fault-ddb", new AmazonDynamoDBClient(AmazonClientConfigHelper.CreateConfig<AmazonDynamoDBConfig>(true)))
    .AddKeyedSingleton<IAmazonS3>("fault-s3", new AmazonS3Client(AmazonClientConfigHelper.CreateConfig<AmazonS3Config>(true)))
    .AddKeyedSingleton<IAmazonSQS>("fault-sqs", new AmazonSQSClient(AmazonClientConfigHelper.CreateConfig<AmazonSQSConfig>(true)))
    .AddKeyedSingleton<IAmazonKinesis>("fault-kinesis", new AmazonKinesisClient(new AmazonKinesisConfig { ServiceURL = "http://localstack:4566" }))
    //error client
    .AddKeyedSingleton<IAmazonDynamoDB>("error-ddb", new AmazonDynamoDBClient(AmazonClientConfigHelper.CreateConfig<AmazonDynamoDBConfig>()))
    .AddKeyedSingleton<IAmazonS3>("error-s3", new AmazonS3Client(AmazonClientConfigHelper.CreateConfig<AmazonS3Config>()))
    .AddKeyedSingleton<IAmazonSQS>("error-sqs", new AmazonSQSClient(AmazonClientConfigHelper.CreateConfig<AmazonSQSConfig>()))
    .AddKeyedSingleton<IAmazonKinesis>("error-kinesis", new AmazonKinesisClient(new AmazonKinesisConfig { ServiceURL = "http://localstack:4566" }))
    .AddSingleton<S3Tests>()
    .AddSingleton<DynamoDBTests>()
    .AddSingleton<SQSTests>()
    .AddSingleton<KinesisTests>()
    .AddSingleton<BedrockTests>();

var app = builder.Build();

app.UseSwagger()
    .UseSwaggerUI();

app.MapGet("s3/createbucket/create-bucket/{bucketName}", (S3Tests s3, string? bucketName) => s3.CreateBucket(bucketName))
    .WithName("create-bucket")
    .WithOpenApi();

app.MapGet("s3/createobject/put-object/some-object/{bucketName}", (S3Tests s3, string? bucketName) => s3.PutObject(bucketName))
    .WithName("put-object")
    .WithOpenApi();

app.MapGet("s3/deleteobject/delete-object/some-object/{bucketName}", (S3Tests s3, string? bucketName) =>
{
    s3.DeleteObject(bucketName);
    return Results.NoContent();
})
.WithName("delete-object")
.WithOpenApi();

app.MapGet("s3/deletebucket/delete-bucket/{bucketName}", (S3Tests s3, string? bucketName) => s3.DeleteBucket(bucketName))
    .WithName("delete-bucket")
    .WithOpenApi();

app.MapGet("s3/fault", (S3Tests s3) => s3.Fault()).WithName("s3-fault").WithOpenApi();

app.MapGet("s3/error", (S3Tests s3) => s3.Error()).WithName("s3-error").WithOpenApi();

app.MapGet("ddb/createtable/some-table", (DynamoDBTests ddb) => ddb.CreateTable())
    .WithName("create-table")
    .WithOpenApi();

app.MapGet("ddb/put-item/some-item", (DynamoDBTests ddb) => ddb.PutItem())
    .WithName("put-item")
    .WithOpenApi();

app.MapGet("ddb/deletetable/delete-table", (DynamoDBTests ddb) => ddb.DeleteTable())
    .WithName("delete-table")
    .WithOpenApi();

app.MapGet("ddb/fault", (DynamoDBTests ddb) => ddb.Fault()).WithName("ddb-fault").WithOpenApi();

app.MapGet("ddb/error", (DynamoDBTests ddb) => ddb.Error()).WithName("ddb-error").WithOpenApi();

app.MapGet("sqs/createqueue/some-queue", (SQSTests sqs) => sqs.CreateQueue())
    .WithName("create-queue")
    .WithOpenApi();

app.MapGet("sqs/publishqueue/some-queue", (SQSTests sqs) => sqs.SendMessage())
    .WithName("publish-queue")
    .WithOpenApi();

app.MapGet("sqs/consumequeue/some-queue", (SQSTests sqs) => sqs.ReceiveMessage())
    .WithName("consume-queue")
    .WithOpenApi();

app.MapGet("sqs/deletequeue/some-queue", (SQSTests sqs) => sqs.DeleteQueue())
    .WithName("delete-queue")
    .WithOpenApi();

app.MapGet("sqs/fault", (SQSTests sqs) => sqs.Fault()).WithName("sqs-fault").WithOpenApi();

app.MapGet("sqs/error", (SQSTests sqs) => sqs.Error()).WithName("sqs-error").WithOpenApi();

app.MapGet("kinesis/createstream/my-stream", (KinesisTests kinesis) => kinesis.CreateStream())
    .WithName("create-stream")
    .WithOpenApi();

app.MapGet("kinesis/putrecord/my-stream", (KinesisTests kinesis) => kinesis.PutRecord())
    .WithName("put-record")
    .WithOpenApi();

app.MapGet("kinesis/deletestream/my-stream", (KinesisTests kinesis) => kinesis.DeleteStream())
    .WithName("delete-stream")
    .WithOpenApi();

app.MapGet("kinesis/fault", (KinesisTests kinesis) => kinesis.Fault()).WithName("kinesis-fault").WithOpenApi();
app.MapGet("kinesis/error", (KinesisTests kinesis) => kinesis.Error()).WithName("kinesis-error").WithOpenApi();

app.MapGet("bedrock/getguardrail/get-guardrail", (BedrockTests bedrock) => bedrock.GetGuardrail())
    .WithName("get-guardrail")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model", (BedrockTests bedrock) => bedrock.InvokeModel())
    .WithName("invoke-model")
    .WithOpenApi();

app.MapGet("bedrock/getagent/get-agent", (BedrockTests bedrock) => bedrock.GetAgent())
    .WithName("get-agent")
    .WithOpenApi();

app.MapGet("bedrock/getknowledgebase/get-knowledge-base", (BedrockTests bedrock) => bedrock.GetKnowledgeBase())
    .WithName("get-knowledge-base")
    .WithOpenApi();

app.MapGet("bedrock/getdatasource/get-data-source", (BedrockTests bedrock) => bedrock.GetDataSource())
    .WithName("get-data-source")
    .WithOpenApi();

app.MapGet("bedrock/invokeagent/invoke-agent", (BedrockTests bedrock) => bedrock.InvokeAgent())
    .WithName("invoke-agent")
    .WithOpenApi();

app.MapGet("bedrock/retrieve/retrieve", (BedrockTests bedrock) => bedrock.Retrieve())
    .WithName("retrieve")
    .WithOpenApi();

// reroute the Bedrock API calls to our mock responses in BedrockTests. While other services use localstack to handle the requests,
// we write our own responses with the necessary data to mimic the expected behavior of the Bedrock services.
app.MapGet("guardrails/test-guardrail", (BedrockTests bedrock) => bedrock.GetGuardrailResponse());
app.MapPost("model/test-model/invoke", (BedrockTests bedrock) => bedrock.InvokeModelResponse());
app.MapGet("agents/test-agent", (BedrockTests bedrock) => bedrock.GetAgentResponse());
app.MapGet("knowledgebases/test-knowledge-base", (BedrockTests bedrock) => bedrock.GetKnowledgeBaseResponse());
app.MapGet("knowledgebases/test-knowledge-base/datasources/test-data-source", (BedrockTests bedrock) => bedrock.GetDataSourceResponse());
app.MapPost("agents/test-agent/agentAliases/test-agent-alias/sessions/test-session/text", (BedrockTests bedrock) => bedrock.InvokeAgentResponse());
app.MapPost("knowledgebases/test-knowledge-base/retrieve", (BedrockTests bedrock) => bedrock.RetrieveResponse());

app.Run();