// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using OpenTelemetry.Resources;
using OtlpResource = OpenTelemetry.Proto.Resource.V1;

/// <summary>
/// OTLP UDP Exporter class. This class is used to build an OtlpUdpExporter to registered as in exporter
/// during the instrumentation initialization phase
/// </summary>
public class OtlpUdpExporter : BaseExporter<Activity>
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<OtlpUdpExporter>();

    private UdpExporter udpExporter;
    private string signalPrefix;
    private Resource processResource;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpUdpExporter"/> class.
    /// </summary>
    /// <param name="endpoint">Endpoint to export requests to</param>
    /// <param name="signalPrefix">Sampled vs UnSampled signal prefix</param>
    /// <param name="processResource">Otel Resource object</param>
    public OtlpUdpExporter(Resource processResource, string? endpoint = null, string? signalPrefix = null)
    {
        endpoint = endpoint ?? UdpExporter.DefaultEndpoint;
        this.udpExporter = new UdpExporter(endpoint);
        this.signalPrefix = signalPrefix ?? UdpExporter.DefaultFormatOtelTracesBinaryPrefix;
        this.processResource = processResource;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        byte[]? serializedData = this.SerializeSpans(batch);
        if (serializedData == null)
        {
            return ExportResult.Failure;
        }

        try
        {
            this.udpExporter.SendData(serializedData, this.signalPrefix);
            return ExportResult.Success;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error exporting spans: {ex.Message}");
            return ExportResult.Failure;
        }
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        try
        {
            this.udpExporter.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error shutting down exporter: {ex.Message}");
            return false;
        }
    }

    // Function that uses reflection to call ResourceExtensions.ToOtlpResource function.
    // This functions converts from an OpenTelemetry.Resources.Resource to
    // OpenTelemetry.Proto.Resource.V1.Resource (protobuf resource to be exported)
    private OtlpResource.Resource? ToOtlpResource(Resource processResource)
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
    private object? GetSdkLimitOptions()
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

    // The SerializeSpans function builds a ExportTraceServiceRequest object by calling private "ToOtlpSpan" function
    // using reflection. "ToOtlpSpan" converts an Activity object into an OpenTelemetry.Proto.Trace.V1.Span object.
    // With the conversion above, the Activity object is converted to an Otel span object to be exported using the
    // UDP exporter. The "ToOtlpSpan" function can be found here:
    // https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/ActivityExtensions.cs#L136
    private byte[]? SerializeSpans(Batch<Activity> batch)
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
        var sdkLimitOptions = this.GetSdkLimitOptions();

        if (sdkLimitOptions == null)
        {
            Logger.LogTrace("SdkLimitOptions Object was not found/created properly using the default parameterless constructor");
            return null;
        }

        OtlpResource.Resource? otlpResource = this.ToOtlpResource(this.processResource);

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
                var settings = new JsonSerializerSettings();
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
}

internal class UdpExporter
{
    internal const string DefaultEndpoint = "127.0.0.1:2000";
    internal const string ProtocolHeader = "{\"format\":\"json\",\"version\":1}\n";
    internal const string DefaultFormatOtelTracesBinaryPrefix = "T1S";

    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<UdpExporter>();

    private string endpoint;
    private string host;
    private int port;
    private UdpClient udpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="UdpExporter"/> class.
    /// </summary>
    /// <param name="endpoint">Endpoint to send udp request to</param>
    internal UdpExporter(string? endpoint = null)
    {
        this.endpoint = endpoint ?? DefaultEndpoint;
        (this.host, this.port) = this.ParseEndpoint(this.endpoint);
        this.udpClient = new UdpClient();
        this.udpClient.Client.ReceiveTimeout = 1000; // Optional: Set timeout
    }

    internal void SendData(byte[] data, string signalFormatPrefix)
    {
        string base64EncodedString = Convert.ToBase64String(data);
        string message = $"{ProtocolHeader}{signalFormatPrefix}{base64EncodedString}";

        try
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            this.udpClient.Send(messageBytes, messageBytes.Length, this.host, this.port);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error sending UDP data: {ex.Message}");
            throw;
        }
    }

    internal void Shutdown()
    {
        this.udpClient.Close();
    }

    private (string, int) ParseEndpoint(string endpoint)
    {
        try
        {
            var parts = endpoint.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            {
                throw new ArgumentException($"Invalid endpoint: {endpoint}");
            }

            return (parts[0], port);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid endpoint: {endpoint}", ex);
        }
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