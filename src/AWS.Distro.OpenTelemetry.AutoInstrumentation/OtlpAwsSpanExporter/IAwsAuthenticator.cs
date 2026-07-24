// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;

/// <summary>
/// Provides AWS authentication and signing capabilities for AWS service requests.
/// </summary>
public interface IAwsAuthenticator
{
    /// <summary>
    /// Asynchronously retrieves AWS credentials that can be used to authenticate requests.
    /// </summary>
    /// <returns>
    /// A Task that resolves to an ImmutableCredentials object containing AWS access credentials.
    /// The credentials include access key, secret key, and optional session token.
    /// </returns>
    Task<ImmutableCredentials> GetCredentialsAsync();

    /// <summary>
    /// Signs an AWS request using AWS Signature Version 4 with the provided credentials snapshot.
    /// </summary>
    /// <param name="request">The request to sign.</param>
    /// <param name="config">The client config supplying the signing region/service.</param>
    /// <param name="credentials">
    /// The resolved credentials to sign with. The caller resolves these once and reuses the same
    /// snapshot for the <c>x-amz-security-token</c> header so the header and signature stay consistent.
    /// </param>
    void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials);
}

/// <summary>
/// Default implementation of IAwsAuthenticator that uses AWS SDK's built-in credential
/// and signing mechanisms.
/// </summary>
public class DefaultAwsAuthenticator : IAwsAuthenticator
{
    private AWSCredentials? awsCredentials;

    // Resolve the credentials provider lazily rather than in the constructor: the exporter is
    // constructed during OTel SDK setup, which can race credential provisioning (init-container /
    // IRSA token). Lazy resolution fails an individual export instead of blocking construction.
    private AWSCredentials AwsCredentials
    {
        get
        {
#pragma warning disable CS0618 // FallbackCredentialsFactory is obsolete in v4 but still functional
            return this.awsCredentials ??= FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618
        }
    }

    /// <inheritdoc/>
    public async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        return await this.AwsCredentials.GetCredentialsAsync();
    }

    /// <inheritdoc/>
    public void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
    {
        // Sign from the caller-provided ImmutableCredentials snapshot. AWS4Signer.Sign(...) would
        // re-resolve credentials internally; signing from SignRequest with the same snapshot the
        // caller used for the token header guarantees the signature and x-amz-security-token match
        // even if the underlying credentials rotate between resolutions.
        var signingResult = new AWS4Signer().SignRequest(request, config, null, credentials.AccessKey, credentials.SecretKey);
        request.AWS4SignerResult = signingResult;
        request.Headers["Authorization"] = signingResult.ForAuthorizationHeader;
    }
}
