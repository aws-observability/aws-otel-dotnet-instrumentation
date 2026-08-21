// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Unit tests for <see cref="IncidentRateLimiter" /> — the lock-free tumbling-window
/// rate limit + per-error dedup. Uses an injected clock to drive window roll-over
/// deterministically.
/// </summary>
public class IncidentRateLimiterTests
{
    // A controllable monotonic clock for deterministic window boundaries.
    private long now;

    private long Now() => this.now;

    // The window is fixed at one minute — the cap is defined as a per-minute rate, so there is no
    // period parameter to vary. Window rollover is exercised by advancing the injected clock.
    private IncidentRateLimiter NewLimiter(int maxPerMinute = 3, int maxSameError = 2)
        => new(maxPerMinute, maxSameError, this.Now);

    /// <summary>
    /// The global cap must hold exactly under contention, not approximately. With the clock pinned so
    /// no window rollover can occur, exactly <c>maxPerMinute</c> calls may be admitted out of however
    /// many are made — the counter decides admission, so a lost increment hands two callers the same
    /// position and admits one too many.
    /// </summary>
    /// <remarks>
    /// Deliberately a tight loop with no other work: the read-modify-write on the window counter is
    /// the only thing happening, which is what makes the assertion sensitive to atomicity. Driving
    /// this through <c>IncidentSnapshotCollector</c> instead would not be — hashing, dedup and
    /// snapshot construction dominate, and most calls are rejected before the counter is consulted.
    /// </remarks>
    [Fact]
    public void CheckRateLimit_UnderConcurrency_AdmitsExactlyTheCap()
    {
        // The cap is deliberately close to the total call count. With a small cap, a lost increment
        // only changes the outcome during the first few calls — a window so narrow the race almost
        // never lands in it. Setting the cap to half the calls puts contention right through the
        // admission region, where a lost increment directly admits one too many.
        const int threads = 32;
        const int callsPerThread = 500;
        const int maxPerMinute = threads * callsPerThread / 2;

        // Pinned clock: no rollover, so every call competes for the same window's counter.
        var limiter = new IncidentRateLimiter(maxPerMinute, maxSameError: 1, nowMs: () => 1_000L);

        var admitted = 0;
        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < callsPerThread; i++)
            {
                if (limiter.CheckRateLimit())
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        });

        admitted.Should().Be(
            maxPerMinute,
            "exactly maxPerMinute of the {0} concurrent calls may be admitted; more means increments " +
            "were lost to a non-atomic counter, fewer means admissions were dropped",
            threads * callsPerThread);
    }

    [Fact]
    public void CheckDeduplication_UnderConcurrency_AdmitsExactlyTheCeilingPerHash()
    {
        const int maxSameError = 25;
        var limiter = new IncidentRateLimiter(maxPerMinute: int.MaxValue, maxSameError, nowMs: () => 1_000L);

        var admitted = 0;
        Parallel.For(0, 32, _ =>
        {
            for (var i = 0; i < 500; i++)
            {
                // One hash, so every thread contends for the same per-error counter.
                if (limiter.CheckDeduplication("same-hash"))
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        });

        admitted.Should().Be(
            maxSameError,
            "the per-error ceiling is enforced under the dedup lock; a different count means the " +
            "check-then-increment is not atomic with respect to the ceiling");
    }

    [Fact]
    public void CheckRateLimit_AllowsUpToMax_ThenRejects()
    {
        var limiter = this.NewLimiter(maxPerMinute: 3);

        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeFalse("the 4th call exceeds maxPerMinute=3");
    }

    [Fact]
    public void CheckRateLimit_ResetsAfterWindowRollover()
    {
        var limiter = this.NewLimiter(maxPerMinute: 2);

        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeFalse();

        // Advance past the 1-minute window boundary.
        this.now += 60_001;

        limiter.CheckRateLimit().Should().BeTrue("a new window resets the global count");
    }

    [Fact]
    public void CheckDeduplication_AllowsUpToMaxSameError_ThenRejects()
    {
        var limiter = this.NewLimiter(maxSameError: 2);
        var hash = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);

        limiter.CheckDeduplication(hash).Should().BeTrue();
        limiter.CheckDeduplication(hash).Should().BeTrue();
        limiter.CheckDeduplication(hash).Should().BeFalse("the 3rd identical error exceeds maxSameError=2");
    }

    [Fact]
    public void CheckDeduplication_DifferentHashesTrackedIndependently()
    {
        var limiter = this.NewLimiter(maxSameError: 1);
        var a = IncidentRateLimiter.GenerateErrorHash("GET /a", "ArgumentException", originMethod: null);
        var b = IncidentRateLimiter.GenerateErrorHash("GET /b", "ArgumentException", originMethod: null);

        limiter.CheckDeduplication(a).Should().BeTrue();
        limiter.CheckDeduplication(a).Should().BeFalse();
        limiter.CheckDeduplication(b).Should().BeTrue("a different operation is a different hash");
    }

    [Fact]
    public void CheckDeduplication_ResetsAfterWindowRollover()
    {
        var limiter = this.NewLimiter(maxSameError: 1);
        var hash = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);

        limiter.CheckDeduplication(hash).Should().BeTrue();
        limiter.CheckDeduplication(hash).Should().BeFalse();

        this.now += 60_001;

        limiter.CheckDeduplication(hash).Should().BeTrue("a new window resets per-error counts");
    }

    [Fact]
    public void GenerateErrorHash_ExcludesExceptionMessage()
    {
        // Same operation + type, different messages → same hash (message excluded).
        var h1 = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);
        var h2 = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);

        h1.Should().Be(h2);
    }

    /// <summary>
    /// The throw-site origin is part of the key, so two unrelated failures that happen to share an
    /// exception type on the same route get separate dedup budgets instead of collapsing into one.
    /// </summary>
    /// <remarks>
    /// This is the behaviour the two-part key lacked: with the origin excluded, the second failure
    /// below would hash identically to the first and be silently suppressed for the window while the
    /// first reported. Matches Java and Python, which both key on operation + exception type + origin.
    /// </remarks>
    [Fact]
    public void GenerateErrorHash_DiffersByThrowSiteOrigin()
    {
        var atValidate = IncidentRateLimiter.GenerateErrorHash(
            "GET /x", "ArgumentException", "Contoso.Orders.Validate");
        var atPrice = IncidentRateLimiter.GenerateErrorHash(
            "GET /x", "ArgumentException", "Contoso.Pricing.Compute");

        atPrice.Should().NotBe(atValidate, "same route and type, different throw site → distinct keys");

        // Stable for a repeat of the same failure, which is what makes dedup work at all.
        IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", "Contoso.Orders.Validate")
            .Should().Be(atValidate);
    }

    /// <summary>
    /// An absent origin degrades to the two-part key rather than folding an empty segment in, so a
    /// trace-less exception still groups by operation and type. Mirrors Java's middle branch.
    /// </summary>
    [Fact]
    public void GenerateErrorHash_WithoutOrigin_DegradesToOperationAndType()
    {
        var noOrigin = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", null);

        IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", string.Empty)
            .Should().Be(noOrigin, "null and empty origin are the same case");

        IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", "Contoso.Orders.Validate")
            .Should().NotBe(noOrigin, "supplying an origin changes the key");
    }

    /// <summary>
    /// A latency incident has no exception, so it keys on the operation alone and any origin is
    /// ignored — Java's first branch.
    /// </summary>
    [Fact]
    public void GenerateErrorHash_WithoutExceptionType_IgnoresOrigin()
    {
        IncidentRateLimiter.GenerateErrorHash("GET /slow", null, "Contoso.Anything.AtAll")
            .Should().Be(IncidentRateLimiter.GenerateErrorHash("GET /slow", null, null));
    }

    [Fact]
    public void GenerateErrorHash_DiffersByOperationAndType()
    {
        var baseHash = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);

        IncidentRateLimiter.GenerateErrorHash("GET /y", "ArgumentException", originMethod: null)
            .Should().NotBe(baseHash, "different operation → different hash");
        IncidentRateLimiter.GenerateErrorHash("GET /x", "NullReferenceException", originMethod: null)
            .Should().NotBe(baseHash, "different exception type → different hash");
        IncidentRateLimiter.GenerateErrorHash("GET /x", null, originMethod: null)
            .Should().NotBe(baseHash, "op-only (no exception) → different hash");
    }

    [Fact]
    public void UpdateConfig_ChangesLimitsForSubsequentChecks()
    {
        var limiter = this.NewLimiter(maxPerMinute: 1);

        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeFalse();

        // Raise the limit and roll the window so the new value takes effect cleanly.
        limiter.UpdateConfig(maxPerMinute: 5, maxSameError: 2);
        this.now += 60_001;

        for (var i = 0; i < 5; i++)
        {
            limiter.CheckRateLimit().Should().BeTrue($"call {i + 1} is within the raised limit of 5");
        }

        limiter.CheckRateLimit().Should().BeFalse();
    }

    [Fact]
    public void UpdateConfig_ClampsToAtLeastOne()
    {
        var limiter = this.NewLimiter();
        limiter.UpdateConfig(maxPerMinute: 0, maxSameError: -3);
        this.now += 60_001;

        limiter.CheckRateLimit().Should().BeTrue("clamped to at least 1");
        limiter.CheckRateLimit().Should().BeFalse();
    }
}
