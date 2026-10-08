// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.XRay;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Supplies AWS Signature Version 4 headers for an OTLP HTTP request.
/// </summary>
internal sealed class AwsAuthHeaderSupplier
{
    private readonly string region;
    private readonly string serviceName;
    private readonly IAwsAuthenticator authenticator;

    public AwsAuthHeaderSupplier(string region, string serviceName, IAwsAuthenticator? authenticator = null)
    {
        this.region = region;
        this.serviceName = serviceName;
        this.authenticator = authenticator ?? new DefaultAwsAuthenticator();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(HttpRequestMessage httpRequest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = httpRequest.RequestUri
            ?? throw new ArgumentException("The signing request must have an endpoint.", nameof(httpRequest));

        // Sign the actual HTTP body, including compression already performed by the exporter.
        // Keeping the payload local to each call avoids sharing mutable batch data between exports.
        byte[] payload = httpRequest.Content != null
#if NET
            ? await httpRequest.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
#else
            ? await httpRequest.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
#endif
            : Array.Empty<byte>();
        using var contentStream = new MemoryStream(payload, writable: false);
        IRequest request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), this.serviceName)
        {
            HttpMethod = httpRequest.Method.Method,
            ContentStream = contentStream,
            Endpoint = endpoint,
            SignatureVersion = SignatureVersion.SigV4,
        };

        foreach (var header in httpRequest.Headers)
        {
            if (!IsSigningHeader(header.Key))
            {
                request.Headers.Add(header.Key, string.Join(",", header.Value));
            }
        }

        if (httpRequest.Content != null)
        {
            foreach (var header in httpRequest.Content.Headers)
            {
                request.Headers.Add(header.Key, string.Join(",", header.Value));
            }
        }

        request.Headers["Host"] = httpRequest.Headers.Host ?? endpoint.Authority;

        var config = new AmazonXRayConfig
        {
            AuthenticationRegion = this.region,
            AuthenticationServiceName = this.serviceName,
            UseHttp = endpoint.Scheme == Uri.UriSchemeHttp,
            ServiceURL = endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(this.region),
        };

        cancellationToken.ThrowIfCancellationRequested();
#if NET
        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
#else
        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync().ConfigureAwait(false);
#endif
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials.UseToken && credentials.Token != null)
        {
            request.Headers.Add("x-amz-security-token", credentials.Token);
        }

        // Resolve once per signing attempt and use the same snapshot for the token and signature.
        // Failures propagate to the exporter so an unsigned request is never sent.
        this.authenticator.Sign(request, config, credentials);
        return new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsSigningHeader(string name) =>
        string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "x-amz-date", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "x-amz-security-token", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "x-amz-content-sha256", StringComparison.OrdinalIgnoreCase);

    private sealed class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
    {
    }
}
