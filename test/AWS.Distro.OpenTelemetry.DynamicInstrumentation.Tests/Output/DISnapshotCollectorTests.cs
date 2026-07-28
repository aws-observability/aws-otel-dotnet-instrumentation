// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

// Mutates the static DIDataStore queue, which is shared process-wide. Join the SerialProcessState
// collection so these never run in parallel with other tests that enqueue/drain it (e.g. the capture
// pipeline and concurrency-stress suites), preventing cross-test queue pollution.
[Collection("SerialProcessState")]
public class DISnapshotCollectorTests
{
    [Fact]
    public async Task Collector_DrainsQueueAndEmits()
    {
        var emitted = new List<PendingCapture>();
        var emitter = new TestEmitter(emitted);
        using var cts = new CancellationTokenSource();

        DIDataStore.Clear();
        DIDataStore.Enqueue(new PendingCapture { InstrumentationKey = "key1", LocationHash = "hash1" });
        DIDataStore.Enqueue(new PendingCapture { InstrumentationKey = "key2", LocationHash = "hash2" });

        var collector = new DISnapshotCollector(emitter, cts.Token);
        collector.Start();

        await Task.Delay(50); // let drain loop run
        cts.Cancel();
        await Task.Delay(20); // let thread exit

        emitted.Should().HaveCount(2);
        emitted[0].InstrumentationKey.Should().Be("key1");
        emitted[1].InstrumentationKey.Should().Be("key2");
    }

    [Fact]
    public async Task Collector_SurvivesPerCaptureErrors()
    {
        var emitted = new List<PendingCapture>();
        var emitter = new ThrowingEmitter(emitted);
        using var cts = new CancellationTokenSource();

        DIDataStore.Clear();
        DIDataStore.Enqueue(new PendingCapture { InstrumentationKey = "bad" });  // will throw
        DIDataStore.Enqueue(new PendingCapture { InstrumentationKey = "good" }); // should still emit

        var collector = new DISnapshotCollector(emitter, cts.Token);
        collector.Start();

        await Task.Delay(50);
        cts.Cancel();
        await Task.Delay(20);

        emitted.Should().HaveCount(1);
        emitted[0].InstrumentationKey.Should().Be("good");
    }

    [Fact]
    public async Task Collector_FinalDrainOnShutdown()
    {
        var emitted = new List<PendingCapture>();
        var emitter = new TestEmitter(emitted);
        using var cts = new CancellationTokenSource();

        DIDataStore.Clear();
        var collector = new DISnapshotCollector(emitter, cts.Token);
        collector.Start();

        await Task.Delay(30); // let collector start

        // Enqueue after collector is running, then immediately cancel
        DIDataStore.Enqueue(new PendingCapture { InstrumentationKey = "late" });
        cts.Cancel();
        await Task.Delay(50); // let final drain happen

        emitted.Should().Contain(c => c.InstrumentationKey == "late");
    }

    private class TestEmitter : IDISnapshotEmitter
    {
        private readonly List<PendingCapture> _emitted;
        public TestEmitter(List<PendingCapture> emitted) => _emitted = emitted;
        public void Emit(PendingCapture capture) => _emitted.Add(capture);
    }

    private class ThrowingEmitter : IDISnapshotEmitter
    {
        private readonly List<PendingCapture> _emitted;
        public ThrowingEmitter(List<PendingCapture> emitted) => _emitted = emitted;
        public void Emit(PendingCapture capture)
        {
            if (capture.InstrumentationKey == "bad")
                throw new InvalidOperationException("simulated failure");
            _emitted.Add(capture);
        }
    }
}
