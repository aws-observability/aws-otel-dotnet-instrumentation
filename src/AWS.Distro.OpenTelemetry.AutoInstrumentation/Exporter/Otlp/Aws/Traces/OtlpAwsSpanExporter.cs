// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using Amazon;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Traces;

/// <summary>
/// Creates the upstream OTLP trace exporter with AWS SigV4 signing over HTTP/protobuf.
/// </summary>
public static class OtlpAwsSpanExporter
{
    /// <summary>
    /// Creates an OTLP trace exporter for the X-Ray endpoint.
    /// </summary>
    /// <param name="options">The OTLP endpoint, timeout, headers, and compression options. The protocol must be HTTP/protobuf.</param>
    /// <returns>The upstream exporter configured with AWS request signing.</returns>
    /// <exception cref="ArgumentException">The protocol is not HTTP/protobuf.</exception>
    public static BaseExporter<Activity> Create(OtlpExporterOptions options)
        => Create(options, null, null);

    internal static BaseExporter<Activity> Create(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator,
        Func<HttpMessageHandler>? transportFactory)
    {
        Configure(options, authenticator, transportFactory);
        return new OtlpTraceExporter(options);
    }

    /// <summary>
    /// Adds AWS request signing to the options used to construct the upstream trace exporter.
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
            throw new ArgumentException("The AWS OTLP span exporter requires HTTP/protobuf (OtlpExportProtocol.HttpProtobuf).", nameof(options));
        }

        var endpoint = options.Endpoint;
        var region = endpoint.Host.Split('.')[1];
        var config = new AmazonXRayConfig
        {
            AuthenticationRegion = region,
            AuthenticationServiceName = "xray",
            UseHttp = endpoint.Scheme == Uri.UriSchemeHttp,
            ServiceURL = endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
        };

        // TODO: In a follow-up PR, detect an existing Authorization header in options.Headers
        // and warn once during exporter setup that SigV4 authentication overrides it.
        var headerSupplier = new AwsAuthHeaderSupplier(config, authenticator);
        options.HttpClientFactory = () => new HttpClient(
            new AwsAuthHttpHandler(headerSupplier, transportFactory?.Invoke() ?? new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
        };
    }
}
