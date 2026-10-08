// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using Moq;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Common;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsAuthHeaderSupplierTest
{
    [Fact]
    public async Task SuppliesXrayScopedHeadersForTheExactPayload()
    {
        var credentials = new ImmutableCredentials("AKIDEXAMPLE", "test-secret", "session-token");
        byte[] payload = { 0, 1, 127, 128, 255 };
        byte[]? signedPayload = null;
        var authenticator = CreateAuthenticator(credentials, (request, config, snapshot) =>
        {
            Assert.Same(credentials, snapshot);
            Assert.Equal("us-west-2", config.AuthenticationRegion);
            Assert.Equal("xray", config.AuthenticationServiceName);
            signedPayload = ReadPayload(request);
        });
        var supplier = new AwsAuthHeaderSupplier("us-west-2", "xray", authenticator.Object);
        using var httpRequest = CreateRequest(payload);
        httpRequest.Headers.Add("x-test-header", "test-value");

        var headers = await supplier.GetAsync(httpRequest);

        Assert.Equal(payload, signedPayload);
        Assert.Equal(payload, await httpRequest.Content!.ReadAsByteArrayAsync());
        Assert.Equal("session-token", headers["x-amz-security-token"]);
        Assert.Equal("application/x-protobuf", headers["content-type"]);
        Assert.Equal("test-value", headers["x-test-header"]);
        Assert.Contains("/us-west-2/xray/aws4_request", headers["Authorization"]);
        Assert.Contains("x-test-header", headers["Authorization"]);
        Assert.Matches("Signature=[0-9a-f]{64}", headers["Authorization"]);
        authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
    }

    [Fact]
    public async Task SignsAlreadyCompressedPayloadWithoutRecompressingIt()
    {
        byte[] payload = Encoding.UTF8.GetBytes("OTLP payload to compress");
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        byte[] compressedPayload = buffer.ToArray();
        byte[]? signedPayload = null;
        var authenticator = CreateAuthenticator(
            new ImmutableCredentials("AKIDEXAMPLE", "test-secret", null),
            (request, _, _) => signedPayload = ReadPayload(request));
        var supplier = new AwsAuthHeaderSupplier("us-west-2", "xray", authenticator.Object);
        using var httpRequest = CreateRequest(compressedPayload);
        httpRequest.Content!.Headers.ContentEncoding.Add("gzip");

        var headers = await supplier.GetAsync(httpRequest);

        Assert.Equal(compressedPayload, signedPayload);
        Assert.Equal(compressedPayload, await httpRequest.Content.ReadAsByteArrayAsync());
        Assert.Equal("gzip", headers["Content-Encoding"]);
        Assert.Contains("content-encoding", headers["Authorization"]);
    }

    [Fact]
    public async Task ReplacesStaleSigningHeadersWithFreshCredentials()
    {
        var first = new ImmutableCredentials("FIRSTKEY", "first-secret", "first-token");
        var second = new ImmutableCredentials("SECONDKEY", "second-secret", null);
        var authenticator = CreateAuthenticator(first);
        authenticator.SetupSequence(a => a.GetCredentialsAsync())
            .ReturnsAsync(first)
            .ReturnsAsync(second);
        var supplier = new AwsAuthHeaderSupplier("us-west-2", "xray", authenticator.Object);
        using var httpRequest = CreateRequest(new byte[] { 1, 2, 3 });
        httpRequest.Headers.TryAddWithoutValidation("Authorization", "stale-authorization");
        httpRequest.Headers.Add("x-amz-security-token", "stale-token");
        httpRequest.Headers.Add("x-amz-date", "stale-date");
        httpRequest.Headers.Add("x-amz-content-sha256", "stale-hash");

        var firstHeaders = await supplier.GetAsync(httpRequest);
        var secondHeaders = await supplier.GetAsync(httpRequest);

        Assert.Contains("Credential=FIRSTKEY/", firstHeaders["Authorization"]);
        Assert.Equal("first-token", firstHeaders["x-amz-security-token"]);
        Assert.Contains("Credential=SECONDKEY/", secondHeaders["Authorization"]);
        Assert.False(secondHeaders.ContainsKey("x-amz-security-token"));
        Assert.NotEqual("stale-date", secondHeaders["x-amz-date"]);
        authenticator.Verify(a => a.GetCredentialsAsync(), Times.Exactly(2));
        authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), first), Times.Once());
        authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), second), Times.Once());
    }

    [Fact]
    public async Task CredentialFailurePropagatesWithoutSigning()
    {
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync())
            .ThrowsAsync(new AmazonClientException("Credentials unavailable."));
        var supplier = new AwsAuthHeaderSupplier("us-west-2", "xray", authenticator.Object);
        using var httpRequest = CreateRequest(new byte[] { 1 });

        await Assert.ThrowsAsync<AmazonClientException>(() => supplier.GetAsync(httpRequest));

        authenticator.Verify(
            a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()),
            Times.Never());
    }

    private static HttpRequestMessage CreateRequest(byte[] payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, "https://xray.us-west-2.amazonaws.com/v1/traces")
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        return request;
    }

    private static Mock<IAwsAuthenticator> CreateAuthenticator(
        ImmutableCredentials credentials,
        Action<IRequest, IClientConfig, ImmutableCredentials>? inspect = null)
    {
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
        authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                inspect?.Invoke(request, config, snapshot);
                new DefaultAwsAuthenticator().Sign(request, config, snapshot);
            });
        return authenticator;
    }

    private static byte[] ReadPayload(IRequest request)
    {
        using var buffer = new MemoryStream();
        request.ContentStream.CopyTo(buffer);
        request.ContentStream.Position = 0;
        return buffer.ToArray();
    }
}
