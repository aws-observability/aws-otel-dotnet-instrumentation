// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.OpenTelemetry.CloudWatch.Plugin;

namespace OpenTelemetry.Metrics;

/// <summary>
/// Extension methods for registering Amazon CloudWatch metrics with OpenTelemetry.
/// </summary>
public static class MeterProviderBuilderExtensions
{
    /// <summary>
    /// Subscribes the meter provider to metrics derived by the CloudWatch span metrics connector.
    /// </summary>
    /// <param name="builder">The meter provider builder being configured.</param>
    /// <returns>The same meter provider builder for call chaining.</returns>
    public static MeterProviderBuilder AddCloudWatchSpanMetrics(this MeterProviderBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.AddMeter(SpanMetricsConnector.ScopeName);
    }
}
