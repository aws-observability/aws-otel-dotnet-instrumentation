// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Config;

/// <summary>
/// Tests for <see cref="ServiceEventsConfig" />.
/// </summary>
/// <remarks>
/// Tests modify process environment variables, so each test isolates the
/// vars it touches via <see cref="EnvScope" />. Tests are not safe to run
/// in parallel within the same process — xUnit's default behavior is one
/// test class at a time which is sufficient here.
/// </remarks>
[Collection("EnvironmentVariables")]
public class ServiceEventsConfigTests
{
    [Fact]
    public void Defaults_ShouldMatchSpec()
    {
        using var _ = EnvScope.Clear(KnownEnvVars);
        var cfg = new ServiceEventsConfig();

        cfg.Enabled.Should().BeNull(
            "the kill switch is tri-state: unset means the Application Signals bundling rule decides, " +
            "which is a different thing from an explicit false");
        cfg.ApplicationSignalsEnabled.Should().BeFalse();
        cfg.OutputFile.Should().BeEmpty();
        cfg.ServiceName.Should().Be("UnknownService");
        cfg.Environment.Should().BeEmpty();
        cfg.FunctionCallFlushInterval.Should().Be(30_000);
        cfg.EndpointFlushInterval.Should().Be(30_000);
        cfg.IncidentSnapshotFlushInterval.Should().Be(10_000);
        cfg.IncidentSnapshotMaxPerMinute.Should().Be(100);
        cfg.IncidentSnapshotDurationThresholdMs.Should().Be(5_000);
        cfg.IncidentSnapshotMaxSameError.Should().Be(
            1, "the env-vars spec sets the per-error dedup ceiling to 1");
        cfg.SamplingMode.Should().Be("always");
        cfg.SampleTier1Threshold.Should().Be(100);
        cfg.SampleTier2Threshold.Should().Be(1000);
        cfg.SampleTier2Rate.Should().Be(10);
        cfg.SampleTier3Rate.Should().Be(100);
        cfg.LogGroup.Should().Be("/serviceevents/telemetry");
        cfg.FunctionInstrumentEnabled.Should().BeTrue();
    }

    /// <summary>
    /// Pins the incident-snapshot rate-limit env var names to the env-vars spec. The window is fixed
    /// at one minute there and is deliberately not configurable, so the knob is
    /// <c>MAX_PER_MINUTE</c>; an earlier <c>MAX_PER_PERIOD</c> plus a <c>PERIOD_MINUTES</c> window
    /// override existed in no spec and matched no other SDK's customer-facing surface.
    /// </summary>
    [Fact]
    public void FromEnvironment_ReadsIncidentSnapshotRateLimitsUnderTheSpecNames()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_PER_MINUTE"] = "250",
            ["OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_SAME_ERROR"] = "7",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.IncidentSnapshotMaxPerMinute.Should().Be(250);
        cfg.IncidentSnapshotMaxSameError.Should().Be(7);
    }

    /// <summary>
    /// The deployment/VCS provenance vars belong to config like every other var. They used to be
    /// read straight from the environment at three separate call sites — the OTLP emitter, the
    /// resource attributes, and the DeploymentEvent context — which meant three places decided what
    /// an env var meant and none of them were covered here.
    /// </summary>
    [Fact]
    public void FromEnvironment_ReadsDeploymentAndVcsProvenance()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_GIT_COMMIT_SHA"] = "0f1e2d3",
            ["OTEL_AWS_SERVICE_EVENTS_GIT_REPO_URL"] = "https://github.com/example/repo",
            ["OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_ID"] = "deploy-42",
            ["OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_URL"] = "https://deploy.example/42",
            ["OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_TIMESTAMP"] = "2026-07-16T00:00:00Z",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.GitCommitSha.Should().Be("0f1e2d3");
        cfg.GitRepoUrl.Should().Be("https://github.com/example/repo");
        cfg.DeploymentId.Should().Be("deploy-42");
        cfg.DeploymentUrl.Should().Be("https://deploy.example/42");
        cfg.DeploymentTimestamp.Should().Be(
            "2026-07-16T00:00:00Z",
            "the timestamp is passed through as the pipeline supplied it rather than reformatted");
    }

    [Fact]
    public void FromEnvironment_WhenAllUnset_ReturnsDefaults()
    {
        using var _ = EnvScope.Clear(KnownEnvVars);

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.Enabled.Should().BeNull("an unset kill switch defers to the bundling rule");
        cfg.ServiceName.Should().Be("UnknownService");
        cfg.FunctionInstrumentEnabled.Should().BeTrue();
        cfg.GitCommitSha.Should().BeEmpty("provenance is absent unless a pipeline supplies it");
        cfg.DeploymentId.Should().BeEmpty();
    }

    [Fact]
    public void FromEnvironment_WhenAllSet_ReadsValues()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
            ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "true",
            ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = "/tmp/serviceevents.ndjson",
            ["OTEL_SERVICE_NAME"] = "my-service",
            ["OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL"] = "5000",
            ["OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED"] = "true",
            ["OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS"] = "POST /api/checkout:500,GET /api/health:50",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.Enabled.Should().BeTrue();
        cfg.ApplicationSignalsEnabled.Should().BeTrue();
        cfg.OutputFile.Should().Be("/tmp/serviceevents.ndjson");
        cfg.ServiceName.Should().Be("my-service");
        cfg.FunctionCallFlushInterval.Should().Be(5000);
        cfg.FunctionInstrumentEnabled.Should().BeTrue();
        cfg.LatencyThresholds.Should().BeEquivalentTo(new[]
        {
            "POST /api/checkout:500",
            "GET /api/health:50",
        });
    }

    [Fact]
    public void FromEnvironment_BoolParse_IsCaseInsensitive()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "TRUE",
            ["OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED"] = "False",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.Enabled.Should().BeTrue();
        cfg.FunctionInstrumentEnabled.Should().BeFalse();
    }

    [Fact]
    public void FromEnvironment_InvalidInt_FallsBackToDefault()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL"] = "not-a-number",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.FunctionCallFlushInterval.Should().Be(30_000);
    }

    [Fact]
    public void FromEnvironment_ServiceName_PrefersOtelServiceNameOverResourceAttrs()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_SERVICE_NAME"] = "explicit-name",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=attr-name,deployment.environment=prod",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.ServiceName.Should().Be("explicit-name");
    }

    [Fact]
    public void FromEnvironment_ServiceName_FallsBackToResourceAttrs()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=attr-name",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.ServiceName.Should().Be("attr-name");
    }

    [Fact]
    public void FromEnvironment_Environment_PrefersDeploymentEnvironmentName()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = "deployment.environment=legacy,deployment.environment.name=preferred",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.Environment.Should().Be("preferred");
    }

    [Fact]
    public void FromEnvironment_Environment_FallsBackToLegacyKey()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = "deployment.environment=legacy-only",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.Environment.Should().Be("legacy-only");
    }

    [Fact]
    public void FromEnvironment_Environment_FallsBackToEnvironmentEnvVar()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["ENVIRONMENT"] = "prod",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.Environment.Should().Be("prod");
    }

    [Fact]
    public void FromEnvironment_Packages_RejectsBareStarSentinel()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE"] = "myapp,*,otherapp",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.PackagesToInstrument.Should().BeEquivalentTo(new[] { "myapp", "otherapp" });
    }

    [Fact]
    public void FromEnvironment_PackagesExclude_ReadsValues()
    {
        using var _ = EnvScope.Isolate(new()
        {
            ["OTEL_AWS_SERVICE_EVENTS_PACKAGES_EXCLUDE"] = "System.*,Microsoft.*",
        });

        var cfg = ServiceEventsConfig.FromEnvironment();

        cfg.ExcludePatterns.Should().BeEquivalentTo(new[] { "System.*", "Microsoft.*" });
    }

    [Fact]
    public void ShouldInstrumentFunction_WhenAllowlistEmpty_ReturnsFalse()
    {
        var cfg = new ServiceEventsConfig();

        cfg.ShouldInstrumentFunction("MyApp.Service.Handle").Should().BeFalse();
    }

    [Fact]
    public void ShouldInstrumentFunction_WhenMatchesAllowlist_ReturnsTrue()
    {
        var cfg = new ServiceEventsConfig
        {
            PackagesToInstrument = new[] { "System.Net.Http.*" },
        };

        cfg.ShouldInstrumentFunction("System.Net.Http.HttpRequestOut").Should().BeTrue();
    }

    [Fact]
    public void ShouldInstrumentFunction_WhenNotInAllowlist_ReturnsFalse()
    {
        var cfg = new ServiceEventsConfig
        {
            PackagesToInstrument = new[] { "System.Net.Http.*" },
        };

        cfg.ShouldInstrumentFunction("Amazon.Runtime.HttpRequest").Should().BeFalse();
    }

    [Fact]
    public void ShouldInstrumentFunction_WhenExcludeMatches_ExcludeWins()
    {
        var cfg = new ServiceEventsConfig
        {
            PackagesToInstrument = new[] { "System.*" },
            ExcludePatterns = new[] { "System.Net.Http.*" },
        };

        cfg.ShouldInstrumentFunction("System.Net.Http.HttpRequestOut").Should().BeFalse();
        cfg.ShouldInstrumentFunction("System.Data.SqlClient.Execute").Should().BeTrue();
    }

    [Theory]
    // (enabledFlag, appSignalsEnabled, isLambda, expected)
    [InlineData(null, false, true, false)]   // unset + AS off + Lambda → false (Lambda always disabled)
    [InlineData(null, true, true, false)]    // unset + AS on + Lambda → false (Lambda wins over AS)
    [InlineData("true", true, true, false)]  // explicit on + Lambda → still false (Lambda wins over explicit)
    [InlineData(null, false, false, false)]  // unset + AS off → false
    [InlineData(null, true, false, true)]    // unset + AS on → true (bundled with App Signals)
    [InlineData("true", false, false, true)] // explicit on → true (overrides AS off)
    [InlineData("false", true, false, false)] // explicit off → false (overrides AS on)
    public void DetermineEnabled_AppliesSpecBundlingRule(string? enabledFlag, bool appSignalsEnabled, bool isLambda, bool expected)
    {
        var envVars = new Dictionary<string, string>();
        if (enabledFlag is not null)
        {
            envVars["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = enabledFlag;
        }

        if (isLambda)
        {
            envVars["AWS_LAMBDA_FUNCTION_NAME"] = "my-fn";
        }

        // Isolate, not Set: this theory deliberately omits keys to exercise the defaults, so an
        // ambient OTEL_AWS_SERVICE_EVENTS_ENABLED or AWS_LAMBDA_FUNCTION_NAME would silently
        // change the outcome.
        using var _ = EnvScope.Isolate(envVars);

        // Built via FromEnvironment so the kill switch travels the real path — env var into
        // config.Enabled, config into the decision. Constructing the config by hand would set only
        // ApplicationSignalsEnabled and leave Enabled unset, which is what let the property go dead
        // in the first place: DetermineEnabled re-read the environment, so a hand-built config could
        // never disagree with it and the wiring was never covered.
        var cfg = ServiceEventsConfig.FromEnvironment() with { ApplicationSignalsEnabled = appSignalsEnabled };

        ServiceEventsConfig.DetermineEnabled(cfg).Should().Be(expected);
    }

    /// <summary>
    /// The enablement decision reads the config object, not the environment.
    /// <para>
    /// This is the case the theory above cannot cover: it sets the environment variable and builds the
    /// config from it, so both a config-reading and an environment-reading implementation pass. Here
    /// the environment is empty and the value exists only on the config, which is exactly what was
    /// broken — <c>DetermineEnabled</c> re-read the environment, so <c>config.Enabled</c> had no
    /// effect on anything and setting it proved nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void DetermineEnabled_ReadsTheConfigObjectRatherThanTheEnvironment()
    {
        // Nothing set: any environment read returns null, so only the config can supply an answer.
        using var _ = EnvScope.Isolate(new());

        ServiceEventsConfig.DetermineEnabled(new ServiceEventsConfig { Enabled = true })
            .Should().BeTrue("an explicit true on the config must force ServiceEvents on");

        ServiceEventsConfig.DetermineEnabled(
                new ServiceEventsConfig { Enabled = false, ApplicationSignalsEnabled = true })
            .Should().BeFalse("an explicit false on the config must win over the bundling rule");

        ServiceEventsConfig.DetermineEnabled(
                new ServiceEventsConfig { Enabled = null, ApplicationSignalsEnabled = true })
            .Should().BeTrue("unset must defer to Application Signals, not read as false");
    }

    [Fact]
    public void GetLatencyThresholdPatterns_ParsesValidEntries()
    {
        var cfg = new ServiceEventsConfig
        {
            LatencyThresholds = new[]
            {
                "POST /api/checkout:500",
                "GET /api/health:50",
                "* /server_request:25",
            },
        };

        var patterns = cfg.GetLatencyThresholdPatterns();

        patterns.Should().HaveCount(3);
        patterns[0].Should().Be(("POST /api/checkout", 500.0));
        patterns[1].Should().Be(("GET /api/health", 50.0));
        patterns[2].Should().Be(("* /server_request", 25.0));
    }

    [Theory]
    [InlineData("malformed-no-colon")]
    [InlineData(":missing-method")]
    [InlineData("GET /route:not-a-number")]
    [InlineData("nomethod-route:100")]
    public void GetLatencyThresholdPatterns_SkipsMalformedEntries(string entry)
    {
        var cfg = new ServiceEventsConfig
        {
            LatencyThresholds = new[] { entry, "POST /good:100" },
        };

        var patterns = cfg.GetLatencyThresholdPatterns();

        patterns.Should().HaveCount(1);
        patterns[0].Pattern.Should().Be("POST /good");
    }

    [Fact]
    public void ShouldTrackEndpoint_NoFilters_AllowsAll()
    {
        var cfg = new ServiceEventsConfig();
        cfg.ShouldTrackEndpoint("/api/users", "GET").Should().BeTrue();
        cfg.ShouldTrackEndpoint("/health", "GET").Should().BeTrue();
    }

    [Fact]
    public void ShouldTrackEndpoint_IncludePatterns_OnlyTracksMatching()
    {
        var cfg = new ServiceEventsConfig
        {
            EndpointIncludePatterns = new[] { "GET /api/*", "POST /api/*" },
        };

        cfg.ShouldTrackEndpoint("/api/users", "GET").Should().BeTrue();
        cfg.ShouldTrackEndpoint("/api/orders", "POST").Should().BeTrue();
        cfg.ShouldTrackEndpoint("/health", "GET").Should().BeFalse();
    }

    [Fact]
    public void ShouldTrackEndpoint_ExcludePatterns_RemovesMatching()
    {
        var cfg = new ServiceEventsConfig
        {
            EndpointExcludePatterns = new[] { "* /health", "* /metrics" },
        };

        cfg.ShouldTrackEndpoint("/api/users", "GET").Should().BeTrue();
        cfg.ShouldTrackEndpoint("/health", "GET").Should().BeFalse();
        cfg.ShouldTrackEndpoint("/metrics", "POST").Should().BeFalse();
    }

    [Fact]
    public void ShouldTrackEndpoint_BothFilters_IncludeWinsThenExcludeFilters()
    {
        var cfg = new ServiceEventsConfig
        {
            EndpointIncludePatterns = new[] { "* /api/*" },
            EndpointExcludePatterns = new[] { "* /api/internal/*" },
        };

        cfg.ShouldTrackEndpoint("/api/users", "GET").Should().BeTrue();
        cfg.ShouldTrackEndpoint("/api/internal/debug", "GET").Should().BeFalse();
        cfg.ShouldTrackEndpoint("/health", "GET").Should().BeFalse(); // not in include
    }

    private static readonly string[] KnownEnvVars = new[]
    {
        "OTEL_AWS_SERVICE_EVENTS_ENABLED",
        "OTEL_AWS_APPLICATION_SIGNALS_ENABLED",
        "OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE",
        "OTEL_SERVICE_NAME",
        "OTEL_RESOURCE_ATTRIBUTES",
        "ENVIRONMENT",
        "OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL",
        "OTEL_AWS_SERVICE_EVENTS_ENDPOINT_FLUSH_INTERVAL",
        "OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_FLUSH_INTERVAL",
        "OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED",
        "OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE",
        "OTEL_AWS_SERVICE_EVENTS_PACKAGES_EXCLUDE",
        "OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS",
        "OTEL_AWS_SERVICE_EVENTS_ENDPOINT_INCLUDE_PATTERNS",
        "OTEL_AWS_SERVICE_EVENTS_ENDPOINT_EXCLUDE_PATTERNS",
        "OTEL_AWS_OTLP_LOGS_ENDPOINT",
        "OTEL_AWS_OTLP_METRICS_ENDPOINT",
        "OTEL_AWS_SERVICE_EVENTS_LOG_GROUP",
        "OTEL_AWS_SERVICE_EVENTS_LOG_STREAM",
        "AWS_LAMBDA_FUNCTION_NAME",
    };
}

/// <summary>
/// Helper that snapshots and restores environment variables around a test
/// scope. Use with <c>using var _ = EnvScope.Set(...)</c> or
/// <c>EnvScope.Clear(...)</c> to keep tests hermetic.
/// </summary>
internal sealed class EnvScope : IDisposable
{
    /// <summary>
    /// Every environment variable that can influence <c>ServiceEventsConfig</c> or the emitted
    /// resource. <see cref="Isolate" /> clears all of them so a test cannot be affected by ambient
    /// values it never set.
    /// </summary>
    /// <remarks>
    /// Keep this complete. A missing entry is a silent flake: an integration test asserting on an
    /// omitted attribute passed locally and failed on any machine that happened to export the
    /// variable. <c>ENVIRONMENT</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c> both feed
    /// <c>deployment.environment</c>, and the deployment/git variables are read directly by
    /// <c>DeploymentEventEmitter</c> rather than through the config record.
    /// </remarks>
    internal static readonly string[] AllInfluencingVars = new[]
    {
        "OTEL_AWS_SERVICE_EVENTS_ENABLED",
        "OTEL_AWS_APPLICATION_SIGNALS_ENABLED",
        "OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE",
        "OTEL_SERVICE_NAME",
        "OTEL_RESOURCE_ATTRIBUTES",
        "ENVIRONMENT",
        "RESOURCE_DETECTORS_ENABLED",
        "AWS_LAMBDA_FUNCTION_NAME",
        "OTEL_AWS_OTLP_LOGS_ENDPOINT",
        "OTEL_AWS_OTLP_METRICS_ENDPOINT",
        "OTEL_AWS_SERVICE_EVENTS_LOG_GROUP",
        "OTEL_AWS_SERVICE_EVENTS_LOG_STREAM",
        "OTEL_AWS_SERVICE_EVENTS_ENDPOINT_FLUSH_INTERVAL",
        "OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL",
        "OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_FLUSH_INTERVAL",
        "OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_PER_MINUTE",
        "OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_SAME_ERROR",
        "OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_DURATION_THRESHOLD_MS",
        "OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS",
        "OTEL_AWS_SERVICE_EVENTS_ENDPOINT_INCLUDE_PATTERNS",
        "OTEL_AWS_SERVICE_EVENTS_ENDPOINT_EXCLUDE_PATTERNS",
        "OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE",
        "OTEL_AWS_SERVICE_EVENTS_PACKAGES_EXCLUDE",
        "OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED",
        "OTEL_AWS_SERVICE_EVENTS_GIT_COMMIT_SHA",
        "OTEL_AWS_SERVICE_EVENTS_GIT_REPO_URL",
        "OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_ID",
        "OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_URL",
        "OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_TIMESTAMP",
    };

    private readonly Dictionary<string, string?> _previous = new();

    private EnvScope(IEnumerable<string> trackedVars)
    {
        foreach (var name in trackedVars)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
        }
    }

    public static EnvScope Set(Dictionary<string, string> vars)
    {
        var scope = new EnvScope(vars.Keys);
        foreach (var (k, v) in vars)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        return scope;
    }

    /// <summary>
    /// Clear every variable in <see cref="AllInfluencingVars" />, then apply <paramref name="vars" />.
    /// Use this instead of <see cref="Set" /> for tests that assert on emitted output, so the
    /// assertion cannot be perturbed by a variable exported on the host.
    /// </summary>
    public static EnvScope Isolate(Dictionary<string, string> vars)
    {
        var tracked = new HashSet<string>(AllInfluencingVars, StringComparer.Ordinal);
        tracked.UnionWith(vars.Keys);

        var scope = new EnvScope(tracked);

        foreach (var name in tracked)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var (k, v) in vars)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        return scope;
    }

    public static EnvScope Clear(IEnumerable<string> names)
    {
        var scope = new EnvScope(names);
        foreach (var name in names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        return scope;
    }

    public void Dispose()
    {
        foreach (var (k, v) in _previous)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
    }
}
