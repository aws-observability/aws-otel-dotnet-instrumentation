// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests;

[Collection("SerialProcessState")]
public class DynamicInstrumentationManagerTests : IDisposable
{
    // The Manager singleton owns a background snapshot collector that drains the process-global
    // DIDataStore. Shut it down after every test so its drain thread is joined and stops competing
    // with other global-state suites for the shared queue.
    public void Dispose() => DynamicInstrumentationManager.Instance.Shutdown();

    // ---------------------------------------------------------------------------------------------
    // APPLIED-PATH ORCHESTRATION.
    //
    // Everything below was UNREACHABLE before the Initialize seam existed, and branch coverage is what
    // exposed it: both translators end in a P/Invoke that no test process can satisfy, so every apply
    // failed and the code downstream of a success never ran. These stub ONLY the native boundary --
    // the registry, sink, status reporter, PDB resolution and the manager's own logic are all real.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A line translator whose native calls are stubbed, so resolution can actually succeed.</summary>
    private static LineProbeTranslator StubbedLineTranslator(List<int>? removedProbeIds = null) =>
        new(
            addLineProbesOverride: (_, _, _) => { },
            removeLineProbeOverride: id => removedProbeIds?.Add(id),
            typeResolver: _ => typeof(PdbReaderTargets));

    /// <summary>A method-level translator whose native call is stubbed, so applies report Applied.</summary>
    private static ProfilerTranslator StubbedProfilerTranslator() =>
        new(addInstrumentationsOverride: (_, _, _) => { });

    private static InstrumentationConfiguration RealLineConfig(string locationHash, string marker, string local) =>
        new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel",
            ClassName = "PdbReaderTargets",
            MethodName = "ThreeStatements",
            LineNumber = PdbReaderTargets.LineOf(marker),
            LocationHash = locationHash,
            Capture = CaptureConfiguration.Default with { CaptureLocals = [local] }
        };

    [Fact]
    public void ApplyLineProbe_WhenResolutionSucceeds_RegistersTheProbeAndMarksItApplied()
    {
        // The success path of ApplyLineProbe, which no test could reach before: the defensive
        // single-location Register, and MarkApplied -- the ONLY thing that makes a config eligible for READY.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(), null, StubbedLineTranslator());

        var config = RealLineConfig("applied-line-hash", "assignsA", "a");
        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { config }).Should().BeTrue(
            "a resolved line probe needs no retry, so the poller may latch this set");

        manager.Registry!.Get(config.InstrumentationKey).Should().NotBeNull();

        manager.Shutdown();
    }

    [Fact]
    public void RemovingAnAppliedLineConfig_UnregistersEveryProbeAndTellsTheProfiler()
    {
        // THE BLIND SPOT. OnConfigurationsChanged_RemovesStaleLineLevelConfigs passes today WITHOUT ever
        // entering this block: nothing registered, so `Unregister(...) == true` was false and the whole body
        // -- RemoveLineProbe per probe, plus the weave-verdict Forget -- was skipped. Measured as 0 lines
        // executed. Removal is the path that stops a deleted probe capturing, so it cannot be untested.
        //
        // THREE locals on purpose: a multi-local config owns one probe per local, and the bug this guards is
        // removing only the first and leaving the rest woven AND registered -- still capturing after the
        // operator deleted the configuration.
        var removed = new List<int>();
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(), null, StubbedLineTranslator(removed));

        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel",
            ClassName = "PdbReaderTargets",
            MethodName = "ThreeStatements",
            LineNumber = PdbReaderTargets.LineOf("assignsA"),
            LocationHash = "removal-line-hash",
            Capture = CaptureConfiguration.Default with { CaptureLocals = ["a", "b", "c"] }
        };

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { config });
        manager.Registry!.Get(config.InstrumentationKey).Should().NotBeNull("precondition: it applied");

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration>());

        manager.Registry.Get(config.InstrumentationKey).Should().BeNull("the config must leave the registry");
        removed.Should().HaveCountGreaterThan(
            1,
            "EVERY probe the config owned must be handed to RemoveLineProbe, not just the first -- three "
            + "captured locals means several probes at one line");
        removed.Should().OnlyHaveUniqueItems();

        manager.Shutdown();
    }

    [Fact]
    public void EditingAnAppliedLineConfigInPlace_RetiresTheOldProbesBeforeApplyingTheNew()
    {
        // The retire-on-edit path (branch coverage 2/6, the worst in the file). An in-place edit keeps the
        // SAME InstrumentationKey and changes the LocationHash, so RemoveStale never sees it as stale --
        // RetireAppliedConfiguration is the only thing that drops the previous incarnation's probes. Without
        // it they keep firing under a LocationHash the operator already replaced.
        var removed = new List<int>();
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(), null, StubbedLineTranslator(removed));

        var before = RealLineConfig("edit-hash-v1", "assignsA", "a");
        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { before });
        removed.Should().BeEmpty("nothing has been retired yet");

        // Same key (type+method+line), new identity.
        var after = RealLineConfig("edit-hash-v2", "assignsA", "a");
        after.InstrumentationKey.Should().Be(before.InstrumentationKey, "an in-place edit keeps the key");

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { after });

        removed.Should().NotBeEmpty(
            "the previous incarnation's probes must be handed to RemoveLineProbe when the config is edited");

        manager.Shutdown();
    }

    [Fact]
    public void MethodLevelApplied_MarksAppliedAndReportsOverloadedMethodsForSameArityCollisions()
    {
        // The method-level Applied arm, also unreachable before the seam: MarkApplied plus the
        // OVERLOADED_METHODS collision loop, which reports an ERROR against EVERY config in an ambiguous
        // bucket rather than only the one that applied last -- captures on same-arity overloads cannot be
        // told apart by args.Length, so the operator needs the whole set named.
        var statusBodies = new List<string>();
        using var server = StatusCapturingApiServer.Start(statusBodies);

        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(apiUrl: server.Url), StubbedProfilerTranslator(), null);

        // Two configs on the SAME type, both resolving to a 1-parameter method => a same-arity collision.
        var first = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests",
            ClassName = "DynamicInstrumentationManagerTests",
            MethodName = nameof(EditableTarget),
            LocationHash = "collide-a",
            Capture = CaptureConfiguration.Default
        };
        var second = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests",
            ClassName = "DynamicInstrumentationManagerTests",
            MethodName = nameof(SecondTarget),
            LocationHash = "collide-b",
            Capture = CaptureConfiguration.Default
        };

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { first, second });

        server.WaitForStatusContaining("OVERLOADED_METHODS", TimeSpan.FromSeconds(10)).Should().BeTrue(
            "two same-arity methods on one type cannot be disambiguated, so both must be reported");

        manager.Shutdown();
    }

    /// <summary>Second same-arity target, so a collision can be provoked. See the collision test.</summary>
    /// <param name="x">Any value.</param>
    /// <returns>x + 2.</returns>
    public static int SecondTarget(int x) => x + 2;

    [Fact]
    public void AMalformedNegativeLineNumber_IsRefused_NotWovenAsAWholeMethodProbe()
    {
        // IsLineLevel (LineNumber > 0) and IsMethodLevel (LineNumber == 0) do NOT partition -- a negative
        // LineNumber satisfies NEITHER. It passes the IsSupported check (which only refuses ctors on the
        // method-level side), gets registered, and then has to be caught explicitly. Falling through to the
        // method-level branch instead wove a FULL METHOD probe for a config the operator scoped to a line,
        // capturing entry/exit arguments they never asked for -- data exfiltration by typo.
        //
        // Parse() rejects LineNumber < 0, so production cannot reach this from the wire; the guard protects
        // the internal API surface, and this test is the only thing that proves it fires.
        var statusBodies = new List<string>();
        using var server = StatusCapturingApiServer.Start(statusBodies);

        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(apiUrl: server.Url), StubbedProfilerTranslator(), StubbedLineTranslator());

        var malformed = new InstrumentationConfiguration
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests",
            ClassName = "DynamicInstrumentationManagerTests",
            MethodName = nameof(EditableTarget),
            LineNumber = -1,
            LocationHash = "malformed-negative-line",
            Capture = CaptureConfiguration.Default
        };

        malformed.IsLineLevel.Should().BeFalse("a negative line number is not line-level");
        malformed.IsMethodLevel.Should().BeFalse("nor is it method-level -- that is the whole hazard");

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { malformed });

        server.WaitForStatusContaining("UNSUPPORTED_TARGET", TimeSpan.FromSeconds(10)).Should().BeTrue(
            "a config that is neither line- nor method-level must be refused and reported, never woven");

        manager.Shutdown();
    }

    [Fact]
    public void ReportLineProbeWeaveFailures_AfterInitialize_IsWiredUpAndSurvivesAMissingProfiler()
    {
        // WIRING, not logic. LineProbeWeaveReporterTests covers the decision; this covers the four lambdas
        // Initialize hands it — translator, sink, registry, status reporter — which nothing else exercises.
        //
        // WHY IT NEEDS ITS OWN TEST. In production this runs from StatusReporter's 60-second timer, and no E2E
        // run lasts that long (measured: the DeployedAppDemo harness reports READY but never an ACTIVE, which
        // is the tell that the timer never fired). So without this, a mis-wired hook — a null field, a lambda
        // over the wrong instance — would ship green.
        //
        // No profiler is loaded in a test process, so GetLineProbeWeaveResults throws DllNotFoundException
        // inside the translator, which swallows it and returns no verdicts. Reporting nothing is the CORRECT
        // outcome for a process where nothing was ever woven; throwing would take down the whole reporting
        // period.
        //
        // SO THE ASSERTION IS ON NULL-VS-ZERO, not on the count. A wired reporter and a missing one BOTH find
        // zero verdicts here, so asserting `== 0` proved nothing — measured: that version passed with the
        // assignment in Initialize deleted. Null means there was no reporter to ask.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var lineConfig = new InstrumentationConfiguration
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "MyApp",
            ClassName = "OrderService",
            MethodName = "Process",
            LineNumber = 42,
            LocationHash = "weave-wiring-hash",
            Capture = CaptureConfiguration.Default
        };
        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { lineConfig });

        var act = () => manager.ReportLineProbeWeaveFailures();

        act.Should().NotThrow("a missing profiler export must not escape into the status timer");
        act().Should().Be(
            0,
            "Initialize must have wired a reporter (non-null), and with no profiler it finds no verdicts (0)");

        manager.Shutdown();

        // AFTER SHUTDOWN the reporter is gone, and the hook must be a no-op rather than a NullReference on a
        // timer callback that Dispose is already waiting for. Null here, not 0 — the difference is the same one
        // that makes the assertion above non-vacuous.
        act.Should().NotThrow();
        act().Should().BeNull("Cleanup drops the reporter, so there is nothing to ask");
    }

    [Fact]
    public void OnConfigurationsChanged_WithNoLineProbeSupportInTheProfiler_ReportsAnErrorForEVERYLineConfig()
    {
        // THE STOCK-PROFILER PATH, and it is not hypothetical: four of the five shipped RIDs carry upstream's
        // native binary, which has no AddLineProbes export (measured on the real v1.16.0 macOS artifact: 0
        // line-level exports, AddInstrumentations present as a control). On those RIDs every line-level probe
        // an operator creates lands here.
        //
        // A test process is that condition exactly — no profiler is loaded, so the P/Invoke throws
        // DllNotFoundException and the translator maps it to ProfilerMissingLineProbeSupport.
        //
        // EVERY config must report, not just one. This started as a discrepancy in a real run: the demo app
        // driven against the stock upstream profiler created seven line-level configs and only ONE ERROR
        // reached the backend. If that is the agent's behaviour rather than an artifact of that run's timing,
        // then on four of five RIDs an operator would create several line probes and be told about one of
        // them, with the rest silently doing nothing.
        //
        // Targets are real methods in this test assembly with a real PDB, so resolution gets PAST the
        // type-loaded and debug-info checks and actually reaches the P/Invoke. That matters: a config that
        // fails EARLIER returns the retryable TypeNotLoaded and deliberately reports nothing, which would make
        // this test pass for the wrong reason.
        var statusBodies = new List<string>();
        using var server = StatusCapturingApiServer.Start(statusBodies);

        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(apiUrl: server.Url));

        const string codeUnit = "AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel";
        const string className = "PdbReaderTargets";

        InstrumentationConfiguration LineConfig(string method, int line, string hash) => new()
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = codeUnit,
            ClassName = className,
            MethodName = method,
            LineNumber = line,
            LocationHash = hash,
            Capture = CaptureConfiguration.Default with { CaptureLocals = ["a"] }
        };

        var configs = new List<InstrumentationConfiguration>
        {
            LineConfig("ThreeStatements", Tests.Instrumentation.LineLevel.PdbReaderTargets.LineOf("assignsA"), "line-hash-1"),
            LineConfig("ThreeStatements", Tests.Instrumentation.LineLevel.PdbReaderTargets.LineOf("assignsB"), "line-hash-2"),
            LineConfig("ThreeStatements", Tests.Instrumentation.LineLevel.PdbReaderTargets.LineOf("assignsC"), "line-hash-3"),
        };

        manager.OnConfigurationsChanged(configs);

        // Status sends are asynchronous (handed to the reporter's worker thread), so wait for the last one
        // rather than sleeping a fixed amount and hoping.
        server.WaitForStatusContaining("line-hash-3", TimeSpan.FromSeconds(10))
            .Should().BeTrue("the third config's status must reach the API");
        Thread.Sleep(500);

        string body;
        lock (statusBodies)
        {
            body = string.Concat(statusBodies);
        }

        // Every one of the three, individually named. Asserting a COUNT alone would pass if the same hash were
        // reported three times.
        body.Should().Contain("line-hash-1");
        body.Should().Contain("line-hash-2");
        body.Should().Contain("line-hash-3");
        CountOccurrences(body, "\"Status\":\"ERROR\"").Should().Be(
            3,
            "one ERROR per line-level config: on a stock profiler none of them can ever fire, so an operator "
            + "who is told about only some of them is left with probes that silently do nothing");
        body.Should().NotContain(
            "\"Status\":\"READY\"", "nothing was woven, so nothing may claim to be live");

        manager.Shutdown();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    private static DynamicInstrumentationConfig CreateConfig(bool enabled = true, string apiUrl = "http://localhost:2000") =>
        new(
            Enabled: enabled,
            ApiUrl: apiUrl,
            ProbePollIntervalSeconds: 600,
            BreakpointPollIntervalSeconds: 60,
            LogsEndpoint: "http://localhost:4317/v1/logs",
            ServiceName: "test-service",
            Environment: "test-env");

    /// <summary>A loadable target, so applying it fails PERMANENTLY (RuntimeError, no profiler in-process)
    /// and the manager therefore RETAINS its applied-state entry — which is what makes the identity visible.</summary>
    /// <param name="x">Any value.</param>
    /// <returns>x + 1.</returns>
    public static int EditableTarget(int x) => x + 1;

    [Fact]
    public void OnConfigurationsChanged_ConfigEditedInPlace_ReAppliesUnderTheNewIdentity()
    {
        // An edit (different captured arguments, a different MaxHits) keeps the InstrumentationKey and changes
        // the LocationHash. Applied-state used to be a key-only set, so RemoveStale did not see the key as
        // stale and the apply loop saw it as already-applied: the edited configuration was never applied and
        // never reported any status, while the backend kept the pre-edit identity.
        //
        // Asserted through the private applied-state map because the distinguishing outcome is bookkeeping:
        // in this process every apply fails (no profiler), so neither version can report READY. RuntimeError
        // is a PERMANENT failure, which is what keeps the entry around to be inspected.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var v1 = EditableConfig("hash-v1");
        var v2 = EditableConfig("hash-v2");
        v2.InstrumentationKey.Should().Be(v1.InstrumentationKey, "an edit must not change the key");

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { v1 });
        AppliedHashFor(manager, v1.InstrumentationKey).Should().Be(
            "hash-v1", "a permanent failure retains applied-state so it is reported exactly once");

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { v2 });
        AppliedHashFor(manager, v2.InstrumentationKey).Should().Be(
            "hash-v2",
            "the edited configuration must be re-applied under its new identity, not skipped as already-applied");

        manager.Shutdown();
    }

    private static InstrumentationConfiguration EditableConfig(string hash) =>
        new()
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = typeof(DynamicInstrumentationManagerTests).Namespace!,
            ClassName = nameof(DynamicInstrumentationManagerTests),
            MethodName = nameof(EditableTarget),
            LocationHash = hash,
            Capture = CaptureConfiguration.Default,
        };

    private static string? AppliedHashFor(DynamicInstrumentationManager manager, string key)
    {
        var field = typeof(DynamicInstrumentationManager).GetField(
            "appliedInstrumentations",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var map = (Dictionary<string, string>)field.GetValue(manager)!;
        return map.TryGetValue(key, out var hash) ? hash : null;
    }

    [Fact]
    public void OnConfigurationsChanged_ConfigThatCouldNotBeApplied_IsNeverReportedReady()
    {
        // End-to-end through the REAL manager, with a real HTTP server standing in for the CloudWatch Agent,
        // so the status wiring is exercised rather than mocked. This test environment has no native profiler
        // and no such target type, so every apply comes back TypeNotLoaded — the manager deliberately reports
        // no ERROR for that (it retries on a later poll), but it used to end the apply pass with a
        // registry-driven READY. The backend was told a probe was instrumented and waiting when nothing had
        // been woven at all.
        var statusBodies = new List<string>();
        using var server = StatusCapturingApiServer.Start(statusBodies);

        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig(apiUrl: server.Url));

        var configs = new List<InstrumentationConfiguration>
        {
            // Positive control: an unsupported target (constructor) is refused up front and DOES report an
            // ERROR. Its arrival proves statuses really reach this server, so the "no READY" assertion below
            // is about READY being suppressed — not about a status channel that was silently broken.
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = ".ctor",
                LocationHash = "hash-unsupported",
                Capture = CaptureConfiguration.Default,
            },

            // The subject: supported, so it is registered, but its type cannot be loaded here.
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = "Process",
                LocationHash = "hash-not-applied",
                Capture = CaptureConfiguration.Default,
            },
        };

        manager.OnConfigurationsChanged(configs);

        // Status sends are asynchronous (handed to the reporter's worker), so wait for the control to land.
        server.WaitForStatusContaining("UNSUPPORTED_TARGET", TimeSpan.FromSeconds(10))
            .Should().BeTrue("the unsupported target must report an ERROR, which proves statuses reach the API");

        // Grace period: a READY, if one were produced, would follow the ERROR immediately.
        Thread.Sleep(500);

        var allStatuses = string.Join("\n", statusBodies);
        allStatuses.Should().NotContain(
            "READY",
            "a config whose apply returned TypeNotLoaded was never instrumented, so it must not be reported READY");
        allStatuses.Should().NotContain("hash-not-applied", "an unapplied config has no status to report yet");

        manager.Shutdown();
    }

    [Fact]
    public void Instance_ReturnsSameSingleton()
    {
        var a = DynamicInstrumentationManager.Instance;
        var b = DynamicInstrumentationManager.Instance;

        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Initialize_SetsConfigAndMarksInitialized()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown(); // reset from any prior test

        var config = CreateConfig();
        manager.Initialize(config);

        manager.IsInitialized.Should().BeTrue();
        manager.Config.Should().Be(config);
    }

    [Fact]
    public void Initialize_ThrowsOnNullConfig()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();

        var act = () => manager.Initialize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();

        var config1 = CreateConfig();
        var config2 = new DynamicInstrumentationConfig(
            true, "http://other:9999", 100, 10, null, "other", "other");

        manager.Initialize(config1);
        manager.Initialize(config2); // second call should be no-op

        manager.Config.Should().Be(config1); // first config wins
    }

    [Fact]
    public void Shutdown_MarksNotInitialized()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        manager.Shutdown();

        manager.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public void Shutdown_IsIdempotent()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();

        // Calling shutdown when not initialized should not throw
        var act = () => manager.Shutdown();
        act.Should().NotThrow();
    }

    [Fact]
    public void OnConfigurationsChanged_RegistersAndApplies()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var configs = new List<InstrumentationConfiguration>
        {
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = "Process",
                LocationHash = "hash1",
                Capture = CaptureConfiguration.Default
            }
        };

        manager.OnConfigurationsChanged(configs);

        manager.Registry.Should().NotBeNull();
        manager.Registry!.Count.Should().Be(1);
        manager.Registry.Get("MyApp.OrderService.Process").Should().NotBeNull();

        manager.Shutdown();
    }

    [Fact]
    public void OnConfigurationsChanged_SkipsUnsupportedTargets()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var configs = new List<InstrumentationConfiguration>
        {
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = ".ctor",
                LocationHash = "hash1",
                Capture = CaptureConfiguration.Default
            }
        };

        manager.OnConfigurationsChanged(configs);

        manager.Registry!.Count.Should().Be(0);

        manager.Shutdown();
    }

    [Fact]
    public void OnConfigurationsChanged_RemovesStaleConfigs()
    {
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var configs = new List<InstrumentationConfiguration>
        {
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "Svc",
                MethodName = "A",
                LocationHash = "h1",
                Capture = CaptureConfiguration.Default
            },
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "Svc",
                MethodName = "B",
                LocationHash = "h2",
                Capture = CaptureConfiguration.Default
            }
        };

        manager.OnConfigurationsChanged(configs);
        manager.Registry!.Count.Should().Be(2);

        // Second call with only method A — B should be removed
        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { configs[0] });
        manager.Registry.Count.Should().Be(1);
        manager.Registry.Get("MyApp.Svc.B").Should().BeNull();

        manager.Shutdown();
    }

    [Fact]
    public void OnConfigurationsChanged_ConcurrentCallers_DoNotCorruptState()
    {
        // The poller invokes OnConfigurationsChanged from BOTH the probe and breakpoint threads.
        // appliedInstrumentations is a plain HashSet; without the configChangeLock guard, two
        // threads mutating it concurrently either throw (torn HashSet) or leave the registry and
        // applied-set diverged. This test reproduces the two-thread contention and asserts the
        // final state is consistent — each caller repeatedly delivers its OWN full set, so the
        // last write from either thread must leave exactly that thread's configs registered.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        static InstrumentationConfiguration Make(string cls, string method, string hash) =>
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = cls,
                MethodName = method,
                LocationHash = hash,
                Capture = CaptureConfiguration.Default,
            };

        // Two disjoint config sets, one per thread. Sets are large so the shared applied-set
        // repeatedly grows/shrinks (and resizes) as the two threads churn each other's keys via
        // RemoveStale — resize under concurrent mutation is where a plain HashSet corrupts/throws.
        var setA = Enumerable.Range(0, 40).Select(i => Make("SvcA", $"A{i}", $"a{i}")).ToList();
        var setB = Enumerable.Range(0, 40).Select(i => Make("SvcB", $"B{i}", $"b{i}")).ToList();

        Exception? failure = null;
        void Hammer(List<InstrumentationConfiguration> set)
        {
            try
            {
                for (int i = 0; i < 2000; i++)
                {
                    manager.OnConfigurationsChanged(set);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        var t1 = new Thread(() => Hammer(setA));
        var t2 = new Thread(() => Hammer(setB));
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        failure.Should().BeNull("concurrent OnConfigurationsChanged must not throw on the shared applied-set");

        // Whichever thread wrote last, the registry must hold exactly that thread's set (40 configs) —
        // never a torn mix or a diverged count. RemoveStale drops the other thread's keys.
        manager.Registry!.Count.Should().Be(40);

        manager.Shutdown();
    }

    [Fact]
    public void OnConfigurationsChanged_RegistersLineLevelConfigs()
    {
        // REGRESSION FOR THE REJECT SITES. Line-level configs used to be dropped by IsSupported before ever
        // reaching the registry — which is what made the entire (built, tested) line-level stack inert in
        // production. A line config must now get a key, a HitState, and an apply attempt.
        //
        // Applying cannot SUCCEED here: resolution needs a loaded target type and a readable PDB for
        // "MyApp.OrderService", which does not exist in the test process. That is the point — this asserts
        // the config is routed and registered, not that a probe wove. The apply failure is permanent
        // (TypeNotLoaded is the retryable one), so registration is what is observable.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var configs = new List<InstrumentationConfiguration>
        {
            new()
            {
                Type = InstrumentationType.BREAKPOINT,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = "Process",
                LineNumber = 42,
                LocationHash = "line-hash",
                Capture = CaptureConfiguration.Default
            }
        };

        manager.OnConfigurationsChanged(configs);

        manager.Registry.Should().NotBeNull();
        manager.Registry!.Count.Should().Be(1);

        // Keyed WITH the line number, so two probes on different lines of one method stay distinct.
        manager.Registry.Get("MyApp.OrderService.Process:42").Should().NotBeNull();

        manager.Shutdown();
    }

    [Fact]
    public void OnConfigurationsChanged_LineAndMethodLevelOnSameMethod_Coexist()
    {
        // A line probe and a method probe on the SAME method must not collide: InstrumentationKey appends
        // ":line" for line-level, so both occupy the registry simultaneously. If the keys collided, adding a
        // line probe would silently displace the function-level capture already running on that method.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var configs = new List<InstrumentationConfiguration>
        {
            new()
            {
                Type = InstrumentationType.PROBE,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = "Process",
                LocationHash = "method-hash",
                Capture = CaptureConfiguration.Default
            },
            new()
            {
                Type = InstrumentationType.BREAKPOINT,
                CodeUnit = "MyApp",
                ClassName = "OrderService",
                MethodName = "Process",
                LineNumber = 42,
                LocationHash = "line-hash",
                Capture = CaptureConfiguration.Default
            }
        };

        manager.OnConfigurationsChanged(configs);

        manager.Registry!.Count.Should().Be(2);
        manager.Registry.Get("MyApp.OrderService.Process").Should().NotBeNull();
        manager.Registry.Get("MyApp.OrderService.Process:42").Should().NotBeNull();

        manager.Shutdown();
    }

    [Fact]
    public void OnConfigurationsChanged_RemovesStaleLineLevelConfigs()
    {
        // Line-level removal goes through the sink's Unregister plus a best-effort native RemoveLineProbe.
        // The registry drop is what actually stops captures (the IL cannot be un-rewritten), so that is what
        // is asserted.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var lineConfig = new InstrumentationConfiguration
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "MyApp",
            ClassName = "OrderService",
            MethodName = "Process",
            LineNumber = 42,
            LocationHash = "line-hash",
            Capture = CaptureConfiguration.Default
        };

        manager.OnConfigurationsChanged(new List<InstrumentationConfiguration> { lineConfig });
        manager.Registry!.Count.Should().Be(1);

        var act = () => manager.OnConfigurationsChanged(new List<InstrumentationConfiguration>());

        // Must not throw even though no native profiler is present — RemoveLineProbe is best-effort.
        act.Should().NotThrow();
        manager.Registry.Count.Should().Be(0);
        manager.Registry.Get("MyApp.OrderService.Process:42").Should().BeNull();

        manager.Shutdown();
    }

    [Fact]
    public void ShutdownThenReinitialize_ReRegistersConfigs()
    {
        // Regression for C3: the applied-instrumentations set must be cleared on
        // Cleanup, otherwise after a restart OnConfigurationsChanged would register the
        // config into the fresh registry but skip re-applying it (stale "already applied"
        // key), leaving registry and applied-set diverged.
        var manager = DynamicInstrumentationManager.Instance;
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "MyApp",
            ClassName = "OrderService",
            MethodName = "Process",
            LocationHash = "hash1",
            Capture = CaptureConfiguration.Default
        };
        var configs = new List<InstrumentationConfiguration> { config };

        manager.OnConfigurationsChanged(configs);
        manager.Registry!.Count.Should().Be(1);

        // Restart
        manager.Shutdown();
        manager.Initialize(CreateConfig());

        // Fresh registry must be empty until reconfigured
        manager.Registry!.Count.Should().Be(0);

        // Re-delivering the same config must register it again (not silently skipped)
        manager.OnConfigurationsChanged(configs);
        manager.Registry.Count.Should().Be(1);
        manager.Registry.Get("MyApp.OrderService.Process").Should().NotBeNull();

        manager.Shutdown();
    }
}
