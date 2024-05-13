// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenTelemetry.Metrics;

/// <summary>
/// Metrics extensions for OpenTelemetry .NET Distribution for AWS
/// </summary>
public static class MeterProviderBuilderExtensions
{
    /// <summary>
    /// Setup metrics
    /// </summary>
    /// <param name="builder"><see cref="MeterProviderBuilder"/></param>
    /// <returns>The modified <see cref="MeterProviderBuilder"/> for chaining calls</returns>
    public static MeterProviderBuilder UseAWS(this MeterProviderBuilder builder)
    {
        // Custom implementation to metrics-related resource / instrumentation / exporter / event source
        return builder;
    }
}