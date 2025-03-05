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
    /// Signs an AWS request using AWS Signature Version 4.
    /// </summary>
    void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials);
}

/// <summary>
/// Default implementation of IAwsAuthenticator that uses AWS SDK's built-in credential
/// and signing mechanisms.
/// </summary>
public class DefaultAwsAuthenticator : IAwsAuthenticator
{
    /// <inheritdoc/>
    public async Task<ImmutableCredentials> GetCredentialsAsync()
    {
        return await FallbackCredentialsFactory.GetCredentials().GetCredentialsAsync();
    }

    /// <inheritdoc/>
    public void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
    {
        new AWS4Signer().Sign(request, config, null, credentials);
    }
}
