// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests;

[Collection("SerialProcessState")]
public class DynamicInstrumentationManagerTests
{
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
}
