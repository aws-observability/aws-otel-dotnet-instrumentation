// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Verifies the hot-path contract of the line-probe callbacks.
/// </summary>
// These callbacks execute inside injected IL, at an arbitrary interior offset in customer code, on the
// customer's thread. The contract is therefore stricter than "works": it must never throw, never block on a
// missing sink, and fail CLOSED in the gate. None of that is expressible in the type system, so each rule
// gets a test with a fake that misbehaves on purpose.
public class DiLineIntegrationHelperTests : IDisposable
{
    public DiLineIntegrationHelperTests()
    {
        // Static state shared across tests in an assembly xunit may run in any order — reset both ends.
        DiLineIntegrationHelper.Configure(null);
    }

    public void Dispose()
    {
        DiLineIntegrationHelper.Configure(null);
    }

    [Fact]
    public void Probe_WithNoSinkConfigured_DoesNothingAndDoesNotThrow()
    {
        // The woven IL stays in place when capture is off; a probe hit must then be a cheap no-op. This is
        // what allows disabling capture WITHOUT re-weaving the method.
        var act = () => DiLineIntegration.Probe(1);

        act.Should().NotThrow();
    }

    [Fact]
    public void CaptureLocal_WithNoSinkConfigured_DoesNothingAndDoesNotThrow()
    {
        var act = () => DiLineIntegration.CaptureLocal(1, 42);

        act.Should().NotThrow();
    }

    [Fact]
    public void ShouldCapture_WithNoSinkConfigured_ReturnsFalse()
    {
        // Fail closed: with nothing configured there is nowhere to send a capture, so the gate must suppress
        // it rather than let the capture path run.
        DiLineIntegration.ShouldCapture(1).Should().BeFalse();
    }

    [Fact]
    public void Probe_ForwardsProbeIdAndNoValue()
    {
        var sink = new RecordingSink();
        DiLineIntegrationHelper.Configure(sink);

        DiLineIntegration.Probe(7);

        sink.Hits.Should().HaveCount(1);
        sink.Hits[0].ProbeId.Should().Be(7);
        sink.Hits[0].HasValue.Should().BeFalse("Legacy mode captures no local");
        sink.Hits[0].Value.Should().BeNull();
    }

    [Fact]
    public void CaptureLocal_ForwardsTheBoxedValue()
    {
        var sink = new RecordingSink();
        DiLineIntegrationHelper.Configure(sink);

        DiLineIntegration.CaptureLocal(9, 123);

        sink.Hits.Should().HaveCount(1);
        sink.Hits[0].ProbeId.Should().Be(9);
        sink.Hits[0].HasValue.Should().BeTrue();
        sink.Hits[0].Value.Should().Be(123);
    }

    [Fact]
    public void CaptureLocal_WithNullValue_StillReportsHasValueTrue()
    {
        // A captured local that IS null is different from no capture at all. Collapsing the two would make a
        // null reference indistinguishable from an unsupported mode in the snapshot.
        var sink = new RecordingSink();
        DiLineIntegrationHelper.Configure(sink);

        DiLineIntegration.CaptureLocal(3, null!);

        sink.Hits[0].HasValue.Should().BeTrue("the local was captured; its value happened to be null");
        sink.Hits[0].Value.Should().BeNull();
    }

    [Fact]
    public void Probe_WhenTheSinkThrows_SwallowsTheException()
    {
        // THE MOST IMPORTANT TEST HERE. An exception escaping this callback does not merely lose telemetry —
        // it alters the customer's control flow at an interior offset their code never anticipated, and can
        // surface as an impossible exception from a line that cannot throw.
        DiLineIntegrationHelper.Configure(new ThrowingSink());

        var act = () => DiLineIntegration.Probe(1);

        act.Should().NotThrow("a throwing sink must never propagate into customer code");
    }

    [Fact]
    public void CaptureLocal_WhenTheSinkThrows_SwallowsTheException()
    {
        DiLineIntegrationHelper.Configure(new ThrowingSink());

        var act = () => DiLineIntegration.CaptureLocal(1, 42);

        act.Should().NotThrow();
    }

    [Fact]
    public void ShouldCapture_WhenTheSinkThrows_ReturnsFalseRatherThanPropagating()
    {
        // Note the asymmetry with the capture callbacks: they swallow and continue, this swallows and returns
        // FALSE. Returning true on failure would run the capture path with an unknown gate state.
        DiLineIntegrationHelper.Configure(new ThrowingSink());

        var result = false;
        var act = () => result = DiLineIntegration.ShouldCapture(1);

        act.Should().NotThrow();
        result.Should().BeFalse("the gate fails closed");
    }

    [Fact]
    public void ShouldCapture_ReturnsWhateverTheSinkDecides()
    {
        var sink = new RecordingSink { GateResult = true };
        DiLineIntegrationHelper.Configure(sink);

        DiLineIntegration.ShouldCapture(5).Should().BeTrue();

        sink.GateResult = false;
        DiLineIntegration.ShouldCapture(5).Should().BeFalse();
        sink.GateCalls.Should().Equal(5, 5);
    }

    [Fact]
    public void Configure_WithNull_StopsForwardingToThePreviousSink()
    {
        // Disabling must take effect immediately without re-weaving: the IL still calls in, and the helper
        // must drop the hit.
        var sink = new RecordingSink();
        DiLineIntegrationHelper.Configure(sink);
        DiLineIntegration.Probe(1);

        DiLineIntegrationHelper.Configure(null);
        DiLineIntegration.Probe(2);

        sink.Hits.Should().HaveCount(1, "only the hit before the sink was cleared should arrive");
        sink.Hits[0].ProbeId.Should().Be(1);
    }

    private sealed class RecordingSink : ILineProbeSink
    {
        public List<(int ProbeId, bool HasValue, object? Value)> Hits { get; } = new();

        public List<int> GateCalls { get; } = new();

        public bool GateResult { get; set; }

        public void OnLineProbeHit(int probeId, bool hasValue, object? value)
        {
            this.Hits.Add((probeId, hasValue, value));
        }

        public bool ShouldCapture(int probeId)
        {
            this.GateCalls.Add(probeId);
            return this.GateResult;
        }
    }

    private sealed class ThrowingSink : ILineProbeSink
    {
        public void OnLineProbeHit(int probeId, bool hasValue, object? value)
            => throw new InvalidOperationException("sink failure");

        public bool ShouldCapture(int probeId)
            => throw new InvalidOperationException("gate failure");
    }
}
