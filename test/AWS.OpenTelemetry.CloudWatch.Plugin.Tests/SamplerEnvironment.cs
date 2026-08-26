// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Tests;

internal sealed class SamplerEnvironment : IDisposable
{
    private readonly string? originalSampler;
    private readonly string? originalSamplerArgument;

    public SamplerEnvironment(string? sampler, string? samplerArgument)
    {
        this.originalSampler = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER");
        this.originalSamplerArgument = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG");
        Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", sampler);
        Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG", samplerArgument);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", this.originalSampler);
        Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG", this.originalSamplerArgument);
    }
}
