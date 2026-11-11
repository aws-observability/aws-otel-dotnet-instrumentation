// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Unit tests for <see cref="EndpointAggregation" /> — the per-endpoint hot-path
/// state. Pure logic; no emitter or Activity involved.
/// </summary>
public class EndpointAggregationTests
{
    [Fact]
    public void NewAggregation_HasZeroCountersAndDefaultOperation()
    {
        var agg = new EndpointAggregation("/users/{id}", "GET");

        agg.Count.Should().Be(0);
        agg.Faults.Should().Be(0);
        agg.Errors.Should().Be(0);
        agg.SumDurationNs.Should().Be(0);
        agg.Operation.Should().Be("GET /users/{id}", "operation defaults to 'method route'");
    }

    [Fact]
    public void Operation_WhenSet_OverridesDefault()
    {
        var agg = new EndpointAggregation("/users/{id}", "GET") { Operation = "GetUser" };

        agg.Operation.Should().Be("GetUser");
    }

    [Fact]
    public void RecordDuration_IncrementsCountAndSum()
    {
        var agg = new EndpointAggregation("/x", "GET");

        agg.RecordDuration(1_000_000); // 1ms in ns
        agg.RecordDuration(3_000_000); // 3ms

        agg.Count.Should().Be(2);
        agg.SumDurationNs.Should().Be(4_000_000);
    }

    [Fact]
    public void IncrementFaultsAndErrors_TrackedSeparately()
    {
        var agg = new EndpointAggregation("/x", "GET");

        agg.IncrementFaults();
        agg.IncrementFaults();
        agg.IncrementErrors();

        agg.Faults.Should().Be(2);
        agg.Errors.Should().Be(1);
    }

    [Fact]
    public void BuildDurationMetrics_Empty_ReturnsEmpty()
    {
        var agg = new EndpointAggregation("/x", "GET");

        var d = agg.BuildDurationMetrics();

        d.Count.Should().Be(0);
        d.Values.Should().BeEmpty();
        d.Counts.Should().BeEmpty();
    }

    [Fact]
    public void BuildDurationMetrics_ConvertsNanosecondsToMicroseconds()
    {
        var agg = new EndpointAggregation("/x", "GET");

        // 2,000,000 ns = 2,000 µs
        agg.RecordDuration(2_000_000);

        var d = agg.BuildDurationMetrics();

        d.Count.Should().Be(1);
        d.Sum.Should().BeApproximately(2_000.0, 1.0, "ns are converted to µs (÷1000)");
        d.Max.Should().BeApproximately(2_000.0, 200.0); // within ~10% SEH error band
        d.Values.Should().ContainSingle();
        d.Counts.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void RecordError_BuildsFlattenedBreakdownByFailureType()
    {
        var agg = new EndpointAggregation("/x", "POST");

        agg.RecordError("500", "RuntimeError", "handler");
        agg.RecordError("500", "RuntimeError", "handler"); // same key → count 2
        agg.RecordError("500", "TypeError", "validate");   // different exception
        agg.RecordError("400", "ValidationError", "parse"); // different failure type

        var breakdown = agg.BuildErrorBreakdown();

        breakdown.Should().HaveCount(3, "one entry per distinct (failureType, exceptionType)");

        var runtime = breakdown.Single(b => b.Exceptions[0].ExceptionType == "RuntimeError");
        runtime.FailureType.Should().Be("500");
        runtime.Count.Should().Be(2);
        runtime.Exceptions[0].FunctionName.Should().Be("handler");

        breakdown.Should().Contain(b => b.FailureType == "400" && b.Exceptions[0].ExceptionType == "ValidationError");
    }

    [Fact]
    public void AddIncidentExemplar_CapsAtTenPerTriggerType()
    {
        var agg = new EndpointAggregation("/x", "GET");

        for (var i = 0; i < 15; i++)
        {
            agg.AddIncidentExemplar($"snap_{i}", "exception", "high", 1000 + i);
        }

        // A different trigger type has its own independent cap.
        agg.AddIncidentExemplar("snap_lat", "latency", "medium", 9999);

        var exemplars = agg.GetExemplars();

        exemplars.Count(e => e.TriggerType == "exception").Should().Be(10, "capped at 10 per trigger type");
        exemplars.Count(e => e.TriggerType == "latency").Should().Be(1);
    }

    [Fact]
    public void Concurrency_ParallelRecords_AreCountedExactly()
    {
        var agg = new EndpointAggregation("/x", "GET");

        // Hammer from many threads to exercise the Interlocked counters + histogram lock.
        Parallel.For(0, 10_000, _ => agg.RecordDuration(1_000_000));

        agg.Count.Should().Be(10_000);
        agg.SumDurationNs.Should().Be(10_000L * 1_000_000);
        agg.BuildDurationMetrics().Count.Should().Be(10_000);
    }

    /// <summary>
    /// The emitted duration metrics must stay internally consistent even when an endpoint's
    /// latencies span more distinct buckets than the histogram cap allows.
    /// </summary>
    /// <remarks>
    /// <c>RecordDuration</c> bumps <c>count</c>/<c>sumDurationNs</c> for every sample, while
    /// <c>Counts</c> comes from the histogram. When the histogram silently dropped samples at the
    /// 100-bucket cap, the emitted record claimed more requests than its buckets contained,
    /// <c>Sum</c> included durations present in no bucket, and <c>Max</c> understated whenever the
    /// dropped sample was the slowest one. 1ns..10s at 1.1x per bucket needs far more than 100
    /// buckets, so this walks straight through the cap.
    /// </remarks>
    [Fact]
    public void BuildDurationMetrics_WhenLatenciesExceedTheBucketCap_StaysSelfConsistent()
    {
        var agg = new EndpointAggregation("/wide", "GET");

        // Step by 1.2x: wider than the histogram's 1.1x bucket width, so each sample lands in its
        // own bucket, and 1ns..10s at that rate needs ~125 of them against a cap of 100.
        var durations = new List<long>();
        for (var ns = 1L; ns <= 10_000_000_000L; ns = (long)(ns * 1.2) + 1)
        {
            durations.Add(ns);
        }

        durations.Count.Should().BeGreaterThan(
            100, "the sample set must actually exceed the 100-bucket cap for this test to bite");

        foreach (var ns in durations)
        {
            agg.RecordDuration(ns);
        }

        var metrics = agg.BuildDurationMetrics();

        metrics.Count.Should().Be(durations.Count, "every request must be counted");
        metrics.Counts.Sum().Should().Be(
            metrics.Count,
            "bucket counts must account for every counted request; dropping a sample at the cap " +
            "used to leave Sum(Counts) < Count");

        // Durations are nanoseconds internally and microseconds on the wire.
        metrics.Sum.Should().BeApproximately(durations.Sum() / 1000.0, 0.001);
        metrics.Min.Should().BeApproximately(durations.Min() / 1000.0, 0.001);
        metrics.Max.Should().BeApproximately(
            durations.Max() / 1000.0,
            0.001,
            "the slowest request must still set Max even if its own bucket could not be created");
    }
}
