// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Amazon.Runtime;
using Amazon.Runtime.Internal;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Wire-level coverage for <see cref="SigV4SigningHandler"/>.
///
/// These assert on the request that actually reaches the transport, not on which object was
/// constructed. Selection-layer tests can only show that a signing path was chosen; they leave the
/// resulting HTTP request inferred. Every case below therefore also asserts that a request arrived,
/// so a test where nothing is sent cannot pass vacuously.
///
/// Authorization is read with GetValues rather than a single-value accessor: a single-value read
/// would hide a duplicate header, which is exactly the defect this design has to avoid.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class SigV4SigningHandlerTest
{
    private const string MetricsEndpoint = "https://monitoring.us-east-1.amazonaws.com/v1/metrics";
    private const string ChinaMetricsEndpoint = "https://monitoring.cn-north-1.amazonaws.com.cn/v1/metrics";
    private const string AuthorizationHeader = "Authorization";
    private const string SecurityTokenHeader = "x-amz-security-token";

    [Fact]
    public async Task TestSignsRequestWithMonitoringCredentialScope()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", StaticCredentials());

        HttpResponseMessage response = await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HttpRequestMessage sent = Assert.Single(inner.Requests);

        string[] authorizations = sent.Headers.GetValues(AuthorizationHeader).ToArray();
        Assert.Single(authorizations);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", authorizations[0]);
        Assert.Contains("/us-east-1/monitoring/aws4_request", authorizations[0]);
        Assert.Matches(@"Signature=[0-9a-f]{64}", authorizations[0]);
        Assert.Contains("SignedHeaders=", authorizations[0]);
        Assert.True(sent.Headers.Contains("X-Amz-Date"));
    }

    [Fact]
    public async Task TestSignsChinaEndpointWithChinaRegionScope()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "cn-north-1", StaticCredentials());

        await client.PostAsync(ChinaMetricsEndpoint, EmptyProtobufContent());

        HttpRequestMessage sent = Assert.Single(inner.Requests);
        string authorization = sent.Headers.GetValues(AuthorizationHeader).Single();
        Assert.Contains("/cn-north-1/monitoring/aws4_request", authorization);
    }

    /// <summary>
    /// The signing service name is what distinguishes the three signals. Signing metrics as "xray"
    /// would produce a well-formed request that the service rejects, so this is asserted directly.
    /// </summary>
    [Theory]
    [InlineData("monitoring")]
    [InlineData("logs")]
    [InlineData("XRay")]
    public async Task TestCredentialScopeUsesTheConfiguredServiceName(string serviceName)
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, serviceName, "us-east-1", StaticCredentials());

        await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());

        string authorization = Assert.Single(inner.Requests).Headers.GetValues(AuthorizationHeader).Single();
        Assert.Contains($"/us-east-1/{serviceName}/aws4_request", authorization);
    }

    [Fact]
    public async Task TestAddsSecurityTokenForTemporaryCredentials()
    {
        var inner = new RecordingHandler();
        var temporary = new ImmutableCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "session-token-value");
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", temporary);

        await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());

        HttpRequestMessage sent = Assert.Single(inner.Requests);
        Assert.Equal("session-token-value", sent.Headers.GetValues(SecurityTokenHeader).Single());
    }

    [Fact]
    public async Task TestOmitsSecurityTokenForLongTermCredentials()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", StaticCredentials());

        await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());

        Assert.False(Assert.Single(inner.Requests).Headers.Contains(SecurityTokenHeader));
    }

    /// <summary>
    /// Fail closed. An unsigned request to a CloudWatch endpoint would produce a repeating 403 loop
    /// and could be mistaken for working role-based authentication, so nothing may reach the
    /// transport when credentials cannot be resolved.
    /// </summary>
    [Fact]
    public async Task TestCredentialFailureSendsNothing()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", new ThrowingAuthenticator());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(MetricsEndpoint, EmptyProtobufContent()));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task TestSigningFailureSendsNothing()
    {
        var inner = new RecordingHandler();
        var authenticator = new ThrowingSignerAuthenticator(StaticCredentials());
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", authenticator);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(MetricsEndpoint, EmptyProtobufContent()));

        Assert.Empty(inner.Requests);
    }

    /// <summary>
    /// Signing runs per attempt, which is what makes upstream retries and credential rotation work
    /// without any refresh logic of our own. Each export must carry its own signature.
    /// </summary>
    [Fact]
    public async Task TestEachExportIsSignedIndependently()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", StaticCredentials());

        await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());
        await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());

        Assert.Equal(2, inner.Requests.Count);
        foreach (HttpRequestMessage sent in inner.Requests)
        {
            Assert.Single(sent.Headers.GetValues(AuthorizationHeader));
            Assert.StartsWith("AWS4-HMAC-SHA256 ", sent.Headers.GetValues(AuthorizationHeader).Single());
        }
    }

    /// <summary>
    /// A different payload must produce a different signature, confirming the body is actually
    /// covered rather than only the headers.
    /// </summary>
    [Fact]
    public async Task TestSignatureCoversThePayload()
    {
        var inner = new RecordingHandler();
        var credentials = StaticCredentials();

        using HttpClient first = CreateClient(inner, "monitoring", "us-east-1", credentials);
        await first.PostAsync(MetricsEndpoint, ProtobufContent(new byte[] { 1, 2, 3 }));

        using HttpClient second = CreateClient(inner, "monitoring", "us-east-1", credentials);
        await second.PostAsync(MetricsEndpoint, ProtobufContent(new byte[] { 9, 9, 9 }));

        Assert.Equal(2, inner.Requests.Count);
        string firstAuth = inner.Requests[0].Headers.GetValues(AuthorizationHeader).Single();
        string secondAuth = inner.Requests[1].Headers.GetValues(AuthorizationHeader).Single();
        Assert.NotEqual(firstAuth, secondAuth);
    }

    /// <summary>
    /// Host is set by HttpClient from the request URI and content-type lives on the content. Both
    /// are part of the signed header set, but re-adding either to the request headers could create a
    /// duplicate that invalidates the signature.
    /// </summary>
    [Fact]
    public async Task TestDoesNotDuplicateHostOrContentType()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", StaticCredentials());

        await client.PostAsync(MetricsEndpoint, EmptyProtobufContent());

        HttpRequestMessage sent = Assert.Single(inner.Requests);

        // content-type stays a content header. HttpRequestMessage.Headers rejects it outright, so
        // copying the signed content-type into request headers would have thrown rather than
        // produced a duplicate.
        Assert.Equal("application/x-protobuf", sent.Content!.Headers.ContentType!.MediaType);

        // Host is left for HttpClient to derive from the request URI.
        Assert.Null(sent.Headers.Host);

        // Both are nonetheless covered by the signature.
        string authorization = sent.Headers.GetValues(AuthorizationHeader).Single();
        Assert.Contains("content-type", authorization);
        Assert.Contains("host", authorization);
    }

    /// <summary>
    /// When the content carries no content-type, none may appear in SignedHeaders. The header is
    /// deliberately not copied back onto the request, so signing a defaulted value would commit to
    /// a header the wire request never carries, and the service would reject the signature without
    /// saying why.
    /// </summary>
    [Fact]
    public async Task TestContentTypeIsNotSignedWhenAbsentFromTheContent()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", StaticCredentials());

        // No ContentType set on the content.
        await client.PostAsync(MetricsEndpoint, new ByteArrayContent(new byte[] { 1, 2, 3 }));

        HttpRequestMessage sent = Assert.Single(inner.Requests);
        string authorization = sent.Headers.GetValues(AuthorizationHeader).Single();

        Assert.DoesNotContain("content-type", authorization);
        Assert.Contains("host", authorization);
        Assert.Null(sent.Content!.Headers.ContentType);
    }

    /// <summary>
    /// The payload must survive signing intact; reading the body to sign it must not consume it.
    /// </summary>
    [Fact]
    public async Task TestPayloadReachesTheTransportUnchanged()
    {
        var inner = new RecordingHandler();
        using HttpClient client = CreateClient(inner, "monitoring", "us-east-1", StaticCredentials());
        byte[] payload = new byte[] { 10, 20, 30, 40 };

        await client.PostAsync(MetricsEndpoint, ProtobufContent(payload));

        Assert.Equal(payload, inner.Bodies.Single());
    }

    private static ImmutableCredentials StaticCredentials()
        => new ImmutableCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", null);

    private static ByteArrayContent EmptyProtobufContent() => ProtobufContent(Array.Empty<byte>());

    private static ByteArrayContent ProtobufContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        return content;
    }

    private static HttpClient CreateClient(RecordingHandler inner, string service, string region, ImmutableCredentials credentials)
        => CreateClient(inner, service, region, new FixedCredentialsAuthenticator(credentials));

    private static HttpClient CreateClient(RecordingHandler inner, string service, string region, IAwsAuthenticator authenticator)
    {
        var handler = new SigV4SigningHandler(service, region, authenticator) { InnerHandler = inner };
        return new HttpClient(handler);
    }

    /// <summary>
    /// Captures what reached the transport. Bodies are copied because the request is disposed once
    /// the client call returns.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

        internal List<byte[]> Bodies { get; } = new List<byte[]>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            this.Bodies.Add(request.Content == null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Supplies a fixed credential snapshot and signs with the production signer, so the assertions
    /// above run against a real SigV4 signature rather than a stubbed header.
    /// </summary>
    private sealed class FixedCredentialsAuthenticator : IAwsAuthenticator
    {
        private readonly ImmutableCredentials credentials;

        internal FixedCredentialsAuthenticator(ImmutableCredentials credentials)
        {
            this.credentials = credentials;
        }

        public Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(this.credentials);

        public void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
            => new DefaultAwsAuthenticator().Sign(request, config, credentials);
    }

    private sealed class ThrowingAuthenticator : IAwsAuthenticator
    {
        public Task<ImmutableCredentials> GetCredentialsAsync()
            => throw new AmazonServiceException("Unable to find credentials");

        public void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
            => throw new InvalidOperationException("Should not be reached");
    }

    private sealed class ThrowingSignerAuthenticator : IAwsAuthenticator
    {
        private readonly ImmutableCredentials credentials;

        internal ThrowingSignerAuthenticator(ImmutableCredentials credentials)
        {
            this.credentials = credentials;
        }

        public Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(this.credentials);

        public void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
            => throw new AmazonClientException("Signing failed");
    }
}
