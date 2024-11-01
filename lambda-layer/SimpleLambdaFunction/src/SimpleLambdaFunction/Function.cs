using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.S3;
using System.Reflection;

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
        Console.WriteLine("This is the executing assembly qualified name: " + typeof(Function).AssemblyQualifiedName);
        Console.WriteLine("This is the executing assembly fullName: " + typeof(Function).FullName);
        Console.WriteLine("This lambda task root: " + Environment.GetEnvironmentVariable("LAMBDA_TASK_ROOT"));
        Console.WriteLine("This is the runtime dir: " + Environment.GetEnvironmentVariable("LAMBDA_RUNTIME_DIR"));
        CallForceFlush();
        //PrintCurrentDirectoryContents();
        PrintOptDirectoryContent();
        context.Logger.LogLine("Making HTTP call to https://aws.amazon.com/");
        await httpClient.GetAsync("https://aws.amazon.com/");

        context.Logger.LogLine("Making AWS S3 ListBuckets call");
        int bucketCount = await ListS3Buckets();

        var traceId = Environment.GetEnvironmentVariable("_X_AMZN_TRACE_ID");
        Console.WriteLine(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));
        Console.WriteLine(Environment.GetEnvironmentVariable("CORECLR_PROFILER_PATH"));
        Console.WriteLine(Environment.GetEnvironmentVariable("OTEL_AWS_APPLICATION_SIGNALS_ENABLED"));

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = $"Hello lambda - found {bucketCount} buckets. X-Ray Trace ID: {traceId}",
            Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
        };
    }

    public static void CallForceFlush()
    {
        Type? instrumentationType = Type.GetType("OpenTelemetry.AutoInstrumentation.Instrumentation, OpenTelemetry.AutoInstrumentation");

        if (instrumentationType == null)
        {
            Console.WriteLine("instrumentationType Type was not found");
            return;
        }

        FieldInfo? tracerProviderField = instrumentationType.GetField("_tracerProvider", BindingFlags.Static | BindingFlags.NonPublic);

        if (tracerProviderField == null)
        {
            Console.WriteLine("Field '_tracerProvider' not found in Instrumentation class.");
        }

        // Get the value of _tracerProvider
        object? tracerProviderValue = tracerProviderField?.GetValue(null); // Pass null for static fields

        Console.WriteLine(tracerProviderValue?.GetType());
    }

    public static void PrintCurrentDirectoryContents()
    {
        // Get the current working directory
        string currentDirectory = Directory.GetCurrentDirectory();
        Console.WriteLine($"Current Directory: {currentDirectory}\n");

        // Get all subdirectories in the current directory
        string[] directories = Directory.GetDirectories(currentDirectory);
        if (directories.Length > 0)
        {
            Console.WriteLine("Directories:");
            foreach (var dir in directories)
            {
                Console.WriteLine($"  {Path.GetFileName(dir)}");
            }
        }
        else
        {
            Console.WriteLine("No directories found.");
        }

        // Get all files in the current directory
        string[] files = Directory.GetFiles(currentDirectory);
        if (files.Length > 0)
        {
            Console.WriteLine("\nFiles:");
            foreach (var file in files)
            {
                Console.WriteLine($"  {Path.GetFileName(file)}");
            }
        }
        else
        {
            Console.WriteLine("No files found.");
        }
    }

    public static void PrintOptDirectoryContent()
    {
        string optDirectoryPath = "/var/runtime";

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
