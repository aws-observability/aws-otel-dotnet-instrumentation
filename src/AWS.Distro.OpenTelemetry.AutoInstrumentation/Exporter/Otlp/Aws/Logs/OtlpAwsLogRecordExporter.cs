// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Amazon.CloudWatchLogs;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Logs;

/// <summary>
/// Creates the upstream OTLP log exporter with AWS SigV4 signing over HTTP/protobuf.
/// </summary>
public static class OtlpAwsLogRecordExporter
{
    /// <summary>
    /// Creates an OTLP log exporter for the CloudWatch Logs endpoint.
    /// </summary>
    /// <param name="options">The OTLP endpoint, timeout, headers, and compression options. The protocol must be HTTP/protobuf.</param>
    /// <returns>The upstream exporter configured with AWS request signing.</returns>
    /// <exception cref="ArgumentException">The protocol is not HTTP/protobuf.</exception>
    public static BaseExporter<LogRecord> Create(OtlpExporterOptions options)
        => Create(options, null, null);

    internal static BaseExporter<LogRecord> Create(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator,
        Func<HttpMessageHandler>? transportFactory)
    {
        Configure(options, authenticator, transportFactory);
        return new OtlpLogExporter(options);
    }

    /// <summary>
    /// Adds AWS request signing to the options used to construct the upstream log exporter.
    /// </summary>
    /// <param name="options">The configured upstream exporter options.</param>
    /// <param name="authenticator">The AWS authenticator, or null to use the default credential provider.</param>
    /// <param name="transportFactory">The HTTP transport factory, or null to use the default transport.</param>
    internal static void Configure(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator = null,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        if (options.Protocol != OtlpExportProtocol.HttpProtobuf)
        {
            throw new ArgumentException("The AWS OTLP log exporter requires HTTP/protobuf (OtlpExportProtocol.HttpProtobuf).", nameof(options));
        }

        var endpoint = options.Endpoint;
        var region = endpoint.Host.Split('.')[1];
        var config = new AmazonCloudWatchLogsConfig
        {
            AuthenticationRegion = region,
            AuthenticationServiceName = "logs",
            UseHttp = endpoint.Scheme == Uri.UriSchemeHttp,
            ServiceURL = endpoint.AbsoluteUri,
        };
        var headerSupplier = new AwsAuthHeaderSupplier(config, authenticator);
        options.HttpClientFactory = () => new HttpClient(
            new AwsAuthHttpHandler(headerSupplier, transportFactory?.Invoke() ?? new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
        };
    }
}
