// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Capture;

[Collection("SerialProcessState")]
public class DIDataStoreTests : IDisposable
{
    public DIDataStoreTests() => DIDataStore.Clear();

    public void Dispose() => DIDataStore.Clear();

    private static PendingCapture Capture(string key) =>
        new() { Type = CaptureType.METHOD, InstrumentationKey = key, LocationHash = key };

    [Fact]
    public void EnqueueThenDrain_ReturnsAllInOrder()
    {
        DIDataStore.Enqueue(Capture("a"));
        DIDataStore.Enqueue(Capture("b"));
        DIDataStore.Enqueue(Capture("c"));

        var drained = DIDataStore.Drain();

        drained.Select(c => c.InstrumentationKey).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Drain_EmptiesTheQueue()
    {
        DIDataStore.Enqueue(Capture("a"));

        DIDataStore.Drain().Should().HaveCount(1);
        DIDataStore.Drain().Should().BeEmpty(); // second drain is empty
        DIDataStore.Count.Should().Be(0);
    }

    [Fact]
    public void Count_ReflectsPendingItems()
    {
        DIDataStore.Count.Should().Be(0);
        DIDataStore.Enqueue(Capture("a"));
        DIDataStore.Enqueue(Capture("b"));
        DIDataStore.Count.Should().Be(2);
    }

    [Fact]
    public void RecordThenRetrieveEntry_ReturnsIt()
    {
        var entry = new PendingEntryData { InstrumentationKey = "k", LocationHash = "h" };
        DIDataStore.RecordEntry("k", entry);

        var retrieved = DIDataStore.RetrieveEntry("k");

        retrieved.Should().BeSameAs(entry);
    }

    [Fact]
    public void RetrieveEntry_RemovesIt_SecondRetrieveReturnsNull()
    {
        DIDataStore.RecordEntry("k", new PendingEntryData { InstrumentationKey = "k" });

        DIDataStore.RetrieveEntry("k").Should().NotBeNull();
        DIDataStore.RetrieveEntry("k").Should().BeNull(); // consumed
    }

    [Fact]
    public void RetrieveEntry_UnknownKey_ReturnsNull()
    {
        DIDataStore.RetrieveEntry("never-recorded").Should().BeNull();
    }

    [Fact]
    public void RecordEntry_SameKeyTwice_LastWins()
    {
        // Models a recursive/re-entrant call on the same instrumentation key: the second
        // entry overwrites the first (documented behavior of the dictionary-per-context).
        var first = new PendingEntryData { InstrumentationKey = "k", LocationHash = "first" };
        var second = new PendingEntryData { InstrumentationKey = "k", LocationHash = "second" };
        DIDataStore.RecordEntry("k", first);
        DIDataStore.RecordEntry("k", second);

        DIDataStore.RetrieveEntry("k")!.LocationHash.Should().Be("second");
    }

    [Fact]
    public void Clear_EmptiesQueueAndEntries()
    {
        DIDataStore.Enqueue(Capture("a"));
        DIDataStore.RecordEntry("k", new PendingEntryData { InstrumentationKey = "k" });

        DIDataStore.Clear();

        DIDataStore.Count.Should().Be(0);
        DIDataStore.RetrieveEntry("k").Should().BeNull();
    }

    [Fact]
    public async Task Entries_FlowAcrossAsyncBoundary_WithinSameContext()
    {
        // AsyncLocal: an entry recorded before an await is visible after it, within the
        // same logical async flow.
        DIDataStore.RecordEntry("k", new PendingEntryData { InstrumentationKey = "k", LocationHash = "h" });

        await Task.Yield();

        DIDataStore.RetrieveEntry("k").Should().NotBeNull();
    }
}
