// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

/// <summary>
/// Supplies service-independent AWS SDK configuration for signing an explicit OTLP endpoint.
/// </summary>
internal sealed class AwsAuthConfig : ClientConfig
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AwsAuthConfig"/> class.
    /// </summary>
    /// <param name="endpoint">The OTLP endpoint.</param>
    /// <param name="region">The signing region.</param>
    /// <param name="serviceName">The SigV4 service name.</param>
    public AwsAuthConfig(Uri endpoint, string region, string serviceName)
        : base(new DefaultConfigurationProvider(new[] { new DefaultConfiguration { Name = DefaultConfigurationMode.Standard } }))
    {
        this.AuthenticationRegion = region;
        this.AuthenticationServiceName = serviceName;
        this.ServiceURL = endpoint.AbsoluteUri;
        this.UseHttp = endpoint.Scheme == Uri.UriSchemeHttp;
        this.DefaultConfigurationMode = DefaultConfigurationMode.Standard;
    }

    /// <inheritdoc/>
    public override string RegionEndpointServiceName => this.AuthenticationServiceName;

    // The SDK's service-client metadata is unused when signing upstream HTTP requests.

    /// <inheritdoc/>
    public override string ServiceVersion => string.Empty;

    /// <inheritdoc/>
    public override string UserAgent => string.Empty;

    /// <inheritdoc/>
    public override Endpoint DetermineServiceOperationEndpoint(ServiceOperationEndpointParameters parameters)
        => new(this.ServiceURL);
}
