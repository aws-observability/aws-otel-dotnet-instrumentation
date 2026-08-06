// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Config;

/// <summary>
/// Configuration for ServiceEvents instrumentation.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Python SDK's <c>ServiceEventsConfig</c> dataclass field-for-field
/// (<c>aws-opentelemetry-distro/.../telemend/config.py</c>). All defaults
/// match the env-vars spec defaults.
/// </para>
/// <para>
/// Construction:
/// <list type="bullet">
/// <item><description><see cref="FromEnvironment" /> reads every <c>OTEL_AWS_SERVICE_EVENTS_*</c> env var with appropriate parsers.</description></item>
/// <item><description>Use <c>with</c> expressions for test overrides.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed record ServiceEventsConfig
{
    /// <summary>
    /// Gets a value indicating whether master kill switch. Defaults to false; the bundling-with-Application-Signals
    /// rule (see <see cref="DetermineEnabled" />) is authoritative for whether
    /// ServiceEvents actually runs.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether Application Signals is enabled. Used to suppress signals that
    /// App Signals already covers (e.g. EndpointSummary). Populated from
    /// <c>OTEL_AWS_APPLICATION_SIGNALS_ENABLED</c>.
    /// </summary>
    public bool ApplicationSignalsEnabled { get; init; } = false;

    /// <summary>
    /// Gets local-testing file exporter path. When set, replaces the OTLP network
    /// exporters (<see cref="LogsEndpoint" /> / <see cref="MetricsEndpoint" />
    /// are ignored). Output is CloudWatch-faithful NDJSON.
    /// </summary>
    public string OutputFile { get; init; } = string.Empty;

    /// <summary>Gets service name, from <c>OTEL_SERVICE_NAME</c> or <c>OTEL_RESOURCE_ATTRIBUTES[service.name]</c>.</summary>
    public string ServiceName { get; init; } = "UnknownService";

    /// <summary>Gets deployment environment, from <c>OTEL_RESOURCE_ATTRIBUTES[deployment.environment(.name)]</c> or <c>ENVIRONMENT</c>. Empty when unset — omitted from signals, no sentinel (spec v2.5).</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>Gets serviceEvents SDK version. Override via <c>OTEL_AWS_SERVICE_EVENTS_SDK_VERSION</c>.</summary>
    public string SdkVersion { get; init; } = "0.1.0";

    /// <summary>Gets functionCall flush cadence in milliseconds.</summary>
    public int FunctionCallFlushInterval { get; init; } = 30_000;

    /// <summary>Gets endpointSummary flush cadence in milliseconds.</summary>
    public int EndpointFlushInterval { get; init; } = 30_000;

    /// <summary>Gets incidentSnapshot flush cadence in milliseconds.</summary>
    public int IncidentSnapshotFlushInterval { get; init; } = 10_000;

    /// <summary>Gets maximum incident snapshots within a rate-limit window.</summary>
    public int IncidentSnapshotMaxPerPeriod { get; init; } = 100;

    /// <summary>Gets rate-limit window length in minutes.</summary>
    public int IncidentSnapshotPeriodMinutes { get; init; } = 1;

    /// <summary>Gets default duration threshold (ms) for latency-triggered snapshots.</summary>
    public int IncidentSnapshotDurationThresholdMs { get; init; } = 5_000;

    /// <summary>Gets per-error dedup ceiling for incident snapshots.</summary>
    public int IncidentSnapshotMaxSameError { get; init; } = 2;

    /// <summary>
    /// Gets per-endpoint latency thresholds in <c>METHOD /route:threshold_ms</c> form,
    /// e.g. <c>"POST /api/checkout:500,GET /api/health:50"</c>. Patterns supported
    /// via <c>fnmatch</c>-style globs.
    /// </summary>
    public IReadOnlyList<string> LatencyThresholds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets endpoint include patterns. If non-empty, only matching endpoints are tracked.
    /// </summary>
    public IReadOnlyList<string> EndpointIncludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Gets endpoint exclude patterns. Removes matched endpoints from tracking.</summary>
    public IReadOnlyList<string> EndpointExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets user opt-in allowlist for FunctionCall instrumentation, from
    /// <c>OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE</c>. Empty disables FunctionCall
    /// entirely (no implicit default scope). Bare <c>*</c> entries are rejected.
    /// In v1 these patterns are matched against the derived <c>function.name</c>
    /// (<c>{Source.Name}.{OperationName}</c>), which is framework-derived — see
    /// <see cref="ShouldInstrumentFunction" />.
    /// </summary>
    public IReadOnlyList<string> PackagesToInstrument { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets user opt-out patterns, from <c>OTEL_AWS_SERVICE_EVENTS_PACKAGES_EXCLUDE</c>.
    /// Always wins over <see cref="PackagesToInstrument" />. Matched against the
    /// derived <c>function.name</c> in v1.
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Gets functionCall sampling mode: <c>"always"</c> (default), <c>"auto"</c>, or <c>"never"</c>. (<c>adaptive</c> removed in spec v2.5.)</summary>
    public string SamplingMode { get; init; } = "always";

    /// <summary>Gets tier-1 cutoff: 100% sampling below this call count.</summary>
    public int SampleTier1Threshold { get; init; } = 100;

    /// <summary>Gets tier-2 cutoff for tiered sampling.</summary>
    public int SampleTier2Threshold { get; init; } = 1_000;

    /// <summary>Gets tier-2 sampling rate (1-in-N).</summary>
    public int SampleTier2Rate { get; init; } = 10;

    /// <summary>Gets tier-3 sampling rate (1-in-N).</summary>
    public int SampleTier3Rate { get; init; } = 100;

    /// <summary>
    /// Gets the OTLP logs endpoint, from the shared ADOT var <c>OTEL_AWS_OTLP_LOGS_ENDPOINT</c>
    /// (no <c>SERVICE_EVENTS</c> infix — identical across Java/Python/JS per spec v2.5 §0/§9).
    /// Empty here means "unset"; the default is applied at init time (4316 when bundled with
    /// App Signals; required when force-enabled without App Signals).
    /// </summary>
    public string LogsEndpoint { get; init; } = string.Empty;

    /// <summary>Gets the OTLP metrics endpoint, from the shared <c>OTEL_AWS_OTLP_METRICS_ENDPOINT</c>. Same defaulting rule as <see cref="LogsEndpoint" />.</summary>
    public string MetricsEndpoint { get; init; } = string.Empty;

    /// <summary>Gets cloudWatch log group header (<c>x-aws-log-group</c>).</summary>
    public string LogGroup { get; init; } = "/serviceevents/telemetry";

    /// <summary>Gets cloudWatch log stream header. Empty falls back to <see cref="ServiceName" />.</summary>
    public string LogStream { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether per-function instrumentation is enabled. Defaults to <c>true</c> per spec
    /// v2.5 — FunctionCall still requires a non-empty <see cref="PackagesToInstrument" />
    /// allowlist, so in practice only <c>PACKAGES_INCLUDE</c> must be set to turn it on.
    /// </summary>
    public bool FunctionInstrumentEnabled { get; init; } = true;

    /// <summary>
    /// Gets .NET-specific: root namespace filter for FunctionCall instrumentation.
    /// Mirrors <c>OTEL_AWS_SERVICE_EVENTS_JAVA_SERVICE_CODE_NAMESPACE</c>.
    /// </summary>
    public string DotnetServiceCodeNamespace { get; init; } = string.Empty;

    /// <summary>Gets resource attributes from OTel detectors (cloud/host/container/k8s).</summary>
    public ResourceAttributes ResourceAttributes { get; init; } = new();

    /// <summary>
    /// Build a <see cref="ServiceEventsConfig" /> from environment variables, applying
    /// the defaults from this class for missing values.
    /// </summary>
    /// <param name="resourceAttributes">Optional resource attributes from OTel detectors.</param>
    /// <returns>A populated config.</returns>
    public static ServiceEventsConfig FromEnvironment(ResourceAttributes? resourceAttributes = null)
    {
        var defaults = new ServiceEventsConfig();

        return new ServiceEventsConfig
        {
            Enabled = GetBool("OTEL_AWS_SERVICE_EVENTS_ENABLED", defaults.Enabled),
            ApplicationSignalsEnabled = GetBool("OTEL_AWS_APPLICATION_SIGNALS_ENABLED", defaults.ApplicationSignalsEnabled),
            OutputFile = GetString("OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE", defaults.OutputFile),
            ServiceName = GetServiceName(defaults.ServiceName),
            Environment = GetEnvironment(defaults.Environment),
            SdkVersion = GetString("OTEL_AWS_SERVICE_EVENTS_SDK_VERSION", defaults.SdkVersion),

            FunctionCallFlushInterval = GetInt("OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL", defaults.FunctionCallFlushInterval),
            EndpointFlushInterval = GetInt("OTEL_AWS_SERVICE_EVENTS_ENDPOINT_FLUSH_INTERVAL", defaults.EndpointFlushInterval),
            IncidentSnapshotFlushInterval = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_FLUSH_INTERVAL", defaults.IncidentSnapshotFlushInterval),

            IncidentSnapshotMaxPerPeriod = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_PER_PERIOD", defaults.IncidentSnapshotMaxPerPeriod),
            IncidentSnapshotPeriodMinutes = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_PERIOD_MINUTES", defaults.IncidentSnapshotPeriodMinutes),
            IncidentSnapshotDurationThresholdMs = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_DURATION_THRESHOLD_MS", defaults.IncidentSnapshotDurationThresholdMs),
            IncidentSnapshotMaxSameError = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_SAME_ERROR", defaults.IncidentSnapshotMaxSameError),
            LatencyThresholds = GetList("OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS", defaults.LatencyThresholds),

            EndpointIncludePatterns = GetList("OTEL_AWS_SERVICE_EVENTS_ENDPOINT_INCLUDE_PATTERNS", defaults.EndpointIncludePatterns),
            EndpointExcludePatterns = GetList("OTEL_AWS_SERVICE_EVENTS_ENDPOINT_EXCLUDE_PATTERNS", defaults.EndpointExcludePatterns),

            PackagesToInstrument = GetPatternList("OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE", defaults.PackagesToInstrument),
            ExcludePatterns = GetPatternList("OTEL_AWS_SERVICE_EVENTS_PACKAGES_EXCLUDE", defaults.ExcludePatterns),

            SamplingMode = GetString("OTEL_AWS_SERVICE_EVENTS_SAMPLING_MODE", defaults.SamplingMode),
            SampleTier1Threshold = GetInt("OTEL_AWS_SERVICE_EVENTS_SAMPLE_TIER1_THRESHOLD", defaults.SampleTier1Threshold),
            SampleTier2Threshold = GetInt("OTEL_AWS_SERVICE_EVENTS_SAMPLE_TIER2_THRESHOLD", defaults.SampleTier2Threshold),
            SampleTier2Rate = GetInt("OTEL_AWS_SERVICE_EVENTS_SAMPLE_TIER2_RATE", defaults.SampleTier2Rate),
            SampleTier3Rate = GetInt("OTEL_AWS_SERVICE_EVENTS_SAMPLE_TIER3_RATE", defaults.SampleTier3Rate),

            LogsEndpoint = GetString("OTEL_AWS_OTLP_LOGS_ENDPOINT", defaults.LogsEndpoint),
            MetricsEndpoint = GetString("OTEL_AWS_OTLP_METRICS_ENDPOINT", defaults.MetricsEndpoint),
            LogGroup = GetString("OTEL_AWS_SERVICE_EVENTS_LOG_GROUP", defaults.LogGroup),
            LogStream = GetString("OTEL_AWS_SERVICE_EVENTS_LOG_STREAM", defaults.LogStream),

            FunctionInstrumentEnabled = GetBool("OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED", defaults.FunctionInstrumentEnabled),

            DotnetServiceCodeNamespace = GetString("OTEL_AWS_SERVICE_EVENTS_DOTNET_SERVICE_CODE_NAMESPACE", defaults.DotnetServiceCodeNamespace),

            ResourceAttributes = resourceAttributes ?? new ResourceAttributes(),
        };
    }

    /// <summary>
    /// Decide whether ServiceEvents should run, applying the spec §3.11 bundling
    /// rule:
    /// <list type="bullet">
    /// <item><description>Lambda is always disabled (detected via <c>AWS_LAMBDA_FUNCTION_NAME</c>).</description></item>
    /// <item><description>Explicit <c>OTEL_AWS_SERVICE_EVENTS_ENABLED=true</c> wins.</description></item>
    /// <item><description>Explicit <c>OTEL_AWS_SERVICE_EVENTS_ENABLED=false</c> wins.</description></item>
    /// <item><description>Unset → follow <c>OTEL_AWS_APPLICATION_SIGNALS_ENABLED</c>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="config">The config to evaluate.</param>
    /// <returns><c>true</c> if ServiceEvents should run, otherwise <c>false</c>.</returns>
    public static bool DetermineEnabled(ServiceEventsConfig config)
    {
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME")))
        {
            return false;
        }

        var explicitFlag = System.Environment.GetEnvironmentVariable("OTEL_AWS_SERVICE_EVENTS_ENABLED");
        if (string.Equals(explicitFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(explicitFlag, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return config.ApplicationSignalsEnabled;
    }

    /// <summary>
    /// Parse <see cref="LatencyThresholds" /> into a list of
    /// <c>(pattern, threshold_ms)</c> tuples for first-match-wins lookup.
    /// </summary>
    /// <returns>Ordered list. Order matters — first match wins.</returns>
    public IReadOnlyList<(string Pattern, double ThresholdMs)> GetLatencyThresholdPatterns()
    {
        var result = new List<(string, double)>();
        foreach (var rawEntry in this.LatencyThresholds)
        {
            var entry = rawEntry.Trim();
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var lastColon = entry.LastIndexOf(':');
            if (lastColon <= 0 || lastColon == entry.Length - 1)
            {
                continue;
            }

            var apiPart = entry.Substring(0, lastColon).Trim();
            var thresholdPart = entry.Substring(lastColon + 1).Trim();

            if (!double.TryParse(thresholdPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var thresholdMs))
            {
                continue;
            }

            var spaceIdx = apiPart.IndexOf(' ');
            if (spaceIdx <= 0)
            {
                continue;
            }

            var method = apiPart.Substring(0, spaceIdx).Trim().ToUpperInvariant();
            var route = apiPart.Substring(spaceIdx + 1).Trim();
            var pattern = $"{method} {route}";
            result.Add((pattern, thresholdMs));
        }

        return result;
    }

    /// <summary>
    /// Resolve the latency threshold (ms) for an operation: first glob-pattern match
    /// from <see cref="GetLatencyThresholdPatterns" /> wins (configured via
    /// <c>OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS</c>); otherwise the default
    /// <see cref="IncidentSnapshotDurationThresholdMs" />.
    /// </summary>
    /// <param name="operation">Operation string, e.g. <c>"GET /users/{id}"</c>.</param>
    /// <returns>The threshold in milliseconds.</returns>
    public double GetLatencyThresholdMs(string operation)
    {
        foreach (var (pattern, thresholdMs) in this.GetLatencyThresholdPatterns())
        {
            if (GlobMatches(pattern, operation))
            {
                return thresholdMs;
            }
        }

        return this.IncidentSnapshotDurationThresholdMs;
    }

    /// <summary>
    /// Apply the endpoint include/exclude pattern filter to a route.
    /// </summary>
    /// <param name="route">Route pattern, e.g. <c>"/api/users"</c>.</param>
    /// <param name="method">HTTP method, e.g. <c>"GET"</c>.</param>
    /// <returns><c>true</c> if the endpoint should be tracked.</returns>
    public bool ShouldTrackEndpoint(string route, string method)
    {
        var endpointStr = $"{method.ToUpperInvariant()} {route}";

        if (this.EndpointIncludePatterns.Count > 0)
        {
            var included = false;
            foreach (var pattern in this.EndpointIncludePatterns)
            {
                if (GlobMatches(pattern, endpointStr))
                {
                    included = true;
                    break;
                }
            }

            if (!included)
            {
                return false;
            }
        }

        foreach (var pattern in this.EndpointExcludePatterns)
        {
            if (GlobMatches(pattern, endpointStr))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decide whether a FunctionCall should be recorded for the given derived
    /// function name (<c>{Source.Name}.{OperationName}</c>).
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ShouldTrackEndpoint" /> precedence: the allowlist
    /// (<see cref="PackagesToInstrument" />) is a hard gate — when it is empty,
    /// nothing is instrumented (the spec's "no implicit default scope" rule).
    /// When non-empty, the name must match an allowlist glob and must not match
    /// any <see cref="ExcludePatterns" /> glob (exclude always wins).
    /// </remarks>
    /// <param name="functionName">The derived function name to test.</param>
    /// <returns><c>true</c> when the function should be recorded.</returns>
    public bool ShouldInstrumentFunction(string functionName)
    {
        if (this.PackagesToInstrument.Count == 0)
        {
            return false;
        }

        var included = false;
        foreach (var pattern in this.PackagesToInstrument)
        {
            if (GlobMatches(pattern, functionName))
            {
                included = true;
                break;
            }
        }

        if (!included)
        {
            return false;
        }

        foreach (var pattern in this.ExcludePatterns)
        {
            if (GlobMatches(pattern, functionName))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetString(string envVar, string defaultValue) =>
        System.Environment.GetEnvironmentVariable(envVar) ?? defaultValue;

    private static bool GetBool(string envVar, bool defaultValue)
    {
        var raw = System.Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }

        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetInt(string envVar, int defaultValue)
    {
        var raw = System.Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static IReadOnlyList<string> GetList(string envVar, IReadOnlyList<string> defaultValue)
    {
        var raw = System.Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }

        var parts = raw.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>
    /// Parse a package-pattern list, rejecting bare <c>*</c> sentinels (matches
    /// Python's behavior — bare <c>*</c> is ambiguous between "match all" and
    /// "default scope", so we strip it and require an explicit empty list to
    /// signal default scope).
    /// </summary>
    private static IReadOnlyList<string> GetPatternList(string envVar, IReadOnlyList<string> defaultValue)
    {
        var raw = GetList(envVar, defaultValue);
        if (raw.Count == 0)
        {
            return raw;
        }

        var normalized = new List<string>(raw.Count);
        foreach (var item in raw)
        {
            if (item == "*")
            {
                continue;
            }

            normalized.Add(item);
        }

        return normalized;
    }

    private static string GetServiceName(string defaultValue)
    {
        var explicitName = System.Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        if (!string.IsNullOrEmpty(explicitName))
        {
            return explicitName;
        }

        var fromAttrs = ParseResourceAttribute("service.name");
        return string.IsNullOrEmpty(fromAttrs) ? defaultValue : fromAttrs!;
    }

    private static string GetEnvironment(string defaultValue)
    {
        // Prefer OTEL_RESOURCE_ATTRIBUTES[deployment.environment.name], then
        // OTEL_RESOURCE_ATTRIBUTES[deployment.environment], then ENVIRONMENT,
        // then default. Matches Python behaviour.
        var newKey = ParseResourceAttribute("deployment.environment.name");
        if (!string.IsNullOrEmpty(newKey))
        {
            return newKey!;
        }

        var legacyKey = ParseResourceAttribute("deployment.environment");
        if (!string.IsNullOrEmpty(legacyKey))
        {
            return legacyKey!;
        }

        var legacyEnvVar = System.Environment.GetEnvironmentVariable("ENVIRONMENT");
        return string.IsNullOrEmpty(legacyEnvVar) ? defaultValue : legacyEnvVar!;
    }

    private static string? ParseResourceAttribute(string key)
    {
        var raw = System.Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        foreach (var pair in raw.Split(','))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var k = pair.Substring(0, idx).Trim();
            var v = pair.Substring(idx + 1).Trim();
            if (string.Equals(k, key, StringComparison.Ordinal))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>
    /// fnmatch-style glob match for endpoint patterns.
    /// </summary>
    private static bool GlobMatches(string pattern, string input)
    {
        // Translate fnmatch glob to regex: * → .*, ? → ., escape regex metas.
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern);
    }
}
