// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// A parsed SigV4 presigned AWS URL.
///
/// <para>Carries the request context needed for attribution that a plain parsed URL cannot express:
/// the HTTP method (which comes from the span, not the URL) and the parsed query parameters. The
/// host and path come from the URL itself.</para>
///
/// <para>The signing service is intentionally not carried here: it is derived from the SigV4
/// credential scope, which URL sanitization redacts. Service identity is instead determined from the
/// endpoint hostname by the service-specific attributor.</para>
/// </summary>
internal sealed class PresignedAwsUrl
{
    private readonly string host;
    private readonly string path;
    private readonly string? httpMethod;
    private readonly IReadOnlyDictionary<string, List<string>> queryParameters;

    internal PresignedAwsUrl(string host, string path, string? httpMethod, IReadOnlyDictionary<string, List<string>> queryParameters)
    {
        this.host = host;
        this.path = string.IsNullOrEmpty(path) ? "/" : path;
        this.httpMethod = httpMethod;
        this.queryParameters = queryParameters;
    }

    internal string? GetHttpMethod()
    {
        return this.httpMethod;
    }

    internal string GetHost()
    {
        return this.host;
    }

    internal string GetPath()
    {
        return this.path;
    }

    // Returns the first value for a query parameter, or null when the parameter is absent.
    //
    // Note: under .NET's URL sanitization every query value is replaced with the literal "Redacted",
    // so callers must treat a returned value as an opaque presence signal only. Operations are keyed
    // on the presence of the parameter, never on its value. See S3PresignedUrlAttributor.
    internal string? GetFirstQueryParameterValue(string name)
    {
        if (this.queryParameters.TryGetValue(name, out List<string>? values) && values.Count > 0)
        {
            return values[0];
        }

        return null;
    }

    // Whether a query parameter is present, regardless of its value. This is the primary signal used
    // for operation resolution because sanitization strips query values.
    internal bool HasQueryParameter(string name)
    {
        return this.queryParameters.ContainsKey(name);
    }
}
