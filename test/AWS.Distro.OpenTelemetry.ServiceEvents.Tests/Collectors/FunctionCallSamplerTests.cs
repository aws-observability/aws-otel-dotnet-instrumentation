// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Tests for <see cref="FunctionCallSampler" /> — the three sampling modes
/// (<c>always</c> / <c>never</c> / <c>auto</c>; <c>adaptive</c> is no longer accepted).
/// </summary>
public class FunctionCallSamplerTests
{
    [Fact]
    public void Always_SamplesEveryCall()
    {
        var sampler = new FunctionCallSampler(new ServiceEventsConfig { SamplingMode = "always" });

        for (var i = 0; i < 10; i++)
        {
            sampler.ShouldSample("fn").Should().BeTrue();
        }
    }

    [Fact]
    public void Never_SamplesNothing()
    {
        var sampler = new FunctionCallSampler(new ServiceEventsConfig { SamplingMode = "never" });

        sampler.ShouldSample("fn").Should().BeFalse();
    }

    [Fact]
    public void UnrecognizedMode_DefaultsToAlways()
    {
        // The config default is "always"; any unrecognized value also records every call.
        var sampler = new FunctionCallSampler(new ServiceEventsConfig { SamplingMode = "bogus" });

        sampler.ShouldSample("fn").Should().BeTrue();
    }

    [Fact]
    public void Auto_ThreeTierDownsample_FollowsThresholds()
    {
        var sampler = new FunctionCallSampler(new ServiceEventsConfig
        {
            SamplingMode = "auto",
            SampleTier1Threshold = 3,
            SampleTier2Threshold = 6,
            SampleTier2Rate = 2,
            SampleTier3Rate = 3,
        });

        // Tier 1: calls 1..3 always sampled.
        sampler.ShouldSample("fn").Should().BeTrue();  // 1
        sampler.ShouldSample("fn").Should().BeTrue();  // 2
        sampler.ShouldSample("fn").Should().BeTrue();  // 3

        // Tier 2 (4..6): every 2nd.
        sampler.ShouldSample("fn").Should().BeTrue();   // 4 % 2 == 0
        sampler.ShouldSample("fn").Should().BeFalse();  // 5 % 2 != 0
        sampler.ShouldSample("fn").Should().BeTrue();   // 6 % 2 == 0

        // Tier 3 (>6): every 3rd.
        sampler.ShouldSample("fn").Should().BeFalse();  // 7 % 3 != 0
        sampler.ShouldSample("fn").Should().BeFalse();  // 8 % 3 != 0
        sampler.ShouldSample("fn").Should().BeTrue();   // 9 % 3 == 0
    }

    [Fact]
    public void Auto_CountersArePerFunction()
    {
        var sampler = new FunctionCallSampler(new ServiceEventsConfig
        {
            SamplingMode = "auto",
            SampleTier1Threshold = 1,
            SampleTier2Threshold = 1,
            SampleTier3Rate = 100,
        });

        // Each function name has its own counter, so the first call of each is tier 1.
        sampler.ShouldSample("fnA").Should().BeTrue();   // fnA #1
        sampler.ShouldSample("fnB").Should().BeTrue();   // fnB #1
        sampler.ShouldSample("fnA").Should().BeFalse();  // fnA #2 → tier3, 2 % 100 != 0
    }
}
