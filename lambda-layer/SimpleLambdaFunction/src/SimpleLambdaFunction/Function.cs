using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.S3;
using Amazon.S3.Model;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SimpleLambdaFunction
{
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
            PrintOptDirectoryContent();
            context.Logger.LogLine("Making HTTP call to https://aws.amazon.com/");
            await httpClient.GetAsync("https://aws.amazon.com/");

            context.Logger.LogLine("Making AWS S3 ListBuckets call");
            int bucketCount = await ListS3Buckets();

            var traceId = System.Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");
            Console.WriteLine(System.Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));
            Console.WriteLine(System.Environment.GetEnvironmentVariable("CORECLR_PROFILER_PATH"));
            Console.WriteLine(System.Environment.GetEnvironmentVariable("OTEL_AWS_APPLICATION_SIGNALS_ENABLED"));

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = $"Hello lambda - found {bucketCount} buckets. X-Ray Trace ID: {traceId}",
                Headers = new System.Collections.Generic.Dictionary<string, string> { { "Content-Type", "text/plain" } }
            };
        }

        public static void PrintOptDirectoryContent()
        {
            string optDirectoryPath = "/opt";

            if (Directory.Exists(optDirectoryPath))
            {
                Console.WriteLine($"Contents of {optDirectoryPath}:");

                // Get all directories
                string[] directories = Directory.GetDirectories(optDirectoryPath);
                if (directories.Length > 0)
                {
                    Console.WriteLine("Directories:");
                    foreach (var dir in directories)
                    {
                        Console.WriteLine(dir);
                    }
                }
                else
                {
                    Console.WriteLine("No directories found.");
                }

                // Get all files
                string[] files = Directory.GetFiles(optDirectoryPath);
                if (files.Length > 0)
                {
                    Console.WriteLine("\nFiles:");
                    foreach (var file in files)
                    {
                        Console.WriteLine(file);
                    }
                }
                else
                {
                    Console.WriteLine("No files found.");
                }
            }
            else
            {
                Console.WriteLine($"{optDirectoryPath} does not exist.");
            }
        }


        /// <summary>
        /// List all S3 buckets using AWS SDK for .NET
        /// </summary>
        /// <returns></returns>
        private async Task<int> ListS3Buckets()
        {
            var response = await s3Client.ListBucketsAsync();
            return response.Buckets.Count;
        }
    }
}
