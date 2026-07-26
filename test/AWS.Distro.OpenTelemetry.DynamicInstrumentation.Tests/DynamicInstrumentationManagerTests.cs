// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests;

[Collection("SerialProcessState")]
public class DynamicInstrumentationManagerTests : IDisposable
{
    // The Manager singleton owns a background snapshot collector that drains the process-global
    // DIDataStore. Shut it down after every test so its drain thread is joined and stops competing
    // with other global-state suites for the shared queue.
    public void Dispose() => DynamicInstrumentationManager.Instance.Shutdown();

    private static DynamicInstrumentationConfig CreateConfig(bool enabled = true) =>
        new(
            Enabled: enabled,
            ApiUrl: "http://localhost:2000",
            ProbePollIntervalSeconds: 600,
            BreakpointPollIntervalSeconds: 60,
            LogsEndpoint: "http://localhost:4317/v1/logs",
            ServiceName: "test-service",
            Environment: "test-env");

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
