using System.Reflection;
using System.Runtime.Loader;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LambdaWrapper;

public class Function
{
    public APIGatewayProxyResponse FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            // Custom logic before invoking the original handler
            context.Logger.LogLine("Custom logic before invoking original handler");

            // Invoke the original handler
            //var response = InvokeOriginalHandler(request, context);

            // Custom logic after invoking the original handler
            context.Logger.LogLine("Custom logic after invoking original handler");

            //return response;

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = $"Hello lambda - this is a test function",
                Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } }
            };
        }

        private APIGatewayProxyResponse InvokeOriginalHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                // Define the fully qualified name of the original handler's class and method
                string originalHandlerClass = "SimpleLambdaFunction::SimpleLambdaFunction.Function";
                string originalHandlerMethod = "FunctionHandler";

                // Load the assembly containing the original handler
                string assemblyPath = "/var/task/MyOriginalAssembly.dll"; // Default location in Lambda
                if (!File.Exists(assemblyPath))
                {
                    throw new FileNotFoundException($"Assembly not found at path: {assemblyPath}");
                }
                var assembly = Assembly.LoadFrom(assemblyPath);

                // Get the type of the original handler class
                var handlerType = assembly.GetType(originalHandlerClass);
                if (handlerType == null)
                {
                    throw new InvalidOperationException($"Type '{originalHandlerClass}' not found in assembly '{assembly.FullName}'.");
                }

                // Get the method info of the original handler method
                var methodInfo = handlerType.GetMethod(originalHandlerMethod, BindingFlags.Instance | BindingFlags.Public);
                if (methodInfo == null)
                {
                    throw new InvalidOperationException($"Method '{originalHandlerMethod}' not found in type '{handlerType.FullName}'.");
                }

                // Create an instance of the original handler class
                var handlerInstance = Activator.CreateInstance(handlerType);

                // Invoke the original handler method
                var result = methodInfo.Invoke(handlerInstance, new object[] { request, context });

                return (APIGatewayProxyResponse)result;
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Error invoking original handler: {ex}");
                throw;
            }
        }
}
