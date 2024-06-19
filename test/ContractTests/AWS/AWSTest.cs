
using Xunit.Abstractions;
using Testcontainers.LocalStack;
using LocalStack.Client.Enums;
using FluentAssertions;

namespace AWS.Distro.Opentelemetry.AutoInstrumentation.ContractTests;

public class AWSTest(ITestOutputHelper output) : TestBase("AWS", output)
{
    private LocalStackContainer? LocalStack;

    protected override int ApplicationPort => 8080;

    private async Task InitializeAsync()
    {
        // Setup Local Stack for AWS Services
        LocalStack =
        new LocalStackBuilder()
            .WithImage($"localstack/localstack:latest")
            // Specify the LocalStack services to start
            .WithEnvironment("SERVICES", "s3,dynamodb,sqs,kinesis")
            // Set the default AWS region
            .WithEnvironment("DEFAULT_REGION", "us-west-2")
            // Connect the container to a Docker network
            .WithNetwork(Network)
            // Set the network aliases for the container
            .WithNetworkAliases(
            "localstack",
            "s3.localstack",
            "create-bucket.s3.localstack",
            "put-object.s3.localstack",
            "get-object.s3.localstack")
            .WithPortBinding(AwsServiceEndpointMetadata.DynamoDb.Port, AwsServiceEndpointMetadata.DynamoDb.Port)
            .WithPortBinding(AwsServiceEndpointMetadata.Sqs.Port, AwsServiceEndpointMetadata.Sqs.Port)
            .WithPortBinding(AwsServiceEndpointMetadata.S3.Port, AwsServiceEndpointMetadata.S3.Port)
            .WithPortBinding(AwsServiceEndpointMetadata.Sns.Port, AwsServiceEndpointMetadata.Sns.Port)
            .WithCleanUp(true)
            .Build();
        // Start the LocalStack container
        await LocalStack.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await LocalStack!.StopAsync();
        await LocalStack.DisposeAsync();
    }

    protected override string GetApplicationImageName()
    {
        // This is the smaple-app image name, pending change as we developing the new
        // AWS Test Sample Application.
        return "aspnetapp:latest";
    }

    protected override Dictionary<string, string> GetApplicationExtraEnvironmentVariables()
    {
        return new Dictionary<string, string>
        {
            {"AWS_SDK_S3_ENDPOINT", "http://s3.localstack:4566"},
            {"AWS_SDK_ENDPOINT", "http://localstack:4566"},
            {"AWS_REGION", "us-west-2"},
            {"OTEL_DOTNET_AUTO_PLUGINS", "AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"},
            {"CORECLR_ENABLE_PROFILING", "1"},
            {"OTEL_EXPORTER_OTLP_PROTOCOL", "grpc"},
            {"OTEL_DOTNET_AUTO_TRACES_CONSOLE_EXPORTER_ENABLED", "true"}
        };
    }

    protected override List<String> GetApplicationNetworkAliases()
    {
        return ["error-bucket.s3.test", "fault-bucket.s3.test", "error.test", "fault.test"];
    }

    // TODO: Once the setup of contract test frame is approved, we will add more test cases to cover supported AWS SDK cases
    // https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/23
    [Fact (Skip = "Skip auto run contract test in build process for now until automation is achieved")]
    public async void TestAWSCall()
    {   
        await this.StartCollector();
        await this.InitializeAsync();
        await this.SetupClients();
        var res = await AppClient!.GetAsync("/aws-sdk-call");
        var traces = await this.MockCollectorClient!.GetTracesAsync();
        traces.Should().NotBeNull();
        // TODO: Sample app DOTNET_STARTUP_HOOKS is not generated correctly due to OS mismatch
        // Manual change required
        // https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/23
        traces.Should().NotBeEmpty();
    }
}
