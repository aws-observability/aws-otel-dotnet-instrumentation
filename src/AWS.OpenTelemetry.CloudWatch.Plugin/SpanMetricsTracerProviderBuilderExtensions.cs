// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.Sampling;
using AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.SpanMetrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel;

/// <summary>
/// Extension methods for registering CloudWatch span metrics.
/// </summary>
public static class SpanMetricsTracerProviderBuilderExtensions
{
    private static readonly ConditionalWeakTable<TracerProviderBuilder, object> Registrations = new();
    private static readonly object RegistrationLock = new();

    /// <summary>
    /// Registers CloudWatch span metrics using the OpenTelemetry SDK default sampling policy.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The supplied tracer provider builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    public static TracerProviderBuilder AddCloudWatchSpanMetrics(this TracerProviderBuilder builder)
    {
        return AddCloudWatchSpanMetrics(builder, new ParentBasedSampler(new AlwaysOnSampler()));
    }

    /// <summary>
    /// Registers CloudWatch span metrics while preserving the supplied sampler's export decisions.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <param name="rootSampler">The application sampler whose export decisions are preserved.</param>
    /// <returns>The supplied tracer provider builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="rootSampler"/> is null.
    /// </exception>
    public static TracerProviderBuilder AddCloudWatchSpanMetrics(
        this TracerProviderBuilder builder,
        Sampler rootSampler)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (rootSampler is null)
        {
            throw new ArgumentNullException(nameof(rootSampler));
        }

        lock (RegistrationLock)
        {
            builder.SetSampler(AlwaysRecordSampler.Create(rootSampler));
            if (!Registrations.TryGetValue(builder, out _))
            {
                builder.AddProcessor(new SpanMetricsConnector());
                Registrations.Add(builder, new object());
            }
        }

        return builder;
    }
}
