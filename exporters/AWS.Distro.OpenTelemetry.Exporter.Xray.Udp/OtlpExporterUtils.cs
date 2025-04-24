// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;

public class OtlpExporterUtils
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpExporterUtils>();

    private static readonly MethodInfo? WriteTraceDataMethod;
    private static readonly object? SdkLimitOptions;

    static OtlpExporterUtils() {
        Type? otlpSerializerType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.Serializer.ProtobufOtlpTraceSerializer, OpenTelemetry.Exporter.OpenTelemetryProtocol");
        Type? sdkLimitOptionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.SdkLimitOptions, OpenTelemetry.Exporter.OpenTelemetryProtocol");

        if (sdkLimitOptionsType == null)
        {
            Logger.LogTrace("SdkLimitOptions Type was not found");
            return;
        }

        if (otlpSerializerType == null)
        {
            Logger.LogTrace("OtlpSerializer Type was not found");
            return;
        }

        WriteTraceDataMethod = otlpSerializerType.GetMethod(
            "WriteTraceData",
            BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] 
            {
                typeof(byte[]).MakeByRefType(),    // ref byte[] buffer
                typeof(int),                       // int writePosition
                sdkLimitOptionsType,           // SdkLimitOptions
                typeof(Resource),                  // Resource?
                typeof(Batch<Activity>).MakeByRefType() // in Batch<Activity>
            },
            null)
            ?? throw new MissingMethodException("WriteTraceData not found");  // :contentReference[oaicite:1]{index=1}

        SdkLimitOptions = GetSdkLimitOptions();
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

        // Invoke static method (null target) :contentReference[oaicite:2]{index=2}
        var result = (int)WriteTraceDataMethod?.Invoke(obj: null, parameters: args)!;

        // Unpack ref-buffer
        buffer = (byte[])args[0];

        return result;
    }

    // Uses reflection to the get the SdkLimitOptions required to invoke the ToOtlpSpan function used in the
    // SerializeSpans function below. More information about SdkLimitOptions can be found in this link:
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
}