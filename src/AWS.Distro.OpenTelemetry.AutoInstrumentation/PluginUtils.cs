// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// PluginUtils class.
/// </summary>
internal static class PluginUtils
{
    /// <summary>
    /// Parses a comma-separated OpenTelemetry exporter selection.
    /// </summary>
    /// <param name="exporters">The value of an OTEL_*_EXPORTER environment variable.</param>
    /// <returns>The trimmed exporter names, excluding empty entries.</returns>
    internal static List<string> ParseOtelExporterEnvVariable(string? exporters)
    {
        var parsedExporters = new List<string>();
        if (string.IsNullOrWhiteSpace(exporters))
        {
            return parsedExporters;
        }

        foreach (var exporter in exporters.Split(','))
        {
            var name = exporter.Trim();
            if (name.Length > 0)
            {
                parsedExporters.Add(name);
            }
        }

        return parsedExporters;
    }

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
        if (headers == null || headers.Length == 0)
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
