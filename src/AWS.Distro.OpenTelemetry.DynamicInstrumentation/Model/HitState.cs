// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

internal enum DisableReason
{
    MAX_HITS_EXCEEDED,
    EXPIRED_AT,
}

/// <summary>
/// Per-instrumentation rate limiting and hit tracking.
/// Fixed-window rate limiter (5 captures/sec) + maxHits + expiry.
/// All operations are thread-safe via Interlocked.
/// </summary>
internal sealed class HitState(int? maxHits, DateTimeOffset? expiresAt, int maxCapturesPerSecond = HitState.DefaultMaxCapturesPerSecond)
{
    private const int DefaultMaxCapturesPerSecond = 5;
    private const int HitCountCap = int.MaxValue - 1;
    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    private readonly int? maxHits = maxHits; // null = unlimited (probes)
    private readonly long expiresAtTicks = expiresAt.HasValue
            ? ComputeExpiresAtTicks(expiresAt.Value)
            : 0; // 0 = no expiry

    private readonly int maxCapturesPerSecond = maxCapturesPerSecond;

    private int hitCount;
    private int disabled; // 0 = active, 1 = disabled (atomic via CompareExchange)
    private int reason; // 0 = none, maps to DisableReason+1; accessed via Volatile.Read/Write
    private long windowStartTicks = Stopwatch.GetTimestamp();
    private int windowCount;
    private volatile bool hitInLastPeriod;

    public int HitCount => Volatile.Read(ref this.hitCount);

    public bool IsDisabled => Volatile.Read(ref this.disabled) == 1;

    public bool HitInLastPeriod => this.hitInLastPeriod;

    public DisableReason? Reason
    {
        get
        {
            var r = Volatile.Read(ref this.reason);
            return r == 0 ? null : (DisableReason)(r - 1);
        }
    }

    /// <summary>
    /// Attempt to record a hit. Returns true if the capture should proceed.
    /// </summary>
    public bool TryHit()
    {
        if (Volatile.Read(ref this.disabled) == 1)
        {
            return false;
        }

        // Check expiry
        if (this.expiresAtTicks > 0 && Stopwatch.GetTimestamp() > this.expiresAtTicks)
        {
            this.TryDisable(DisableReason.EXPIRED_AT);
            return false;
        }

        // Fixed-window rate limiting (check before counting toward maxHits)
        var now = Stopwatch.GetTimestamp();
        var windowStart = Interlocked.Read(ref this.windowStartTicks);

        if (now - windowStart >= TicksPerSecond)
        {
            if (Interlocked.CompareExchange(ref this.windowStartTicks, now, windowStart) == windowStart)
            {
                Interlocked.Exchange(ref this.windowCount, 1);
                if (!this.IncrementAndCheckMaxHits())
                {
                    return false;
                }

                this.hitInLastPeriod = true;
                return true;
            }
        }

        var windowHits = Interlocked.Increment(ref this.windowCount);
        if (windowHits > this.maxCapturesPerSecond)
        {
            Interlocked.Decrement(ref this.windowCount);
            return false;
        }

        // Only count toward maxHits if rate limiter allowed it
        if (!this.IncrementAndCheckMaxHits())
        {
            return false;
        }

        this.hitInLastPeriod = true;
        return true;
    }

    /// <summary>
    /// Reset the hit-in-last-period flag. Called by StatusReporter after each reporting cycle.
    /// </summary>
    public void ResetHitInLastPeriod()
    {
        this.hitInLastPeriod = false;
    }

    private static long ComputeExpiresAtTicks(DateTimeOffset expiresAt)
    {
        var secondsUntilExpiry = (expiresAt - DateTimeOffset.UtcNow).TotalSeconds;
        if (secondsUntilExpiry <= 0)
        {
            return Stopwatch.GetTimestamp();
        }

        var maxSeconds = long.MaxValue / TicksPerSecond;
        var clampedSeconds = Math.Min(secondsUntilExpiry, maxSeconds);
        return Stopwatch.GetTimestamp() + (long)(clampedSeconds * TicksPerSecond);
    }

    private bool IncrementAndCheckMaxHits()
    {
        var count = Interlocked.Increment(ref this.hitCount);
        if (count > HitCountCap)
        {
            Interlocked.Exchange(ref this.hitCount, HitCountCap);
            count = HitCountCap;
        }

        if (this.maxHits.HasValue && count > this.maxHits.Value)
        {
            this.TryDisable(DisableReason.MAX_HITS_EXCEEDED);
            return false;
        }

        return true;
    }

    private void TryDisable(DisableReason reason)
    {
        if (Interlocked.CompareExchange(ref this.disabled, 1, 0) == 0)
        {
            Volatile.Write(ref this.reason, (int)reason + 1);
        }
    }
}
