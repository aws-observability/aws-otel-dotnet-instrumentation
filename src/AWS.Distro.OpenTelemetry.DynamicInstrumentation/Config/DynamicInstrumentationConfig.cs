// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;

/// <summary>
/// Dynamic Instrumentation configuration resolved from environment variables.
/// </summary>
/// <param name="Enabled">Whether the feature is enabled.</param>
/// <param name="ApiUrl">Base URL of the configuration API (the local CloudWatch Agent).</param>
/// <param name="ProbePollIntervalSeconds">Seconds between probe configuration polls.</param>
/// <param name="BreakpointPollIntervalSeconds">Seconds between breakpoint configuration polls.</param>
/// <param name="LogsEndpoint">
/// OTLP logs endpoint for emitting snapshots. Always populated by <see cref="FromEnvironment"/>; nullable
/// only for callers that construct a configuration directly and want capture without export.
/// </param>
/// <param name="ServiceName">Resolved service name.</param>
/// <param name="Environment">Resolved deployment environment.</param>
public sealed record DynamicInstrumentationConfig(
    bool Enabled,
    string ApiUrl,
    int ProbePollIntervalSeconds,
    int BreakpointPollIntervalSeconds,
    string? LogsEndpoint,
    string ServiceName,
    string Environment)
{
    private const string Prefix = "OTEL_AWS_DYNAMIC_INSTRUMENTATION_";

    private const int MinPollIntervalSeconds = 10;

    /// <summary>
    /// Default OTLP logs endpoint — the local CloudWatch Agent's receiver, byte-identical to the Java, Python
    /// and JS agents' default so one documented value works across all four SDKs.
    /// </summary>
    private const string DefaultLogsEndpoint = "http://localhost:4316/v1/logs";

    /// <summary>Builds a configuration from the process environment variables.</summary>
    /// <returns>The resolved configuration.</returns>
    public static DynamicInstrumentationConfig FromEnvironment()
    {
        var enabled = GetBool($"{Prefix}ENABLED", false);
        var apiUrl = GetString($"{Prefix}API_URL", "http://localhost:2000");
        var probePoll = Math.Max(MinPollIntervalSeconds, GetInt($"{Prefix}PROBE_POLL_INTERVAL", 600));
        var breakpointPoll = Math.Max(MinPollIntervalSeconds, GetInt($"{Prefix}BREAKPOINT_POLL_INTERVAL", 60));

        // Cross-SDK env var (NOT under the DI prefix) — matches the Java/Python/JS agents, INCLUDING the
        // default. All three fall back to the local CloudWatch Agent's OTLP logs receiver when it is unset:
        // Java's DynamicInstrumentationConfig.DEFAULT_LOGS_ENDPOINT, Python's _DEFAULT_LOGS_ENDPOINT in
        // _snapshot_otlp_emitter.py, and JS's getEnvStr(..., 'http://localhost:4316/v1/logs') in config.ts.
        // Without a default, an operator who enabled DI and created a probe saw it report ACTIVE while every
        // snapshot was dropped on the floor, with nothing anywhere saying why.
        //
        // Whitespace-only counts as unset, as it does in Python (endpoint.strip() or default) and JS
        // (raw.trim() || DEFAULT), so a variable set to "" cannot silently disable snapshot export.
        var logsEndpoint = ResolveLogsEndpoint();
        var serviceName = ResolveServiceName();
        var environment = ResolveEnvironment();

        return new DynamicInstrumentationConfig(
            enabled, apiUrl, probePoll, breakpointPoll, logsEndpoint, serviceName, environment);
    }

    private static string ResolveLogsEndpoint()
    {
        var configured = System.Environment.GetEnvironmentVariable("OTEL_AWS_OTLP_LOGS_ENDPOINT");

        return string.IsNullOrWhiteSpace(configured) ? DefaultLogsEndpoint : configured.Trim();
    }

    private static string ResolveServiceName()
    {
        var name = System.Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        var attrs = System.Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES") ?? string.Empty;
        return ExtractResourceAttribute(attrs, "service.name") ?? $"unknown_service:{GetProcessName()}";
    }

    private static string ResolveEnvironment()
    {
        var attrs = System.Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES") ?? string.Empty;

        // Newer stable key, falling back to the legacy key.
        return ExtractResourceAttribute(attrs, "deployment.environment.name")
            ?? ExtractResourceAttribute(attrs, "deployment.environment")
            ?? string.Empty;
    }

    private static string? ExtractResourceAttribute(string attrs, string key)
    {
        foreach (var pair in attrs.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == key)
            {
                return Uri.UnescapeDataString(parts[1].Trim());
            }
        }

        return null;
    }

    private static bool GetBool(string name, bool defaultValue)
    {
        var val = System.Environment.GetEnvironmentVariable(name);
        return val != null ? val.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;
    }

    private static string GetString(string name, string? defaultValue) =>
        System.Environment.GetEnvironmentVariable(name) ?? defaultValue ?? string.Empty;

    private static int GetInt(string name, int defaultValue)
    {
        var val = System.Environment.GetEnvironmentVariable(name);
        return val != null && int.TryParse(val, out var result) ? result : defaultValue;
    }

    private static string GetProcessName()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        }
        catch
        {
            return "dotnet";
        }
    }
}
