// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Output;

/// <summary>
/// Native trace context and timestamp on snapshot LogRecords, exercised through the REAL logging pipeline.
/// </summary>
// Driven end to end on purpose. The bug was that the SDK fills TraceId/SpanId/Timestamp from the drain
// thread's ambient Activity, and the fix depends on two things only the SDK decides: that the log state stays
// reachable as SnapshotLogState (rather than being flattened into an attribute list) and that a processor runs
// before the exporter serializes. Neither can be asserted by calling OnEnd on a hand-built record.
public class DISnapshotTraceContextProcessorTests
{
    private const string CapturedTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string CapturedSpanId = "b7ad6b7169203331";

    // What the exporter would have serialized, copied out of the record. LogRecord instances are pooled and
    // reused by the SDK, so holding references and reading them after the fact would be reading whatever the
    // pool handed to the next log call.
    private sealed record Stamped(ActivityTraceId TraceId, ActivitySpanId SpanId, DateTime Timestamp);

    [Fact]
    public void Emit_StampsTheNativeTraceContextAndTimestamp_FromTheCapturedValues()
    {
        var stamped = new List<Stamped>();

        // An ambient Activity on THIS thread, deliberately unrelated to the captured ids. It stands in for the
        // drain thread's context — the thing the SDK would otherwise stamp onto the record, and the reason a
        // snapshot used to correlate to the wrong trace (or to none).
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("di.tests.unrelated");
        using var unrelated = source.StartActivity("unrelated");
        unrelated.Should().NotBeNull("the test needs a live ambient Activity to prove it is NOT the one used");

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = false;
                options.AddProcessor(new DISnapshotTraceContextProcessor());
                options.AddProcessor(new CapturingProcessor(stamped));
            });
        }))
        {
            var emitter = new DISnapshotOtlpEmitter(factory);
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
                TraceId = CapturedTraceId,
                SpanId = CapturedSpanId,
                TimestampMs = 1_785_000_000_000,
            });
        }

        stamped.Should().HaveCount(1);
        stamped[0].TraceId.ToHexString().Should().Be(
            CapturedTraceId,
            "the backend correlates on the native TraceId, so it must be the one captured on the user's thread");
        stamped[0].SpanId.ToHexString().Should().Be(CapturedSpanId);
        stamped[0].TraceId.ToHexString().Should().NotBe(
            unrelated!.TraceId.ToHexString(),
            "the ambient Activity where the snapshot is EXPORTED must never supply the trace context");
        stamped[0].Timestamp.Should().Be(
            DateTimeOffset.FromUnixTimeMilliseconds(1_785_000_000_000).UtcDateTime,
            "the timestamp must be the capture instant, not the moment the drain thread exported");
    }

    [Fact]
    public void Emit_CaptureWithNoTraceContext_LeavesTheNativeIdsUnset()
    {
        // A probe can fire outside any trace (a background thread, a startup path). The ids must then stay
        // empty rather than inheriting the drain thread's, which would invent a correlation that never existed.
        var stamped = new List<Stamped>();

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = false;
                options.AddProcessor(new DISnapshotTraceContextProcessor());
                options.AddProcessor(new CapturingProcessor(stamped));
            });
        }))
        {
            var emitter = new DISnapshotOtlpEmitter(factory);
            emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
                TimestampMs = 1_785_000_000_000,
            });
        }

        stamped.Should().HaveCount(1);
        stamped[0].TraceId.Should().Be(default(ActivityTraceId));
        stamped[0].SpanId.Should().Be(default(ActivitySpanId));
        stamped[0].Timestamp.Should().Be(
            DateTimeOffset.FromUnixTimeMilliseconds(1_785_000_000_000).UtcDateTime,
            "the capture timestamp is independent of whether a trace was active");
    }

    [Theory]
    [InlineData("not-hex", "b7ad6b7169203331")]
    [InlineData("0af7651916cd43dd8448eb211c80319c", "short")]
    [InlineData("", "b7ad6b7169203331")]
    public void Emit_MalformedIds_AreIgnoredRatherThanThrowingOnTheDrainThread(string traceId, string spanId)
    {
        // ActivityTraceId.CreateFromString throws on malformed input, and this runs on the drain thread where
        // a throw would cost the whole batch of snapshots, not just this one.
        var stamped = new List<Stamped>();

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = false;
                options.AddProcessor(new DISnapshotTraceContextProcessor());
                options.AddProcessor(new CapturingProcessor(stamped));
            });
        }))
        {
            var emitter = new DISnapshotOtlpEmitter(factory);
            var act = () => emitter.Emit(new PendingCapture
            {
                Type = CaptureType.METHOD,
                InstrumentationKey = "MyApp.Svc.Run",
                LocationHash = "hash1",
                TraceId = traceId,
                SpanId = spanId,
                TimestampMs = 1_785_000_000_000,
            });

            act.Should().NotThrow();
        }

        stamped.Should().HaveCount(1, "the snapshot is still exported; only the unusable context is dropped");
        stamped[0].TraceId.Should().Be(default(ActivityTraceId), "a malformed id must not be half-applied");
        stamped[0].SpanId.Should().Be(default(ActivitySpanId));
    }

    // Runs after the processor under test, so it sees exactly what the exporter would have serialized.
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        private readonly List<Stamped> stamped;

        public CapturingProcessor(List<Stamped> stamped) => this.stamped = stamped;

        public override void OnEnd(LogRecord data) =>
            this.stamped.Add(new Stamped(data.TraceId, data.SpanId, data.Timestamp));
    }
}
