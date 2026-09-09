// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Output;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Client;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Verifies that a probe the native rewriter refused stops being reported READY — and that a probe it has
/// simply not reached yet is left alone.
/// </summary>
// THE BUG THIS CLASS GUARDS. A line probe reports READY the moment its MANAGED resolution succeeds, because
// that is all that is knowable then: the rewrite happens later, on a ReJIT thread, when the target method is
// next invoked. Anything the rewriter then declines was reported live and never corrected.
//
// The two failure modes pull in opposite directions, and both are tested here because fixing one by hand
// tends to create the other:
//   * UNDER-reporting — a real refusal never surfaces, so READY is a lie.
//   * OVER-reporting  — a probe with NO verdict yet (a method nobody has called) is reported as an error,
//                       so every idle code path turns into a console full of failures.
public class LineProbeWeaveReporterTests
{
    private const string CodeUnit = "MyApp";
    private const string ClassName = "OrderService";

    [Fact]
    public void Report_WhenEveryProbeIsWoven_ReportsNothing()
    {
        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");
        harness.Verdicts = [(1, LineProbeWeaveOutcome.Woven)];

        harness.Reporter.Report().Should().Be(0);

        harness.Reported.Should().BeEmpty("a woven probe is exactly what READY claims");
    }

    [Fact]
    public void Report_WhenAProbeHasNoVerdictYet_ReportsNothing()
    {
        // PENDING IS THE STEADY STATE, not a failure. Most probes on most services sit here: the operator
        // created a probe on a method that has not been called since. Reporting it would make DI look broken
        // on every idle code path, which is worse than the silence it replaced.
        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");
        harness.Verdicts = [(1, LineProbeWeaveOutcome.Pending)];

        harness.Reporter.Report().Should().Be(0);

        harness.Reported.Should().BeEmpty();
    }

    [Fact]
    public void Report_WhenTheRewriterRefusedAProbe_ReportsErrorForItsConfigurationExactlyOnce()
    {
        // The whole point: the manager already told the backend READY, and this is the correction.
        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");
        harness.Verdicts = [(1, LineProbeWeaveOutcome.CallbackAssemblyRefFailed)];

        harness.Reporter.Report().Should().Be(1);

        harness.Reported.Should().ContainSingle();
        harness.Reported[0].LocationHash.Should().Be("hash-a");
        harness.Reported[0].Cause.Should().Be("RUNTIME_ERROR");

        // REPEATED POLLS MUST NOT REPEAT THE ERROR. The native log holds a verdict as STATE, not as an event:
        // it returns the same failure on every poll for the life of the probe. Without dedup this would send
        // one ERROR per 60-second period, forever, for a single broken probe.
        harness.Reporter.Report().Should().Be(0);
        harness.Reported.Should().ContainSingle("the verdict is re-read every period but is not news twice");
    }

    [Fact]
    public void Report_WhenEveryProbeOfAMultiLocalConfigFails_ReportsOneErrorNotThree()
    {
        // A config capturing three locals owns THREE probe ids at one line, and a whole-method failure
        // (Import/Export) fails all three at once. The backend models status per CONFIGURATION, so three
        // identical ERRORs for one probe is noise that also burns the status queue's bounded capacity.
        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");
        harness.Register(probeId: 2, locationHash: "hash-a");
        harness.Register(probeId: 3, locationHash: "hash-a");
        harness.Verdicts =
        [
            (1, LineProbeWeaveOutcome.ExportFailed),
            (2, LineProbeWeaveOutcome.ExportFailed),
            (3, LineProbeWeaveOutcome.ExportFailed),
        ];

        harness.Reporter.Report().Should().Be(1);

        harness.Reported.Should().ContainSingle();
        harness.Reported[0].LocationHash.Should().Be("hash-a");
    }

    [Fact]
    public void Report_AProbePendingOnOnePassAndFailedOnTheNext_IsStillReported()
    {
        // THE ORDERING TRAP, and the reason Pending must not be recorded as "already examined". The realistic
        // sequence is exactly this one: the probe is applied, several reporting periods pass with the target
        // method never called (PENDING), and then it runs and the rewriter refuses it. A dedup set that
        // remembered the PENDING pass would treat the real verdict as old news and the failure would never be
        // reported at all — the original bug, reintroduced by an over-eager cache.
        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");

        harness.Verdicts = [(1, LineProbeWeaveOutcome.Pending)];
        harness.Reporter.Report().Should().Be(0);
        harness.Reporter.Report().Should().Be(0);

        harness.Verdicts = [(1, LineProbeWeaveOutcome.EhClauseBoundary)];

        harness.Reporter.Report().Should().Be(1);
        harness.Reported.Should().ContainSingle();
    }

    [Fact]
    public void Report_WhenTheConfigurationWasAlreadyRemoved_ReportsNothing()
    {
        // A verdict can outlive its configuration by one pass: the native log is cleared on removal, but this
        // pass may have read it just before. Reporting then would tell the operator that a probe they deleted
        // has failed.
        var harness = new Harness();
        harness.Verdicts = [(99, LineProbeWeaveOutcome.OffsetNotInstructionBoundary)];

        harness.Reporter.Report().Should().Be(0);

        harness.Reported.Should().BeEmpty();
    }

    [Fact]
    public void Report_AfterForget_ReportsTheSameConfigurationAgain()
    {
        // RECOVERY. An operator whose probe was refused fixes the cause and re-creates the probe. Retirement
        // runs Forget, so the fresh incarnation is judged on its own — otherwise a config that had ever failed
        // would be permanently unreportable and the operator would never see it come back.
        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");
        harness.Verdicts = [(1, LineProbeWeaveOutcome.BoxTypeUnresolvable)];

        harness.Reporter.Report().Should().Be(1);
        harness.Reporter.Report().Should().Be(0);

        harness.Reporter.Forget("hash-a", [1]);
        harness.Reporter.ReportedConfigurationCount.Should().Be(0);

        harness.Reporter.Report().Should().Be(1);
        harness.Reported.Should().HaveCount(2);
    }

    [Theory]
    // Refusals that are properties of the requested LINE. The operator's fix is to move the probe, which is
    // what LINE_NOT_EXECUTABLE tells them — the only cause code in the shared backend enum that carries an
    // action.
    // Raw enum VALUES, not typed constants: the enum is internal and an InlineData of it would make this
    // public test method less accessible than its own parameter. The names are in the comments beside each.
    [InlineData(8, "LINE_NOT_EXECUTABLE")]  // OffsetNotInstructionBoundary
    [InlineData(9, "LINE_NOT_EXECUTABLE")]  // EhClauseBoundary

    // Everything else is an agent-side or environment condition, and no amount of editing the probe helps.
    [InlineData(2, "RUNTIME_ERROR")]  // CallbackAssemblyRefFailed
    [InlineData(3, "RUNTIME_ERROR")]  // CallbackTypeRefFailed
    [InlineData(4, "RUNTIME_ERROR")]  // CallbackMemberRefFailed
    [InlineData(5, "RUNTIME_ERROR")]  // GateMemberRefFailed
    [InlineData(6, "RUNTIME_ERROR")]  // BoxTypeUnresolvable
    [InlineData(7, "RUNTIME_ERROR")]  // LocalSlotOutOfRange
    [InlineData(10, "RUNTIME_ERROR")] // ImportFailed
    [InlineData(11, "RUNTIME_ERROR")] // ExportFailed
    public void Report_MapsEachRefusalToItsBackendErrorCause(int outcomeValue, string expectedCause)
    {
        // The cast doubles as a check that the numeric value really is the named member: an unrecognised value
        // would fall through IsWeaveFailure and report nothing, failing the count assertion below.
        var outcome = (LineProbeWeaveOutcome)outcomeValue;
        Enum.IsDefined(typeof(LineProbeWeaveOutcome), outcome).Should().BeTrue(
            $"{outcomeValue} must name a real outcome; the native enum in line_probe.h pins these values");

        var harness = new Harness();
        harness.Register(probeId: 1, locationHash: "hash-a");
        harness.Verdicts = [(1, outcome)];

        harness.Reporter.Report().Should().Be(1);

        harness.Reported.Should().ContainSingle();
        harness.Reported[0].Cause.Should().Be(expectedCause);
    }

    [Fact]
    public void IsWeaveFailure_TreatsOnlyWovenAndPendingAsNotFailures()
    {
        // Enumerated over the WHOLE enum rather than a hand-picked list, so a reason code added later is
        // classified deliberately instead of defaulting into whichever branch someone wrote first.
        foreach (var outcome in Enum.GetValues<LineProbeWeaveOutcome>())
        {
            var expected = outcome != LineProbeWeaveOutcome.Woven && outcome != LineProbeWeaveOutcome.Pending;
            outcome.IsWeaveFailure().Should().Be(expected, $"{outcome} must be classified deliberately");
        }
    }

    [Fact]
    public void Report_ThroughTheRealSinkRegistryAndStatusReporter_PutsAnErrorOnTheWire()
    {
        // END TO END ACROSS THE MANAGED CHAIN, with the real collaborators the manager wires up: a real
        // LineProbeSink for probeId -> key, a real InstrumentationRegistry for key -> config, and a real
        // StatusReporter for the HTTP shape. The per-test doubles above prove the DECISION; this proves the
        // decision reaches the backend in the form the backend parses.
        //
        // The two ends not covered here are covered elsewhere on purpose: the P/Invoke and the native verdicts
        // by the W1WeaveStatusE2E harness against a real profiler, and this hook's cadence by
        // StatusReporterTests.
        var sentBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            sentBodies.Add(new StreamReader(req.Content!.ReadAsStream()).ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new DynamicInstrumentationClient(
            new HttpClient(handler), "http://localhost:2000", "svc", "env");

        var registry = new InstrumentationRegistry();
        var sink = new LineProbeSink(registry);
        using var cts = new CancellationTokenSource();
        var statusReporter = new StatusReporter(client, registry, cts.Token);

        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = CodeUnit,
            ClassName = ClassName,
            MethodName = "Process",
            LineNumber = 42,
            LocationHash = "wire-hash",
            Capture = CaptureConfiguration.Default,
        };
        registry.Register(config);

        var probeId = sink.AllocateProbeId();
        sink.Register(
            probeId,
            config,
            new LineProbeLocation(
                MethodToken: 0x06000001,
                AssemblyName: CodeUnit,
                TypeName: $"{CodeUnit}.{ClassName}",
                MethodName: "Process",
                ParameterCount: 0,
                IlOffset: 12,
                LocalSlot: 0,
                LocalName: "total"),
            gated: false);

        // The manager's optimistic READY, which is what this whole mechanism exists to correct.
        statusReporter.MarkApplied(config);
        statusReporter.ReportReadyForNew();

        var reporter = new LineProbeWeaveReporter(
            () => [(probeId, LineProbeWeaveOutcome.CallbackAssemblyRefFailed)],
            id => sink.TryGetInstrumentationKey(id, out var key) ? key : null,
            key => registry.Get(key)?.Config,
            statusReporter.ReportError);

        reporter.Report().Should().Be(1);
        statusReporter.FlushPending();

        var body = string.Concat(sentBodies);
        body.Should().Contain("READY", "the optimistic status really was sent first — that is the premise");
        body.Should().Contain("\"Status\":\"ERROR\"");
        body.Should().Contain("\"ErrorCause\":\"RUNTIME_ERROR\"");
        body.Should().Contain("wire-hash");

        // AND THE READY MUST NOT COME BACK. `errored` is what stops it, and without that a later
        // ReportReadyForNew — every config poll calls one — would re-assert that the broken probe is live.
        sentBodies.Clear();
        statusReporter.ReportReadyForNew();
        statusReporter.FlushPending();
        sentBodies.Should().BeEmpty("a config reported ERROR must not go back to READY on the next poll");
    }

    /// <summary>
    /// Drives the reporter with no native profiler, no registry, and no HTTP.
    /// </summary>
    // Deliberately hand-rolled rather than built on InstrumentationRegistry + LineProbeSink: those bring
    // process-global state (DIDataStore, the static probe-id counter) and would put this class in the serial
    // collection for no gain. What is under test is the reporting decision, and its inputs are three lookups.
    private sealed class Harness
    {
        private readonly Dictionary<int, string> keysByProbeId = new();
        private readonly Dictionary<string, InstrumentationConfiguration> configsByKey = new();

        public Harness()
        {
            this.Reporter = new LineProbeWeaveReporter(
                () => this.Verdicts,
                probeId => this.keysByProbeId.TryGetValue(probeId, out var key) ? key : null,
                key => this.configsByKey.TryGetValue(key, out var config) ? config : null,
                (config, cause) => this.Reported.Add((config.LocationHash, cause)));
        }

        public LineProbeWeaveReporter Reporter { get; }

        public IReadOnlyList<(int ProbeId, LineProbeWeaveOutcome Outcome)> Verdicts { get; set; } =
            Array.Empty<(int, LineProbeWeaveOutcome)>();

        public List<(string LocationHash, string Cause)> Reported { get; } = new();

        public void Register(int probeId, string locationHash)
        {
            var config = new InstrumentationConfiguration
            {
                Type = InstrumentationType.BREAKPOINT,
                CodeUnit = CodeUnit,
                ClassName = ClassName,
                MethodName = "Process",
                LineNumber = 42,
                LocationHash = locationHash,
                Capture = CaptureConfiguration.Default,
            };

            this.keysByProbeId[probeId] = config.InstrumentationKey;
            this.configsByKey[config.InstrumentationKey] = config;
        }
    }
}
