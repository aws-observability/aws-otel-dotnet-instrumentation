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

    // Regression: the old code degraded at `failedAttempts >= BackoffDelaysMs.Length` (== 3),
    // which flipped degraded ON the 3rd attempt and skipped the 120s tier entirely
    // (10s→30s→300s). Degrade must only happen AFTER all 3 tiers are used.
    [Theory]
    [InlineData(1, false)] // 10s tier
    [InlineData(2, false)] // 30s tier
    [InlineData(3, false)] // 120s tier — must NOT be degraded yet
    [InlineData(4, true)]  // all tiers exhausted → degraded
    [InlineData(5, true)]
    public void ShouldDegrade_OnlyAfterAllBackoffTiers(int failedAttempts, bool expected)
    {
        ConfigurationPoller.ShouldDegrade(failedAttempts).Should().Be(expected);
    }

    [Fact]
    public void BackoffSequence_ReachesAllThreeTiersBeforeDegrading()
    {
        // Walk the exact sequence PollLoop produces: each failure increments failedAttempts,
        // degraded is derived from ShouldDegrade, then ComputeSleepMs picks the delay.
        int[] observed = new int[4];
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var degraded = ConfigurationPoller.ShouldDegrade(attempt);
            observed[attempt - 1] = ConfigurationPoller.ComputeSleepMs(60_000, attempt, degraded);
        }

        observed[0].Should().Be(10_000);
        observed[1].Should().Be(30_000);
        observed[2].Should().Be(120_000); // the tier the off-by-one used to skip
        observed[3].Should().BeInRange(300_000, 375_000); // degraded
    }
}
