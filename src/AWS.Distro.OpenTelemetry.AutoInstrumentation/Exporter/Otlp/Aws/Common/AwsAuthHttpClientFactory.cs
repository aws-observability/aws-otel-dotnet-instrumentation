// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

/// <summary>
/// Creates HTTP clients with AWS request signing for OTLP exporters.
/// </summary>
internal sealed class AwsAuthHttpClientFactory
{
    private readonly OtlpExporterOptions options;
    private readonly AwsAuthHeaderSupplier headerSupplier;
    private readonly Func<HttpMessageHandler>? transportFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AwsAuthHttpClientFactory"/> class.
    /// </summary>
    /// <param name="options">The configured upstream exporter options.</param>
    /// <param name="headerSupplier">The supplier of AWS signing headers.</param>
    /// <param name="transportFactory">The HTTP transport factory, or null to use the default transport.</param>
    internal AwsAuthHttpClientFactory(
        OtlpExporterOptions options,
        AwsAuthHeaderSupplier headerSupplier,
        Func<HttpMessageHandler>? transportFactory = null)
    {
        this.options = options;
        this.headerSupplier = headerSupplier;
        this.transportFactory = transportFactory;
    }

    /// <summary>
    /// Creates an HTTP client with an <see cref="AwsAuthHttpHandler"/>.
    /// </summary>
    /// <returns>An HTTP client configured to sign AWS requests.</returns>
    internal HttpClient Create() => new(
        new AwsAuthHttpHandler(this.headerSupplier, this.transportFactory?.Invoke() ?? new HttpClientHandler()))
    {
        Timeout = TimeSpan.FromMilliseconds(this.options.TimeoutMilliseconds),
    };
}
