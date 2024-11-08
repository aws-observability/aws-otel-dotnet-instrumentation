using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.S3;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SimpleLambdaFunction;

public class Function
{
    private static readonly HttpClient httpClient = new HttpClient();
    private static readonly AmazonS3Client s3Client = new AmazonS3Client();

    /// <summary>
    /// This function handles API Gateway requests and returns results from an HTTP request and S3 call.
    /// </summary>
    /// <param name="apigProxyEvent"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest apigProxyEvent, ILambdaContext context)
    {
        context.Logger.LogLine("Making HTTP call to https://aws.amazon.com/");
        await httpClient.GetAsync("https://aws.amazon.com/");

        context.Logger.LogLine("Making AWS S3 ListBuckets call");
        int bucketCount = await ListS3Buckets().ConfigureAwait(false);

        var traceId = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = $"Hello lambda - found {bucketCount} buckets. X-Ray Trace ID: {traceId}",
            Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
        };
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
