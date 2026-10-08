// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws;

/// <summary>
/// Defines common assertions for SigV4 headers, exporter settings, compression, and repeated exports.
/// </summary>
/// <remarks>
/// Derived fixtures implement <see cref="CreateExpectedPayload"/>, <see cref="Export"/>, and <see cref="ValidateOtlpPayload"/>
/// to create and validate their signal's payload using the provided options, authenticator, and request spy.
/// </remarks>
/// <typeparam name="TExpectedPayload">The signal's expected OTLP payload model.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public abstract class BaseOtlpAwsExporterTest<TExpectedPayload>
{
    private readonly string region;
    private readonly string serviceName;
    private readonly ImmutableCredentials credentials = new("AKIDEXAMPLE", "test-secret", "session-token");
    private readonly List<byte[]> signedPayloads = new();

    protected BaseOtlpAwsExporterTest(Uri endpoint, string region, string serviceName)
    {
        this.region = region;
        this.serviceName = serviceName;
        this.Options = new OtlpExporterOptions
        {
            Endpoint = endpoint,
            Protocol = OtlpExportProtocol.HttpProtobuf,
            TimeoutMilliseconds = 1234,
            Headers = "x-test-header=test-value",
            HttpClientFactory = () => throw new InvalidOperationException("The AWS exporter must install its signing HTTP client factory."),
        };
        this.Authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(this.credentials);
        this.Authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                using var buffer = new MemoryStream();
                request.ContentStream.CopyTo(buffer);
                request.ContentStream.Position = 0;
                this.signedPayloads.Add(buffer.ToArray());
                new DefaultAwsAuthenticator().Sign(request, config, snapshot);
            });
    }

    protected OtlpExporterOptions Options { get; }

    protected Mock<IAwsAuthenticator> Authenticator { get; } = new();

    private protected RequestSpyingHttpHandler Transport { get; } = new();

    [Theory]
    [InlineData(OtlpExportProtocol.Grpc)]
    [InlineData((OtlpExportProtocol)byte.MaxValue)]
    public void ShouldRejectUnsupportedProtocolBeforeConfiguringExporter(OtlpExportProtocol protocol)
    {
        this.Options.Protocol = protocol;
        var originalHttpClientFactory = this.Options.HttpClientFactory;
        var expectedEndpoint = this.Options.Endpoint;
        var expectedTimeoutMilliseconds = this.Options.TimeoutMilliseconds;
        var expectedHeaders = this.Options.Headers;
        var expectedCompression = this.Options.Compression;
        var expectedPayload = this.CreateExpectedPayload();

        var exception = Assert.Throws<ArgumentException>(() => this.Export(expectedPayload));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("requires HTTP/protobuf", exception.Message);
        Assert.Equal(protocol, this.Options.Protocol);
        Assert.Same(originalHttpClientFactory, this.Options.HttpClientFactory);
        Assert.Equal(expectedEndpoint, this.Options.Endpoint);
        Assert.Equal(expectedTimeoutMilliseconds, this.Options.TimeoutMilliseconds);
        Assert.Equal(expectedHeaders, this.Options.Headers);
        Assert.Equal(expectedCompression, this.Options.Compression);
        Assert.Empty(this.Transport.Requests);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Never());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Never());
    }

    [Fact]
    public void ShouldDefaultCompressionToNone()
    {
        this.ValidateOtlpRequest(compression: null);
    }

    [Fact]
    public void ShouldAllowGzipCompression()
    {
        this.ValidateOtlpRequest(OtlpExportCompression.GZip);
    }

    [Theory]
    [InlineData(OtlpExportCompression.None)]
    [InlineData(OtlpExportCompression.GZip)]
    public void ShouldPreserveEachRequestAcrossMultipleExports(OtlpExportCompression compression)
    {
        const int exportCount = 10;
        this.Options.Compression = compression;
        var expectedCredentials = new List<ImmutableCredentials>();
        var expectedPayloads = new List<TExpectedPayload>();
        for (int i = 0; i < exportCount; i++)
        {
            var credentials = new ImmutableCredentials($"TESTKEY{i}", $"test-secret-{i}", $"session-token-{i}");
            expectedCredentials.Add(credentials);
            this.Authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
            var expectedPayload = this.CreateExpectedPayload();
            expectedPayloads.Add(expectedPayload);

            Assert.Equal(ExportResult.Success, this.Export(expectedPayload));
        }

        Assert.Equal(exportCount, this.Transport.Requests.Count);
        Assert.Equal(exportCount, this.Transport.SynchronousSendCount);
        Assert.Equal(exportCount, this.signedPayloads.Count);
        for (int i = 0; i < exportCount; i++)
        {
            this.ValidateOtlpRequest(this.Transport.Requests[i], compression, expectedCredentials[i], expectedPayloads[i], this.signedPayloads[i]);
            this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), expectedCredentials[i]), Times.Once());
        }

        Assert.Equal(exportCount, this.Transport.Requests.Select(request => request.Headers["Authorization"]).Distinct().Count());
        Assert.Equal(exportCount, this.Transport.Requests.Select(request => request.Headers["Authorization"].Split("Signature=")[1]).Distinct().Count());
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Exactly(exportCount));
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Exactly(exportCount));
    }

    [Fact]
    public void ShouldAddSigV4Headers()
    {
        var expected = this.SetSigningHeaders(0);

        Assert.Equal(ExportResult.Success, this.Export(this.CreateExpectedPayload()));

        var request = Assert.Single(this.Transport.Requests);
        Assert.Equal(expected.Authorization, request.Headers["Authorization"]);
        Assert.Equal(expected.Date, request.Headers["x-amz-date"]);
        Assert.Equal(expected.Token, request.Headers["x-amz-security-token"]);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Once());
    }

    [Fact]
    public void ShouldAddDifferentSigV4HeadersForEachExport()
    {
        for (int i = 0; i < 10; i++)
        {
            var expected = this.SetSigningHeaders(i);

            Assert.Equal(ExportResult.Success, this.Export(this.CreateExpectedPayload()));

            Assert.Equal(i + 1, this.Transport.Requests.Count);
            var request = this.Transport.Requests[i];
            Assert.Equal(expected.Authorization, request.Headers["Authorization"]);
            Assert.Equal(expected.Date, request.Headers["x-amz-date"]);
            Assert.Equal(expected.Token, request.Headers["x-amz-security-token"]);
        }

        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Exactly(10));
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Exactly(10));
    }

    [Fact]
    public void ShouldNotAddSigV4HeadersWhenCredentialRetrievalFails()
    {
        this.Authenticator.Setup(a => a.GetCredentialsAsync())
            .ThrowsAsync(new AmazonClientException("Credentials unavailable."));

        Assert.Equal(ExportResult.Failure, this.Export(this.CreateExpectedPayload()));

        Assert.Empty(this.Transport.Requests);
        Assert.False(Sdk.SuppressInstrumentation);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Never());
    }

    [Fact]
    public void ShouldNotAddSigV4HeadersWhenSigningFails()
    {
        this.SetSigningHeaders(0);
        this.Authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Throws(new AmazonClientException("Signing failed."));

        Assert.Equal(ExportResult.Failure, this.Export(this.CreateExpectedPayload()));

        Assert.Empty(this.Transport.Requests);
        Assert.False(Sdk.SuppressInstrumentation);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Once());
    }

    /// <summary>
    /// Creates an independent expected payload for an export.
    /// </summary>
    /// <returns>The expected signal model.</returns>
    protected abstract TExpectedPayload CreateExpectedPayload();

    /// <summary>
    /// Creates and exports a signal matching the expected payload.
    /// </summary>
    /// <param name="expectedPayload">The expected signal model, populated with any generated identifiers before export.</param>
    /// <returns>The exporter result.</returns>
    protected abstract ExportResult Export(TExpectedPayload expectedPayload);

    /// <summary>
    /// Validates the decoded OTLP payload against the signal and resource expectations for this export.
    /// </summary>
    /// <param name="payload">The delivered body after any decompression.</param>
    /// <param name="expectedPayload">The expected signal model supplied to the export.</param>
    protected abstract void ValidateOtlpPayload(byte[] payload, TExpectedPayload expectedPayload);

    private void ValidateOtlpRequest(OtlpExportCompression? compression)
    {
        var expectedEndpoint = this.Options.Endpoint;
        var expectedProtocol = this.Options.Protocol;
        var expectedTimeoutMilliseconds = this.Options.TimeoutMilliseconds;
        var expectedHeaders = this.Options.Headers;
        var originalHttpClientFactory = this.Options.HttpClientFactory;
        if (compression.HasValue)
        {
            this.Options.Compression = compression.Value;
        }

        var expectedPayload = this.CreateExpectedPayload();
        Assert.Equal(ExportResult.Success, this.Export(expectedPayload));

        var delivered = Assert.Single(this.Transport.Requests);
        Assert.Equal(1, this.Transport.SynchronousSendCount);
        Assert.Equal(expectedProtocol, this.Options.Protocol);
        Assert.NotSame(originalHttpClientFactory, this.Options.HttpClientFactory);
        Assert.Equal(expectedTimeoutMilliseconds, this.Options.TimeoutMilliseconds);
        Assert.Equal(expectedHeaders, this.Options.Headers);
        Assert.Equal(compression ?? OtlpExportCompression.None, this.Options.Compression);
        Assert.Equal(expectedEndpoint, this.Options.Endpoint);
        Assert.Equal(expectedEndpoint, delivered.Endpoint);

        this.ValidateOtlpRequest(delivered, compression ?? OtlpExportCompression.None, this.credentials, expectedPayload, Assert.Single(this.signedPayloads));
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
    }

    private void ValidateOtlpRequest(
        CapturedRequest delivered,
        OtlpExportCompression compression,
        ImmutableCredentials expectedCredentials,
        TExpectedPayload expectedPayload,
        byte[] signedPayload)
    {
        Assert.Equal(this.Options.Endpoint, delivered.Endpoint);
        Assert.Equal(HttpMethod.Post, delivered.Method);
        Assert.Equal("application/x-protobuf", delivered.ContentType);
        Assert.Equal(signedPayload, delivered.Payload);
        Assert.Equal(expectedCredentials.Token, delivered.Headers["x-amz-security-token"]);
        Assert.Equal("test-value", delivered.Headers["x-test-header"]);
        Assert.Contains($"Credential={expectedCredentials.AccessKey}/", delivered.Headers["Authorization"]);
        Assert.Contains($"/{this.region}/{this.serviceName}/aws4_request", delivered.Headers["Authorization"]);
        Assert.Contains("x-test-header", delivered.Headers["Authorization"]);
        Assert.Matches("Signature=[0-9a-f]{64}", delivered.Headers["Authorization"]);
        Assert.Equal(compression == OtlpExportCompression.GZip ? "gzip" : string.Empty, delivered.ContentEncoding);
        Assert.True(delivered.InstrumentationSuppressed);
        Assert.False(Sdk.SuppressInstrumentation);

        byte[] payload = delivered.Payload;
        if (compression == OtlpExportCompression.GZip)
        {
            using var buffer = new MemoryStream(payload);
            using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
            using var uncompressed = new MemoryStream();
            gzip.CopyTo(uncompressed);
            payload = uncompressed.ToArray();
        }

        this.ValidateOtlpPayload(payload, expectedPayload);
    }

    private (string Authorization, string Date, string Token) SetSigningHeaders(int index)
    {
        var credentials = new ImmutableCredentials("test_access_key", "test_secret_key", $"test_token{index}");
        var signedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index);
        var date = signedAt.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var signature = new byte[32];
        signature[0] = (byte)index;
        var signingResult = new AWS4SigningResult(
            credentials.AccessKey,
            signedAt,
            "host;x-amz-date;x-amz-security-token",
            $"20260101/{this.region}/{this.serviceName}/aws4_request",
            new byte[32],
            signature);
        this.Authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
        this.Authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                Assert.Same(credentials, snapshot);
                Assert.Equal(this.region, config.AuthenticationRegion);
                Assert.Equal(this.serviceName, config.AuthenticationServiceName);
                request.Headers["x-amz-date"] = date;
                request.AWS4SignerResult = signingResult;
            });
        return (signingResult.ForAuthorizationHeader, date, credentials.Token);
    }
}
