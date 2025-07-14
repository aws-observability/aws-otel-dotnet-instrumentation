// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Logs;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;

public class OtlpExporterUtils
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpExporterUtils>();

    private static readonly MethodInfo? WriteTraceDataMethod;
    private static readonly MethodInfo? WriteLogsDataMethod;
    private static readonly object? SdkLimitOptions;
    private static readonly object? ExperimentalOptions;

    static OtlpExporterUtils() {
        Type? otlpTraceSerializerType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer.ProtobufOtlpTraceSerializer, OpenTelemetry.Exporter.OpenTelemetryProtocol");
        Type? otlpLogSerializerType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer.ProtobufOtlpLogSerializer, OpenTelemetry.Exporter.OpenTelemetryProtocol");
        Type? sdkLimitOptionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.SdkLimitOptions, OpenTelemetry.Exporter.OpenTelemetryProtocol");
        Type? experimentalOptionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.ExperimentalOptions, OpenTelemetry.Exporter.OpenTelemetryProtocol");

        if (sdkLimitOptionsType == null)
        {
            Logger.LogTrace("SdkLimitOptions Type was not found");
            return;
        }

        if (experimentalOptionsType == null)
        {
            Logger.LogTrace("ExperimentalOptions Type was not found");
            return;
        }

        if (otlpTraceSerializerType == null)
        {
            Logger.LogTrace("OtlpTraceSerializer Type was not found");
            return;
        }

        if (otlpLogSerializerType == null)
        {
            Logger.LogTrace("OtlpLogSerializer Type was not found");
            return;
        }

        WriteTraceDataMethod = otlpTraceSerializerType.GetMethod(
            "WriteTraceData",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] 
            {
                typeof(byte[]).MakeByRefType(),    // ref byte[] buffer
                typeof(int),                       // int writePosition
                sdkLimitOptionsType,               // SdkLimitOptions
                typeof(Resource),                  // Resource?
                typeof(Batch<Activity>).MakeByRefType() // in Batch<Activity>
            },
            null)
            ?? throw new MissingMethodException("WriteTraceData not found");

        // Get the WriteLogsData method from the ProtobufOtlpLogSerializer using reflection. "WriteLogsData" is based on the
        // OpenTelemetry.Exporter.OpenTelemetryProtocol dependency found at
        // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpLogSerializer.cs
        WriteLogsDataMethod = otlpLogSerializerType.GetMethod(
            "WriteLogsData",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] 
            {
                typeof(byte[]).MakeByRefType(),    // ref byte[] buffer
                typeof(int),                       // int writePosition
                sdkLimitOptionsType,               // SdkLimitOptions
                experimentalOptionsType,           // ExperimentalOptions
                typeof(Resource),                  // Resource?
                typeof(Batch<LogRecord>).MakeByRefType() // in Batch<LogRecord>
            },
            null)
            ?? throw new MissingMethodException("WriteLogsData not found");

        SdkLimitOptions = GetSdkLimitOptions();
        ExperimentalOptions = GetExperimentalOptions();
    }

    // The WriteTraceData function builds writes data to the buffer byte[] object by calling private "WriteTraceData" function
    // using reflection. "WriteTraceData" is based on the latest v1.11.2 version of the OpenTelemetry.Exporter.OpenTelemetryProtocol
    // depedency specifically found at https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpTraceSerializer.cs#L23
    // and used the by the OTLP Exporters.
    public static int WriteTraceData(
        ref byte[] buffer,
        int writePosition,
        Resource? resource,
        in Batch<Activity> batch)
    {
        if (SdkLimitOptions == null)
        {
            Logger.LogTrace("SdkLimitOptions Object was not found/created properly using the default parameterless constructor");
            return -1;
        }
        
        // Pack arguments (ref/in remain by-ref in the args array)
        object[] args = { buffer, writePosition, SdkLimitOptions, resource!, batch! };

        // Invoke static method (null target)
        var result = (int)WriteTraceDataMethod?.Invoke(obj: null, parameters: args)!;

        // Unpack ref-buffer
        buffer = (byte[])args[0];

        return result;
    }

    // The WriteLogsData function writes log data to the buffer byte[] object by calling private "WriteLogsData" function
    // using reflection. "WriteLogsData" is based on the OpenTelemetry.Exporter.OpenTelemetryProtocol dependency found at
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/ProtobufOtlpLogSerializer.cs
    public static int WriteLogsData(
        ref byte[] buffer,
        int writePosition,
        Resource? resource,
        in Batch<LogRecord> batch)
    {
        if (SdkLimitOptions == null || ExperimentalOptions == null)
        {
            Logger.LogTrace("SdkLimitOptions or ExperimentalOptions Object was not found/created properly");
            return -1;
        }
        
        // Pack arguments (ref/in remain by-ref in the args array)
        object[] args = { buffer, writePosition, SdkLimitOptions, ExperimentalOptions, resource!, batch! };

        // Invoke static method (null target)
        var result = (int)WriteLogsDataMethod?.Invoke(obj: null, parameters: args)!;

        // Unpack ref-buffer
        buffer = (byte[])args[0];

        return result;
    }

    // Uses reflection to get the SdkLimitOptions required to invoke the serialization functions.
    // More information about SdkLimitOptions can be found in this link:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/SdkLimitOptions.cs#L24
    private static object? GetSdkLimitOptions()
    {
        Type? sdkLimitOptionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.SdkLimitOptions, OpenTelemetry.Exporter.OpenTelemetryProtocol");

        if (sdkLimitOptionsType == null)
        {
            Logger.LogTrace("SdkLimitOptions Type was not found");
            return null;
        }

        // Create an instance of SdkLimitOptions using the default parameterless constructor
        object? sdkLimitOptionsInstance = Activator.CreateInstance(sdkLimitOptionsType);
        return sdkLimitOptionsInstance;
    }

    // Uses reflection to get the ExperimentalOptions required for log serialization.
    // More information about ExperimentalOptions can be found in the OpenTelemetry implementation:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ExperimentalOptions.cs
    private static object? GetExperimentalOptions()
    {
        Type? experimentalOptionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.ExperimentalOptions, OpenTelemetry.Exporter.OpenTelemetryProtocol");
        if (experimentalOptionsType == null)
        {
            Logger.LogTrace("ExperimentalOptions Type was not found");
            return null;
        }

        // Create an instance of ExperimentalOptions using the default parameterless constructor
        object? experimentalOptionsInstance = Activator.CreateInstance(experimentalOptionsType);
        return experimentalOptionsInstance;
    }
}