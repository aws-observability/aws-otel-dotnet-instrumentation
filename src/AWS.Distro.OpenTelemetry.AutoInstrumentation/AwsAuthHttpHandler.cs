// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Signs the serialized HTTP request before delivering it to the AWS OTLP endpoint.
/// </summary>
internal sealed class AwsAuthHttpHandler : DelegatingHandler
{
    private readonly AwsAuthHeaderSupplier headerSupplier;

    public AwsAuthHttpHandler(AwsAuthHeaderSupplier headerSupplier, HttpMessageHandler transport)
        : base(transport)
    {
        this.headerSupplier = headerSupplier;
    }

#if NET
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Upstream HTTP/protobuf exporters use synchronous Send on modern .NET.
        // Resolve asynchronous credentials off the caller's synchronization context.
        Task.Run(() => this.SignAsync(request, cancellationToken), cancellationToken).GetAwaiter().GetResult();
        return base.Send(request, cancellationToken);
    }
#endif

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await this.SignAsync(request, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task SignAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var signedHeaders = await this.headerSupplier.GetAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Credentials can rotate from a session token to credentials without a token.
        foreach (var name in request.Headers.Select(header => header.Key).Where(AwsAuthHeaderSupplier.IsSigningHeader).ToArray())
        {
            request.Headers.Remove(name);
        }

        var contentHeaderNames = new HashSet<string>(
            request.Content?.Headers.Select(header => header.Key) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var header in signedHeaders)
        {
            // Content headers already describe the exact bytes signed by the supplier.
            // They cannot be added to HttpRequestHeaders.
            if (!contentHeaderNames.Contains(header.Key))
            {
                request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }
}
