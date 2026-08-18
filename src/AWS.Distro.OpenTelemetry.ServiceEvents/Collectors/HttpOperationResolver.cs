// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Resolves the operation key for an HTTP server span — the <c>"METHOD /route"</c> string that
/// identifies an endpoint across every ServiceEvents signal.
/// </summary>
/// <remarks>
/// <para>
/// Shared deliberately. Both the endpoint path and the FunctionCall path need this key, and the
/// FunctionCall metric's <c>operation</c> attribute is only useful if it is byte-identical to the
/// endpoint summary's — otherwise the two signals cannot be joined for the same request. They were
/// previously resolved by two separate private copies that had already drifted: one collapsed an
/// unmatched path to its first segment, the other emitted it raw, so a request to
/// <c>/wp-admin/setup.php</c> appeared as <c>/wp-admin</c> on one signal and in full on the other.
/// </para>
/// <para>
/// The route chain is <c>http.route</c>, then the first segment of <c>url.path</c>, then
/// <see cref="Activity.DisplayName" />.
/// </para>
/// </remarks>
internal static class HttpOperationResolver
{
    /// <summary>The HTTP method tag. See the remarks on <see cref="ResolveRoute" />.</summary>
    internal const string HttpRequestMethodTag = "http.request.method";

    private const string HttpRouteTag = "http.route";
    private const string UrlPathTag = "url.path";

    /// <summary>
    /// Resolve the <c>"METHOD /route"</c> operation key from an HTTP server span, or <c>null</c> when
    /// the span carries no HTTP method and so is not an HTTP server span at all.
    /// </summary>
    /// <param name="serverSpan">A server-kind activity.</param>
    /// <returns>The operation key, or <c>null</c>.</returns>
    public static string? ResolveOperation(Activity serverSpan)
    {
        if (serverSpan.GetTagItem(HttpRequestMethodTag) is not string method || string.IsNullOrEmpty(method))
        {
            return null;
        }

        return $"{method.ToUpperInvariant()} {ResolveRoute(serverSpan)}";
    }

    /// <summary>
    /// Resolve the route template for an HTTP server span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When no route matched (404s, scanner traffic) the raw path would put one distinct value per
    /// URL into every signal keyed on the operation, so it is collapsed to the first path segment —
    /// <c>"/wp-admin/setup.php"</c> becomes <c>"/wp-admin"</c>.
    /// </para>
    /// <para>
    /// That reduces cardinality without bounding it: traffic spread across many distinct first
    /// segments still yields one value per segment per flush window. The window resets on every
    /// flush so nothing accumulates, and no sibling distro caps this today, so the behaviour stays
    /// consistent across distros rather than being capped here unilaterally.
    /// </para>
    /// <para>
    /// The tag names are literals rather than semantic-convention constants. The pinned
    /// <c>OpenTelemetry.SemanticConventions</c> package (1.0.0-rc9.9, the only version ever
    /// published) predates the HTTP convention stabilisation: it defines <c>http.method</c> and
    /// <c>http.target</c>, and has no constant for <c>http.request.method</c> or <c>url.path</c>.
    /// Sourcing these from it would stop them matching what the ASP.NET Core instrumentation emits.
    /// </para>
    /// </remarks>
    /// <param name="serverSpan">A server-kind activity.</param>
    /// <returns>The route template, a collapsed path segment, or the activity's display name.</returns>
    public static string ResolveRoute(Activity serverSpan)
    {
        if (serverSpan.GetTagItem(HttpRouteTag) is string route && !string.IsNullOrEmpty(route))
        {
            return route;
        }

        if (serverSpan.GetTagItem(UrlPathTag) is string path && !string.IsNullOrEmpty(path))
        {
            return FirstPathSegment(path);
        }

        return serverSpan.DisplayName;
    }

    /// <summary>
    /// Return the first path segment with a leading slash: <c>"/wp-admin/x"</c> becomes
    /// <c>"/wp-admin"</c>, and <c>"/"</c> stays <c>"/"</c>.
    /// </summary>
    /// <param name="path">A URL path.</param>
    /// <returns>The first segment, slash-prefixed.</returns>
    private static string FirstPathSegment(string path)
    {
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var first = slash >= 0 ? trimmed.Substring(0, slash) : trimmed;
        return "/" + first;
    }
}
