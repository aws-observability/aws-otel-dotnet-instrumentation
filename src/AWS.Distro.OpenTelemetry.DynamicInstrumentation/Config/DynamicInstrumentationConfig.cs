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
/// <param name="LogsEndpoint">Optional OTLP logs endpoint for emitting snapshots.</param>
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

    /// <summary>Builds a configuration from the process environment variables.</summary>
    /// <returns>The resolved configuration.</returns>
    public static DynamicInstrumentationConfig FromEnvironment()
    {
        var enabled = GetBool($"{Prefix}ENABLED", false);
        var apiUrl = GetString($"{Prefix}API_URL", "http://localhost:2000");
        var probePoll = Math.Max(MinPollIntervalSeconds, GetInt($"{Prefix}PROBE_POLL_INTERVAL", 600));
        var breakpointPoll = Math.Max(MinPollIntervalSeconds, GetInt($"{Prefix}BREAKPOINT_POLL_INTERVAL", 60));
        var logsEndpoint = GetString($"{Prefix}LOGS_ENDPOINT", null);
        var serviceName = ResolveServiceName();
        var environment = ResolveEnvironment();

        return new DynamicInstrumentationConfig(
            enabled, apiUrl, probePoll, breakpointPoll, logsEndpoint, serviceName, environment);
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
        return val != null ? val.Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;
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
