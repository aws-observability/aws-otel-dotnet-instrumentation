// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation.Sampling;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel;

/// <summary>
/// CloudWatch auto-instrumentation plugin for OpenTelemetry .NET.
/// </summary>
public sealed class CloudWatchPlugin
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CloudWatchPlugin"/> class.
    /// </summary>
    public CloudWatchPlugin()
    {
    }

    /// <summary>
    /// Installs an always-record wrapper around the sampler selected by OpenTelemetry environment variables.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The configured tracer provider builder.</returns>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        return builder.AddCloudWatchSpanMetrics(SamplerFactory.Create());
    }

    /// <summary>
    /// Subscribes the application meter provider to the span metrics instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <returns>The configured meter provider builder.</returns>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        return builder.AddCloudWatchSpanMetrics();
    }
}
