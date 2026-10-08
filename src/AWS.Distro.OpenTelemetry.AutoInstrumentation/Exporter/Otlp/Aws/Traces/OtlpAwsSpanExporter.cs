// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Traces;

/// <summary>
/// Uses the upstream OTLP trace exporter with AWS SigV4 signing over HTTP/protobuf.
/// </summary>
public class OtlpAwsSpanExporter : OtlpTraceExporter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpAwsSpanExporter"/> class.
    /// </summary>
    /// <param name="options">The OTLP endpoint, timeout, headers, and compression options.</param>
    public OtlpAwsSpanExporter(OtlpExporterOptions options)
        : this(options, null, null)
    {
    }

    internal OtlpAwsSpanExporter(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator,
        Func<HttpMessageHandler>? transportFactory)
        : base(ConfigureOptions(options, authenticator, transportFactory))
    {
    }

    internal static OtlpExporterOptions ConfigureOptions(
        OtlpExporterOptions options,
        IAwsAuthenticator? authenticator = null,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        var headerSupplier = new AwsAuthHeaderSupplier(options.Endpoint.Host.Split('.')[1], "xray", authenticator);
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.HttpClientFactory = () => new HttpClient(
            new AwsAuthHttpHandler(headerSupplier, transportFactory?.Invoke() ?? new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds),
        };
        return options;
    }
}
