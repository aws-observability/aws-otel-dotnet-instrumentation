// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Utils;

/// <summary>
/// CloudWatch SEH (Sparse Exponential Histogram) — a direct port of the Python
/// SDK's <c>utils/seh_histogram.py</c>, which itself ports the SEH1 algorithm
/// used by the CloudWatch agent
/// (<see href="https://github.com/aws/amazon-cloudwatch-agent/blob/main/metric/distribution/seh1/seh1_distribution.go" />).
/// </summary>
/// <remarks>
/// <para>
/// Exponentially-spaced buckets give ~10% relative error per bucket while
/// compressing an unbounded number of samples into at most
/// <see cref="maxBuckets" /> (CloudWatch EMF / OTLP limit: 100). This is how the
/// per-endpoint <c>duration</c> distribution is aggregated on the hot path
/// without storing every sample.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> Callers serialize access (the collector holds a
/// per-aggregation lock around <see cref="Record" />).
/// </para>
/// </remarks>
internal sealed class SehHistogram
{
    /// <summary>Special bucket number for exact-zero values (Int16.MinValue equivalent).</summary>
    private const int BucketForZero = -32768;

    /// <summary>Bucket width factor: <c>ln(1.1)</c> gives ~10% relative error per bucket.</summary>
    private static readonly double BucketFactor = Math.Log(1.1);

    /// <summary>Supported value range: ±2^360 (practically unlimited for durations).</summary>
    private static readonly double MaxValue = Math.Pow(2, 360);
    private static readonly double MinValue = -MaxValue;

    private readonly int maxBuckets;

    // Sparse map of bucket_number -> weighted count (only non-empty buckets stored).
    private readonly Dictionary<int, double> buckets = new();

    private double? minimum;
    private double? maximum;

    /// <summary>Initializes a new instance of the <see cref="SehHistogram"/> class.</summary>
    /// <param name="maxBuckets">Maximum distinct buckets (CloudWatch limit: 100).</param>
    public SehHistogram(int maxBuckets = 100)
    {
        this.maxBuckets = maxBuckets;
    }

    /// <summary>Gets sum of all values × weights.</summary>
    public double Sum { get; private set; }

    /// <summary>Gets total weighted sample count.</summary>
    public double Count { get; private set; }

    /// <summary>Gets a value indicating whether no samples have been recorded.</summary>
    public bool IsEmpty => this.Count == 0;

    /// <summary>
    /// Record a value into the histogram with an optional weight.
    /// </summary>
    /// <param name="value">Value to record (e.g. duration in nanoseconds).</param>
    /// <param name="weight">Weight for this sample (default 1.0).</param>
    /// <returns><c>true</c> if recorded; <c>false</c> if rejected because the bucket cap was reached for a new bucket.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If value/weight is NaN, infinite, weight &lt;= 0, or value is out of the supported range.</exception>
    public bool Record(double value, double weight = 1.0)
    {
        ValidateInput(value, weight);

        var bucketNum = GetBucket(value);

        // Enforce the bucket cap only when this would create a *new* bucket.
        if (!this.buckets.ContainsKey(bucketNum) && this.buckets.Count >= this.maxBuckets)
        {
            return false;
        }

        this.Count += weight;
        this.Sum += value * weight;

        if (this.minimum is null || value < this.minimum)
        {
            this.minimum = value;
        }

        if (this.maximum is null || value > this.maximum)
        {
            this.maximum = value;
        }

        if (this.buckets.TryGetValue(bucketNum, out var existing))
        {
            this.buckets[bucketNum] = existing + weight;
        }
        else
        {
            this.buckets[bucketNum] = weight;
        }

        return true;
    }

    /// <summary>
    /// Get the histogram as parallel arrays of representative values (bucket
    /// midpoints) and their counts, sorted by bucket number ascending.
    /// Compatible with the spec's <c>duration</c> body shape.
    /// </summary>
    public (IReadOnlyList<double> Values, IReadOnlyList<double> Counts) GetValuesAndCounts()
    {
        if (this.buckets.Count == 0)
        {
            return (Array.Empty<double>(), Array.Empty<double>());
        }

        var sortedKeys = new List<int>(this.buckets.Keys);
        sortedKeys.Sort();

        var values = new double[sortedKeys.Count];
        var counts = new double[sortedKeys.Count];

        for (var i = 0; i < sortedKeys.Count; i++)
        {
            var bucketNum = sortedKeys[i];
            values[i] = RecoverValue(bucketNum);
            counts[i] = this.buckets[bucketNum];
        }

        return (values, counts);
    }

    /// <summary>Summary statistics (min, max — 0 when empty).</summary>
    public (double Min, double Max, double Sum, double Count) GetStatistics() =>
        (this.minimum ?? 0.0, this.maximum ?? 0.0, this.Sum, this.Count);

    /// <inheritdoc />
    public override string ToString() =>
        $"SehHistogram(count={this.Count}, buckets={this.buckets.Count}, min={this.minimum}, max={this.maximum}, sum={this.Sum})";

    private static void ValidateInput(double value, double weight)
    {
        if (double.IsNaN(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value cannot be NaN");
        }

        if (double.IsNaN(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight cannot be NaN");
        }

        if (double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value cannot be infinite");
        }

        if (double.IsInfinity(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight cannot be infinite");
        }

        if (weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be positive");
        }

        // 0.1% tolerance, matching the Go/Python implementations.
        const double tolerance = 1.001;
        if (value < MinValue * tolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value is below the minimum supported value");
        }

        if (value > MaxValue * tolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value exceeds the maximum supported value");
        }
    }

    /// <summary>
    /// Calculate the bucket number for a value:
    /// <c>floor(ln(|value|) / ln(1.1))</c>, sign-applied. Zero maps to
    /// <see cref="BucketForZero" />.
    /// </summary>
    private static int GetBucket(double value)
    {
        if (value == 0)
        {
            return BucketForZero;
        }

        var absValue = Math.Abs(value);
        var bucketNum = (int)Math.Floor(Math.Log(absValue) / BucketFactor);

        return value < 0 ? -bucketNum : bucketNum;
    }

    /// <summary>
    /// Recover a representative value from a bucket number — the geometric
    /// midpoint of the bucket's range: <c>exp((bucket + 0.5) × ln(1.1))</c>.
    /// </summary>
    private static double RecoverValue(int bucketNum)
    {
        if (bucketNum == BucketForZero)
        {
            return 0.0;
        }

        return Math.Exp((bucketNum + 0.5) * BucketFactor);
    }
}
