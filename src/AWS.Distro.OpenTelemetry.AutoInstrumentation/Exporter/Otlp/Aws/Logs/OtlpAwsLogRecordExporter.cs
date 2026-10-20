// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
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
    /// Creates an OTLP log exporter with configured AWS service and region resolution.
    /// </summary>
    /// <param name="options">The OTLP endpoint, timeout, headers, and compression options. The protocol must be HTTP/protobuf.</param>
    /// <returns>The upstream exporter configured with AWS request signing.</returns>
    /// <exception cref="ArgumentException">The protocol is not HTTP/protobuf or no signing region can be resolved.</exception>
    public static BaseExporter<LogRecord> Create(OtlpExporterOptions options)
    {
        Configure(options);
        return new OtlpLogExporter(options);
    }

    internal static BaseExporter<LogRecord> Create(
        OtlpExporterOptions options,
        string service,
        string region,
        IAwsAuthenticator? authenticator,
        Func<HttpMessageHandler>? transportFactory)
    {
        Configure(options, service, region, authenticator, transportFactory);
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

        var awsOptions = new OtlpAwsExporterOptions(OtlpAwsExporterOptions.Logs, options);
        Configure(options, awsOptions.Service, awsOptions.Region, authenticator, transportFactory);
    }

    /// <summary>
    /// Adds AWS request signing using an explicit service and region.
    /// </summary>
    /// <param name="options">The configured upstream exporter options.</param>
    /// <param name="service">The SigV4 service name.</param>
    /// <param name="region">The signing region.</param>
    /// <param name="authenticator">The AWS authenticator, or null to use the default credential provider.</param>
    /// <param name="transportFactory">The HTTP transport factory, or null to use the default transport.</param>
    internal static void Configure(
        OtlpExporterOptions options,
        string service,
        string region,
        IAwsAuthenticator? authenticator = null,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        if (options.Protocol != OtlpExportProtocol.HttpProtobuf)
        {
            throw new ArgumentException("The AWS OTLP log exporter requires HTTP/protobuf (OtlpExportProtocol.HttpProtobuf).", nameof(options));
        }

        var config = new AwsAuthConfig(options.Endpoint, region, service);
        var headerSupplier = new AwsAuthHeaderSupplier(config, authenticator);
        options.HttpClientFactory = () => new HttpClient(
            new AwsAuthHttpHandler(headerSupplier, transportFactory?.Invoke() ?? new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
        };
    }
}
