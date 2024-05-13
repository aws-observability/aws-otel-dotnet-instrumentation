// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace OpenTelemetry.Trace;

/// <summary>
/// Tracing extensions for OpenTelemetry .NET Distribution for AWS
/// </summary>
public static class TracerProviderBuilderExtensions
{
    /// <summary>
    /// Setup tracing
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/></param>
    /// <returns>The modified <see cref="TracerProviderBuilder"/> for chaining calls</returns>
    public static TracerProviderBuilder UseAWS(this TracerProviderBuilder builder)
    {
        // Custom implementation to trace-related resource / instrumentation / exporter / event source
        return builder;
    }
}