// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

#if !NETFRAMEWORK
using System.Reflection;
using System.Runtime.CompilerServices;
using Amazon.Lambda.Core;
using Newtonsoft.Json.Linq;
using OpenTelemetry.Instrumentation.AWSLambda;
using OpenTelemetry.Trace;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// LambdaWrapper class. This class is used by the instrument.sh file to wrap around Lambda Functions
/// and forceFlush spans at the end of the lambda function execution
/// </summary>
public class LambdaWrapper
{
    private static readonly TracerProvider? TracerProvider;

    static LambdaWrapper()
    {
        Type? instrumentationType = Type.GetType("OpenTelemetry.AutoInstrumentation.Instrumentation, OpenTelemetry.AutoInstrumentation");

        if (instrumentationType == null)
        {
            throw new Exception("instrumentationType Type was not found. Cannot load the tracer provider!");
        }

        FieldInfo? tracerProviderField = instrumentationType.GetField("_tracerProvider", BindingFlags.Static | BindingFlags.NonPublic);

        if (tracerProviderField == null)
        {
            throw new Exception("Field '_tracerProvider' not found in Instrumentation class. Cannot load the tracer provider!");
        }

        object? tracerProviderValue = tracerProviderField?.GetValue(null);

        if (tracerProviderValue != null)
        {
            TracerProvider = (TracerProvider)tracerProviderValue;
        }
        else
        {
            throw new Exception("tracerProviderValue was null. Cannot load the tracer provider!");
        }
    }

    /// <summary>
    /// Wrapper function handler for that handles all types of events.
    /// </summary>
    /// <param name="input">Input as JObject which will be converted to the correct object type during runtime</param>
    /// <param name="context">ILambdaContext lambda context</param>
    /// <returns>returns an object that will be contain the same structure as the original handler output type</returns>
    public Task<object?> TracingFunctionHandler(JObject input, ILambdaContext context)
    => AWSLambdaWrapper.TraceAsync(TracerProvider, this.FunctionHandler, input, context);

    /// <summary>
    /// The following are assumptions made about the lambda handler function parameters.
    ///     * Maximum Parameters: A .NET Lambda handler function can have up to two parameters.
    ///     * Parameter Order: If both parameters are used, the event input parameter must come first, followed by the ILambdaContext.
    ///     * Return Types: The handler can return void, a specific type, or a Task/Task<T> for asynchronous methods.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    /// <exception cref="Exception">Multiple exceptions that act as safe gaurds in case any of the
    /// assumptions are wrong or if for any reason reflection is failing to get the original function and it's info.</exception>
    private async Task<object?> FunctionHandler(JObject input, ILambdaContext context)
    {
        if (input == null)
        {
            throw new Exception($"Input cannot be null.");
        }

        (MethodInfo handlerMethod, object handlerInstance) = this.ExtractOriginalHandler();

        object? originalHandlerResult;
        ParameterInfo[] parameters = handlerMethod.GetParameters();

        // A .NET Lambda handler function can have zero, one, or two parameters, depending on the customer's needs:
        //      * Zero Parameters:  When no input data or context is needed.
        //      * One Parameter:    When only the event data is needed.
        //      * Two Parameters:   When both the event data and execution context are needed.
        if (parameters.Length == 2)
        {
            Type inputParameterType = parameters[0].ParameterType;
            object? inputObject = input.ToObject(inputParameterType);

            if (inputObject == null)
            {
                throw new Exception($"Wrapper wasn't able to convert the input object to type: {inputParameterType}!");
            }

            originalHandlerResult = handlerMethod.Invoke(handlerInstance, new object[] { inputObject, context });
        }
        else if (parameters.Length == 1)
        {
            Type inputParameterType = parameters[0].ParameterType;
            object? inputObject = input.ToObject(inputParameterType);

            if (inputObject == null)
            {
                throw new Exception($"Wrapper wasn't able to convert the input object to type: {inputParameterType}!");
            }

            originalHandlerResult = handlerMethod.Invoke(handlerInstance, new object[] { inputObject });
        }
        else if (parameters.Length == 0)
        {
            originalHandlerResult = handlerMethod.Invoke(handlerInstance, new object[] { });
        }
        else
        {
            throw new Exception($"Wrapper handler doesn't support more than 2 input paramaters");
        }

        Type returnType = handlerMethod.ReturnType;
        if (originalHandlerResult == null && returnType.ToString() != typeof(void).ToString())
        {
            throw new Exception($"originalHandlerResult of type: {returnType} returned from the original handler is null!");
        }

        // AsyncStateMachineAttribute can be used to determine whether the original handler method is
        // asynchronous vs synchronous
        Type asyncAttribType = typeof(AsyncStateMachineAttribute);
        var asyncAttrib = (AsyncStateMachineAttribute?)handlerMethod.GetCustomAttribute(asyncAttribType);

        // The return type of the original lambda function is Task<T> or Task so we await for it
        if (asyncAttrib != null && originalHandlerResult != null)
        {
            await (Task)originalHandlerResult;
            var resultProperty = originalHandlerResult.GetType().GetProperty("Result");
            object? wrappedHandlerResult = resultProperty?.GetValue(originalHandlerResult);

            return wrappedHandlerResult;
        }

        // The original handler method is not async so the return type is just T. In this case,
        // we wrap it with a task just to maintain to one-wrapper-fits-all methodology
        else if (originalHandlerResult != null)
        {
            return await Task.Run(() => originalHandlerResult);
        }

        // The return type of the original handler method for some reason is void. In this case, we return null.
        else
        {
            return null;
        }
    }

    private (MethodInfo, object) ExtractOriginalHandler()
    {
        string? originalHandler = Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_AWS_LAMBDA_HANDLER");
        if (string.IsNullOrEmpty(originalHandler))
        {
            throw new Exception("OTEL_INSTRUMENTATION_AWS_LAMBDA_HANDLER not found;");
        }

        var split = originalHandler.Split("::");
        var assembly = split[0];
        var type = split[1];
        var method = split[2];

        Type? handlerType = Type.GetType($"{type}, {assembly}");
        if (handlerType == null)
        {
            throw new Exception($"handlerType of type: ${type} and assembly: ${assembly} was not found");
        }

        object? handlerInstance = Activator.CreateInstance(handlerType);
        if (handlerInstance == null)
        {
            throw new Exception("handlerInstance was not created");
        }

        MethodInfo? handlerMethod = handlerType.GetMethod(method);
        if (handlerMethod == null)
        {
            throw new Exception($"handlerMethod: ${method} was not found");
        }

        return (handlerMethod, handlerInstance);
    }
}
#endif