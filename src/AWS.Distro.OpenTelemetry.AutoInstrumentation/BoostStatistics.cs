// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal sealed class BoostStatistics
{
    private long totalCount;
    private long anomalyCount;
    private long sampledAnomalyCount;

    internal long TotalCount => this.totalCount;

    internal long AnomalyCount => this.anomalyCount;

    internal long SampledAnomalyCount => this.sampledAnomalyCount;

    internal void IncrementTotal() => Interlocked.Increment(ref this.totalCount);

    internal void IncrementAnomaly() => Interlocked.Increment(ref this.anomalyCount);

    internal void IncrementSampledAnomaly() => Interlocked.Increment(ref this.sampledAnomalyCount);

    internal BoostStatistics SnapshotAndReset()
    {
        var snapshot = new BoostStatistics();
        snapshot.totalCount = Interlocked.Exchange(ref this.totalCount, 0);
        snapshot.anomalyCount = Interlocked.Exchange(ref this.anomalyCount, 0);
        snapshot.sampledAnomalyCount = Interlocked.Exchange(ref this.sampledAnomalyCount, 0);
        return snapshot;
    }
}
