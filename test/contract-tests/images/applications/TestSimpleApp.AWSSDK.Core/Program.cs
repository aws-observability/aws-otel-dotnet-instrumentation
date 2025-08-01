using Amazon.Runtime;
using Amazon.Bedrock;
using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.Kinesis;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.StepFunctions;
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
    .AddSingleton<IAmazonSecretsManager>(provider => new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig { ServiceURL = "http://localstack:4566" }))
    .AddSingleton<IAmazonSimpleNotificationService>(provider => new AmazonSimpleNotificationServiceClient(new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localstack:4566" }))
    .AddSingleton<IAmazonStepFunctions>(provider => new AmazonStepFunctionsClient(new AmazonStepFunctionsConfig { ServiceURL = "http://localstack:4566" }))
    // Bedrock services are not supported by localstack, so we mock the API responses on the aws-application-signals-tests-testsimpleapp server.
    .AddSingleton<IAmazonBedrock>(provider => new AmazonBedrockClient(new AmazonBedrockConfig { ServiceURL = "http://localhost:8080" }))
    .AddSingleton<IAmazonBedrockRuntime>(provider => new AmazonBedrockRuntimeClient(new AmazonBedrockRuntimeConfig { ServiceURL = "http://localhost:8080" }))
    .AddSingleton<IAmazonBedrockAgent>(provider => new AmazonBedrockAgentClient(new AmazonBedrockAgentConfig { ServiceURL = "http://localhost:8080" }))
    .AddSingleton<IAmazonBedrockAgentRuntime>(provider => new AmazonBedrockAgentRuntimeClient(new AmazonBedrockAgentRuntimeConfig { ServiceURL = "http://localhost:8080" }))
    // fault client
    .AddKeyedSingleton<IAmazonDynamoDB>("fault-ddb", new AmazonDynamoDBClient(AmazonClientConfigHelper.CreateConfig<AmazonDynamoDBConfig>(true)))
    .AddKeyedSingleton<IAmazonS3>("fault-s3", new AmazonS3Client(AmazonClientConfigHelper.CreateConfig<AmazonS3Config>(true)))
    .AddKeyedSingleton<IAmazonSQS>("fault-sqs", new AmazonSQSClient(AmazonClientConfigHelper.CreateConfig<AmazonSQSConfig>(true)))
    .AddKeyedSingleton<IAmazonKinesis>("fault-kinesis", new AmazonKinesisClient(new AmazonKinesisConfig { ServiceURL = "http://localstack:4566" }))
    .AddKeyedSingleton<IAmazonSecretsManager>("fault-secretsmanager", new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig { ServiceURL = "http://localstack:4566" }))
    .AddKeyedSingleton<IAmazonSimpleNotificationService>("fault-sns", new AmazonSimpleNotificationServiceClient(new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localstack:4566" }))
    .AddKeyedSingleton<IAmazonStepFunctions>("fault-stepfunctions", new AmazonStepFunctionsClient(new AmazonStepFunctionsConfig { ServiceURL = "http://localstack:4566" }))
    //error client
    .AddKeyedSingleton<IAmazonDynamoDB>("error-ddb", new AmazonDynamoDBClient(AmazonClientConfigHelper.CreateConfig<AmazonDynamoDBConfig>()))
    .AddKeyedSingleton<IAmazonS3>("error-s3", new AmazonS3Client(AmazonClientConfigHelper.CreateConfig<AmazonS3Config>()))
    .AddKeyedSingleton<IAmazonSQS>("error-sqs", new AmazonSQSClient(AmazonClientConfigHelper.CreateConfig<AmazonSQSConfig>()))
    .AddKeyedSingleton<IAmazonKinesis>("error-kinesis", new AmazonKinesisClient(new AmazonKinesisConfig { ServiceURL = "http://localstack:4566" }))
    .AddKeyedSingleton<IAmazonSecretsManager>("error-secretsmanager", new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig {ServiceURL = "http://localstack:4566" }))
    .AddKeyedSingleton<IAmazonSimpleNotificationService>("error-sns", new AmazonSimpleNotificationServiceClient(new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localstack:4566" }))
    .AddKeyedSingleton<IAmazonStepFunctions>("error-stepfunctions", new AmazonStepFunctionsClient(new AmazonStepFunctionsConfig { ServiceURL = "http://localstack:4566" }))
    // cross account client - simulating STS assumed credentials for account B
    .AddKeyedSingleton<IAmazonS3>("cross-account-s3", new AmazonS3Client(
        new SessionAWSCredentials("account_b_access_key_id", "account_b_secret_access_key", "account_b_token"),
        new AmazonS3Config { 
            ServiceURL = "http://localstack:4566", 
            ForcePathStyle = true, 
            AuthenticationRegion = "eu-central-1"
        }))
    .AddSingleton<S3Tests>()
    .AddSingleton<DynamoDBTests>()
    .AddSingleton<SQSTests>()
    .AddSingleton<KinesisTests>()
    .AddSingleton<SecretsManagerTests>()
    .AddSingleton<SNSTests>()
    .AddSingleton<StepFunctionsTests>()
    .AddSingleton<BedrockTests>()
    .AddSingleton<CrossAccountTests>();

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

app.MapGet("ddb/describetable/some-table", (DynamoDBTests ddb) => ddb.DescribeTable())
    .WithName("describe-table")
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

app.MapGet("kinesis/describestream/my-stream", (KinesisTests Kinesis) => Kinesis.DescribeStream())
    .WithName("describe-stream")
    .WithOpenApi();

app.MapGet("kinesis/deletestream/my-stream", (KinesisTests kinesis) => kinesis.DeleteStream())
    .WithName("delete-stream")
    .WithOpenApi();

app.MapGet("kinesis/fault", (KinesisTests kinesis) => kinesis.Fault()).WithName("kinesis-fault").WithOpenApi();
app.MapGet("kinesis/error", (KinesisTests kinesis) => kinesis.Error()).WithName("kinesis-error").WithOpenApi();

app.MapGet("secretsmanager/createsecret/some-secret", (SecretsManagerTests secretsManager) => secretsManager.CreateSecret())
    .WithName("create-secret")
    .WithOpenApi();

app.MapGet("secretsmanager/getsecretvalue/some-secret", (SecretsManagerTests secretsManager) => secretsManager.GetSecretValue())
    .WithName("get-secret-value")
    .WithOpenApi();

app.MapGet("secretsmanager/fault", (SecretsManagerTests secretsManager) => secretsManager.Fault()).WithName("secretsmanager-fault").WithOpenApi();
app.MapGet("secretsmanager/error", (SecretsManagerTests secretsManager) => secretsManager.Error()).WithName("secretsmanager-error").WithOpenApi();

app.MapGet("sns/publish/some-topic", (SNSTests sns) => sns.Publish())
    .WithName("publish")
    .WithOpenApi();

app.MapGet("sns/fault", (SNSTests sns) => sns.Fault()).WithName("sns-fault").WithOpenApi();
app.MapGet("sns/error", (SNSTests sns) => sns.Error()).WithName("sns-error").WithOpenApi();

app.MapGet("stepfunctions/describestatemachine/some-state-machine", (StepFunctionsTests stepFunctions) => stepFunctions.DescribeStateMachine())
    .WithName("describe-state-machine")
    .WithOpenApi();

app.MapGet("stepfunctions/describeactivity/some-activity", (StepFunctionsTests stepFunctions) => stepFunctions.DescribeActivity())
    .WithName("describe-activity")
    .WithOpenApi();

app.MapGet("stepfunctions/fault", (StepFunctionsTests stepFunctions) => stepFunctions.Fault()).WithName("stepfunctions-fault").WithOpenApi();
app.MapGet("stepfunctions/error", (StepFunctionsTests stepFunctions) => stepFunctions.Error()).WithName("stepfunctions-error").WithOpenApi();

app.MapGet("bedrock/getguardrail/get-guardrail", (BedrockTests bedrock) => bedrock.GetGuardrail())
    .WithName("get-guardrail")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-nova", (BedrockTests bedrock) => bedrock.InvokeModelAmazonNova())
    .WithName("invoke-model-nova")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-titan", (BedrockTests bedrock) => bedrock.InvokeModelAmazonTitan())
    .WithName("invoke-model-titan")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-claude", (BedrockTests bedrock) => bedrock.InvokeModelAnthropicClaude())
    .WithName("invoke-model-claude")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-llama", (BedrockTests bedrock) => bedrock.InvokeModelMetaLlama())
    .WithName("invoke-model-llama")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-command", (BedrockTests bedrock) => bedrock.InvokeModelCohereCommand())
    .WithName("invoke-model-command")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-jamba", (BedrockTests bedrock) => bedrock.InvokeModelAi21Jamba())
    .WithName("invoke-model-jamba")
    .WithOpenApi();

app.MapGet("bedrock/invokemodel/invoke-model-mistral", (BedrockTests bedrock) => bedrock.InvokeModelMistralAi())
    .WithName("invoke-model-mistral")
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

// Create some resources in advance to be accessed by tests
async Task PrepareAWSServer(IServiceProvider services)
{
    var snsTests = services.GetRequiredService<SNSTests>();
    var stepfunctionsTests = services.GetRequiredService<StepFunctionsTests>();
    var ddbTests = services.GetRequiredService<DynamoDBTests>();
    var kinesisTests = services.GetRequiredService<KinesisTests>();
    
    // Create a topic for the SNS tests
    await snsTests.CreateTopic("test-topic");

    // Create a state machine and activity for the Step Functions tests
    await stepfunctionsTests.CreateStateMachine("test-state-machine");
    await stepfunctionsTests.CreateActivity("test-activity");
    
    var existingTables = await ddbTests.ListTables();
    if (!existingTables.TableNames.Contains("test-table"))
    {
        await ddbTests.CreateTable("test-table");
    }

    var existingStreams = await kinesisTests.ListStreams();
    if (!existingStreams.StreamNames.Contains("test-stream"))
    { 
        await kinesisTests.CreateStream("test-stream");
    }
    // TODO: create resources for Lambda event source mapping test
}

// Reroute the Bedrock API calls to our mock responses in BedrockTests. While other services use localstack to handle the requests,
// we write our own responses with the necessary data to mimic the expected behavior of the Bedrock services.
app.MapGet("guardrails/test-guardrail", (BedrockTests bedrock) => bedrock.GetGuardrailResponse());
// For invoke model, we have one test case for each of the 7 suppported models.
app.MapPost("model/us.amazon.nova-micro-v1:0/invoke", (BedrockTests bedrock) => bedrock.InvokeModelAmazonNovaResponse());
app.MapPost("model/amazon.titan-text-express-v1/invoke", (BedrockTests bedrock) => bedrock.InvokeModelAmazonTitanResponse());
app.MapPost("model/us.anthropic.claude-3-5-haiku-20241022-v1:0/invoke", (BedrockTests bedrock) => bedrock.InvokeModelAnthropicClaudeResponse());
app.MapPost("model/meta.llama3-8b-instruct-v1:0/invoke", (BedrockTests bedrock) => bedrock.InvokeModelMetaLlamaResponse());
app.MapPost("model/cohere.command-r-v1:0/invoke", (BedrockTests bedrock) => bedrock.InvokeModelCohereCommandResponse());
app.MapPost("model/ai21.jamba-1-5-large-v1:0/invoke", (BedrockTests bedrock) => bedrock.InvokeModelAi21JambaResponse());
app.MapPost("model/mistral.mistral-7b-instruct-v0:2/invoke", (BedrockTests bedrock) => bedrock.InvokeModelMistralAiResponse());
app.MapGet("agents/test-agent", (BedrockTests bedrock) => bedrock.GetAgentResponse());
app.MapGet("knowledgebases/test-knowledge-base", (BedrockTests bedrock) => bedrock.GetKnowledgeBaseResponse());
app.MapGet("knowledgebases/test-knowledge-base/datasources/test-data-source", (BedrockTests bedrock) => bedrock.GetDataSourceResponse());
app.MapPost("agents/test-agent/agentAliases/test-agent-alias/sessions/test-session/text", (BedrockTests bedrock) => bedrock.InvokeAgentResponse());
app.MapPost("knowledgebases/test-knowledge-base/retrieve", (BedrockTests bedrock) => bedrock.RetrieveResponse());

// Add a route for cross-account bucket creation
app.MapGet("cross-account/createbucket/account_b", (CrossAccountTests crossAccount) => crossAccount.CreateBucketCrossAccount())
    .WithName("create-bucket-cross-account")
    .WithOpenApi();

await PrepareAWSServer(app.Services);

app.Run();
