// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;

internal static class EnvironmentSamplerFactory
{
    private const string TracesSamplerEnvironmentVariable = "OTEL_TRACES_SAMPLER";
    private const string TracesSamplerArgumentEnvironmentVariable = "OTEL_TRACES_SAMPLER_ARG";

    public static Sampler Create()
    {
        var samplerName = Environment.GetEnvironmentVariable(TracesSamplerEnvironmentVariable);
        var samplerArgument = Environment.GetEnvironmentVariable(TracesSamplerArgumentEnvironmentVariable);

        return samplerName switch
        {
            "always_on" => new AlwaysOnSampler(),
            "always_off" => new AlwaysOffSampler(),
            "traceidratio" => CreateTraceIdRatioSampler(samplerArgument),
            "parentbased_always_on" => new ParentBasedSampler(new AlwaysOnSampler()),
            "parentbased_always_off" => new ParentBasedSampler(new AlwaysOffSampler()),
            "parentbased_traceidratio" => new ParentBasedSampler(CreateTraceIdRatioSampler(samplerArgument)),
            _ => new ParentBasedSampler(new AlwaysOnSampler()),
        };
    }

    private static Sampler CreateTraceIdRatioSampler(string? samplerArgument)
    {
        if (!double.TryParse(
                samplerArgument,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var probability) ||
            probability < 0 ||
            probability > 1)
        {
            probability = 1;
        }

        return new TraceIdRatioBasedSampler(probability);
    }
}
