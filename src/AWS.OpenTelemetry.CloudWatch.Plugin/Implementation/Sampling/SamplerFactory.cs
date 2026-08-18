// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;

internal static class SamplerFactory
{
    private const string TracesSamplerEnvironmentVariable = "OTEL_TRACES_SAMPLER";
    private const string TracesSamplerArgumentEnvironmentVariable = "OTEL_TRACES_SAMPLER_ARG";

    public static Sampler Create()
    {
        // OTel parses sampler configuration internally and exposes no supported way for a plugin
        // to read the configured sampler, so recreate it here before wrapping it.
        var samplerName = Environment.GetEnvironmentVariable(TracesSamplerEnvironmentVariable);
        var samplerArgument = Environment.GetEnvironmentVariable(TracesSamplerArgumentEnvironmentVariable);

        if (samplerName is null)
        {
            return new ParentBasedSampler(new AlwaysOnSampler());
        }

        if (string.Equals(samplerName, "always_on", StringComparison.OrdinalIgnoreCase))
        {
            return new AlwaysOnSampler();
        }

        if (string.Equals(samplerName, "always_off", StringComparison.OrdinalIgnoreCase))
        {
            return new AlwaysOffSampler();
        }

        if (string.Equals(samplerName, "traceidratio", StringComparison.OrdinalIgnoreCase))
        {
            return CreateTraceIdRatioSampler(samplerArgument);
        }

        if (string.Equals(samplerName, "parentbased_always_on", StringComparison.OrdinalIgnoreCase))
        {
            return new ParentBasedSampler(new AlwaysOnSampler());
        }

        if (string.Equals(samplerName, "parentbased_always_off", StringComparison.OrdinalIgnoreCase))
        {
            return new ParentBasedSampler(new AlwaysOffSampler());
        }

        if (string.Equals(samplerName, "parentbased_traceidratio", StringComparison.OrdinalIgnoreCase))
        {
            return new ParentBasedSampler(CreateTraceIdRatioSampler(samplerArgument));
        }

        CloudWatchPluginEventSource.Log.UnsupportedSamplerConfiguration(samplerName);
        throw new NotSupportedException(
            $"OTEL_TRACES_SAMPLER value '{samplerName}' is not supported by the CloudWatch plugin.");
    }

    private static Sampler CreateTraceIdRatioSampler(string? samplerArgument)
    {
        if (!double.TryParse(
                samplerArgument,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var probability) ||
            double.IsNaN(probability) ||
            double.IsInfinity(probability) ||
            probability < 0 ||
            probability > 1)
        {
            probability = 1;
        }

        return new TraceIdRatioBasedSampler(probability);
    }
}
