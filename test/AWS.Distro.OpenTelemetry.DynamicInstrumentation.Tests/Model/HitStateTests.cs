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
        var state = new HitState(maxHits: null, expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-100));

        state.TryHit().Should().BeFalse();
        state.IsDisabled.Should().BeTrue();
        state.Reason.Should().Be(DisableReason.EXPIRED_AT);
    }

    [Fact]
    public void TryHit_NotExpired_Allows()
    {
        var state = new HitState(maxHits: null, expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        state.TryHit().Should().BeTrue();
    }

    [Fact]
    public void TryHit_RateLimiter_BlocksExcessInSameWindow()
    {
        var state = new HitState(maxHits: null, expiresAt: null, maxCapturesPerSecond: 3);

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
        var state = new HitState(maxHits: null, expiresAt: null, maxCapturesPerSecond: 1000);

        for (int i = 0; i < 500; i++)
            state.TryHit().Should().BeTrue();

        state.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public void TryHit_ExactlyMaxHitsAllowed_ThenMaxPlusOneBlocked()
    {
        var state = new HitState(maxHits: 5, expiresAt: null, maxCapturesPerSecond: 100);

        // Exactly maxHits (5) captures are allowed: count reaches maxHits without exceeding it.
        for (int i = 0; i < 5; i++)
            state.TryHit().Should().BeTrue();

        state.HitCount.Should().Be(5);
        state.IsDisabled.Should().BeFalse();

        // The (maxHits + 1)th capture is blocked and disables the state.
        state.TryHit().Should().BeFalse();
        state.IsDisabled.Should().BeTrue();
        state.Reason.Should().Be(DisableReason.MAX_HITS_EXCEEDED);
    }

    [Fact]
    public void TryHit_RateWindowElapses_WindowCounterResets_AllowsAgain()
    {
        // maxHits null (unlimited) isolates the fixed-window rate limiter behavior.
        var state = new HitState(maxHits: null, expiresAt: null, maxCapturesPerSecond: 2);

        state.TryHit().Should().BeTrue();  // window 1, hit 1
        state.TryHit().Should().BeTrue();  // window 1, hit 2
        state.TryHit().Should().BeFalse(); // window 1, hit 3 — rate limited

        // Let the 1-second fixed window elapse so windowCount resets.
        Thread.Sleep(1100);

        state.TryHit().Should().BeTrue();  // window 2, hit 1 — allowed again after reset
        state.TryHit().Should().BeTrue();  // window 2, hit 2
        state.TryHit().Should().BeFalse(); // window 2, hit 3 — rate limited again

        state.IsDisabled.Should().BeFalse(); // rate limiting never disables the state
    }

    [Fact]
    public void TryHit_MaxHits_IsCumulativeTotal_NotPerWindow()
    {
        // maxHits is enforced as a running total across ALL rate windows (see IncrementAndCheckMaxHits),
        // not as a per-second budget. This test pins that behavior.
        var state = new HitState(maxHits: 3, expiresAt: null, maxCapturesPerSecond: 2);

        state.TryHit().Should().BeTrue();  // total 1
        state.TryHit().Should().BeTrue();  // total 2
        state.TryHit().Should().BeFalse(); // rate limited within window 1 (does not count toward maxHits)

        Thread.Sleep(1100); // elapse the rate window so per-second budget resets

        state.TryHit().Should().BeTrue();  // total 3 — new window has rate budget, still under maxHits

        // Even though the fresh window still has rate budget, the cumulative total now exceeds maxHits=3.
        // TODO(parity): confirm JS/Python treat maxHits the same way (cumulative total) rather than
        // resetting the maxHits allowance every per-second window.
        state.TryHit().Should().BeFalse();
        state.IsDisabled.Should().BeTrue();
        state.Reason.Should().Be(DisableReason.MAX_HITS_EXCEEDED);
    }

    [Fact]
    public void Reason_BeforeAnyDisable_IsNull()
    {
        var state = new HitState(maxHits: 5, expiresAt: null);

        state.IsDisabled.Should().BeFalse();
        state.Reason.Should().BeNull();
    }

    [Fact]
    public void TryHit_ExpiresDuringLifetime_TransitionsFromActiveToDisabledExpired()
    {
        var state = new HitState(maxHits: null, expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(300));

        // Active before expiry.
        state.TryHit().Should().BeTrue();
        state.IsDisabled.Should().BeFalse();
        state.Reason.Should().BeNull();

        Thread.Sleep(500); // cross the expiry boundary

        // Expiry is detected lazily on the next TryHit, which disables the state.
        state.TryHit().Should().BeFalse();
        state.IsDisabled.Should().BeTrue();
        state.Reason.Should().Be(DisableReason.EXPIRED_AT);
    }

    [Fact]
    public void TryHit_AfterDisabled_DoesNotIncrementHitCount()
    {
        var state = new HitState(maxHits: 1, expiresAt: null, maxCapturesPerSecond: 100);

        state.TryHit().Should().BeTrue();  // total 1 (== maxHits, allowed)
        state.TryHit().Should().BeFalse(); // total 2 exceeds maxHits, disables
        state.IsDisabled.Should().BeTrue();

        var frozenCount = state.HitCount;

        // Once disabled, TryHit short-circuits before counting.
        state.TryHit().Should().BeFalse();
        state.TryHit().Should().BeFalse();
        state.HitCount.Should().Be(frozenCount);
    }

    [Fact]
    public void Reason_LatchesFirstDisableReason_MaxHits()
    {
        var state = new HitState(maxHits: 1, expiresAt: null, maxCapturesPerSecond: 100);

        state.TryHit();
        state.TryHit(); // disables with MAX_HITS_EXCEEDED

        state.Reason.Should().Be(DisableReason.MAX_HITS_EXCEEDED);

        // Reason stays latched across subsequent blocked calls.
        state.TryHit();
        state.Reason.Should().Be(DisableReason.MAX_HITS_EXCEEDED);
    }
}
