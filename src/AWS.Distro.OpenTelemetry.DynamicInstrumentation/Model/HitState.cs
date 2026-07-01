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
    private static readonly long TicksPerSecond = Stopwatch.Frequency;

    private readonly int _maxHits;
    private readonly long _expiresAtTicks; // 0 = no expiry
    private readonly int _maxCapturesPerSecond;

    private int _hitCount;
    private volatile bool _disabled;
    private long _windowStartTicks;
    private int _windowCount;
    private volatile bool _hitInLastPeriod;

    public HitState(int maxHits, DateTimeOffset? expiresAt, int maxCapturesPerSecond = DefaultMaxCapturesPerSecond)
    {
        _maxHits = maxHits;
        _maxCapturesPerSecond = maxCapturesPerSecond;
        _expiresAtTicks = expiresAt.HasValue
            ? Stopwatch.GetTimestamp() + (long)((expiresAt.Value - DateTimeOffset.UtcNow).TotalSeconds * TicksPerSecond)
            : 0;
        _windowStartTicks = Stopwatch.GetTimestamp();
    }

    public int HitCount => _hitCount;
    public bool IsDisabled => _disabled;
    public bool HitInLastPeriod => _hitInLastPeriod;
    public DisableReason? Reason { get; private set; }

    /// <summary>
    /// Attempt to record a hit. Returns true if the capture should proceed.
    /// </summary>
    public bool TryHit()
    {
        if (_disabled)
            return false;

        // Check expiry
        if (_expiresAtTicks > 0 && Stopwatch.GetTimestamp() > _expiresAtTicks)
        {
            _disabled = true;
            Reason = DisableReason.EXPIRED_AT;
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
                var c = Interlocked.Increment(ref _hitCount);
                if (c > _maxHits)
                {
                    _disabled = true;
                    Reason = DisableReason.MAX_HITS_EXCEEDED;
                    return false;
                }
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
        var count = Interlocked.Increment(ref _hitCount);
        if (count > _maxHits)
        {
            _disabled = true;
            Reason = DisableReason.MAX_HITS_EXCEEDED;
            return false;
        }

        _hitInLastPeriod = true;
        return true;
    }

    /// <summary>
    /// Reset the hit-in-last-period flag. Called by StatusReporter after each reporting cycle.
    /// </summary>
    public void ResetHitInLastPeriod()
    {
        _hitInLastPeriod = false;
    }
}

internal enum DisableReason
{
    MAX_HITS_EXCEEDED,
    EXPIRED_AT
}
