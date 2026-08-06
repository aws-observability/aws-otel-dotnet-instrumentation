// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Utils;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Utils;

/// <summary>
/// Tests for <see cref="SehHistogram" /> — the CloudWatch Sparse Exponential
/// Histogram port. Verifies bucketing, aggregation, statistics, the bucket cap,
/// and input validation against the Python reference behavior.
/// </summary>
public class SehHistogramTests
{
    [Fact]
    public void NewHistogram_IsEmpty()
    {
        var h = new SehHistogram();

        h.IsEmpty.Should().BeTrue();
        h.Count.Should().Be(0);
        h.Sum.Should().Be(0);

        var (values, counts) = h.GetValuesAndCounts();
        values.Should().BeEmpty();
        counts.Should().BeEmpty();
    }

    [Fact]
    public void Record_SingleValue_UpdatesCountSumStats()
    {
        var h = new SehHistogram();

        h.Record(1000.0).Should().BeTrue();

        h.IsEmpty.Should().BeFalse();
        h.Count.Should().Be(1);
        h.Sum.Should().Be(1000.0);

        var (min, max, sum, count) = h.GetStatistics();
        min.Should().Be(1000.0);
        max.Should().Be(1000.0);
        sum.Should().Be(1000.0);
        count.Should().Be(1);
    }

    [Fact]
    public void Record_TracksMinAndMaxExactly()
    {
        var h = new SehHistogram();

        h.Record(500.0);
        h.Record(9000.0);
        h.Record(2000.0);

        var (min, max, _, _) = h.GetStatistics();
        min.Should().Be(500.0);
        max.Should().Be(9000.0);
        h.Count.Should().Be(3);
        h.Sum.Should().Be(11500.0);
    }

    [Fact]
    public void Record_ValuesInSameBucket_Aggregate()
    {
        var h = new SehHistogram();

        // Two values within ~10% land in the same exponential bucket.
        h.Record(1000.0);
        h.Record(1050.0);

        var (values, counts) = h.GetValuesAndCounts();
        values.Should().HaveCount(1, "both samples fall in the same ~10% bucket");
        counts[0].Should().Be(2);
    }

    [Fact]
    public void Record_ValuesInDifferentBuckets_ProduceSeparateBuckets()
    {
        var h = new SehHistogram();

        h.Record(100.0);
        h.Record(10000.0); // 100x apart → clearly different buckets

        var (values, counts) = h.GetValuesAndCounts();
        values.Should().HaveCount(2);
        counts.Should().AllSatisfy(c => c.Should().Be(1));
    }

    [Fact]
    public void GetValuesAndCounts_AreSortedAscendingByValue()
    {
        var h = new SehHistogram();

        h.Record(50000.0);
        h.Record(100.0);
        h.Record(2000.0);

        var (values, _) = h.GetValuesAndCounts();

        values.Should().BeInAscendingOrder();
    }

    [Fact]
    public void RecoveredValue_IsWithinTenPercentOfInput()
    {
        var h = new SehHistogram();
        h.Record(1000.0);

        var (values, _) = h.GetValuesAndCounts();

        // Bucket midpoint should be within the ~10% relative error band.
        values[0].Should().BeInRange(900.0, 1100.0);
    }

    [Fact]
    public void Record_Weighted_AddsWeightToCountAndSum()
    {
        var h = new SehHistogram();

        h.Record(1000.0, weight: 3.0);

        h.Count.Should().Be(3.0);
        h.Sum.Should().Be(3000.0);

        var (_, counts) = h.GetValuesAndCounts();
        counts[0].Should().Be(3.0);
    }

    [Fact]
    public void Record_Zero_UsesZeroBucketAndRecoversZero()
    {
        var h = new SehHistogram();

        h.Record(0.0);

        var (values, counts) = h.GetValuesAndCounts();
        values.Should().ContainSingle().Which.Should().Be(0.0);
        counts[0].Should().Be(1);
    }

    [Fact]
    public void Record_BeyondBucketCap_RejectsNewBucketsButKeepsExistingOnes()
    {
        var h = new SehHistogram(maxBuckets: 3);

        // Fill 3 distinct buckets (values far enough apart).
        h.Record(100.0).Should().BeTrue();
        h.Record(10000.0).Should().BeTrue();
        h.Record(1000000.0).Should().BeTrue();

        // A 4th distinct bucket is rejected.
        h.Record(100000000.0).Should().BeFalse();

        // But a value landing in an existing bucket still records.
        h.Record(101.0).Should().BeTrue();

        var (values, _) = h.GetValuesAndCounts();
        values.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Record_InvalidValue_Throws(double value)
    {
        var h = new SehHistogram();

        var act = () => h.Record(value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Record_InvalidWeight_Throws(double weight)
    {
        var h = new SehHistogram();

        var act = () => h.Record(1000.0, weight);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Record_ManyValues_StaysWithinBucketCap()
    {
        var h = new SehHistogram(maxBuckets: 100);

        // 100k samples across a wide range (1ns..60ms). Exponential bucketing
        // keeps the bucket count bounded; samples that would create a new bucket
        // beyond the cap are rejected.
        var rnd = new Random(42);
        var accepted = 0;
        for (var i = 0; i < 100_000; i++)
        {
            if (h.Record(rnd.Next(1, 60_000_000)))
            {
                accepted++;
            }
        }

        var (values, counts) = h.GetValuesAndCounts();

        values.Count.Should().BeLessThanOrEqualTo(100, "the bucket cap bounds memory regardless of sample count");

        // Invariant: only accepted samples are counted, and bucket counts sum to the total count.
        counts.Sum().Should().Be(accepted);
        h.Count.Should().Be(accepted);
    }
}
