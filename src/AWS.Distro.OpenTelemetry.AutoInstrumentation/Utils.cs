// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Utilities for inspecting OTLP exporter headers.
/// </summary>
internal static class Utils
{
    /// <summary>
    /// Parses comma-separated OTLP headers in key=value format.
    /// </summary>
    /// <remarks>
    /// See the <see href="https://opentelemetry.io/docs/specs/otel/protocol/exporter/#specifying-headers-via-environment-variables">OpenTelemetry header format</see>.
    /// Keys are case-insensitive. Values remain encoded as configured.
    /// Malformed entries are ignored, and the last value wins for duplicate keys.
    /// </remarks>
    /// <param name="headers">The OTLP headers in key=value format.</param>
    /// <returns>A map of header names to values.</returns>
    internal static Dictionary<string, string> ParseOtlpHeadersEnvVariable(string? headers)
    {
        var parsedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(headers))
        {
            return parsedHeaders;
        }

        foreach (var header in headers.Split(','))
        {
            var separator = header.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = header.Substring(0, separator).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            parsedHeaders[key] = header.Substring(separator + 1).Trim();
        }

        return parsedHeaders;
    }
}
