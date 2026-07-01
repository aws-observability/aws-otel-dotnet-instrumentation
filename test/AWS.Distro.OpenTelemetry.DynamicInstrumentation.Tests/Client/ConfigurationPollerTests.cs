// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Client;

public class ConfigurationPollerTests
{
    [Fact]
    public void ComputeSleepMs_NormalInterval_ReturnsBaseWithJitter()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 0, degraded: false);

        // Base is 60s, jitter adds up to 25% = 60000 to 75000
        result.Should().BeInRange(60_000, 75_000);
    }

    [Fact]
    public void ComputeSleepMs_FirstFailure_Returns10Seconds()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 1, degraded: false);

        result.Should().Be(10_000);
    }

    [Fact]
    public void ComputeSleepMs_SecondFailure_Returns30Seconds()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 2, degraded: false);

        result.Should().Be(30_000);
    }

    [Fact]
    public void ComputeSleepMs_ThirdFailure_Returns120Seconds()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 3, degraded: false);

        result.Should().Be(120_000);
    }

    [Fact]
    public void ComputeSleepMs_Degraded_Returns300SecondsWithJitter()
    {
        var result = ConfigurationPoller.ComputeSleepMs(60_000, failedAttempts: 5, degraded: true);

        // Degraded base is 300s, jitter adds up to 25% = 300000 to 375000
        result.Should().BeInRange(300_000, 375_000);
    }

    [Fact]
    public void AddJitter_AlwaysPositive_NeverExceeds25Percent()
    {
        for (int i = 0; i < 100; i++)
        {
            var result = ConfigurationPoller.AddJitter(60_000);
            result.Should().BeInRange(60_000, 75_000);
        }
    }

    [Fact]
    public void AddJitter_ZeroBase_ReturnsZero()
    {
        var result = ConfigurationPoller.AddJitter(0);
        result.Should().Be(0);
    }
}
