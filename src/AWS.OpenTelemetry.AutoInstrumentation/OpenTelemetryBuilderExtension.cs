// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Extensions for <see cref="OpenTelemetry.IOpenTelemetryBuilder"/> interface
/// provided by OpenTelemetry .NET distribution for AWS.
/// </summary>
public static class OpenTelemetryBuilderExtension
{
    /// <summary>
    /// Configures AWS SDK instrumentation for tracing, metrics, logging
    /// </summary>
    /// <param name="builder"><see cref="IOpenTelemetryBuilder"/></param>
    /// <param name="configure">Callback action for configure <see cref="AWSOpenTelemetryOptions"/></param>
    /// <returns>The modified <see cref="IOpenTelemetryBuilder"/>.</returns>
    public static IOpenTelemetryBuilder UseAWS(this IOpenTelemetryBuilder builder, Action<AWSOpenTelemetryOptions>? configure = default)
    {
        // Custom implementation to configure TraceProviderBuilder and MeterProviderBuilder
        return builder
        .WithTracing(tracerProviderBuilder => tracerProviderBuilder.UseAWS())
        .WithMetrics(meterProviderBuilder => meterProviderBuilder.UseAWS());
    }
}