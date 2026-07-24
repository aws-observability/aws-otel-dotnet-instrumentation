// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.XRay;
using Moq;
using Moq.Protected;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsSpanExporterTest
{
    private const string XrayOtlpEndpoint = "https://xray.us-east-1.amazonaws.com/v1/traces";

    // Matches the private ServiceName in OtlpAwsSpanExporter; the SigV4 credential scope is derived from it.
    private const string XRayServiceName = "XRay";
    private const string AuthorizationHeader = "Authorization";
    private const string XAmzDateHeader = "X-Amz-Date";
    private const string XAmzContentSha256Header = "X-Amz-Content-Sha256";
    private const string ExpectedAuth =
        "AWS4-HMAC-SHA256 Credential=test_key/some_date/us-east-1/xray/aws4_request";

    private const string ExpectedAmzContentSha256 = "test_sha256";
    private const string ExpectedAmzDate = "test_date";

    private Dictionary<string, string> baseHeaders = new Dictionary<string, string>
    {
            { AuthorizationHeader, ExpectedAuth },
            { XAmzDateHeader, ExpectedAmzDate },
            { XAmzContentSha256Header, ExpectedAmzContentSha256 },
    };

    private TracerProvider tracerProvider = Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault())
        .Build();

    private OtlpExporterOptions options;

    public OtlpAwsSpanExporterTest()
    {
        this.options = new OtlpExporterOptions();
        this.options.Endpoint = new Uri(XrayOtlpEndpoint);
        this.options.TimeoutMilliseconds = 1000;
    }

    // Verifies that the exporter can successfully export a span request with sigv4 headers.
    [Fact]
    public void TestAwsSpanExporterInjectSigV4Headers()
    {
        MockAuthenticator mockAuth = new MockAuthenticator(
            new ImmutableCredentials(
            "test_key",
            "test_key1",
            "secret_token"),
            this.baseHeaders);

        BaseExporter<Activity> exporter = new OtlpAwsSpanExporter(this.options, this.tracerProvider.GetDefaultResource(), mockAuth);

        HttpResponseMessage response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(string.Empty),
        };

        (ExportResult Result, HttpRequestMessage? CapturedRequest) execute = SetupMockExporterAndCaptureRequest(exporter, response);
        int callCount = mockAuth.CallCount;

        // Verify the result
        Assert.Equal(ExportResult.Success, execute.Result);
        Assert.Equal(1, callCount);
        Assert.NotNull(execute.CapturedRequest);
        ValidateSigV4Headers(execute.CapturedRequest, ExpectedAuth + callCount, ExpectedAmzDate + callCount, ExpectedAmzContentSha256 + callCount);
    }

    // Verifies that the exporter can successfully export multiple span requests with different sigv4 headers.
    [Fact]
    public void TestAwsSpanExporterInjectsSigV4HeadersMultipleExports()
    {
        MockAuthenticator mockAuth = new MockAuthenticator(
            new ImmutableCredentials("test_key", "test_key1", "secret_token"),
            this.baseHeaders);

        BaseExporter<Activity> exporter = new OtlpAwsSpanExporter(this.options, this.tracerProvider.GetDefaultResource(), mockAuth);

        HttpResponseMessage response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(string.Empty),
        };

        for (int i = 1; i < 11; i += 1)
        {
            (ExportResult Result, HttpRequestMessage? CapturedRequest) execute = SetupMockExporterAndCaptureRequest(exporter, response);

            var callCount = mockAuth.CallCount;

            Assert.Equal(ExportResult.Success, execute.Result);
            Assert.Equal(i, callCount);
            Assert.NotNull(execute.CapturedRequest);
            ValidateSigV4Headers(execute.CapturedRequest, ExpectedAuth + callCount, ExpectedAmzDate + callCount, ExpectedAmzContentSha256 + callCount);
        }
    }

    // Verifies that if a unretryable status code occurs. The exporter sends the http request at most once.
    [Fact]
    public void TestAwsSpanExporterShouldNotRetryWithUnRetryableStatusCode()
    {
        HttpStatusCode[] unRetryableStatusCodes = [HttpStatusCode.RequestTimeout, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized];

        foreach (HttpStatusCode statusCode in unRetryableStatusCodes)
        {
            MockAuthenticator mockAuth = new MockAuthenticator(
            new ImmutableCredentials(
            "test_key",
            "test_key1",
            "secret_token"),
            this.baseHeaders);

            HttpResponseMessage response = new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(string.Empty),
            };

            BaseExporter<Activity> exporter = new OtlpAwsSpanExporter(this.options, this.tracerProvider.GetDefaultResource(), mockAuth);
            (ExportResult Result, HttpRequestMessage? CapturedRequest) execute = SetupMockExporterAndCaptureRequest(exporter, response);

            int callCount = mockAuth.CallCount;
            Assert.Equal(ExportResult.Failure, execute.Result);
            Assert.Equal(1, callCount);
            Assert.NotNull(execute.CapturedRequest);
            ValidateSigV4Headers(execute.CapturedRequest, ExpectedAuth + callCount, ExpectedAmzDate + callCount, ExpectedAmzContentSha256 + callCount);
        }
    }

    // Verifies that if a retryable status code occurs. The exporter retries the request [options.timeout / retryDelay] more times.
    // In this case, twice.
    [Fact]
    public void TestAwsSpanExporterShouldRetryWithRetryableStatusCode()
    {
        HttpStatusCode[] retryableStatusCodes = [HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

        foreach (HttpStatusCode statusCode in retryableStatusCodes)
        {
            MockAuthenticator mockAuth = new MockAuthenticator(
                new ImmutableCredentials(
                "test_key",
                "test_key1",
                "secret_token"),
                this.baseHeaders);

            HttpResponseMessage response = new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(string.Empty),
            };

            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(500));
            BaseExporter<Activity> exporter = new OtlpAwsSpanExporter(this.options, this.tracerProvider.GetDefaultResource(), mockAuth);

            (ExportResult Result, HttpRequestMessage? CapturedRequest) execute = SetupMockExporterAndCaptureRequest(exporter, response);

            int callCount = mockAuth.CallCount;
            Assert.Equal(ExportResult.Failure, execute.Result);
            Assert.Equal(2, callCount);
            Assert.NotNull(execute.CapturedRequest);
            ValidateSigV4Headers(execute.CapturedRequest, ExpectedAuth + callCount, ExpectedAmzDate + callCount, ExpectedAmzContentSha256 + callCount);
        }
    }

    // Regression test for the AWS SDK v4 migration: the exporter must sign with the SAME
    // ImmutableCredentials snapshot it resolved for the x-amz-security-token header. v4's
    // AWS4Signer.Sign(...BaseIdentity) re-resolves credentials internally, which could produce a
    // signature from a different snapshot than the token header if credentials rotate between the
    // two resolutions -> SignatureDoesNotMatch. The exporter resolves once (GetCredentialsAsync)
    // and passes that snapshot to Sign; this asserts the two see the same credentials.
    [Fact]
    public void TestAwsSpanExporterSignsWithSameCredentialsSnapshotAsTokenHeader()
    {
        var credentials = new ImmutableCredentials("test_key", "test_key1", "secret_token");
        RecordingCredentialsAuthenticator mockAuth = new RecordingCredentialsAuthenticator(credentials);

        BaseExporter<Activity> exporter = new OtlpAwsSpanExporter(this.options, this.tracerProvider.GetDefaultResource(), mockAuth);

        HttpResponseMessage response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(string.Empty),
        };

        (ExportResult Result, HttpRequestMessage? CapturedRequest) execute = SetupMockExporterAndCaptureRequest(exporter, response);

        Assert.Equal(ExportResult.Success, execute.Result);
        Assert.NotNull(mockAuth.ResolvedCredentials);
        Assert.NotNull(mockAuth.SignedCredentials);

        // The snapshot resolved for the token header and the snapshot handed to Sign must be identical.
        Assert.Same(mockAuth.ResolvedCredentials, mockAuth.SignedCredentials);
        Assert.Equal(credentials.AccessKey, mockAuth.SignedCredentials!.AccessKey);
        Assert.Equal(credentials.SecretKey, mockAuth.SignedCredentials!.SecretKey);
        Assert.Equal(credentials.Token, mockAuth.SignedCredentials!.Token);
    }

    // Exercises the REAL signing path (DefaultAwsAuthenticator.Sign -> AWS4Signer.SignRequest) rather
    // than a mock no-op. The other tests inject fake Authorization/X-Amz-* strings via mock authenticators,
    // so a genuine signing regression (wrong service/region, malformed header, or a throw from the v4
    // signer -> SignatureDoesNotMatch at runtime) would ship undetected. The signature itself is not
    // deterministic (X-Amz-Date is wall-clock), so we assert the signer produces a structurally valid
    // SigV4 Authorization header scoped to the xray service/region and populates AWS4SignerResult.
    [Fact]
    public void TestDefaultAwsAuthenticatorProducesValidSigV4Signature()
    {
        var authenticator = new DefaultAwsAuthenticator();
        var credentials = new ImmutableCredentials("AKIDEXAMPLE", "test_secret_key", null);

        IRequest request = new DefaultRequest(new EmptySigningRequest(), XRayServiceName)
        {
            HttpMethod = "POST",
            Endpoint = new Uri(XrayOtlpEndpoint),
            SignatureVersion = SignatureVersion.SigV4,
        };
        request.Headers.Add("Host", request.Endpoint.Host);
        request.Headers.Add("content-type", "application/x-protobuf");

        var config = new AmazonXRayConfig
        {
            AuthenticationRegion = "us-east-1",
            AuthenticationServiceName = XRayServiceName,
            UseHttp = false,
            ServiceURL = XrayOtlpEndpoint,
            RegionEndpoint = RegionEndpoint.GetBySystemName("us-east-1"),
        };

        authenticator.Sign(request, config, credentials);

        // The v4 signer must have run and stored its result.
        Assert.NotNull(request.AWS4SignerResult);
        Assert.True(request.Headers.ContainsKey(AuthorizationHeader));

        var authorization = request.Headers[AuthorizationHeader];

        // A well-formed SigV4 header, scoped to the xray service in us-east-1, with a 64-hex signature.
        // Service segment is matched case-insensitively since the signer canonicalizes the scope.
        Assert.StartsWith("AWS4-HMAC-SHA256 ", authorization);
        Assert.Contains("Credential=AKIDEXAMPLE/", authorization);
        Assert.Matches(@"/us-east-1/(xray|XRay)/aws4_request", authorization);
        Assert.Contains("SignedHeaders=", authorization);
        Assert.Matches("Signature=[0-9a-f]{64}", authorization);
    }

    // Verifies that if a signing or authentication error occurs. The exporter retries the request atleast one more.
    [Fact]
    public void TestAwsSpanExporterShouldRetryIfFailureToSignSigV4()
    {
        MockCounterAuthenticator[] mockAuths = [new MockThrowableSignerAuthenticator(), new MockThrowableCredentialsAuthenticator()];

        foreach (MockCounterAuthenticator mockAuth in mockAuths)
        {
            HttpResponseMessage response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(string.Empty),
            };
            this.options.TimeoutMilliseconds = 5000;
            BaseExporter<Activity> exporter = new OtlpAwsSpanExporter(this.options, this.tracerProvider.GetDefaultResource(), mockAuth);
            (ExportResult Result, HttpRequestMessage? CapturedRequest) execute = SetupMockExporterAndCaptureRequest(exporter, response);
            Assert.Equal(ExportResult.Failure, execute.Result);
            Assert.True(mockAuth.CallCount > 1); // The delay is random for exceptions. Hard to tell how times it retries.
            Assert.Null(execute.CapturedRequest);
        }

        this.options.TimeoutMilliseconds = 1000;
    }

    private static void ValidateSigV4Headers(HttpRequestMessage? capturedRequest, string expectedAuth, string expectedAmzDate, string expectedAmzContentSha256)
    {
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains(AuthorizationHeader));
        Assert.True(capturedRequest.Headers.Contains(XAmzDateHeader));
        Assert.True(capturedRequest.Headers.Contains(XAmzContentSha256Header));

        Assert.Equal(expectedAuth, capturedRequest.Headers.GetValues(AuthorizationHeader).First());
        Assert.Equal(expectedAmzDate, capturedRequest.Headers.GetValues(XAmzDateHeader).First());
        Assert.Equal(expectedAmzContentSha256, capturedRequest.Headers.GetValues(XAmzContentSha256Header).First());
    }

    private static (ExportResult Result, HttpRequestMessage? CapturedRequest) SetupMockExporterAndCaptureRequest(BaseExporter<Activity> exporter, HttpResponseMessage response)
    {
        HttpRequestMessage? capturedRequest = null;

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
        .Protected()
        .Setup<Task<HttpResponseMessage>>(
            "SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>())
        .Callback<HttpRequestMessage, CancellationToken>((request, token) => capturedRequest = request)
        .ReturnsAsync(response);

        var httpClientField = typeof(OtlpAwsSpanExporter).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance);
        var httpClient = new HttpClient(mockHttpMessageHandler.Object);
        httpClientField?.SetValue(exporter, httpClient);

        Batch<Activity> batch = default;
        var result = exporter.Export(batch);

        return (result, capturedRequest);
    }
}

// A Mock base abstact testing class used to track the number of times SigV4 signing was attempted to verify
// retry purposes.
internal abstract class MockCounterAuthenticator : IAwsAuthenticator
{
    public int CallCount { get; set; } = 0;

    public abstract Task<ImmutableCredentials> GetCredentialsAsync();

    public abstract void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials);
}

// A Mock authenticator that injects different SigV4 headers using the number of times an attempt was made for SigV4 signing.
internal class MockAuthenticator : MockCounterAuthenticator
{
    private ImmutableCredentials credentials;
    private Dictionary<string, string> customHeaders;

    internal MockAuthenticator(ImmutableCredentials credentials, Dictionary<string, string> customHeaders)
    {
        this.credentials = credentials;
        this.customHeaders = customHeaders;
    }

    public async override Task<ImmutableCredentials> GetCredentialsAsync()
    {
        this.CallCount += 1;
        return await Task.Run(() => this.credentials);
    }

    public override void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
    {
        foreach (string key in this.customHeaders.Keys)
        {
            request.Headers[key] = this.customHeaders[key] + this.CallCount;
        }
    }
}

// A Mock authenticator that throws when signing SigV4.
internal class MockThrowableSignerAuthenticator : MockCounterAuthenticator
{
    public override async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        this.CallCount += 1;
        return await Task.Run(() => new ImmutableCredentials("test_key", "test_key1", "test_tokens"));
    }

    public override void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
    {
        throw new AmazonClientException(string.Empty);
    }
}

// A Mock authenticator that throws when getting AWS credentials.
internal class MockThrowableCredentialsAuthenticator : MockCounterAuthenticator
{
    public override async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        this.CallCount += 1;
        return await Task.FromException<ImmutableCredentials>(new AmazonClientException(string.Empty));
    }

    public override void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
    {
        return;
    }
}

// A Mock authenticator that records the credentials snapshot returned from GetCredentialsAsync and
// the snapshot passed into Sign, so a test can assert the exporter uses the same instance for both.
internal class RecordingCredentialsAuthenticator : MockCounterAuthenticator
{
    private readonly ImmutableCredentials credentials;

    internal RecordingCredentialsAuthenticator(ImmutableCredentials credentials)
    {
        this.credentials = credentials;
    }

    public ImmutableCredentials? ResolvedCredentials { get; private set; }

    public ImmutableCredentials? SignedCredentials { get; private set; }

    public override async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        this.CallCount += 1;
        this.ResolvedCredentials = this.credentials;
        return await Task.Run(() => this.credentials);
    }

    public override void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
    {
        this.SignedCredentials = credentials;
    }
}

// Empty request used to build a DefaultRequest for the real-signing test, mirroring the exporter's
// private EmptyAmazonWebServiceRequest (which is not accessible from the test assembly).
internal class EmptySigningRequest : AmazonWebServiceRequest
{
}
