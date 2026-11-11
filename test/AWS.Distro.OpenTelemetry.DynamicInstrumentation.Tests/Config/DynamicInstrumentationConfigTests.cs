// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Config;

[Collection("SerialProcessState")]
public class DynamicInstrumentationConfigTests : IDisposable
{
    private readonly List<string> _envVarsSet = new();

    public void Dispose()
    {
        foreach (var key in _envVarsSet)
            Environment.SetEnvironmentVariable(key, null);
    }

    private void SetEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _envVarsSet.Add(key);
    }

    [Fact]
    public void FromEnvironment_Defaults_WhenNoEnvVarsSet()
    {
        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.Enabled.Should().BeFalse();
        config.ApiUrl.Should().Be("http://localhost:2000");
        config.ProbePollIntervalSeconds.Should().Be(600);
        config.BreakpointPollIntervalSeconds.Should().Be(60);
        config.LogsEndpoint.Should().Be(
            "http://localhost:4316/v1/logs",
            "snapshots must have somewhere to go by default, matching the Java/Python/JS agents; with no "
            + "default a probe reported ACTIVE while every snapshot was silently dropped");
        config.ServiceName.Should().StartWith("unknown_service:");
        config.Environment.Should().BeEmpty();
    }

    [Fact]
    public void FromEnvironment_ReadsAllEnvVars()
    {
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED", "true");
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL", "http://myagent:3000");
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL", "300");
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL", "30");
        SetEnv("OTEL_AWS_OTLP_LOGS_ENDPOINT", "http://collector:4317/v1/logs");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.Enabled.Should().BeTrue();
        config.ApiUrl.Should().Be("http://myagent:3000");
        config.ProbePollIntervalSeconds.Should().Be(300);
        config.BreakpointPollIntervalSeconds.Should().Be(30);
        config.LogsEndpoint.Should().Be("http://collector:4317/v1/logs");
    }

    // Matches Python (endpoint.strip() or _DEFAULT_LOGS_ENDPOINT) and JS (raw.trim() || DEFAULT): a variable
    // exported with a blank value must not disable snapshot export, because that failure is silent — probes
    // still report ACTIVE and the snapshots simply vanish.
    //
    // NOTE ON THE "" CASE: measured, Environment.SetEnvironmentVariable(name, "") DELETES the variable rather
    // than storing an empty value, so that row exercises the unset path, not a genuinely empty one. A shell's
    // `export VAR=""` does produce an empty value, and IsNullOrWhiteSpace covers it, but that state cannot be
    // created from inside the process. The "   " row is what actually exercises the blank branch.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvironment_TreatsABlankLogsEndpointAsUnset(string configured)
    {
        SetEnv("OTEL_AWS_OTLP_LOGS_ENDPOINT", configured);

        DynamicInstrumentationConfig.FromEnvironment().LogsEndpoint
            .Should().Be("http://localhost:4316/v1/logs");
    }

    [Fact]
    public void FromEnvironment_TrimsTheConfiguredLogsEndpoint()
    {
        // A trailing newline is what a Docker env file or a `read`-into-variable most often leaves behind.
        // This is normalization for parity with Python and JS, NOT crash avoidance: measured, the Uri
        // constructor accepts surrounding whitespace and normalizes it away, so an untrimmed value would still
        // have exported correctly. What it would have corrupted is the stored config value itself, which is
        // what diagnostics and any future equality check on the endpoint read.
        SetEnv("OTEL_AWS_OTLP_LOGS_ENDPOINT", "  http://collector:4318/v1/logs\n");

        DynamicInstrumentationConfig.FromEnvironment().LogsEndpoint
            .Should().Be("http://collector:4318/v1/logs");
    }

    [Fact]
    public void FromEnvironment_EnabledIsCaseInsensitive()
    {
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED", "True");
        DynamicInstrumentationConfig.FromEnvironment().Enabled.Should().BeTrue();

        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED", "TRUE");
        DynamicInstrumentationConfig.FromEnvironment().Enabled.Should().BeTrue();

        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED", "false");
        DynamicInstrumentationConfig.FromEnvironment().Enabled.Should().BeFalse();
    }

    [Fact]
    public void FromEnvironment_InvalidIntFallsBackToDefault()
    {
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL", "not_a_number");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.ProbePollIntervalSeconds.Should().Be(600);
    }

    [Fact]
    public void FromEnvironment_ClampsZeroPollIntervalToMinimum()
    {
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL", "0");
        SetEnv("OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL", "-5");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.ProbePollIntervalSeconds.Should().Be(10);
        config.BreakpointPollIntervalSeconds.Should().Be(10);
    }

    [Fact]
    public void FromEnvironment_ResolvesServiceNameFromOtelServiceName()
    {
        SetEnv("OTEL_SERVICE_NAME", "my-dotnet-app");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.ServiceName.Should().Be("my-dotnet-app");
    }

    [Fact]
    public void FromEnvironment_ResolvesServiceNameFromResourceAttributes()
    {
        SetEnv("OTEL_RESOURCE_ATTRIBUTES", "service.name=my-service,deployment.environment.name=prod");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.ServiceName.Should().Be("my-service");
        config.Environment.Should().Be("prod");
    }

    [Fact]
    public void FromEnvironment_OtelServiceNameTakesPrecedenceOverResourceAttributes()
    {
        SetEnv("OTEL_SERVICE_NAME", "from-service-name");
        SetEnv("OTEL_RESOURCE_ATTRIBUTES", "service.name=from-attributes");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.ServiceName.Should().Be("from-service-name");
    }

    [Fact]
    public void FromEnvironment_HandlesEmptyResourceAttributes()
    {
        SetEnv("OTEL_RESOURCE_ATTRIBUTES", "");

        var config = DynamicInstrumentationConfig.FromEnvironment();

        config.ServiceName.Should().StartWith("unknown_service:");
        config.Environment.Should().BeEmpty();
    }
}
