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
        var callId = DIDataStore.RecordEntry(entry);

        var retrieved = DIDataStore.RetrieveEntry(callId);

        retrieved.Should().BeSameAs(entry);
    }

    [Fact]
    public void RetrieveEntry_RemovesIt_SecondRetrieveReturnsNull()
    {
        var callId = DIDataStore.RecordEntry(new PendingEntryData { InstrumentationKey = "k" });

        DIDataStore.RetrieveEntry(callId).Should().NotBeNull();
        DIDataStore.RetrieveEntry(callId).Should().BeNull(); // consumed
    }

    [Fact]
    public void RetrieveEntry_UnknownCallId_ReturnsNull()
    {
        DIDataStore.RetrieveEntry(999999).Should().BeNull();
    }

    [Fact]
    public void RecordEntry_RecursiveCallsOnSameKey_EachEntrySurvivesIndependently()
    {
        // Regression: a recursive/re-entrant call on the same instrumentation key used to overwrite
        // the outer entry (keyed by instrumentation key), so the outer capture was silently dropped.
        // Per-call ids keep each invocation's entry distinct — inner and outer both pair correctly.
        var outer = new PendingEntryData { InstrumentationKey = "k", LocationHash = "outer" };
        var inner = new PendingEntryData { InstrumentationKey = "k", LocationHash = "inner" };
        var outerId = DIDataStore.RecordEntry(outer);   // outer Begin
        var innerId = DIDataStore.RecordEntry(inner);   // inner Begin (recursion)

        // Ends unwind inner-first, then outer — each retrieves its OWN entry, neither is lost.
        DIDataStore.RetrieveEntry(innerId)!.LocationHash.Should().Be("inner");
        DIDataStore.RetrieveEntry(outerId)!.LocationHash.Should().Be("outer");
        outerId.Should().NotBe(innerId);
    }

    [Fact]
    public void RecordRetrieve_ProbedParentFansOutToParallelChildren_NoLossOrCorruption()
    {
        // Regression: AsyncLocal isolates the slot, not the object. A probed parent sets .Value, then its
        // entry dictionary flows by reference to every child in a Parallel.ForEach; each child mutating a
        // plain Dictionary concurrently would drop entries, throw, or hang. A ConcurrentDictionary is safe.
        var parentId = DIDataStore.RecordEntry(new PendingEntryData { InstrumentationKey = "parent" }); // sets .Value, which flows to children

        const int childCount = 500;
        var retrieved = new System.Collections.Concurrent.ConcurrentBag<long>();

        var act = () => Parallel.For(0, childCount, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            // Each child does a full Begin/End pair on the SHARED, parent-provided dictionary.
            var id = DIDataStore.RecordEntry(new PendingEntryData { InstrumentationKey = $"child-{i}" });
            var entry = DIDataStore.RetrieveEntry(id);
            if (entry != null)
            {
                retrieved.Add(id);
            }
        });

        act.Should().NotThrow("concurrent Begin/End on the shared AsyncLocal dictionary must not corrupt it");
        retrieved.Should().HaveCount(childCount, "every child's entry must be recorded and retrieved without loss");
        DIDataStore.RetrieveEntry(parentId).Should().NotBeNull("the parent's own entry must survive the children's concurrent mutations");
    }

    [Fact]
    public void Clear_EmptiesQueueAndEntries()
    {
        DIDataStore.Enqueue(Capture("a"));
        var callId = DIDataStore.RecordEntry(new PendingEntryData { InstrumentationKey = "k" });

        DIDataStore.Clear();

        DIDataStore.Count.Should().Be(0);
        DIDataStore.RetrieveEntry(callId).Should().BeNull();
    }

    [Fact]
    public async Task Entries_FlowAcrossAsyncBoundary_WithinSameContext()
    {
        // AsyncLocal: an entry recorded before an await is visible after it, within the
        // same logical async flow.
        var callId = DIDataStore.RecordEntry(new PendingEntryData { InstrumentationKey = "k", LocationHash = "h" });

        await Task.Yield();

        DIDataStore.RetrieveEntry(callId).Should().NotBeNull();
    }
}
