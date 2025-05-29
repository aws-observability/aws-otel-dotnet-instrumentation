// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;

/// <summary>
/// OTLP UDP Exporter class. This class is used to build an XrayUdpExporter to registered as in exporter
/// during the instrumentation initialization phase
/// </summary>
public class XrayUdpExporter : BaseExporter<Activity>
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<XrayUdpExporter>();

    private UdpExporter udpExporter;
    private string signalPrefix;
    private Resource processResource;

    /// <summary>
    /// Initializes a new instance of the <see cref="XrayUdpExporter"/> class.
    /// </summary>
    /// <param name="endpoint">Endpoint to export requests to</param>
    /// <param name="signalPrefix">Sampled vs UnSampled signal prefix</param>
    /// <param name="processResource">Otel Resource object</param>
    public XrayUdpExporter(Resource processResource, string? endpoint = null, string? signalPrefix = null)
    {
        // Check if running in AWS Lambda environment
        if (endpoint == null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME")))
        {
            endpoint = Environment.GetEnvironmentVariable("AWS_XRAY_DAEMON_ADDRESS") ?? UdpExporter.DefaultEndpoint;
        }
        else
        {
            endpoint = endpoint ?? UdpExporter.DefaultEndpoint;
        }
        this.udpExporter = new UdpExporter(endpoint);
        this.signalPrefix = signalPrefix ?? UdpExporter.DefaultFormatOtelTracesBinaryPrefix;
        this.processResource = processResource;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        // Chose 65,527 because this is the max size of the data in UDP Datagram
        byte[] serializedData = new byte[65527];

        int serializedDataLength = OtlpExporterUtils.WriteTraceData(ref serializedData, 0, this.processResource, batch);
        
        if (serializedData == null || serializedDataLength == -1)
        {
            return ExportResult.Failure;
        }

        try
        {
            this.udpExporter.SendData(serializedData, 0, serializedDataLength, this.signalPrefix);
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

    internal void SendData(byte[] data, int offset, int serializedDataLength, string signalFormatPrefix)
    {
        string base64EncodedString = Convert.ToBase64String(data, offset, serializedDataLength);
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
