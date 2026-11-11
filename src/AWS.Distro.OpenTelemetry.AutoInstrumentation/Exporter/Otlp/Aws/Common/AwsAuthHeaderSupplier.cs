// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using Amazon.Runtime;
using Amazon.Runtime.Internal;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

/// <summary>
/// Supplies AWS Signature Version 4 headers for an OTLP HTTP request.
/// </summary>
internal sealed class AwsAuthHeaderSupplier
{
    // SigV4 header reference:
    // https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html
    private const string AuthorizationHeader = "Authorization";
    private const string XAmzDateHeader = "x-amz-date";
    private const string XAmzSecurityTokenHeader = "x-amz-security-token";
    private const string XAmzContentSha256Header = "x-amz-content-sha256";

    private readonly IClientConfig config;
    private readonly IAwsAuthenticator authenticator;

    public AwsAuthHeaderSupplier(IClientConfig config, IAwsAuthenticator? authenticator = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
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
        IRequest request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), this.config.AuthenticationServiceName)
        {
            HttpMethod = httpRequest.Method.Method,
            ContentStream = contentStream,
            Endpoint = endpoint,
            SignatureVersion = SignatureVersion.SigV4,
        };

        // Leave transport and custom OTLP headers on the outgoing request without signing
        // values that a proxy or HTTP handler may rewrite.
        foreach (var header in httpRequest.Headers)
        {
            if (ShouldIncludeInSignature(header.Key))
            {
                request.Headers.Add(header.Key, string.Join(",", header.Value));
            }
        }

        if (httpRequest.Content != null)
        {
            foreach (var header in httpRequest.Content.Headers)
            {
                if (ShouldIncludeInSignature(header.Key))
                {
                    request.Headers.Add(header.Key, string.Join(",", header.Value));
                }
            }
        }

        request.Headers["Host"] = httpRequest.Headers.Host ?? endpoint.Authority;

        cancellationToken.ThrowIfCancellationRequested();
#if NET
        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
#else
        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync().ConfigureAwait(false);
#endif
        cancellationToken.ThrowIfCancellationRequested();
        if (credentials.UseToken && credentials.Token != null)
        {
            request.Headers.Add(XAmzSecurityTokenHeader, credentials.Token);
        }

        // Resolve once per signing attempt and use the same snapshot for the token and signature.
        // Failures propagate to the exporter so an unsigned request is never sent.
        this.authenticator.Sign(request, this.config, credentials);
        request.Headers[AuthorizationHeader] = request.AWS4SignerResult.ForAuthorizationHeader;
        return new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsSigningHeader(string name) =>
        string.Equals(name, AuthorizationHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, XAmzDateHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, XAmzSecurityTokenHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, XAmzContentSha256Header, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldIncludeInSignature(string name) =>
        !IsSigningHeader(name)
        && (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase));

    private sealed class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
    {
    }
}
