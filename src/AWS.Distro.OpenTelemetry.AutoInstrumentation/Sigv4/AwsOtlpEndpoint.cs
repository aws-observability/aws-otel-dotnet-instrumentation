// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.RegularExpressions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Recognizes the CloudWatch OTLP endpoints that require AWS SigV4 signing, and supplies the
/// SigV4 service name and signing region for each signal.
///
/// These patterns decide whether a request is signed at all. A false negative silently disables
/// authentication (the export then fails with HTTP 403 at the service); a false positive would
/// sign a request to a host that is not AWS. Both are guarded by anchoring every pattern.
///
/// Previously the trace and log patterns were string constants on <c>Plugin</c>. They live here so
/// that adding a signal, or a partition, happens in one place.
/// </summary>
internal static class AwsOtlpEndpoint
{
    /// <summary>
    /// SigV4 service name for the X-Ray OTLP traces endpoint.
    ///
    /// Lowercase, because this value goes into the credential scope verbatim. A live probe against
    /// cn-north-1 with "XRay" was rejected with
    /// <c>Credential should be scoped to correct service: 'xray'</c>. Note the existing
    /// <c>OtlpAwsSpanExporter</c> passes "XRay" and still works: it never sets
    /// <c>AuthenticationServiceName</c> on the client config, so the signer falls back to the X-Ray
    /// config's own lowercase service name. Anything that sets the service name explicitly, as
    /// <see cref="SigV4SigningHandler"/> does, has to spell it the way the service expects.
    /// </summary>
    internal const string TracesSigningServiceName = "xray";

    /// <summary>SigV4 service name for the CloudWatch Logs OTLP endpoint.</summary>
    internal const string LogsSigningServiceName = "logs";

    /// <summary>
    /// SigV4 service name for the CloudWatch Metrics OTLP endpoint. Note it is "monitoring", not
    /// "cloudwatch" or "metrics"; the IAM action is <c>cloudwatch:PutMetricData</c>.
    /// </summary>
    internal const string MetricsSigningServiceName = "monitoring";

    // The optional "\.cn" suffix covers the AWS China partition (cn-north-1, cn-northwest-1), whose
    // endpoints are *.amazonaws.com.cn. Every pattern stays anchored with ^...$ so the optional
    // group does not loosen matching: a lookalike host such as
    // "https://xray.cn-north-1.amazonaws.com.cn.evil/v1/traces" still fails to match and falls back
    // to the unsigned exporter. The same optional-suffix form is already used by
    // S3PresignedUrlAttributor.
    //
    // GovCloud, FIPS, PrivateLink and European Sovereign Cloud endpoint forms are deliberately not
    // matched; those partitions are out of scope and fall through to unsigned upstream behavior.
    //
    // Matching is case-sensitive, preserving the behavior these patterns had as Plugin constants.
    // The cross-language design calls for case-insensitive detection; adopting that here would
    // change shipped trace and log behavior, so it is tracked as a follow-up rather than folded in.
    internal const string TracesPattern = "^https://xray\\.([a-z0-9-]+)\\.amazonaws\\.com(?:\\.cn)?/v1/traces$";

    /// <summary>Matches the CloudWatch Logs OTLP endpoint, commercial and China partitions.</summary>
    internal const string LogsPattern = "^https://logs\\.([a-z0-9-]+)\\.amazonaws\\.com(?:\\.cn)?/v1/logs$";

    /// <summary>Matches the CloudWatch Metrics OTLP endpoint, commercial and China partitions.</summary>
    internal const string MetricsPattern = "^https://monitoring\\.([a-z0-9-]+)\\.amazonaws\\.com(?:\\.cn)?/v1/metrics$";

    private static readonly Regex TracesRegex = new Regex(TracesPattern, RegexOptions.Compiled);
    private static readonly Regex LogsRegex = new Regex(LogsPattern, RegexOptions.Compiled);
    private static readonly Regex MetricsRegex = new Regex(MetricsPattern, RegexOptions.Compiled);

    /// <summary>
    /// Whether the endpoint is the X-Ray OTLP traces endpoint.
    /// </summary>
    /// <param name="endpoint">Configured endpoint, may be null or empty.</param>
    /// <returns>True when SigV4 signing applies to traces for this endpoint.</returns>
    internal static bool IsTracesEndpoint(string? endpoint)
        => !string.IsNullOrEmpty(endpoint) && TracesRegex.IsMatch(endpoint!);

    /// <summary>
    /// Whether the endpoint is the CloudWatch Logs OTLP endpoint.
    /// </summary>
    /// <param name="endpoint">Configured endpoint, may be null or empty.</param>
    /// <returns>True when SigV4 signing applies to logs for this endpoint.</returns>
    internal static bool IsLogsEndpoint(string? endpoint)
        => !string.IsNullOrEmpty(endpoint) && LogsRegex.IsMatch(endpoint!);

    /// <summary>
    /// Whether the endpoint is the CloudWatch Metrics OTLP endpoint.
    /// </summary>
    /// <param name="endpoint">Configured endpoint, may be null or empty.</param>
    /// <returns>True when SigV4 signing applies to metrics for this endpoint.</returns>
    internal static bool IsMetricsEndpoint(string? endpoint)
        => !string.IsNullOrEmpty(endpoint) && MetricsRegex.IsMatch(endpoint!);

    /// <summary>
    /// Extracts the signing region from an endpoint that has already matched one of the signal
    /// patterns above.
    ///
    /// The region is taken from the pattern's own capture group rather than by splitting the host,
    /// so the value can never disagree with what was matched. For a China endpoint this yields
    /// "cn-north-1" rather than a DNS fragment, which is why the China support needed no change to
    /// region handling. Signing uses this region together with an explicit ServiceURL, so the DNS
    /// suffix never reaches the signer.
    /// </summary>
    /// <param name="endpoint">An endpoint that matched one of the signal patterns.</param>
    /// <returns>The AWS region, or null when the endpoint matches no known pattern.</returns>
    internal static string? GetRegion(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        foreach (Regex regex in new[] { TracesRegex, LogsRegex, MetricsRegex })
        {
            Match match = regex.Match(endpoint!);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }
}
