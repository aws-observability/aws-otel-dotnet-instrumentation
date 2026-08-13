// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation.Plugins.SpanMetrics;

/// <summary>
/// Auto-instrumentation plugin that registers span metrics processing.
/// </summary>
public class SpanMetricsConnectorPlugin
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpanMetricsConnectorPlugin"/> class.
    /// </summary>
    public SpanMetricsConnectorPlugin()
    {
    }

    /// <summary>
    /// Installs an always-record wrapper around the sampler selected by OpenTelemetry environment variables.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The configured tracer provider builder.</returns>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        var rootSampler = SpanMetricsSamplerFactory.Create();
        builder.SetSampler(new AlwaysRecordSampler(rootSampler));
        return builder;
    }

    /// <summary>
    /// Adds the span metrics processor after the tracer provider is built.
    /// </summary>
    /// <param name="tracerProvider">The tracer provider.</param>
    public void TracerProviderInitialized(TracerProvider tracerProvider)
    {
        tracerProvider.AddProcessor(new SpanMetricsConnector());
    }

    /// <summary>
    /// Subscribes the application meter provider to the span metrics instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <returns>The configured meter provider builder.</returns>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        builder.AddMeter(SpanMetricsConnector.ScopeName);
        return builder;
    }
}
