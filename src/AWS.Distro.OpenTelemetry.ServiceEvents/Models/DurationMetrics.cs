// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// SEH-histogram derived duration metrics. Serialized to the <c>duration</c> body field with
/// <b>CamelCase</b> keys — <c>{Values, Counts, Max, Min, Count, Sum}</c>. That casing is what the
/// ServiceEvents wire format requires for this field specifically, so it must not be normalized to
/// match the camelCase used elsewhere in the payload.
/// </summary>
/// <param name="Values">Histogram bucket midpoints in microseconds.</param>
/// <param name="Counts">Per-bucket sample counts.</param>
/// <param name="Max">Maximum observed value, microseconds.</param>
/// <param name="Min">Minimum observed value, microseconds.</param>
/// <param name="Count">Total sample count across all buckets.</param>
/// <param name="Sum">Sum of all observed values, microseconds.</param>
public sealed record DurationMetrics(
    IReadOnlyList<double> Values,
    IReadOnlyList<long> Counts,
    double Max,
    double Min,
    long Count,
    double Sum)
{
    /// <summary>Gets an empty histogram (no observations yet).</summary>
    public static DurationMetrics Empty { get; } = new(
        Values: Array.Empty<double>(),
        Counts: Array.Empty<long>(),
        Max: 0,
        Min: 0,
        Count: 0,
        Sum: 0);
}
