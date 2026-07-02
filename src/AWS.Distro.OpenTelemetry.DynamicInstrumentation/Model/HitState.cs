// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

/// <summary>
/// Per-instrumentation rate limiting and hit tracking.
/// Fixed-window rate limiter (5 captures/sec) + maxHits + expiry.
/// All operations are thread-safe via Interlocked.
/// </summary>
internal sealed class HitState
{
    private const int DefaultMaxCapturesPerSecond = 5;
    private const int HitCountCap = int.MaxValue - 1;
    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    private readonly int? _maxHits; // null = unlimited (probes)
    private readonly long _expiresAtTicks; // 0 = no expiry
    private readonly int _maxCapturesPerSecond;

    private int _hitCount;
    private int _disabled; // 0 = active, 1 = disabled (atomic via CompareExchange)
    private volatile int _reason; // 0 = none, maps to DisableReason+1
    private long _windowStartTicks;
    private int _windowCount;
    private volatile bool _hitInLastPeriod;

    public HitState(int? maxHits, DateTimeOffset? expiresAt, int maxCapturesPerSecond = DefaultMaxCapturesPerSecond)
    {
        _maxHits = maxHits;
        _maxCapturesPerSecond = maxCapturesPerSecond;
        _expiresAtTicks = expiresAt.HasValue
            ? ComputeExpiresAtTicks(expiresAt.Value)
            : 0;
        _windowStartTicks = Stopwatch.GetTimestamp();
    }

    public int HitCount => Volatile.Read(ref _hitCount);
    public bool IsDisabled => Volatile.Read(ref _disabled) == 1;
    public bool HitInLastPeriod => _hitInLastPeriod;
    public DisableReason? Reason => _reason == 0 ? null : (DisableReason)(_reason - 1);

    /// <summary>
    /// Attempt to record a hit. Returns true if the capture should proceed.
    /// </summary>
    public bool TryHit()
    {
        if (Volatile.Read(ref _disabled) == 1)
            return false;

        // Check expiry
        if (_expiresAtTicks > 0 && Stopwatch.GetTimestamp() > _expiresAtTicks)
        {
            TryDisable(DisableReason.EXPIRED_AT);
            return false;
        }

        // Fixed-window rate limiting (check before counting toward maxHits)
        var now = Stopwatch.GetTimestamp();
        var windowStart = Interlocked.Read(ref _windowStartTicks);

        if (now - windowStart >= TicksPerSecond)
        {
            if (Interlocked.CompareExchange(ref _windowStartTicks, now, windowStart) == windowStart)
            {
                Interlocked.Exchange(ref _windowCount, 1);
                if (!IncrementAndCheckMaxHits())
                    return false;
                _hitInLastPeriod = true;
                return true;
            }
        }

        var windowHits = Interlocked.Increment(ref _windowCount);
        if (windowHits > _maxCapturesPerSecond)
        {
            Interlocked.Decrement(ref _windowCount);
            return false;
        }

        // Only count toward maxHits if rate limiter allowed it
        if (!IncrementAndCheckMaxHits())
            return false;

        _hitInLastPeriod = true;
        return true;
    }

    private bool IncrementAndCheckMaxHits()
    {
        var count = Interlocked.Increment(ref _hitCount);
        if (count > HitCountCap)
        {
            Interlocked.Exchange(ref _hitCount, HitCountCap);
            count = HitCountCap;
        }
        if (_maxHits.HasValue && count > _maxHits.Value)
        {
            TryDisable(DisableReason.MAX_HITS_EXCEEDED);
            return false;
        }
        return true;
    }

    private void TryDisable(DisableReason reason)
    {
        if (Interlocked.CompareExchange(ref _disabled, 1, 0) == 0)
            Volatile.Write(ref _reason, (int)reason + 1);
    }

    /// <summary>
    /// Reset the hit-in-last-period flag. Called by StatusReporter after each reporting cycle.
    /// </summary>
    public void ResetHitInLastPeriod()
    {
        _hitInLastPeriod = false;
    }

    private static long ComputeExpiresAtTicks(DateTimeOffset expiresAt)
    {
        var secondsUntilExpiry = (expiresAt - DateTimeOffset.UtcNow).TotalSeconds;
        if (secondsUntilExpiry <= 0)
            return Stopwatch.GetTimestamp();
        var maxSeconds = long.MaxValue / TicksPerSecond;
        var clampedSeconds = Math.Min(secondsUntilExpiry, maxSeconds);
        return Stopwatch.GetTimestamp() + (long)(clampedSeconds * TicksPerSecond);
    }
}

internal enum DisableReason
{
    MAX_HITS_EXCEEDED,
    EXPIRED_AT
}
