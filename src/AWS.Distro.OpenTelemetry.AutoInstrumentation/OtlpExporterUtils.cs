// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Resources;
using OtlpResource = OpenTelemetry.Proto.Resource.V1;

public class OtlpExporterUtils
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpExporterUtils>();

    // The SerializeSpans function builds a ExportTraceServiceRequest object by calling private "ToOtlpSpan" function
    // using reflection. "ToOtlpSpan" converts an Activity object into an OpenTelemetry.Proto.Trace.V1.Span object.
    // With the conversion above, the Activity object is converted to an Otel span object to be exported using the
    // UDP exporter. The "ToOtlpSpan" function can be found here:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ActivityExtensions.cs#L136
    public static byte[]? SerializeSpans(Batch<Activity> batch, Resource processResource)
    {
        Type? activityExtensionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.ActivityExtensions, OpenTelemetry.Exporter.OpenTelemetryProtocol");

        Type? sdkLimitOptionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.SdkLimitOptions, OpenTelemetry.Exporter.OpenTelemetryProtocol");

        if (sdkLimitOptionsType == null)
        {
            Logger.LogTrace("SdkLimitOptions Type was not found");
            return null;
        }

        MethodInfo? toOtlpSpanMethod = activityExtensionsType?.GetMethod(
            "ToOtlpSpan",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(Activity), sdkLimitOptionsType },
            null);

        var request = new ExportTraceServiceRequest();
        var sdkLimitOptions = OtlpExporterUtils.GetSdkLimitOptions();

        if (sdkLimitOptions == null)
        {
            Logger.LogTrace("SdkLimitOptions Object was not found/created properly using the default parameterless constructor");
            return null;
        }

        var otlpResource = OtlpExporterUtils.ToOtlpResource(processResource);

        // Create a ResourceSpans instance to hold the span and the otlpResource
        ResourceSpans resourceSpans = new ResourceSpans
        {
            Resource = otlpResource,
        };
        var scopeSpans = new ScopeSpans();

        if (toOtlpSpanMethod != null)
        {
            foreach (var activity in batch)
            {
                var otlpSpan = toOtlpSpanMethod.Invoke(null, new object[] { activity, sdkLimitOptions });

                // The converters below are required since the the JsonConvert.DeserializeObject doesn't
                // know how to deserialize a BytesString or SpanKinds from otlp proto json object.
                var settings = new Newtonsoft.Json.JsonSerializerSettings();
                settings.Converters.Add(new ByteStringConverter());
                settings.Converters.Add(new SpanKindConverter());
                settings.Converters.Add(new StatusCodeConverter());

                // Below is a workaround to casting and works by converting an object into JSON then converting the
                // JSON string back into the required object type. The reason casting isn't working is because of different
                // assemblies being used. To use the protobuf library, we need to have a local copy of the protobuf assembly.
                // Since upstream also has their own copy of the protobuf library, casting is not possible since the complier
                // is recognizing them as two different types.
                try
                {
                    var otlpSpanJson = otlpSpan?.ToString();
                    if (otlpSpanJson == null)
                    {
                        continue;
                    }

                    var otlpSpanConverted = JsonConvert.DeserializeObject<Span>(otlpSpanJson, settings);
                    scopeSpans.Spans.Add(otlpSpanConverted);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error converting OtlpSpan to/from JSON: {ex.Message}");
                }
            }

            resourceSpans.ScopeSpans.Add(scopeSpans);
            request.ResourceSpans.Add(resourceSpans);
        }
        else
        {
            Logger.LogTrace("ActivityExtensions.ToOtlpSpan method is not found");
        }

        return request.ToByteArray();
    }

    // Function that uses reflection to call ResourceExtensions.ToOtlpResource function.
    // This functions converts from an OpenTelemetry.Resources.Resource to
    // OpenTelemetry.Proto.Resource.V1.Resource (protobuf resource to be exported)
    private static OtlpResource.Resource? ToOtlpResource(Resource processResource)
    {
        Type? resourceExtensionsType = Type.GetType("OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.ResourceExtensions, OpenTelemetry.Exporter.OpenTelemetryProtocol");

        if (resourceExtensionsType == null)
        {
            Logger.LogTrace("ResourceExtensions Type was not found");
            return null;
        }

        MethodInfo? toOtlpResourceMethod = resourceExtensionsType.GetMethod(
            "ToOtlpResource",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(Resource) },
            null);

        if (toOtlpResourceMethod == null)
        {
            Logger.LogTrace("ResourceExtensions.ToOtlpResource Method was not found");
            return null;
        }

        var otlpResource = toOtlpResourceMethod.Invoke(null, new object[] { processResource });

        if (otlpResource == null)
        {
            Logger.LogTrace("OtlpResource object cannot be converted from OpenTelemetry.Resources");
            return null;
        }

        // Below is a workaround to casting and works by converting an object into JSON then converting the
        // JSON string back into the required object type. The reason casting isn't working is because of different
        // assemblies being used. To use the protobuf library, we need to have a local copy of the protobuf assembly.
        // Since upstream also has their own copy of the protobuf library, casting is not possible since the complier
        // is recognizing them as two different types.
        try
        {
            // ToString method from OpenTelemetry.Proto.Resource.V1.Resource already converts the object into
            // Json using the proper converters.
            string? otlpResourceJson = otlpResource.ToString();
            if (otlpResourceJson == null)
            {
                Logger.LogTrace("OtlpResource object cannot be converted to JSON");
                return null;
            }

            var otlpResourceConverted = JsonConvert.DeserializeObject<OtlpResource.Resource>(otlpResourceJson);
            return otlpResourceConverted;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error converting OtlpResource to/from JSON: {ex.Message}");
            return null;
        }
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

internal class ByteStringConverter : JsonConverter<ByteString>
{
    /// <inheritdoc/>
    public override ByteString? ReadJson(JsonReader reader, Type objectType, ByteString? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var base64String = (string?)reader.Value;
        return ByteString.FromBase64(base64String);
    }

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, ByteString? value, JsonSerializer serializer)
    {
        writer.WriteValue(value?.ToBase64());
    }
}

internal class SpanKindConverter : JsonConverter<Span.Types.SpanKind>
{
    /// <inheritdoc/>
    public override Span.Types.SpanKind ReadJson(JsonReader reader, Type objectType, Span.Types.SpanKind existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        // Handle the string to enum conversion
        string? enumString = reader.Value?.ToString();

        // Convert the string representation to the corresponding enum value
        switch (enumString)
        {
            case "SPAN_KIND_CLIENT":
                return Span.Types.SpanKind.Client;
            case "SPAN_KIND_SERVER":
                return Span.Types.SpanKind.Server;
            case "SPAN_KIND_INTERNAL":
                return Span.Types.SpanKind.Internal;
            case "SPAN_KIND_PRODUCER":
                return Span.Types.SpanKind.Producer;
            case "SPAN_KIND_CONSUMER":
                return Span.Types.SpanKind.Consumer;
            default:
                throw new JsonSerializationException($"Unknown SpanKind: {enumString}");
        }
    }

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, Span.Types.SpanKind value, JsonSerializer serializer)
    {
        // Write the string representation of the enum
        writer.WriteValue(value.ToString());
    }
}

internal class StatusCodeConverter : JsonConverter<Status.Types.StatusCode>
{
    /// <inheritdoc/>
    public override Status.Types.StatusCode ReadJson(JsonReader reader, Type objectType, Status.Types.StatusCode existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        // Handle the string to enum conversion
        string? enumString = reader.Value?.ToString();

        // Convert the string representation to the corresponding enum value
        switch (enumString)
        {
            case "STATUS_CODE_UNSET":
                return Status.Types.StatusCode.Unset;
            case "STATUS_CODE_OK":
                return Status.Types.StatusCode.Ok;
            case "STATUS_CODE_ERROR":
                return Status.Types.StatusCode.Error;
            default:
                throw new JsonSerializationException($"Unknown StatusCode: {enumString}");
        }
    }

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, Status.Types.StatusCode value, JsonSerializer serializer)
    {
        // Write the string representation of the enum
        writer.WriteValue(value.ToString());
    }
}
