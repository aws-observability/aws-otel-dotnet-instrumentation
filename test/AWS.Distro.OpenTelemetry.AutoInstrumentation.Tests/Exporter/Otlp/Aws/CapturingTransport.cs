// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
internal sealed class CapturingTransport : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = new();

    public int SynchronousSendCount { get; private set; }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.SynchronousSendCount++;
        return this.Capture(request);
    }

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

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
internal sealed record CapturedRequest(
    Uri? Endpoint,
    HttpMethod Method,
    Dictionary<string, string> Headers,
    string? ContentType,
    string ContentEncoding,
    byte[] Payload,
    bool InstrumentationSuppressed);
