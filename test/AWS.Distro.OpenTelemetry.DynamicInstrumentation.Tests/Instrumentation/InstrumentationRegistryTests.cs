// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

public class InstrumentationRegistryTests
{
    private static InstrumentationConfiguration CreateConfig(
        string locationHash = "hash1",
        string method = "Process",
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Type = InstrumentationType.PROBE,
            CodeUnit = "MyApp",
            ClassName = "Svc",
            MethodName = method,
            LocationHash = locationHash,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Capture = CaptureConfiguration.Default
        };

    [Fact]
    public void Register_AddsConfig()
    {
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();

        registry.Register(config);

        registry.Count.Should().Be(1);
        registry.Get(config.InstrumentationKey).Should().NotBeNull();
    }

    [Fact]
    public void Register_SameConfig_PreservesHitState()
    {
        var registry = new InstrumentationRegistry();
        var created = DateTimeOffset.UtcNow;
        var config = CreateConfig(createdAt: created);

        registry.Register(config);
        registry.TryHit(config.InstrumentationKey); // increment hit count

        // Re-register same config (same locationHash + createdAt)
        var sameConfig = CreateConfig(createdAt: created);
        registry.Register(sameConfig);

        // Hit state preserved — count should still be 1
        var reg = registry.Get(config.InstrumentationKey);
        reg!.HitState.HitCount.Should().Be(1);
    }

    [Fact]
    public void Register_ChangedConfig_ResetsHitState()
    {
        var registry = new InstrumentationRegistry();
        var config = CreateConfig(locationHash: "v1", createdAt: DateTimeOffset.UtcNow);

        registry.Register(config);
        registry.TryHit(config.InstrumentationKey);

        // Re-register with different locationHash (config changed)
        var newConfig = CreateConfig(locationHash: "v2", createdAt: DateTimeOffset.UtcNow);
        registry.Register(newConfig);

        var reg = registry.Get(newConfig.InstrumentationKey);
        reg!.HitState.HitCount.Should().Be(0);
    }

    [Fact]
    public void RemoveStale_RemovesConfigsNotInActiveSet()
    {
        var registry = new InstrumentationRegistry();
        var config1 = CreateConfig(method: "A");
        var config2 = CreateConfig(method: "B");

        registry.Register(config1);
        registry.Register(config2);

        var activeKeys = new HashSet<string> { config1.InstrumentationKey };
        var removed = registry.RemoveStale(activeKeys);

        removed.Should().Contain(config2.InstrumentationKey);
        registry.Count.Should().Be(1);
        registry.Get(config2.InstrumentationKey).Should().BeNull();
    }

    [Fact]
    public void RemoveStale_KeepsActiveConfigs()
    {
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();

        registry.Register(config);

        var activeKeys = new HashSet<string> { config.InstrumentationKey };
        var removed = registry.RemoveStale(activeKeys);

        removed.Should().BeEmpty();
        registry.Count.Should().Be(1);
    }

    [Fact]
    public void TryHit_RegisteredConfig_ReturnsTrue()
    {
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();
        registry.Register(config);

        registry.TryHit(config.InstrumentationKey).Should().BeTrue();
    }

    [Fact]
    public void TryHit_UnknownKey_ReturnsFalse()
    {
        var registry = new InstrumentationRegistry();

        registry.TryHit("nonexistent.key").Should().BeFalse();
    }

    [Fact]
    public void TryHit_DisabledConfig_ReturnsFalse()
    {
        var registry = new InstrumentationRegistry();
        var config = new InstrumentationConfiguration
        {
            Type = InstrumentationType.BREAKPOINT,
            CodeUnit = "MyApp",
            ClassName = "Svc",
            MethodName = "Limited",
            LocationHash = "hash1",
            Capture = new CaptureConfiguration(
                null, null, true, false,
                255, 20, 3, 3, 20, 20, MaxHits: 2)
        };

        registry.Register(config);

        registry.TryHit(config.InstrumentationKey).Should().BeTrue();  // 1
        registry.TryHit(config.InstrumentationKey).Should().BeTrue();  // 2
        registry.TryHit(config.InstrumentationKey).Should().BeFalse(); // 3 — maxHits exceeded
    }

    [Fact]
    public void GetAll_ReturnsAllRegistered()
    {
        var registry = new InstrumentationRegistry();
        registry.Register(CreateConfig(method: "A"));
        registry.Register(CreateConfig(method: "B"));
        registry.Register(CreateConfig(method: "C"));

        registry.GetAll().Should().HaveCount(3);
    }
}
