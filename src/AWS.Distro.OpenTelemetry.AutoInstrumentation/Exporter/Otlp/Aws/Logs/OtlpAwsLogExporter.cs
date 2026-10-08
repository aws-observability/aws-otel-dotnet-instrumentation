// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Amazon;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Logs;

/// <summary>
/// Creates the upstream OTLP log exporter with AWS SigV4 signing over HTTP/protobuf.
/// </summary>
/// <remarks>
/// The upstream log exporter is sealed. Returning it directly lets the SDK bind the logger
/// provider's resource and manage the exporter's lifecycle.
/// </remarks>
public static class OtlpAwsLogExporter
{
    /// <summary>
    /// Creates an OTLP log exporter for the CloudWatch Logs endpoint.
    /// </summary>
    /// <param name="options">The OTLP endpoint, timeout, headers, and compression options. The protocol must be HTTP/protobuf.</param>
    /// <returns>The upstream exporter configured with AWS request signing.</returns>
    /// <exception cref="ArgumentException">The protocol is not HTTP/protobuf.</exception>
    public static OtlpLogExporter Create(OtlpExporterOptions options)
        => Create(options, null, null);

    internal static OtlpLogExporter Create(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator,
        Func<HttpMessageHandler>? transportFactory)
    {
        if (options.Protocol != OtlpExportProtocol.HttpProtobuf)
        {
            throw new ArgumentException("The AWS OTLP log exporter requires HTTP/protobuf (OtlpExportProtocol.HttpProtobuf).", nameof(options));
        }

        var endpoint = options.Endpoint;
        var region = endpoint.Host.Split('.')[1];
        var config = new AmazonXRayConfig
        {
            AuthenticationRegion = region,
            AuthenticationServiceName = "logs",
            UseHttp = endpoint.Scheme == Uri.UriSchemeHttp,
            ServiceURL = endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };
        var headerSupplier = new AwsAuthHeaderSupplier(config, authenticator);
        options.HttpClientFactory = () => new HttpClient(
            new AwsAuthHttpHandler(headerSupplier, transportFactory?.Invoke() ?? new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
        };
        return new OtlpLogExporter(options);
    }
}
