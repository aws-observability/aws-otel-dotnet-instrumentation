// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace AWS.OpenTelemetry.CloudWatch.Plugin;

internal static class SpanMetricsConstants
{
    public const string ScopeName = "cloudwatch.plugin.otel.span_metrics";
    public const string CallsName = "traces.span.metrics.calls";
    public const string DurationName = "traces.span.metrics.duration";
    public const string DurationUnit = "s";
    public const string SpanName = "span.name";
    public const string SpanKind = "span.kind";
    public const string StatusCode = "status.code";
    public const string Schema = "aws.otel.span.metrics.schema";
    public const string SchemaVersion = "v1";
    public const string LibraryVersionKey = "aws.otel.extension.lib.version";
    public static readonly double[] DurationBucketBoundaries =
    {
        0.002,
        0.004,
        0.006,
        0.008,
        0.01,
        0.05,
        0.1,
        0.2,
        0.4,
        0.8,
        1.0,
        1.4,
        2.0,
        5.0,
        10.0,
        15.0,
    };

    public static readonly string LibraryVersion = GetLibraryVersion();

    private static string GetLibraryVersion()
    {
        var informationalVersion = typeof(SpanMetricsConstants).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informationalVersion))
        {
            return "unknown";
        }

        var metadataSeparator = informationalVersion.IndexOf('+');
        return metadataSeparator > 0
            ? informationalVersion.Substring(0, metadataSeparator)
            : informationalVersion;
    }
}
