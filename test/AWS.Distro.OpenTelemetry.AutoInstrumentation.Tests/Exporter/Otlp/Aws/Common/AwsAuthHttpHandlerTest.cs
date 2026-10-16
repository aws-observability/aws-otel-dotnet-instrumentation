// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Traces;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Common;

/// <summary>
/// Verifies request signing, credential refresh, and cancellation across HTTP delivery.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsAuthHttpHandlerTest
{
    [Theory]
    [InlineData(false, 1234)]
    [InlineData(true, 1234)]
    [InlineData(false, 2345)]
    [InlineData(true, 2345)]
    public async Task ShouldRefreshCredentialsAndRemoveStaleSigningHeaders(bool synchronous, int timeoutMilliseconds)
    {
        var first = new ImmutableCredentials("FIRSTKEY", "first-secret", "first-token");
        var second = new ImmutableCredentials("SECONDKEY", "second-secret", null);
        var authenticator = CreateAuthenticator(first);
        authenticator.SetupSequence(a => a.GetCredentialsAsync()).ReturnsAsync(first).ReturnsAsync(second);
        using var transport = new RequestSpyingHttpHandler();
        using var exporter = CreateExporter(authenticator.Object, transport, out var options, timeoutMilliseconds);
        using var client = options.HttpClientFactory();
        Assert.Equal(timeoutMilliseconds, options.TimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(timeoutMilliseconds), client.Timeout);

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
    public async Task ShouldPreserveUnsignedHeadersDuringDelivery(bool synchronous)
    {
        var authenticator = CreateAuthenticator(new ImmutableCredentials("AKIDEXAMPLE", "test-secret", "session-token"));
        using var transport = new RequestSpyingHttpHandler();
        using var exporter = CreateExporter(authenticator.Object, transport, out var options);
        using var client = options.HttpClientFactory();
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        request.Content.Headers.ContentLength = 3;
        request.Headers.UserAgent.ParseAdd("test-agent/1.0");
        request.Headers.Add("x-test-header", "custom-value");
        request.Headers.Add("x-aws-log-group", "/test/group");
        request.Headers.Add("x-aws-log-stream", "test-stream");
        request.Headers.Add("x-amz-test-header", "aws-value");

        using var response = synchronous ? client.Send(request) : await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivered = Assert.Single(transport.Requests);
        Assert.Equal("test-agent/1.0", delivered.Headers["User-Agent"]);
        Assert.Equal("custom-value", delivered.Headers["x-test-header"]);
        Assert.Equal("/test/group", delivered.Headers["x-aws-log-group"]);
        Assert.Equal("test-stream", delivered.Headers["x-aws-log-stream"]);
        Assert.Equal("aws-value", delivered.Headers["x-amz-test-header"]);
        Assert.Equal("application/x-protobuf", delivered.ContentType);
        Assert.Equal(3, request.Content.Headers.ContentLength);
        Assert.Contains(
            "SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token;x-amz-test-header,",
            delivered.Headers["Authorization"]);
    }

    [Fact]
    public async Task ShouldNotSendAnUnsignedRequestWhenCredentialLookupIsCancelled()
    {
        var lookupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentials = new TaskCompletionSource<ImmutableCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync()).Returns(() =>
        {
            lookupStarted.SetResult(true);
            return credentials.Task;
        });
        using var transport = new RequestSpyingHttpHandler();
        using var exporter = CreateExporter(authenticator.Object, transport, out var options);
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

    private static BaseExporter<Activity> CreateExporter(
        IAwsAuthenticator authenticator,
        HttpMessageHandler transport,
        out OtlpExporterOptions options,
        int timeoutMilliseconds = 1234)
    {
        options = new OtlpExporterOptions
        {
            Endpoint = new Uri("https://xray.us-west-2.amazonaws.com/v1/traces"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            TimeoutMilliseconds = timeoutMilliseconds,
        };
        return OtlpAwsSpanExporter.Create(options, authenticator, () => transport);
    }

    private static Mock<IAwsAuthenticator> CreateAuthenticator(ImmutableCredentials credentials)
    {
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
        authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                new DefaultAwsAuthenticator().Sign(request, config, snapshot);
            });
        return authenticator;
    }
}
