// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Amazon;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Logs;

/// <summary>
/// Uses the upstream OTLP log exporter with AWS SigV4 signing over HTTP/protobuf.
/// </summary>
public sealed class OtlpAwsLogRecordExporter : BaseOtlpAwsExporter<LogRecord>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsLogRecordExporter"/> class.
    /// </summary>
    /// <param name="options">The OTLP endpoint, timeout, headers, and compression options. The protocol must be HTTP/protobuf.</param>
    /// <exception cref="ArgumentException">The protocol is not HTTP/protobuf.</exception>
    public OtlpAwsLogRecordExporter(OtlpExporterOptions options)
        : this(options, null, null)
    {
    }

    internal OtlpAwsLogRecordExporter(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator,
        Func<HttpMessageHandler>? transportFactory)
        : base(CreateExporter(options, authenticator, transportFactory))
    {
    }

    private static OtlpLogExporter CreateExporter(
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
