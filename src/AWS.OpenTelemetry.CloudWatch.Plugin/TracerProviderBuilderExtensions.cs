// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.OpenTelemetry.CloudWatch.Plugin;

namespace OpenTelemetry.Trace;

/// <summary>
/// Extension methods for registering Amazon CloudWatch tracing features with OpenTelemetry.
/// </summary>
public static class TracerProviderBuilderExtensions
{
    /// <summary>
    /// Adds CloudWatch span metrics using the sampler selected by OpenTelemetry environment variables.
    /// </summary>
    /// <param name="builder">The tracer provider builder being configured.</param>
    /// <returns>The same tracer provider builder for call chaining.</returns>
    public static TracerProviderBuilder AddCloudWatchSpanMetrics(this TracerProviderBuilder builder)
    {
        return AddCloudWatchSpanMetrics(builder, SpanMetricsSamplerFactory.Create());
    }

    /// <summary>
    /// Adds CloudWatch span metrics while preserving the supplied sampler's export decisions.
    /// </summary>
    /// <param name="builder">The tracer provider builder being configured.</param>
    /// <param name="sampler">The application sampler whose export decisions are preserved.</param>
    /// <returns>The same tracer provider builder for call chaining.</returns>
    public static TracerProviderBuilder AddCloudWatchSpanMetrics(
        this TracerProviderBuilder builder,
        Sampler sampler)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (sampler is null)
        {
            throw new ArgumentNullException(nameof(sampler));
        }

        return builder
            .SetSampler(new AlwaysRecordSampler(sampler))
            .AddProcessor(new SpanMetricsConnector());
    }
}
