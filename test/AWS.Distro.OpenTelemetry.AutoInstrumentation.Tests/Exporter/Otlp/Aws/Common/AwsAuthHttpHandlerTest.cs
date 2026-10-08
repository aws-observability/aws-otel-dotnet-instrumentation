// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Traces;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Common;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsAuthHttpHandlerTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpstreamTraceExporterSignsTheDeliveredPayloadAndPreservesProviderResources(bool compress)
    {
        var credentials = new ImmutableCredentials("AKIDEXAMPLE", "test-secret", "session-token");
        byte[]? signedPayload = null;
        var authenticator = CreateAuthenticator(credentials, request =>
        {
            using var buffer = new MemoryStream();
            request.ContentStream.CopyTo(buffer);
            request.ContentStream.Position = 0;
            signedPayload = buffer.ToArray();
        });
        using var transport = new CapturingTransport();
        var options = CreateOptions(authenticator.Object, transport);
        options.Compression = compress ? OtlpExportCompression.GZip : OtlpExportCompression.None;
        options.Headers = "x-test-header=test-value";

        using (var source = new ActivitySource($"SigV4.Tests.{Guid.NewGuid()}"))
        {
            using var provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(source.Name)
                .SetSampler(new AlwaysOnSampler())
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("test-aws-service"))
                .AddProcessor(new SimpleActivityExportProcessor(new OtlpAwsSpanExporter(options, authenticator.Object, () => transport)))
                .Build();
            using var activity = source.StartActivity("test span");
            Assert.NotNull(activity);
            activity.Dispose();
        }

        var delivered = Assert.Single(transport.Requests);
        Assert.Equal(1, transport.SynchronousSendCount);
        Assert.Equal(options.Endpoint, delivered.Endpoint);
        Assert.Equal(HttpMethod.Post, delivered.Method);
        Assert.Equal("application/x-protobuf", delivered.ContentType);
        Assert.Equal(signedPayload, delivered.Payload);
        Assert.Equal("session-token", delivered.Headers["x-amz-security-token"]);
        Assert.Equal("test-value", delivered.Headers["x-test-header"]);
        Assert.Contains("/us-west-2/xray/aws4_request", delivered.Headers["Authorization"]);
        Assert.Contains("x-test-header", delivered.Headers["Authorization"]);
        Assert.Matches("Signature=[0-9a-f]{64}", delivered.Headers["Authorization"]);
        Assert.Equal(compress ? "gzip" : string.Empty, delivered.ContentEncoding);
        Assert.True(delivered.InstrumentationSuppressed);
        Assert.False(Sdk.SuppressInstrumentation);

        byte[] payload = delivered.Payload;
        if (compress)
        {
            using var buffer = new MemoryStream(payload);
            using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
            using var uncompressed = new MemoryStream();
            gzip.CopyTo(uncompressed);
            payload = uncompressed.ToArray();
        }

        var encodedPayload = Encoding.UTF8.GetString(payload);
        Assert.Contains("service.name", encodedPayload);
        Assert.Contains("test-aws-service", encodedPayload);
        Assert.Contains("test span", encodedPayload);
        authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothHttpPathsRefreshCredentialsAndRemoveStaleSigningHeaders(bool synchronous)
    {
        var first = new ImmutableCredentials("FIRSTKEY", "first-secret", "first-token");
        var second = new ImmutableCredentials("SECONDKEY", "second-secret", null);
        var authenticator = CreateAuthenticator(first);
        authenticator.SetupSequence(a => a.GetCredentialsAsync()).ReturnsAsync(first).ReturnsAsync(second);
        using var transport = new CapturingTransport();
        var options = CreateOptions(authenticator.Object, transport);
        using var client = options.HttpClientFactory();
        Assert.Equal(TimeSpan.FromMilliseconds(options.TimeoutMilliseconds), client.Timeout);

        for (int i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "stale-authorization");
            request.Headers.Add("x-amz-security-token", "stale-token");
            request.Headers.Add("x-amz-date", "stale-date");
            request.Headers.Add("x-amz-content-sha256", "stale-hash");
            using var response = synchronous ? client.Send(request) : await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(synchronous ? 2 : 0, transport.SynchronousSendCount);
        Assert.Contains("Credential=FIRSTKEY/", transport.Requests[0].Headers["Authorization"]);
        Assert.Equal("first-token", transport.Requests[0].Headers["x-amz-security-token"]);
        Assert.Contains("Credential=SECONDKEY/", transport.Requests[1].Headers["Authorization"]);
        Assert.False(transport.Requests[1].Headers.ContainsKey("x-amz-security-token"));
        Assert.NotEqual("stale-date", transport.Requests[1].Headers["x-amz-date"]);
        authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), first), Times.Once());
        authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), second), Times.Once());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpstreamTraceExporterReportsAuthenticationFailureWithoutSending(bool failSigning)
    {
        var authenticator = CreateAuthenticator(new ImmutableCredentials("AKIDEXAMPLE", "test-secret", null));
        if (failSigning)
        {
            authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
                .Throws(new AmazonClientException("Signing failed."));
        }
        else
        {
            authenticator.Setup(a => a.GetCredentialsAsync()).ThrowsAsync(new AmazonClientException("Credentials unavailable."));
        }

        using var transport = new CapturingTransport();
        var options = CreateOptions(authenticator.Object, transport);
        using var exporter = new OtlpAwsSpanExporter(options, authenticator.Object, () => transport);
        using var provider = Sdk.CreateTracerProviderBuilder().AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        var result = exporter.Export(default);

        Assert.Equal(ExportResult.Failure, result);
        Assert.Empty(transport.Requests);
        Assert.False(Sdk.SuppressInstrumentation);
    }

    [Fact]
    public async Task CancellationDuringCredentialLookupDoesNotSendAnUnsignedRequest()
    {
        var lookupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentials = new TaskCompletionSource<ImmutableCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync()).Returns(() =>
        {
            lookupStarted.SetResult(true);
            return credentials.Task;
        });
        using var transport = new CapturingTransport();
        var options = CreateOptions(authenticator.Object, transport);
        using var client = options.HttpClientFactory();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);

        var send = client.SendAsync(request, cancellation.Token);
        await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        credentials.SetResult(new ImmutableCredentials("AKIDEXAMPLE", "test-secret", null));

        Assert.Empty(transport.Requests);
        authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Never());
    }

    private static OtlpExporterOptions CreateOptions(IAwsAuthenticator authenticator, HttpMessageHandler transport)
    {
        var options = new OtlpExporterOptions
        {
            Endpoint = new Uri("https://xray.us-west-2.amazonaws.com/v1/traces"),
            TimeoutMilliseconds = 1234,
        };
        OtlpAwsSpanExporter.ConfigureOptions(options, authenticator, () => transport);
        return options;
    }

    private static Mock<IAwsAuthenticator> CreateAuthenticator(ImmutableCredentials credentials, Action<IRequest>? inspect = null)
    {
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
        authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                inspect?.Invoke(request);
                new DefaultAwsAuthenticator().Sign(request, config, snapshot);
            });
        return authenticator;
    }

    private sealed class CapturingTransport : HttpMessageHandler
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

    private sealed record CapturedRequest(
        Uri? Endpoint,
        HttpMethod Method,
        Dictionary<string, string> Headers,
        string? ContentType,
        string ContentEncoding,
        byte[] Payload,
        bool InstrumentationSuppressed);
}
