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

    [Fact]
    public void TryResolveKeyByType_ResolvesRegisteredTypeViaIndex()
    {
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();

        registry.Register(config);

        // MyApp.Svc -> the registered key, resolved by the TypeName index (not a scan).
        registry.TryResolveKeyByType("MyApp.Svc").Should().Be(config.InstrumentationKey);
        registry.TryResolveKeyByType("Not.Registered").Should().BeNull();
    }

    [Fact]
    public void TryResolveKeyByType_AfterRemoveStale_NoLongerResolves()
    {
        // The TypeName index must stay in sync with RemoveStale: once the only config for a type is
        // dropped, that type must stop resolving (otherwise a woven call resolves to a dead key).
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();
        registry.Register(config);
        registry.TryResolveKeyByType("MyApp.Svc").Should().NotBeNull();

        registry.RemoveStale(new HashSet<string>()); // nothing active → remove all

        registry.TryResolveKeyByType("MyApp.Svc").Should().BeNull();
    }

    [Fact]
    public void TryResolveKeyByType_MultipleMethodsSameType_ReturnsNull_ThenResolvesWhenUnambiguous()
    {
        // Type-only resolution must NOT guess among multiple configs on one type: guessing wrong
        // misattributes a capture to the wrong probe (worse than dropping it, see #3). So while two
        // methods are registered it returns null; once only one remains it's unambiguous and resolves.
        var registry = new InstrumentationRegistry();
        var a = CreateConfig(locationHash: "ha", method: "A");
        var b = CreateConfig(locationHash: "hb", method: "B");
        registry.Register(a);
        registry.Register(b);

        // Two configs on MyApp.Svc → ambiguous by type alone → null (drop rather than misattribute).
        registry.TryResolveKeyByType("MyApp.Svc").Should().BeNull();

        // Drop A; the type now hosts exactly one config → unambiguous → resolves to the survivor.
        registry.RemoveStale(new HashSet<string> { b.InstrumentationKey });
        registry.TryResolveKeyByType("MyApp.Svc").Should().Be(b.InstrumentationKey);
    }

    [Fact]
    public void TryResolveKeyByTypeAndArity_DifferentArities_DisambiguatesCoLocatedMethods()
    {
        // The core #3 fix: two methods on one type, one arg-count each. A woven call resolves to the
        // config whose method has the matching parameter count — not "first key wins".
        var registry = new InstrumentationRegistry();
        var oneArg = CreateConfig(locationHash: "h1", method: "Process");
        var twoArg = CreateConfig(locationHash: "h2", method: "Validate");
        registry.Register(oneArg);
        registry.Register(twoArg);
        registry.IndexArities("MyApp.Svc", oneArg.InstrumentationKey, new[] { 1 });
        registry.IndexArities("MyApp.Svc", twoArg.InstrumentationKey, new[] { 2 });

        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 1).Should().Be(oneArg.InstrumentationKey);
        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 2).Should().Be(twoArg.InstrumentationKey);
    }

    [Fact]
    public void TryResolveKeyByTypeAndArity_UnindexedArity_ReturnsNull()
    {
        // Resolution is precise: an arity that was never indexed does not resolve (caller falls back to
        // the type-only index).
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();
        registry.Register(config);
        registry.IndexArities("MyApp.Svc", config.InstrumentationKey, new[] { 1 });

        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 2).Should().BeNull();
        registry.TryResolveKeyByTypeAndArity("Not.Registered", 1).Should().BeNull();
    }

    [Fact]
    public void IndexArities_Overloads_IndexEachArity()
    {
        // One config can weave several arities (overloaded method) — each must resolve to that config.
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();
        registry.Register(config);
        registry.IndexArities("MyApp.Svc", config.InstrumentationKey, new[] { 0, 1, 3 });

        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 0).Should().Be(config.InstrumentationKey);
        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 1).Should().Be(config.InstrumentationKey);
        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 3).Should().Be(config.InstrumentationKey);
    }

    [Fact]
    public void IndexArities_SameArityTwoMethods_ReportsCollision()
    {
        // The documented #3 residual: two methods on one type sharing a parameter count are
        // indistinguishable at capture time (args.Length can't separate them). IndexArities flags it.
        var registry = new InstrumentationRegistry();
        var a = CreateConfig(locationHash: "ha", method: "Process");
        var b = CreateConfig(locationHash: "hb", method: "Validate");
        registry.Register(a);
        registry.Register(b);

        // First method at arity 1: no prior key → no collision.
        registry.IndexArities("MyApp.Svc", a.InstrumentationKey, new[] { 1 }).Should().BeFalse();

        // Second method also at arity 1: collides with the first.
        registry.IndexArities("MyApp.Svc", b.InstrumentationKey, new[] { 1 }).Should().BeTrue();
    }

    [Fact]
    public void IndexArities_SameKeyReindexed_DoesNotReportCollision()
    {
        // Re-applying the same config (e.g. a later poll) must not be mistaken for a collision.
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();
        registry.Register(config);

        registry.IndexArities("MyApp.Svc", config.InstrumentationKey, new[] { 1 }).Should().BeFalse();
        registry.IndexArities("MyApp.Svc", config.InstrumentationKey, new[] { 1 }).Should().BeFalse();
    }

    [Fact]
    public void TryResolveKeyByTypeAndArity_AfterRemoveStale_NoLongerResolves()
    {
        // The arity index must stay in sync with RemoveStale, exactly like the type-only index — a dropped
        // config must stop resolving by arity too, or a woven call resolves to a dead key.
        var registry = new InstrumentationRegistry();
        var config = CreateConfig();
        registry.Register(config);
        registry.IndexArities("MyApp.Svc", config.InstrumentationKey, new[] { 1 });
        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 1).Should().NotBeNull();

        registry.RemoveStale(new HashSet<string>()); // nothing active → remove all

        registry.TryResolveKeyByTypeAndArity("MyApp.Svc", 1).Should().BeNull();
    }
}
