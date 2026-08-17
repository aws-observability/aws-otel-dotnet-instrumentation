// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AWS.OpenTelemetry.CloudWatch.Plugin.Implementation.Sampling;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests.Implementation.Sampling;

[Collection(SpanMetricsTestsCollection.Name)]
public class SamplerFactoryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("always_on", null)]
    [InlineData("always_off", null)]
    [InlineData("traceidratio", "0.25")]
    [InlineData("parentbased_always_on", null)]
    [InlineData("parentbased_always_off", null)]
    [InlineData("parentbased_traceidratio", "0.25")]
    [InlineData("unknown", null)]
    public void CreateUsesConfiguredSampler(string? samplerName, string? samplerArgument)
    {
        using var environment = new SamplerEnvironment(samplerName, samplerArgument);
        var expected = CreateExpectedSampler(samplerName, samplerArgument);

        var actual = SamplerFactory.Create();

        Assert.Equal(expected.Description, actual.Description);
    }

    [Fact]
    public void CreateReadsEnvironmentForEveryCall()
    {
        using var alwaysOffEnvironment = new SamplerEnvironment("always_off", null);
        var alwaysOffSampler = SamplerFactory.Create();

        using var alwaysOnEnvironment = new SamplerEnvironment("always_on", null);
        var alwaysOnSampler = SamplerFactory.Create();

        Assert.Equal("AlwaysOffSampler", alwaysOffSampler.Description);
        Assert.Equal("AlwaysOnSampler", alwaysOnSampler.Description);
    }

    private static Sampler CreateExpectedSampler(string? samplerName, string? samplerArgument)
    {
        return samplerName switch
        {
            "always_on" => new AlwaysOnSampler(),
            "always_off" => new AlwaysOffSampler(),
            "traceidratio" => new TraceIdRatioBasedSampler(
                double.Parse(samplerArgument!, CultureInfo.InvariantCulture)),
            "parentbased_always_on" => new ParentBasedSampler(new AlwaysOnSampler()),
            "parentbased_always_off" => new ParentBasedSampler(new AlwaysOffSampler()),
            "parentbased_traceidratio" => new ParentBasedSampler(
                new TraceIdRatioBasedSampler(double.Parse(samplerArgument!, CultureInfo.InvariantCulture))),
            _ => new ParentBasedSampler(new AlwaysOnSampler()),
        };
    }
}
