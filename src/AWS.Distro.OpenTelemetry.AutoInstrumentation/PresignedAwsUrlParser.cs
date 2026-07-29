// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Recognizes a SigV4/SigV4a presigned AWS URL from a span's URL.
///
/// <para>Detection relies only on the presence of the six SigV4 query-string parameters that a
/// presigned (query-authenticated) request always carries: <c>X-Amz-Algorithm</c>,
/// <c>X-Amz-Credential</c>, <c>X-Amz-Signature</c>, <c>X-Amz-Date</c>, <c>X-Amz-Expires</c>, and
/// <c>X-Amz-SignedHeaders</c>. Their co-occurrence — anchored by <c>X-Amz-Expires</c>, which is
/// specific to presigned URLs — is a high-specificity fingerprint.</para>
///
/// <para><b>.NET-specific divergence from the Java/Python/JS ports:</b> those parsers additionally
/// check the <c>X-Amz-Algorithm</c> value against a SigV4 algorithm allowlist and require the other
/// parameters to be non-empty. This distro's URL sanitization (see
/// <c>OpenTelemetry.Internal.RedactionHelper.GetRedactedQueryString</c>) blanks <b>every</b> query
/// value to the literal <c>Redacted</c> before metric attribution runs — even a value that was
/// originally empty becomes <c>Redacted</c>. So no query value can be read here: detection and
/// operation resolution key on parameter <b>presence</b> only. Dropping the algorithm allowlist is
/// safe because nothing downstream branches on SigV4 vs SigV4a — the signing service is derived from
/// the endpoint hostname, not the credential scope.</para>
///
/// <para>Query-string (presigned) SigV4 authentication parameters are defined here:
/// https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-query-string-auth.html. Per
/// https://docs.aws.amazon.com/prescriptive-guidance/latest/presigned-url-best-practices/overview.html,
/// the <c>X-Amz-Expires</c> parameter is what distinguishes a presigned URL from other signed
/// requests.</para>
/// </summary>
internal sealed class PresignedAwsUrlParser
{
    private const string XAmzAlgorithm = "X-Amz-Algorithm";
    private const string XAmzCredential = "X-Amz-Credential";
    private const string XAmzSignature = "X-Amz-Signature";
    private const string XAmzDate = "X-Amz-Date";
    private const string XAmzExpires = "X-Amz-Expires";
    private const string XAmzSignedHeaders = "X-Amz-SignedHeaders";

    private PresignedAwsUrlParser()
    {
    }

    // Parses the span's URL/method attributes (stable keys first, then legacy) into a
    // PresignedAwsUrl, or null when the span does not carry a recognizable presigned URL.
    internal static PresignedAwsUrl? Parse(Activity span)
    {
        // URL: stable `url.full` first, then legacy `http.url`.
        string? url = (string?)span.GetTagItem(AttributeUrlFull) ?? (string?)span.GetTagItem(AttributeHttpUrl);

        // Method: stable `http.request.method` first, then legacy `http.method`.
        string? httpMethod = (string?)span.GetTagItem(AttributeHttpRequestMethod) ?? (string?)span.GetTagItem(AttributeHttpMethod);
        return Parse(url, httpMethod);
    }

    internal static PresignedAwsUrl? Parse(string? url, string? httpMethod)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        Uri uri;
        try
        {
            uri = new Uri(url!);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        // Uri.Query includes the leading '?'; strip it before splitting on '&'.
        IReadOnlyDictionary<string, List<string>> queryParameters = ParseQueryParameters(uri.Query.TrimStart('?'));
        if (!IsPresignedSigV4Request(queryParameters))
        {
            return null;
        }

        // Use AbsolutePath (not the raw query) so operation/bucket resolution sees only the path.
        return new PresignedAwsUrl(uri.Host, uri.AbsolutePath, httpMethod, queryParameters);
    }

    /// <summary>
    /// A request is a presigned SigV4/SigV4a request when it carries all six SigV4 query parameters.
    /// Only presence is checked — see the class remarks for why values are unavailable in this distro.
    /// </summary>
    private static bool IsPresignedSigV4Request(IReadOnlyDictionary<string, List<string>> queryParameters)
    {
        return queryParameters.ContainsKey(XAmzAlgorithm)
            && queryParameters.ContainsKey(XAmzCredential)
            && queryParameters.ContainsKey(XAmzSignature)
            && queryParameters.ContainsKey(XAmzDate)
            && queryParameters.ContainsKey(XAmzExpires)
            && queryParameters.ContainsKey(XAmzSignedHeaders);
    }

    private static IReadOnlyDictionary<string, List<string>> ParseQueryParameters(string rawQuery)
    {
        Dictionary<string, List<string>> queryParameters = new Dictionary<string, List<string>>();
        if (string.IsNullOrEmpty(rawQuery))
        {
            return queryParameters;
        }

        foreach (string pair in rawQuery.Split('&'))
        {
            int delimiterIndex = pair.IndexOf('=');
            string name = delimiterIndex >= 0 ? pair.Substring(0, delimiterIndex) : pair;
            string value = delimiterIndex >= 0 ? pair.Substring(delimiterIndex + 1) : string.Empty;
            string decodedName = Decode(name);
            if (!queryParameters.TryGetValue(decodedName, out List<string>? values))
            {
                values = new List<string>();
                queryParameters[decodedName] = values;
            }

            values.Add(Decode(value));
        }

        return queryParameters;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception)
        {
            // Malformed percent-encoding; keep the raw value rather than dropping the parameter, so
            // the presence-based checks still see it.
            return value;
        }
    }
}
