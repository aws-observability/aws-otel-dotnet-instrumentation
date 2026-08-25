// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Metrics;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel;

/// <summary>
/// Extension methods for registering CloudWatch span metrics.
/// </summary>
public static class SpanMetricsMeterProviderBuilderExtensions
{
    /// <summary>
    /// Subscribes the meter provider to the CloudWatch span metrics instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <returns>The supplied meter provider builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    public static MeterProviderBuilder AddCloudWatchSpanMetrics(this MeterProviderBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddMeter(Implementation.SpanMetrics.SpanMetricsConnector.ScopeName);
    }
}
