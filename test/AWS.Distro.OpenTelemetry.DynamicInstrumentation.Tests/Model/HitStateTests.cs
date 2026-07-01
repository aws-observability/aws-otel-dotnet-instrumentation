// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Model;

public class HitStateTests
{
    [Fact]
    public void TryHit_FirstCall_ReturnsTrue()
    {
        var state = new HitState(maxHits: 100, expiresAt: null);

        state.TryHit().Should().BeTrue();
        state.HitCount.Should().Be(1);
    }

    [Fact]
    public void TryHit_UnderMaxHits_AllowsAll()
    {
        var state = new HitState(maxHits: 3, expiresAt: null, maxCapturesPerSecond: 100);

        state.TryHit().Should().BeTrue();
        state.TryHit().Should().BeTrue();
        state.TryHit().Should().BeTrue();
        state.HitCount.Should().Be(3);
    }

    [Fact]
    public void TryHit_ExceedsMaxHits_DisablesAndReturnsFalse()
    {
        var state = new HitState(maxHits: 2, expiresAt: null, maxCapturesPerSecond: 100);

        state.TryHit().Should().BeTrue();
        state.TryHit().Should().BeTrue();
        state.TryHit().Should().BeFalse();

        state.IsDisabled.Should().BeTrue();
        state.Reason.Should().Be(DisableReason.MAX_HITS_EXCEEDED);
    }

    [Fact]
    public void TryHit_AfterDisabled_AlwaysReturnsFalse()
    {
        var state = new HitState(maxHits: 1, expiresAt: null, maxCapturesPerSecond: 100);

        state.TryHit(); // hit 1 — allowed
        state.TryHit(); // hit 2 — disables

        state.TryHit().Should().BeFalse();
        state.TryHit().Should().BeFalse();
    }

    [Fact]
    public void TryHit_Expired_DisablesAndReturnsFalse()
    {
        var state = new HitState(maxHits: int.MaxValue, expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-100));

        state.TryHit().Should().BeFalse();
        state.IsDisabled.Should().BeTrue();
        state.Reason.Should().Be(DisableReason.EXPIRED_AT);
    }

    [Fact]
    public void TryHit_NotExpired_Allows()
    {
        var state = new HitState(maxHits: int.MaxValue, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        state.TryHit().Should().BeTrue();
    }

    [Fact]
    public void TryHit_RateLimiter_BlocksExcessInSameWindow()
    {
        var state = new HitState(maxHits: int.MaxValue, expiresAt: null, maxCapturesPerSecond: 3);

        state.TryHit().Should().BeTrue();  // 1
        state.TryHit().Should().BeTrue();  // 2
        state.TryHit().Should().BeTrue();  // 3
        state.TryHit().Should().BeFalse(); // 4 — rate limited

        state.IsDisabled.Should().BeFalse(); // not disabled, just rate limited
        state.HitCount.Should().Be(3); // rate-limited calls don't count toward maxHits
    }

    [Fact]
    public void HitInLastPeriod_SetOnHit_ResetByCaller()
    {
        var state = new HitState(maxHits: 100, expiresAt: null);

        state.HitInLastPeriod.Should().BeFalse();
        state.TryHit();
        state.HitInLastPeriod.Should().BeTrue();
        state.ResetHitInLastPeriod();
        state.HitInLastPeriod.Should().BeFalse();
    }

    [Fact]
    public void TryHit_UnlimitedMaxHits_NeverDisablesFromCount()
    {
        var state = new HitState(maxHits: int.MaxValue, expiresAt: null, maxCapturesPerSecond: 1000);

        for (int i = 0; i < 500; i++)
            state.TryHit().Should().BeTrue();

        state.IsDisabled.Should().BeFalse();
    }
}
