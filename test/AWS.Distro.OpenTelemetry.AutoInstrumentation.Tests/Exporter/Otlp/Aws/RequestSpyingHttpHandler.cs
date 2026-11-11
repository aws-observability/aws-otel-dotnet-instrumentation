// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws;

/// <summary>
/// Records outgoing HTTP requests and returns HTTP 200 without contacting an endpoint.
/// </summary>
/// <remarks>
/// Install this as the final handler in the HTTP pipeline to observe requests after serialization,
/// compression, and signing.
/// Each snapshot retains the headers and payload for assertions after the original request is disposed.
/// Both synchronous and asynchronous sends are recorded, including whether instrumentation was suppressed.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
internal sealed class RequestSpyingHttpHandler : HttpMessageHandler
{
    /// <summary>
    /// Gets the request snapshots in delivery order.
    /// </summary>
    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>
    /// Gets the number of requests delivered through synchronous HTTP sends.
    /// </summary>
    public int SynchronousSendCount { get; private set; }

    /// <inheritdoc/>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.SynchronousSendCount++;
        return this.Capture(request);
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(this.Capture(request));

    private HttpResponseMessage Capture(HttpRequestMessage request)
    {
        this.Requests.Add(new CapturedRequest(
            request.RequestUri,
            request.Method,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.MediaType,
            string.Join(",", request.Content?.Headers.ContentEncoding ?? Enumerable.Empty<string>()),
            request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? Array.Empty<byte>(),
            Sdk.SuppressInstrumentation));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    }
}

/// <summary>
/// A snapshot of a request observed at the end of the HTTP pipeline.
/// </summary>
/// <param name="Endpoint">The outgoing request URI.</param>
/// <param name="Method">The outgoing HTTP method.</param>
/// <param name="Headers">The request headers, including authentication headers.</param>
/// <param name="ContentType">The payload's media type.</param>
/// <param name="ContentEncoding">The payload's content encodings.</param>
/// <param name="Payload">The delivered body bytes, including any compression.</param>
/// <param name="InstrumentationSuppressed">Whether instrumentation was suppressed at delivery.</param>
internal sealed record CapturedRequest(
    Uri? Endpoint,
    HttpMethod Method,
    Dictionary<string, string> Headers,
    string? ContentType,
    string ContentEncoding,
    byte[] Payload,
    bool InstrumentationSuppressed);
