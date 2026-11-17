using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SimpleLambdaFunction;

public class Function
{
    private static readonly HttpClient httpClient = new HttpClient();
    private static readonly AmazonS3Client s3Client = new AmazonS3Client();
    // Keep the container so we can flush the logger provider added by auto-instrumentation.
    private static readonly ServiceProvider loggingServices = new ServiceCollection()
        .AddLogging(builder => builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug))
        .BuildServiceProvider();
    private static readonly ILogger<Function> logger = loggingServices.GetRequiredService<ILoggerFactory>().CreateLogger<Function>();

    /// <summary>
    /// This function handles API Gateway requests and returns results from an HTTP request and S3 call.
    /// </summary>
    /// <param name="apigProxyEvent"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest apigProxyEvent, ILambdaContext context)
    {
        try
        {
            logger.LogDebug("debug-level-test-message");
            logger.LogInformation("info-level-test-message");
            logger.LogWarning("warn-level-test-message");
            logger.LogError("error-level-test-message");

            logger.LogInformation("Making HTTP call to https://aws.amazon.com/");
            await httpClient.GetAsync("https://aws.amazon.com/");

            logger.LogInformation("Making AWS S3 ListBuckets call");
            int bucketCount = await ListS3Buckets().ConfigureAwait(false);

            var traceId = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = $"Hello lambda - found {bucketCount} buckets. X-Ray Trace ID: {traceId}",
                Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
            };
        }
        finally
        {
            // Temporary workaround: AWSLambdaWrapper only flushes traces before Lambda freezes.
            // Remove when the upstream fix is available through auto-instrumentation:
            // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/5404
            if (loggingServices.GetService<LoggerProvider>() is { } provider && !provider.ForceFlush(5000))
            {
                context.Logger.LogLine("OpenTelemetry log force flush failed or timed out.");
            }
        }
    }

    /// <summary>
    /// List all S3 buckets using AWS SDK for .NET
    /// </summary>
    /// <returns>Number of buckets available</returns>
    private async Task<int> ListS3Buckets()
    {
        var response = await s3Client.ListBucketsAsync();
        return response.Buckets.Count;
    }
}
