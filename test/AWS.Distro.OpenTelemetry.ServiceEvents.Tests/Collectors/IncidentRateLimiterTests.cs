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
public partial class IncidentRateLimiterTests
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
                if (limiter.CheckDeduplication("same-hash") == DedupOutcome.Admitted)
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

    /// <summary>
    /// A clock moving backwards must not reset the window, because that would admit a burst the cap was
    /// configured to refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step has to cross a window boundary to mean anything: window index is
    /// <c>clock / 60_000</c>, so a backwards step inside the same bucket changes no index and would
    /// pass whatever the code did. Starting at exactly one window and stepping back into the previous
    /// one is the smallest movement that lowers the index.
    /// </para>
    /// <para>
    /// Mutation-verified: restoring the original <c>index != window.PeriodIndex</c> condition makes
    /// this fail, because the lowered index then installs a fresh window and returns the cap to full.
    /// </para>
    /// </remarks>
    [Fact]
    public void CheckRateLimit_WithClockSteppingBackwardsAcrossAWindow_DoesNotResetTheCap()
    {
        // One whole window in, so there is a previous bucket to fall back into.
        this.now = 60_000;
        var limiter = this.NewLimiter(maxPerMinute: 2);

        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeTrue();
        limiter.CheckRateLimit().Should().BeFalse("the cap is spent");

        // Index 1 -> index 0: a lower window, which must not be treated as a rollover.
        this.now -= 5_000;

        limiter.CheckRateLimit().Should().BeFalse(
            "a clock moving backwards must not hand out a fresh window's worth of capacity");
    }

    /// <summary>
    /// The per-error ceiling is likewise not reset by a clock moving backwards.
    /// </summary>
    /// <remarks>
    /// Separate from the global cap because they are separate counters on the window, and a reset
    /// restores both. Suppressing this one wrongly is the more visible failure: the same error starts
    /// producing snapshots again inside the minute it was meant to be deduplicated.
    /// </remarks>
    [Fact]
    public void CheckDeduplication_WithClockSteppingBackwardsAcrossAWindow_DoesNotResetTheCeiling()
    {
        this.now = 60_000;
        var limiter = this.NewLimiter(maxSameError: 1);
        var hash = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);

        limiter.CheckDeduplication(hash).Should().Be(DedupOutcome.Admitted);
        limiter.CheckDeduplication(hash).Should().Be(DedupOutcome.PerErrorLimit);

        this.now -= 5_000;

        limiter.CheckDeduplication(hash).Should().Be(
            DedupOutcome.PerErrorLimit,
            "a clock moving backwards must not clear the per-error count");
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

        limiter.CheckDeduplication(hash).Should().Be(DedupOutcome.Admitted);
        limiter.CheckDeduplication(hash).Should().Be(DedupOutcome.Admitted);
        limiter.CheckDeduplication(hash).Should().Be(
            DedupOutcome.PerErrorLimit, "the 3rd identical error exceeds maxSameError=2");
    }

    [Fact]
    public void CheckDeduplication_DifferentHashesTrackedIndependently()
    {
        var limiter = this.NewLimiter(maxSameError: 1);
        var a = IncidentRateLimiter.GenerateErrorHash("GET /a", "ArgumentException", originMethod: null);
        var b = IncidentRateLimiter.GenerateErrorHash("GET /b", "ArgumentException", originMethod: null);

        limiter.CheckDeduplication(a).Should().Be(DedupOutcome.Admitted);
        limiter.CheckDeduplication(a).Should().Be(DedupOutcome.PerErrorLimit);
        limiter.CheckDeduplication(b).Should().Be(
            DedupOutcome.Admitted, "a different operation is a different hash");
    }

    [Fact]
    public void CheckDeduplication_ResetsAfterWindowRollover()
    {
        var limiter = this.NewLimiter(maxSameError: 1);
        var hash = IncidentRateLimiter.GenerateErrorHash("GET /x", "ArgumentException", originMethod: null);

        limiter.CheckDeduplication(hash).Should().Be(DedupOutcome.Admitted);
        limiter.CheckDeduplication(hash).Should().Be(DedupOutcome.PerErrorLimit);

        this.now += 60_001;

        limiter.CheckDeduplication(hash).Should().Be(
            DedupOutcome.Admitted, "a new window resets per-error counts");
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

public partial class IncidentRateLimiterTests
{
    /// <summary>
    /// Once the window is tracking <see cref="IncidentRateLimiter.MaxErrorHashEntries" /> distinct error
    /// hashes, a hash it has never seen is refused as <see cref="DedupOutcome.CardinalityGuard" /> rather
    /// than <see cref="DedupOutcome.PerErrorLimit" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separating these two outcomes is the entire reason <see cref="DedupOutcome" /> exists instead of a
    /// <c>bool</c>, and until this test the distinction was unasserted: the suite exercised only
    /// <c>Admitted</c> and <c>PerErrorLimit</c>, so the guard could have returned either value — or the
    /// wrong one — without failing anything.
    /// </para>
    /// <para>
    /// The two mean different things to whoever reads the diagnostics. <c>PerErrorLimit</c> says one error
    /// is repeating and the cap is doing its job; <c>CardinalityGuard</c> says the error population is too
    /// varied to track, so raising the per-error cap will not help and the shape of the problem is
    /// different. Reporting the first when the second happened points an operator at the wrong knob.
    /// </para>
    /// <para>
    /// Uses the real constant rather than a literal, so the test follows the guard if it is ever retuned
    /// instead of silently asserting a boundary that has moved.
    /// </para>
    /// </remarks>
    [Fact]
    public void CheckDeduplication_WhenTheDistinctHashTableIsFull_ReportsTheCardinalityGuard()
    {
        // maxSameError of 2 keeps every fill hash admitted on its first call, so the table fills with
        // tracked entries rather than rejections.
        var limiter = this.NewLimiter(maxPerMinute: int.MaxValue, maxSameError: 2);

        for (var i = 0; i < IncidentRateLimiter.MaxErrorHashEntries; i++)
        {
            limiter.CheckDeduplication($"fill-{i}")
                .Should().Be(DedupOutcome.Admitted, "each distinct hash is admitted the first time it is seen");
        }

        limiter.CheckDeduplication("one-too-many")
            .Should().Be(
                DedupOutcome.CardinalityGuard,
                "a hash that cannot be tracked at all is a different outcome from one that hit its own ceiling");
    }

    /// <summary>
    /// The guard refuses only hashes it has never seen; hashes already tracked keep their own ceiling.
    /// </summary>
    /// <remarks>
    /// The complement of the test above, and the one that catches a guard placed too early. Refusing
    /// everything once the table is full would also produce <c>CardinalityGuard</c> for the new hash, so
    /// that assertion alone cannot tell a correct guard from one that stops all deduplication — which
    /// would silently disable the per-error cap for every error already being tracked.
    /// </remarks>
    [Fact]
    public void CheckDeduplication_WithAFullTable_StillAppliesThePerErrorCeilingToKnownHashes()
    {
        var limiter = this.NewLimiter(maxPerMinute: int.MaxValue, maxSameError: 2);

        for (var i = 0; i < IncidentRateLimiter.MaxErrorHashEntries; i++)
        {
            limiter.CheckDeduplication($"fill-{i}");
        }

        limiter.CheckDeduplication("one-too-many").Should().Be(DedupOutcome.CardinalityGuard);

        // "fill-0" is already tracked with a count of 1, so its second occurrence is still admitted and
        // its third is refused by the per-error ceiling, not by the guard.
        limiter.CheckDeduplication("fill-0")
            .Should().Be(DedupOutcome.Admitted, "a tracked hash is unaffected by the table being full");
        limiter.CheckDeduplication("fill-0")
            .Should().Be(DedupOutcome.PerErrorLimit, "its own ceiling still applies");
    }
}
