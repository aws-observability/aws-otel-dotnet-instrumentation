// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Traces;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public abstract class OtlpAwsExporterTest
{
    protected Mock<IAwsAuthenticator> Authenticator { get; } = new();

    private protected CapturingTransport Transport { get; } = new();

    [Fact]
    public void TestAwsExporterAddsSigV4Headers()
    {
        var expected = this.SetSigningHeaders(0);

        Assert.Equal(ExportResult.Success, this.Export());

        var request = Assert.Single(this.Transport.Requests);
        Assert.Equal(expected.Authorization, request.Headers["Authorization"]);
        Assert.Equal(expected.Date, request.Headers["x-amz-date"]);
        Assert.Equal(expected.Token, request.Headers["x-amz-security-token"]);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Once());
    }

    [Fact]
    public void TestAwsExporterExportCorrectlyAddsDifferentSigV4Headers()
    {
        for (int i = 0; i < 10; i++)
        {
            var expected = this.SetSigningHeaders(i);

            Assert.Equal(ExportResult.Success, this.Export());

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
    public void TestAwsExporterDoesNotAddSigV4HeadersIfFailureToRetrieveCredentials()
    {
        this.Authenticator.Setup(a => a.GetCredentialsAsync())
            .ThrowsAsync(new AmazonClientException("Credentials unavailable."));

        Assert.Equal(ExportResult.Failure, this.Export());

        Assert.Empty(this.Transport.Requests);
        Assert.False(Sdk.SuppressInstrumentation);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Never());
    }

    [Fact]
    public void TestAwsExporterDoesNotAddSigV4HeadersIfFailureToSignHeaders()
    {
        this.SetSigningHeaders(0);
        this.Authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Throws(new AmazonClientException("Signing failed."));

        Assert.Equal(ExportResult.Failure, this.Export());

        Assert.Empty(this.Transport.Requests);
        Assert.False(Sdk.SuppressInstrumentation);
        this.Authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
        this.Authenticator.Verify(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()), Times.Once());
    }

    protected abstract ExportResult Export();

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
            "20260101/us-west-2/xray/aws4_request",
            new byte[32],
            signature);
        this.Authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
        this.Authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                Assert.Same(credentials, snapshot);
                Assert.Equal("us-west-2", config.AuthenticationRegion);
                Assert.Equal("xray", config.AuthenticationServiceName);
                request.Headers["x-amz-date"] = date;
                request.AWS4SignerResult = signingResult;
            });
        return (signingResult.ForAuthorizationHeader, date, credentials.Token);
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsSpanExporterTest : OtlpAwsExporterTest, IDisposable
{
    private readonly OtlpAwsSpanExporter exporter;
    private readonly TracerProvider provider;

    public OtlpAwsSpanExporterTest()
    {
        this.exporter = new OtlpAwsSpanExporter(CreateOptions(), this.Authenticator.Object, () => this.Transport);
        this.provider = Sdk.CreateTracerProviderBuilder()
            .AddProcessor(new SimpleActivityExportProcessor(this.exporter))
            .Build();
    }

    [Fact]
    public void TestSpanExporterCompressionDefaultsToNone()
    {
        VerifyDeliveredPayload(compression: null);
    }

    [Fact]
    public void TestSpanExporterCompressionCanBeSetToGzip()
    {
        VerifyDeliveredPayload(OtlpExportCompression.GZip);
    }

    public void Dispose()
    {
        this.provider.Dispose();
        this.exporter.Dispose();
        this.Transport.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override ExportResult Export() => this.exporter.Export(default);

    private static OtlpExporterOptions CreateOptions() => new()
    {
        Endpoint = new Uri("https://xray.us-west-2.amazonaws.com/v1/traces"),
        TimeoutMilliseconds = 1234,
    };

    private static void VerifyDeliveredPayload(OtlpExportCompression? compression)
    {
        var credentials = new ImmutableCredentials("AKIDEXAMPLE", "test-secret", "session-token");
        byte[]? signedPayload = null;
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync()).ReturnsAsync(credentials);
        authenticator.Setup(a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()))
            .Callback<IRequest, IClientConfig, ImmutableCredentials>((request, config, snapshot) =>
            {
                using var buffer = new MemoryStream();
                request.ContentStream.CopyTo(buffer);
                request.ContentStream.Position = 0;
                signedPayload = buffer.ToArray();
                new DefaultAwsAuthenticator().Sign(request, config, snapshot);
            });
        using var transport = new CapturingTransport();
        var options = CreateOptions();
        if (compression.HasValue)
        {
            options.Compression = compression.Value;
        }

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
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.Protocol);
        Assert.Equal(compression ?? OtlpExportCompression.None, options.Compression);
        Assert.Equal(options.Endpoint, delivered.Endpoint);
        Assert.Equal(HttpMethod.Post, delivered.Method);
        Assert.Equal("application/x-protobuf", delivered.ContentType);
        Assert.Equal(signedPayload, delivered.Payload);
        Assert.Equal("session-token", delivered.Headers["x-amz-security-token"]);
        Assert.Equal("test-value", delivered.Headers["x-test-header"]);
        Assert.Contains("/us-west-2/xray/aws4_request", delivered.Headers["Authorization"]);
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

        var encodedPayload = Encoding.UTF8.GetString(payload);
        Assert.Contains("service.name", encodedPayload);
        Assert.Contains("test-aws-service", encodedPayload);
        Assert.Contains("test span", encodedPayload);
        authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
    }
}
