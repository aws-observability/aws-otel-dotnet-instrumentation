// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Amazon.Lambda.Core;
using Newtonsoft.Json.Linq;
using OpenTelemetry.Instrumentation.AWSLambda;
using OpenTelemetry.Trace;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// LambdaWrapper class
/// </summary>
public class LambdaWrapper
{
    private static readonly TracerProvider TracerProvider;

    static LambdaWrapper()
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

        if (tracerProviderValue != null)
        {
            TracerProvider = tracerProviderValue as TracerProvider;
        }
    }

    public string TracingFunctionHandler(JObject input, ILambdaContext context)
    => AWSLambdaWrapper.Trace(TracerProvider, FunctionHandler, input, context);

    private string FunctionHandler(JObject input, ILambdaContext context)
    {
        PrintCurrentDirectoryContents();
        return "hello";
    }

    private static void PrintCurrentDirectoryContents()
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

}