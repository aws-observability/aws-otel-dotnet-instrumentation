// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials.Internal;
using Amazon.Runtime.Internal;
using Amazon.Runtime.SharedInterfaces;
using Amazon.RuntimeDependencies;
using Amazon.Util;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using Moq;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Common;

// Provider reference: https://docs.aws.amazon.com/sdkref/latest/guide/standardized-credentials.html
/// <summary>
/// Validates SigV4 service scopes, payload signing, refreshed credentials, and AWS credential providers.
/// </summary>
[Collection("AWS credential providers")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsAuthHeaderSupplierTest
{
    private static readonly DateTime Expiration = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("us-west-2", "xray")]
    [InlineData("us-east-1", "logs")]
    public async Task ShouldSupplyServiceScopedHeadersForTheExactPayload(string region, string serviceName)
    {
        var config = CreateConfig(region, serviceName);
        var credentials = new ImmutableCredentials("AKIDEXAMPLE", "test-secret", "session-token");
        byte[] payload = { 0, 1, 127, 128, 255 };
        byte[]? signedPayload = null;
        var authenticator = CreateAuthenticator(credentials, (request, signingConfig, snapshot) =>
        {
            Assert.Same(config, signingConfig);
            Assert.Same(credentials, snapshot);
            Assert.Equal(region, signingConfig.AuthenticationRegion);
            Assert.Equal(serviceName, signingConfig.AuthenticationServiceName);
            Assert.Equal(serviceName, request.ServiceName);
            Assert.False(request.Headers.ContainsKey("Content-Length"));
            Assert.False(request.Headers.ContainsKey("User-Agent"));
            Assert.False(request.Headers.ContainsKey("x-test-header"));
            signedPayload = ReadPayload(request);
        });
        var supplier = new AwsAuthHeaderSupplier(config, authenticator.Object);
        using var httpRequest = CreateRequest(payload);
        httpRequest.Headers.Add("x-test-header", "test-value");
        httpRequest.Headers.UserAgent.ParseAdd("test-agent/1.0");
        httpRequest.Content!.Headers.ContentLength = payload.Length;

        var headers = await supplier.GetAsync(httpRequest);

        Assert.Equal(payload, signedPayload);
        Assert.Equal(payload, await httpRequest.Content!.ReadAsByteArrayAsync());
        Assert.Equal("session-token", headers["x-amz-security-token"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), headers["x-amz-content-sha256"]);
        Assert.Equal("application/x-protobuf", headers["content-type"]);
        Assert.False(headers.ContainsKey("x-test-header"));
        Assert.Equal("test-value", Assert.Single(httpRequest.Headers.GetValues("x-test-header")));
        Assert.Equal("test-agent/1.0", httpRequest.Headers.UserAgent.ToString());
        Assert.Equal(payload.Length, httpRequest.Content.Headers.ContentLength);
        Assert.Contains($"/{region}/{serviceName}/aws4_request", headers["Authorization"]);
        Assert.Contains(
            "SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token,",
            headers["Authorization"]);
        Assert.Matches("Signature=[0-9a-f]{64}", headers["Authorization"]);
        authenticator.Verify(a => a.GetCredentialsAsync(), Times.Once());
    }

    [Theory]
    [InlineData("x-amz-test-header")]
    [InlineData("X-AmZ-Test-Header")]
    public async Task ShouldSignAwsHeadersRegardlessOfCasing(string headerName)
    {
        var authenticator = CreateAuthenticator(new ImmutableCredentials("AKIDEXAMPLE", "test-secret", null));
        var supplier = new AwsAuthHeaderSupplier(CreateConfig(), authenticator.Object);
        using var httpRequest = CreateRequest();
        httpRequest.Headers.Add(headerName, "aws-value");

        var headers = await supplier.GetAsync(httpRequest);

        Assert.Equal("aws-value", headers[headerName]);
        Assert.Contains(
            "SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-test-header,",
            headers["Authorization"]);
        Assert.Equal("aws-value", Assert.Single(httpRequest.Headers.GetValues(headerName)));
    }

    [Fact]
    public async Task ShouldSignAlreadyCompressedPayloadWithoutRecompressingIt()
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
        var supplier = new AwsAuthHeaderSupplier(CreateConfig(), authenticator.Object);
        using var httpRequest = CreateRequest(compressedPayload);
        httpRequest.Content!.Headers.ContentEncoding.Add("gzip");

        var headers = await supplier.GetAsync(httpRequest);

        Assert.Equal(compressedPayload, signedPayload);
        Assert.Equal(compressedPayload, await httpRequest.Content.ReadAsByteArrayAsync());
        Assert.Equal("gzip", Assert.Single(httpRequest.Content.Headers.ContentEncoding));
        Assert.False(headers.ContainsKey("Content-Encoding"));
        Assert.DoesNotContain("content-encoding", headers["Authorization"]);
    }

    [Fact]
    public async Task ShouldReplaceStaleSigningHeadersWithFreshCredentials()
    {
        var first = new ImmutableCredentials("FIRSTKEY", "first-secret", "first-token");
        var second = new ImmutableCredentials("SECONDKEY", "second-secret", null);
        var authenticator = CreateAuthenticator(first);
        authenticator.SetupSequence(a => a.GetCredentialsAsync())
            .ReturnsAsync(first)
            .ReturnsAsync(second);
        var supplier = new AwsAuthHeaderSupplier(CreateConfig(), authenticator.Object);
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
    public async Task ShouldPropagateCredentialFailureWithoutSigning()
    {
        var authenticator = new Mock<IAwsAuthenticator>();
        authenticator.Setup(a => a.GetCredentialsAsync())
            .ThrowsAsync(new AmazonClientException("Credentials unavailable."));
        var supplier = new AwsAuthHeaderSupplier(CreateConfig(), authenticator.Object);
        using var httpRequest = CreateRequest(new byte[] { 1 });

        await Assert.ThrowsAsync<AmazonClientException>(() => supplier.GetAsync(httpRequest));

        authenticator.Verify(
            a => a.Sign(It.IsAny<IRequest>(), It.IsAny<IClientConfig>(), It.IsAny<ImmutableCredentials>()),
            Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldResolveEnvironmentCredentials(bool useSessionToken)
    {
        using var environment = new AwsCredentialProviderHelper();
        AwsCredentialProviderHelper.Set("AWS_ACCESS_KEY_ID", environment.Credentials.AccessKey);
        AwsCredentialProviderHelper.Set("AWS_SECRET_ACCESS_KEY", environment.Credentials.SecretKey);
        AwsCredentialProviderHelper.Set("AWS_SESSION_TOKEN", useSessionToken ? environment.Credentials.Token : null);

        await ValidateHeaders<EnvironmentVariablesAWSCredentials>(environment, useSessionToken);

        Assert.Empty(environment.Requests);
    }

    [Theory]
    [InlineData("default", false, false)]
    [InlineData("named-profile", true, false)]
    [InlineData("sdk-profile", true, true)]
    public async Task ShouldResolveSharedCredentialsProfiles(string profile, bool useSessionToken, bool selectThroughSdk)
    {
        using var environment = new AwsCredentialProviderHelper();
        environment.WriteCredentials($"""
            [{profile}]
            aws_access_key_id = {environment.Credentials.AccessKey}
            aws_secret_access_key = {environment.Credentials.SecretKey}
            {(useSessionToken ? $"aws_session_token = {environment.Credentials.Token}" : string.Empty)}
            """);
        if (selectThroughSdk)
        {
            AWSConfigs.AWSProfileName = profile;
        }
        else if (profile != "default")
        {
            AwsCredentialProviderHelper.Set("AWS_PROFILE", profile);
        }

        if (useSessionToken)
        {
            await ValidateHeaders<SessionAWSCredentials>(environment);
        }
        else
        {
            await ValidateHeaders<BasicAWSCredentials>(environment, useSessionToken: false);
        }

        Assert.Empty(environment.Requests);
    }

    [Fact]
    public async Task ShouldResolveEksWebIdentityCredentials()
    {
        using var environment = new AwsCredentialProviderHelper();
        string tokenFile = Path.Combine(environment.DirectoryPath, "web-identity-token");
        File.WriteAllText(tokenFile, "test-web-identity-token");
        const string roleArn = "arn:aws:iam::111122223333:role/test-web-identity-role";
        AwsCredentialProviderHelper.Set("AWS_WEB_IDENTITY_TOKEN_FILE", tokenFile);
        AwsCredentialProviderHelper.Set("AWS_ROLE_ARN", roleArn);
        AwsCredentialProviderHelper.Set("AWS_ROLE_SESSION_NAME", "test-web-identity-session");
        var sts = new Mock<ICoreAmazonSTS>(MockBehavior.Strict);
        sts.Setup(client => client.CredentialsFromAssumeRoleWithWebIdentityAuthenticationAsync(
                "test-web-identity-token",
                roleArn,
                "test-web-identity-session",
                It.IsAny<AssumeRoleWithWebIdentityCredentialsOptions>()))
            .ReturnsAsync(CreateRoleCredentials(environment));
        environment.RegisterSts(sts.Object);

        await ValidateHeaders<AssumeRoleWithWebIdentityCredentials>(environment);

        sts.Verify(
            client => client.CredentialsFromAssumeRoleWithWebIdentityAuthenticationAsync(
                "test-web-identity-token",
                roleArn,
                "test-web-identity-session",
                It.IsAny<AssumeRoleWithWebIdentityCredentialsOptions>()),
            Times.Once());
        sts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ShouldResolveAssumeRoleCredentialsFromASourceProfile()
    {
        using var environment = new AwsCredentialProviderHelper();
        const string roleArn = "arn:aws:iam::111122223333:role/test-assumed-role";
        environment.WriteCredentials("""
            [source]
            aws_access_key_id = SOURCEACCESSKEY
            aws_secret_access_key = source-test-secret
            """);
        environment.WriteConfig($"""
            [profile assume-role]
            role_arn = {roleArn}
            source_profile = source
            role_session_name = test-role-session
            """);
        AwsCredentialProviderHelper.Set("AWS_PROFILE", "assume-role");
        var sts = new Mock<ICoreAmazonSTS>(MockBehavior.Strict);
        sts.Setup(client => client.CredentialsFromAssumeRoleAuthenticationAsync(
                roleArn,
                "test-role-session",
                It.IsAny<AssumeRoleAWSCredentialsOptions>()))
            .ReturnsAsync(CreateRoleCredentials(environment));
        environment.RegisterSts(sts.Object);

        await ValidateHeaders<AssumeRoleAWSCredentials>(environment);

        sts.Verify(
            client => client.CredentialsFromAssumeRoleAuthenticationAsync(
                roleArn,
                "test-role-session",
                It.IsAny<AssumeRoleAWSCredentialsOptions>()),
            Times.Once());
        sts.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ecs")]
    [InlineData("eks")]
    [InlineData("lambda-snapstart")]
    public async Task ShouldResolveContainerCredentials(string hostEnvironment)
    {
        using var environment = new AwsCredentialProviderHelper();
        Uri expectedEndpoint;
        string? expectedAuthorization = null;
        if (hostEnvironment == "ecs")
        {
            AwsCredentialProviderHelper.Set("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "/test-task");
            expectedEndpoint = new Uri("http://169.254.170.2/test-task");
        }
        else if (hostEnvironment == "eks")
        {
            expectedEndpoint = new Uri("http://169.254.170.23/test-pod");
            expectedAuthorization = "test-pod-authorization";
            string tokenFile = Path.Combine(environment.DirectoryPath, "pod-authorization-token");
            File.WriteAllText(tokenFile, expectedAuthorization);
            AwsCredentialProviderHelper.Set("AWS_CONTAINER_CREDENTIALS_FULL_URI", expectedEndpoint.AbsoluteUri);
            AwsCredentialProviderHelper.Set("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE", tokenFile);
        }
        else
        {
            expectedEndpoint = new Uri("http://127.0.0.1/test-snapstart");
            expectedAuthorization = "test-snapstart-authorization";
            AwsCredentialProviderHelper.Set("AWS_CONTAINER_CREDENTIALS_FULL_URI", expectedEndpoint.AbsoluteUri);
            AwsCredentialProviderHelper.Set("AWS_CONTAINER_AUTHORIZATION_TOKEN", expectedAuthorization);
        }

        await ValidateHeaders<GenericContainerCredentials>(environment);

        var request = Assert.Single(environment.Requests);
        Assert.Equal(expectedEndpoint, environment.ContainerEndpoint);
        Assert.Equal(new Uri(environment.MetadataEndpoint, expectedEndpoint.PathAndQuery), request.Uri);
        Assert.Equal("GET", request.Method);
        if (expectedAuthorization == null)
        {
            Assert.False(request.Headers.ContainsKey("Authorization"));
        }
        else
        {
            Assert.Equal(expectedAuthorization, request.Headers["Authorization"]);
        }
    }

    [Fact]
    public async Task ShouldResolveCachedEc2InstanceProfileCredentialsUsingImdsv2()
    {
        using var environment = new AwsCredentialProviderHelper();
        AwsCredentialProviderHelper.Set("AWS_EC2_METADATA_DISABLED", "false");
        AwsCredentialProviderHelper.Set("AWS_EC2_METADATA_V1_DISABLED", "true");
        AwsCredentialProviderHelper.Set("AWS_EC2_METADATA_SERVICE_ENDPOINT", environment.MetadataEndpoint.GetLeftPart(UriPartial.Authority));
        AwsCredentialProviderHelper.Reset();

        var provider = environment.ResolveProvider();

        // AWSSDK.Core 4.0.3.3 holds a thread-affine lock across await on a cold IMDS lookup.
        // Populate its cache through the synchronous SDK lookup before testing async header resolution.
        Assert.Equal(environment.Credentials.AccessKey, provider.GetCredentials().AccessKey);
        await ValidateHeaders(environment, "DefaultInstanceProfileAWSCredentials");

        Assert.Contains(environment.Requests, request => request.Method == "PUT" && request.Uri.AbsolutePath == "/latest/api/token");
        var metadataRequests = environment.Requests.Where(request => request.Method == "GET").ToArray();
        Assert.NotEmpty(metadataRequests);
        Assert.All(metadataRequests, request =>
        {
            Assert.Equal("127.0.0.1", request.Uri.Host);
            Assert.Equal("test-imds-token", request.Headers["x-aws-ec2-metadata-token"]);
        });
        Assert.Contains(metadataRequests, request => request.Uri.AbsolutePath == "/latest/meta-data/iam/security-credentials/test-role");
    }

    [Fact]
    public async Task ShouldResolveCredentialsFromAnExternalProcess()
    {
        using var environment = new AwsCredentialProviderHelper();
        string responseFile = Path.Combine(environment.DirectoryPath, "process-credentials.json");
        File.WriteAllText(responseFile, JsonSerializer.Serialize(new
        {
            Version = 1,
            AccessKeyId = environment.Credentials.AccessKey,
            SecretAccessKey = environment.Credentials.SecretKey,
            SessionToken = environment.Credentials.Token,
            Expiration = "2099-01-01T00:00:00Z",
        }));
        string command = OperatingSystem.IsWindows() ? $"cmd.exe /c type \"{responseFile}\"" : $"cat \"{responseFile}\"";
        environment.WriteConfig($"""
            [profile process]
            credential_process = {command}
            """);
        AwsCredentialProviderHelper.Set("AWS_PROFILE", "process");

        await ValidateHeaders<ProcessAWSCredentials>(environment);

        Assert.Empty(environment.Requests);
    }

    [Fact]
    public async Task ShouldResolveConfiguredIamIdentityCenterCredentialsFromACachedSsoToken()
    {
        using var environment = new AwsCredentialProviderHelper();
        const string startUrl = "https://adot-test.awsapps.com/start";
        environment.WriteConfig($"""
            [profile sso]
            sso_account_id = 111122223333
            sso_role_name = test-role
            sso_region = us-west-2
            sso_start_url = {startUrl}
            """);
        AwsCredentialProviderHelper.Set("AWS_PROFILE", "sso");
        string cacheFile = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(startUrl))).ToLowerInvariant() + ".json";
        File.WriteAllText(Path.Combine(environment.TokenCacheDirectory, cacheFile), JsonSerializer.Serialize(new
        {
            startUrl,
            region = "us-west-2",
            accessToken = "test-sso-access-token",
            expiresAt = "2099-01-01T00:00:00Z",
        }));
        var sso = new Mock<ICoreAmazonSSO>(MockBehavior.Strict);
        var oidc = new Mock<ICoreAmazonSSOOIDC>(MockBehavior.Strict);
        sso.Setup(client => client.CredentialsFromSsoAccessTokenAsync(
                "111122223333",
                "test-role",
                "test-sso-access-token",
                It.IsAny<IDictionary<string, object>>()))
            .ReturnsAsync(new SSOImmutableCredentials(
                environment.Credentials.AccessKey, environment.Credentials.SecretKey, environment.Credentials.Token, Expiration));
        environment.RegisterSso(sso.Object, oidc.Object);
        environment.ConfigureProfileProvider("sso");

        await ValidateHeaders<SSOAWSCredentials>(environment);

        sso.Verify(
            client => client.CredentialsFromSsoAccessTokenAsync(
                "111122223333",
                "test-role",
                "test-sso-access-token",
                It.IsAny<IDictionary<string, object>>()),
            Times.Once());
        sso.VerifyNoOtherCalls();
        oidc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ShouldResolveCachedAwsLoginCredentials()
    {
        using var environment = new AwsCredentialProviderHelper();
        const string loginSession = "arn:aws:iam::111122223333:user/test-login";
        environment.WriteConfig($"""
            [profile login]
            login_session = {loginSession}
            region = us-west-2
            """);
        AwsCredentialProviderHelper.Set("AWS_PROFILE", "login");
        string cacheFile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loginSession))).ToLowerInvariant() + ".json";
        File.WriteAllText(Path.Combine(environment.TokenCacheDirectory, cacheFile), JsonSerializer.Serialize(new
        {
            accessToken = new
            {
                accessKeyId = environment.Credentials.AccessKey,
                secretAccessKey = environment.Credentials.SecretKey,
                sessionToken = environment.Credentials.Token,
                accountId = "111122223333",
                expiresAt = "2099-01-01T00:00:00Z",
            },
            tokenType = "aws_sigv4",
            idToken = "test-id-token",
            refreshToken = "test-refresh-token",
            clientId = "arn:aws:signin:::devtools/same-device",
        }));
        var signin = new Mock<ICoreAmazonSignin>(MockBehavior.Strict);
        environment.RegisterSignin(signin.Object);

        await ValidateHeaders<LoginAWSCredentials>(environment);

        signin.VerifyNoOtherCalls();
        Assert.Empty(environment.Requests);
    }

    [Fact]
    public async Task ShouldFailWhenNoCredentialProviderIsAvailable()
    {
        using var environment = new AwsCredentialProviderHelper();
        var supplier = new AwsAuthHeaderSupplier(CreateConfig());
        using var request = CreateRequest();

        await Assert.ThrowsAsync<AmazonServiceException>(() => supplier.GetAsync(request));

        Assert.Empty(environment.Requests);
    }

    private static IClientConfig CreateConfig(string region = "us-west-2", string serviceName = "test-service")
    {
        var config = new Mock<IClientConfig>();
        config.SetupGet(c => c.AuthenticationRegion).Returns(region);
        config.SetupGet(c => c.AuthenticationServiceName).Returns(serviceName);
        config.SetupGet(c => c.RegionEndpointServiceName).Returns(serviceName);
        config.SetupGet(c => c.RegionEndpoint).Returns(RegionEndpoint.GetBySystemName(region));
        return config.Object;
    }

    private static HttpRequestMessage CreateRequest() => CreateRequest(new byte[] { 1, 2, 3 });

    private static HttpRequestMessage CreateRequest(byte[] payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, "https://example.com/otlp")
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

    private static AssumeRoleImmutableCredentials CreateRoleCredentials(AwsCredentialProviderHelper environment) =>
        new(environment.Credentials.AccessKey, environment.Credentials.SecretKey, environment.Credentials.Token, Expiration);

    private static byte[] ReadPayload(IRequest request)
    {
        using var buffer = new MemoryStream();
        request.ContentStream.CopyTo(buffer);
        request.ContentStream.Position = 0;
        return buffer.ToArray();
    }

    private static Task ValidateHeaders<TProvider>(AwsCredentialProviderHelper environment, bool useSessionToken = true)
        where TProvider : AWSCredentials =>
        ValidateHeaders(environment, typeof(TProvider).Name, useSessionToken);

    private static async Task ValidateHeaders(AwsCredentialProviderHelper environment, string expectedProvider, bool useSessionToken = true)
    {
        var provider = environment.ResolveProvider();
        Assert.Equal(expectedProvider, provider.GetType().Name);
        var supplier = new AwsAuthHeaderSupplier(CreateConfig());
        using var request = CreateRequest();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var headers = await supplier.GetAsync(request, cancellation.Token);

        Assert.Contains($"Credential={environment.Credentials.AccessKey}/", headers["Authorization"]);
        Assert.Contains("/us-west-2/test-service/aws4_request", headers["Authorization"]);
        Assert.Matches("Signature=[0-9a-f]{64}", headers["Authorization"]);
        Assert.Matches("^[0-9]{8}T[0-9]{6}Z$", headers["x-amz-date"]);
        Assert.Equal("application/x-protobuf", headers["Content-Type"]);
        var resolved = await provider.GetCredentialsAsync();
        Assert.True(resolved.SecretKey == environment.Credentials.SecretKey, "The provider should resolve the isolated test secret.");
        if (useSessionToken)
        {
            Assert.Equal(environment.Credentials.Token, headers["x-amz-security-token"]);
            Assert.Contains("x-amz-security-token", headers["Authorization"]);
        }
        else
        {
            Assert.False(headers.ContainsKey("x-amz-security-token"));
        }
    }
}

/// <summary>
/// Runs credential tests without parallel collections while they modify shared AWS SDK and environment settings.
/// </summary>
[CollectionDefinition("AWS credential providers", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsCredentialProviderCollection
{
}

/// <summary>
/// Isolates credential tests using temporary files, restored SDK and environment settings,
/// and a local HTTP server for metadata responses.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Test fixture")]
internal sealed class AwsCredentialProviderHelper : IDisposable
{
    private static readonly string[] VariableNames =
    {
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "AWS_SECURITY_TOKEN",
        "AWS_PROFILE", "AWS_DEFAULT_PROFILE", "AWS_SHARED_CREDENTIALS_FILE", "AWS_CONFIG_FILE",
        "AWS_WEB_IDENTITY_TOKEN_FILE", "AWS_ROLE_ARN", "AWS_ROLE_SESSION_NAME",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN", "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE",
        "AWS_EC2_METADATA_DISABLED", "AWS_EC2_METADATA_SERVICE_ENDPOINT", "AWS_EC2_METADATA_V1_DISABLED",
        "AWS_REGION", "AWS_DEFAULT_REGION", "AWS_LOGIN_CACHE_DIRECTORY",
    };

    private readonly Dictionary<string, string?> originalEnvironment;
    private readonly string? originalProfileName = AWSConfigs.AWSProfileName;
    private readonly string? originalProfilesLocation = AWSConfigs.AWSProfilesLocation;
    private readonly string? originalRegion = AWSConfigs.AWSRegion;
    private readonly bool originalDisableLegacyPersistenceStore = AWSConfigs.DisableLegacyPersistenceStore;
#pragma warning disable CS0618 // Preserve the SDK provider configuration across tests.
    private readonly List<FallbackCredentialsFactory.CredentialsGenerator> originalGenerators = FallbackCredentialsFactory.CredentialsGenerators;
#pragma warning restore CS0618
    private readonly TcpListener metadataServer = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource serverCancellation = new();
    private readonly Task serverTask;
    private readonly List<Action> restoreSdkState = new();
    private AWSCredentials? provider;

    public AwsCredentialProviderHelper()
    {
        // Initialize defaults before changing the environment so the SDK never retains our temporary paths.
        _ = SharedCredentialsFile.DefaultFilePath;
        _ = SharedCredentialsFile.DefaultConfigFilePath;
        RuntimeHelpers.RunClassConstructor(typeof(LoginAWSCredentials).TypeHandle);
        var metadataCache = typeof(EC2InstanceMetadata).GetField("_cache", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalMetadataCache = metadataCache.GetValue(null);
        metadataCache.SetValue(null, new Dictionary<string, string>());
        this.restoreSdkState.Add(() => metadataCache.SetValue(null, originalMetadataCache));
        this.DirectoryPath = Directory.CreateTempSubdirectory("adot-credentials-").FullName;
        this.CredentialsFile = Path.Combine(this.DirectoryPath, "credentials");
        this.ConfigFile = Path.Combine(this.DirectoryPath, "config");
        this.TokenCacheDirectory = Directory.CreateDirectory(Path.Combine(this.DirectoryPath, "cache")).FullName;
        File.WriteAllText(this.CredentialsFile, string.Empty);
        File.WriteAllText(this.ConfigFile, string.Empty);
        this.originalEnvironment = VariableNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (string name in VariableNames)
        {
            Set(name, null);
        }

        Set("AWS_SHARED_CREDENTIALS_FILE", this.CredentialsFile);

        // Let the SDK discover config beside our credentials file. In this SDK version,
        // AWS_CONFIG_FILE is cached when SharedCredentialsFile is first initialized.
        Set("AWS_EC2_METADATA_DISABLED", "true");
        Set("AWS_LOGIN_CACHE_DIRECTORY", this.TokenCacheDirectory);
        AWSConfigs.AWSProfileName = null;
        AWSConfigs.AWSProfilesLocation = this.CredentialsFile;
        AWSConfigs.AWSRegion = "us-west-2";
        AWSConfigs.DisableLegacyPersistenceStore = true;
        Reset();
        this.metadataServer.Start();
        this.MetadataEndpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)this.metadataServer.LocalEndpoint).Port}");
        this.serverTask = Task.Run(this.ServeAsync);
    }

    public string DirectoryPath { get; }

    public string CredentialsFile { get; }

    public string ConfigFile { get; }

    public string TokenCacheDirectory { get; }

    public Uri MetadataEndpoint { get; }

    public Uri? ContainerEndpoint { get; private set; }

    public ImmutableCredentials Credentials { get; } = new("TESTACCESSKEY", "test-secret", "test-session-token");

    public ConcurrentQueue<CredentialHttpRequest> Requests { get; } = new();

    public static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    public static void Reset()
    {
#pragma warning disable CS0618 // Exercise the same credential resolver used by DefaultAwsAuthenticator.
        FallbackCredentialsFactory.Reset();
#pragma warning restore CS0618
        FallbackInternalConfigurationFactory.Reset();
    }

    public AWSCredentials ResolveProvider()
    {
#pragma warning disable CS0618
        if (this.provider != null)
        {
            return this.provider;
        }

        this.provider = FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618
        if (this.provider is GenericContainerCredentials)
        {
            // Verify SDK endpoint selection before redirecting delivery to the local credential server.
            var endpoint = typeof(GenericContainerCredentials).GetProperty("ResolvedEndpointUri", BindingFlags.Instance | BindingFlags.NonPublic)!;
            this.ContainerEndpoint = (Uri)endpoint.GetValue(this.provider)!;
            endpoint.SetValue(this.provider, new Uri(this.MetadataEndpoint, this.ContainerEndpoint.PathAndQuery));
        }
        else if (this.provider is SSOAWSCredentials)
        {
            var manager = GetField(this.provider, "_ssoTokenManager");
            this.UseTokenCache(GetField(manager, "_ssoTokenFileCache"), "_defaultSSOCacheDirectory");
        }
        else if (this.provider is LoginAWSCredentials)
        {
            var cache = typeof(LoginAWSCredentials).GetField("_loginFileCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            this.UseTokenCache(cache, "_defaultLoginCacheDirectory");
        }

        return this.provider;
    }

    public void WriteCredentials(string contents) => File.WriteAllText(this.CredentialsFile, contents);

    public void WriteConfig(string contents) => File.WriteAllText(this.ConfigFile, contents);

    public void ConfigureProfileProvider(string profileName)
    {
        // Use the SDK's profile API to configure SSO without its fallback resolver checking the
        // user's fixed cache path. The provider still reads and parses our real cached token file.
        var profiles = new CredentialProfileStoreChain(this.CredentialsFile);
        Assert.True(profiles.TryGetAWSCredentials(profileName, out var configuredProvider));
#pragma warning disable CS0618
        FallbackCredentialsFactory.CredentialsGenerators = new() { () => configuredProvider };
#pragma warning restore CS0618
    }

    public void RegisterSts(ICoreAmazonSTS client)
    {
        var registry = GlobalRuntimeDependencyRegistry.Instance;
        var original = registry.GetInstance<ICoreAmazonSTS>(
            "AWSSDK.SecurityToken",
            "Amazon.SecurityToken.AmazonSecurityTokenServiceClient",
            new CreateInstanceContext(new SecurityTokenServiceClientContext { Region = RegionEndpoint.USWest2 }));
        this.restoreSdkState.Add(() => registry.RegisterSecurityTokenServiceClient((object)original!));
        registry.RegisterSecurityTokenServiceClient(client);
    }

    public void RegisterSso(ICoreAmazonSSO client, ICoreAmazonSSOOIDC oidcClient)
    {
        var registry = GlobalRuntimeDependencyRegistry.Instance;
        var original = registry.GetInstance<ICoreAmazonSSO>(
            "AWSSDK.SSO",
            "Amazon.SSO.AmazonSSOClient",
            new CreateInstanceContext(new SSOClientContext { Region = RegionEndpoint.USWest2 }));
        var originalOidc = registry.GetInstance<ICoreAmazonSSOOIDC>(
            "AWSSDK.SSOOIDC",
            "Amazon.SSOOIDC.AmazonSSOOIDCClient",
            new CreateInstanceContext(new SSOOIDCClientContext { Region = RegionEndpoint.USWest2 }));
        this.restoreSdkState.Add(() => registry.RegisterSSOClient((object)original!));
        this.restoreSdkState.Add(() => registry.RegisterSSOOIDCClient((object)originalOidc!));
        registry.RegisterSSOClient(client);
        registry.RegisterSSOOIDCClient(oidcClient);
    }

    public void RegisterSignin(ICoreAmazonSignin client)
    {
        var registry = GlobalRuntimeDependencyRegistry.Instance;
        var original = registry.GetInstance<ICoreAmazonSignin>(
            "AWSSDK.Signin",
            "Amazon.Signin.AmazonSigninClient",
            new CreateInstanceContext(new SigninClientContext { Region = RegionEndpoint.USWest2 }));
        this.restoreSdkState.Add(() => registry.RegisterSigninClient((object)original!));
        registry.RegisterSigninClient(client);
    }

    public void Dispose()
    {
        try
        {
            (this.provider as IDisposable)?.Dispose();
            this.serverCancellation.Cancel();
            this.metadataServer.Stop();
            this.serverTask.GetAwaiter().GetResult();
        }
        finally
        {
            this.serverCancellation.Dispose();
            foreach (var restore in this.restoreSdkState)
            {
                restore();
            }

            foreach (var variable in this.originalEnvironment)
            {
                Set(variable.Key, variable.Value);
            }

            AWSConfigs.AWSProfileName = this.originalProfileName;
            AWSConfigs.AWSProfilesLocation = this.originalProfilesLocation;
            AWSConfigs.AWSRegion = this.originalRegion;
            AWSConfigs.DisableLegacyPersistenceStore = this.originalDisableLegacyPersistenceStore;
            Reset();
#pragma warning disable CS0618
            FallbackCredentialsFactory.CredentialsGenerators = this.originalGenerators;
#pragma warning restore CS0618
            Directory.Delete(this.DirectoryPath, recursive: true);
        }
    }

    private static object GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private void UseTokenCache(object cache, string name)
    {
        // The SDK has no public cache-directory option on these credential providers.
        // Change only the path so the actual SDK cache reader parses our isolated test files.
        var field = cache.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = field.GetValue(cache);
        field.SetValue(cache, this.TokenCacheDirectory);
        this.restoreSdkState.Add(() => field.SetValue(cache, original));
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!this.serverCancellation.IsCancellationRequested)
            {
                using var client = await this.metadataServer.AcceptTcpClientAsync(this.serverCancellation.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string[] requestLine = (await reader.ReadLineAsync(this.serverCancellation.Token))!.Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(this.serverCancellation.Token) is { Length: > 0 } line)
                {
                    int separator = line.IndexOf(':');
                    headers.Add(line[..separator], line[(separator + 1)..].Trim());
                }

                string response = this.Respond(new Uri(this.MetadataEndpoint, requestLine[1]), requestLine[0], headers);
                byte[] body = Encoding.UTF8.GetBytes(response);
                byte[] responseHeaders = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, this.serverCancellation.Token);
                await stream.WriteAsync(body, this.serverCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (this.serverCancellation.IsCancellationRequested)
        {
        }
    }

    private string Respond(Uri uri, string method, IDictionary<string, string>? headers)
    {
        this.Requests.Enqueue(new CredentialHttpRequest(
            uri, method, new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)));
        if (method == "PUT" && uri.AbsolutePath == "/latest/api/token")
        {
            return "test-imds-token";
        }

        if (uri.AbsolutePath == "/latest/meta-data/iam/info")
        {
            return """{"Code":"Success","InstanceProfileArn":"arn:aws:iam::111122223333:instance-profile/test-role","InstanceProfileId":"test-profile"}""";
        }

        if (uri.AbsolutePath.TrimEnd('/') == "/latest/meta-data/iam/security-credentials")
        {
            return "test-role";
        }

        if (uri.AbsolutePath.TrimEnd('/') == "/latest/meta-data/iam")
        {
            return "info\nsecurity-credentials/";
        }

        Assert.Contains(uri.AbsolutePath, new[] { "/test-task", "/test-pod", "/test-snapstart", "/latest/meta-data/iam/security-credentials/test-role" });
        return JsonSerializer.Serialize(new
        {
            Code = "Success",
            Type = "AWS-HMAC",
            AccessKeyId = this.Credentials.AccessKey,
            SecretAccessKey = this.Credentials.SecretKey,
            Token = this.Credentials.Token,
            Expiration = "2099-01-01T00:00:00Z",
            LastUpdated = "2026-01-01T00:00:00Z",
        });
    }

    internal sealed record CredentialHttpRequest(Uri Uri, string Method, IReadOnlyDictionary<string, string> Headers);
}
