// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

#if !NETFRAMEWORK
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.XRay;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that adds AWS SigV4 authentication to an outbound OTLP/HTTP
/// request.
///
/// This signs requests made by an unmodified upstream OTLP exporter, rather than reimplementing the
/// exporter. Supplying an <see cref="System.Net.Http.HttpClient"/> built on this handler through
/// <c>OtlpExporterOptions.HttpClientFactory</c> leaves upstream in charge of serialization,
/// temporality, aggregation, compression, timeout, TLS and retry, so none of those behaviors can be
/// changed by accident. Verified against OpenTelemetry 1.16.0, where
/// <c>OtlpExporterOptionsExtensions.GetExportClient</c> resolves its transport through
/// <c>HttpClientFactory</c> for every signal.
///
/// Signing happens per HTTP attempt. An upstream retry therefore re-signs with a fresh timestamp,
/// and rotated credentials are picked up without any extra refresh logic.
///
/// The handler fails closed: if credentials cannot be resolved or signing throws, it raises an
/// exception instead of forwarding an unsigned request. Sending unsigned telemetry to a CloudWatch
/// endpoint would produce a repeating 403 loop and could mislead an operator into believing
/// role-based authentication was active.
/// </summary>
internal sealed class SigV4SigningHandler : DelegatingHandler
{
    private const string ContentTypeHeader = "content-type";
    private const string HostHeader = "Host";

    // Export attempts repeat on a fixed interval, so an unresolvable credential chain would
    // otherwise emit an identical error on every cycle.
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(1);

#pragma warning disable CS0436 // Type conflicts with imported type
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436 // Type conflicts with imported type
    private static readonly ILogger Logger = Factory.CreateLogger<SigV4SigningHandler>();

    private readonly string signingServiceName;
    private readonly string region;
    private readonly IAwsAuthenticator authenticator;
    private DateTime lastErrorLoggedUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SigV4SigningHandler"/> class.
    /// </summary>
    /// <param name="signingServiceName">SigV4 service name, for example "monitoring".</param>
    /// <param name="region">Signing region, derived from the endpoint by <see cref="AwsOtlpEndpoint.GetRegion"/>.</param>
    /// <param name="authenticator">Credential resolution and signing. Defaults to <see cref="DefaultAwsAuthenticator"/>; injected in tests.</param>
    internal SigV4SigningHandler(string signingServiceName, string region, IAwsAuthenticator? authenticator = null)
    {
        this.signingServiceName = signingServiceName;
        this.region = region;
        this.authenticator = authenticator ?? new DefaultAwsAuthenticator();
    }

    /// <summary>
    /// Signs the request before forwarding it.
    ///
    /// This override is as load-bearing as the async one: upstream's OTLP/HTTP export client calls
    /// the <b>synchronous</b> <c>HttpClient.Send</c> on every platform where it is supported
    /// (everywhere except Android, Browser and iOS) unless HTTP/2 is required, which OTLP/HTTP does
    /// not require. <see cref="DelegatingHandler.Send"/> forwards straight to the inner handler, so
    /// overriding only <c>SendAsync</c> let the real exporter send unsigned requests even though
    /// every handler test passed, because those tests drove the async path through
    /// <c>HttpClient.PostAsync</c>.
    /// </summary>
    /// <param name="request">The request to sign and send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the inner handler.</returns>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Blocking on the signing path is acceptable here: the caller is already synchronous, and
        // credential resolution is normally served from a cached provider.
        this.SignOrThrow(request);

        return base.Send(request, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await this.SignOrThrowAsync(request).ConfigureAwait(false);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void SignOrThrow(HttpRequestMessage request)
        => this.SignOrThrowAsync(request).GetAwaiter().GetResult();

    private async Task SignOrThrowAsync(HttpRequestMessage request)
    {
        try
        {
            await this.SignAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogSigningFailure(ex);

            // Fail closed. Never hand an unsigned request to the inner handler.
            throw new HttpRequestException(
                $"Failed to SigV4-sign the OTLP request for service '{this.signingServiceName}' in region '{this.region}'.", ex);
        }
    }

    private async Task SignAsync(HttpRequestMessage request)
    {
        Uri endpoint = request.RequestUri ?? throw new InvalidOperationException("OTLP request has no RequestUri to sign against.");

        // The body is already serialized (and already compressed, if compression is enabled) by the
        // time it reaches a message handler. Reading it here is what the signature is computed over.
        byte[] body = request.Content == null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        // Only ever sign a content-type that will actually be sent. The header lives on the content
        // and is deliberately not copied back onto the request, so defaulting a missing value would
        // put content-type into SignedHeaders while the wire request carried none, and the service
        // would reject the signature with no indication why. Upstream always sets it for OTLP/HTTP,
        // so in practice this is null only if that ever changes.
        string? contentType = request.Content?.Headers?.ContentType?.ToString();

        using var contentStream = new MemoryStream(body, writable: false);

        IRequest sigV4Request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), this.signingServiceName)
        {
            HttpMethod = request.Method.Method,
            ContentStream = contentStream,
            Endpoint = endpoint,
            SignatureVersion = SignatureVersion.SigV4,
        };

        // AmazonXRayConfig is used only as a carrier for the signing region, service name and URL.
        // Every field the signer reads is set explicitly below, so the X-Ray-specific defaults are
        // never consulted. This mirrors what the existing trace and log exporters already do.
        var config = new AmazonXRayConfig
        {
            AuthenticationRegion = this.region,
            AuthenticationServiceName = this.signingServiceName,
            UseHttp = false,
            ServiceURL = endpoint.AbsoluteUri,
            RegionEndpoint = RegionEndpoint.GetBySystemName(this.region),
        };

        ImmutableCredentials credentials = await this.authenticator.GetCredentialsAsync().ConfigureAwait(false);

        // Temporary credentials from STS require the session token as a header; the signing library
        // does not add it. It must come from the same snapshot used for the signature, or the two
        // disagree when credentials rotate mid-flight.
        if (credentials.UseToken && credentials.Token != null)
        {
            sigV4Request.Headers["x-amz-security-token"] = credentials.Token;
        }

        sigV4Request.Headers[HostHeader] = endpoint.Host;

        if (contentType != null)
        {
            sigV4Request.Headers[ContentTypeHeader] = contentType;
        }

        this.authenticator.Sign(sigV4Request, config, credentials);

        foreach (var header in sigV4Request.Headers)
        {
            // Host is managed by HttpClient from the request URI, and content-type already lives on
            // the content. Re-adding either here would risk a duplicate that invalidates the
            // signature; both are still part of the signed header set.
            if (string.Equals(header.Key, HostHeader, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, ContentTypeHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private void LogSigningFailure(Exception ex)
    {
        DateTime now = DateTime.UtcNow;
        if (now - this.lastErrorLoggedUtc < ErrorLogInterval)
        {
            return;
        }

        this.lastErrorLoggedUtc = now;

        // Message only. An exception from the credential chain or signer must never be allowed to
        // carry key material into logs.
        Logger.Log(
            LogLevel.Error,
            "Unable to SigV4-sign the OTLP request for service {0} in region {1}; the export will fail rather than send unsigned telemetry. Verify that AWS credentials are available to the process. Reason: {2}",
            this.signingServiceName,
            this.region,
            ex.Message);
    }

    private class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
    {
    }
}
#endif
