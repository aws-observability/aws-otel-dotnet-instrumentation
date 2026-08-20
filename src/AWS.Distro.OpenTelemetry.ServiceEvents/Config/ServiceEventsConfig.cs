// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Config;

/// <summary>
/// Configuration for ServiceEvents instrumentation.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the Python distro's
/// <see href="https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/serviceevents/config.py"><c>ServiceEventsConfig</c></see>
/// dataclass field-for-field.
/// </para>
/// <para>
/// Construction:
/// <list type="bullet">
/// <item><description><see cref="FromEnvironment" /> reads every <c>OTEL_AWS_SERVICE_EVENTS_*</c> env var with appropriate parsers.</description></item>
/// <item><description>Use <c>with</c> expressions for test overrides.</description></item>
/// </list>
/// </para>
/// <para>
/// Because the surface mirrors Python field-for-field, some properties are parsed here before the
/// component that reads them exists — the FunctionCall knobs (<c>PACKAGES_*</c>,
/// <c>FUNCTION_INSTRUMENT_ENABLED</c>, <c>SAMPLING_MODE</c>, <c>SAMPLE_TIER*</c>,
/// <c>FUNCTION_CALL_FLUSH_INTERVAL</c>) and the incident-snapshot cadence knob
/// (<c>INCIDENT_SNAPSHOT_FLUSH_INTERVAL</c>) land with their collectors in a follow-up change.
/// They are intentionally inert here rather than dead:
/// parsing is validated by unit tests so the contract is fixed before the consumers arrive. Config
/// backing a component that this assembly <i>does</i> ship must always be consumed — an unread
/// property there is a bug, not scaffolding.
/// </para>
/// </remarks>
public sealed record ServiceEventsConfig
{
    /// <summary>
    /// ServiceEvents schema version. Deliberately a constant rather than a configurable property:
    /// the other SDKs derive this from their package version and expose no override, and a value a
    /// customer can rewrite would let telemetry misreport which SDK produced it.
    /// <para>
    /// Version identity that actually reaches the wire does not come from here — it rides on the
    /// resource as <c>telemetry.sdk.version</c> (from the OTel SDK) and
    /// <c>telemetry.distro.version</c> (from <see cref="DistroVersion" />, supplied by the distro).
    /// </para>
    /// </summary>
    internal const string SdkVersion = "0.1.0";

    /// <summary>
    /// Upper bound on a single glob match. The patterns themselves are operator-supplied, but the
    /// string matched against them is request-derived, so a pathological pattern plus a long route
    /// is a real if unlikely way to stall a request thread. The Java distro has no equivalent
    /// because <c>java.util.regex.Pattern</c> cannot express a match timeout; .NET can, so this
    /// does. 100 ms is already far beyond what a per-request check should ever cost — it is a
    /// backstop, not a tuning knob.
    /// </summary>
    private static readonly TimeSpan GlobMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Glob pattern to compiled regex, one entry per distinct pattern for the life of the process.
    /// <para>
    /// This buys what the Java distro gets by pre-compiling into <c>List&lt;Pattern&gt;</c> in its
    /// <c>EndpointFilter</c> constructor. That exact shape is not available here: the pattern lists
    /// are <c>init</c> properties, assigned after the constructor body has already run, so there is
    /// nothing to compile at construction time. Keying a static cache on the pattern string gets
    /// the same compile-once behaviour, and unlike a precompiled instance field it survives
    /// <c>with</c> copies — the record copy constructor would carry a stale field across while the
    /// pattern property changed underneath it.
    /// </para>
    /// <para>
    /// Growth is bounded by the number of distinct configured patterns rather than by traffic: the
    /// regex is derived from the pattern, and only the string matched against it comes from the
    /// request.
    /// </para>
    /// <para>
    /// A <c>null</c> value marks a pattern that would not compile, so it is skipped from then on
    /// instead of being retried per request. The Java distro behaves the same way, rejecting bad
    /// globs when it builds the filter and carrying on with the remaining entries.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex?> GlobCache = new();

    /// <summary>
    /// Gets the explicit master kill switch from <c>OTEL_AWS_SERVICE_EVENTS_ENABLED</c>:
    /// <c>true</c> forces ServiceEvents on, <c>false</c> forces it off, and <c>null</c> means the
    /// variable was unset and the Application-Signals bundling rule decides. See
    /// <see cref="DetermineEnabled" />.
    /// </summary>
    /// <remarks>
    /// Nullable because the rule genuinely has three cases and a plain <c>bool</c> cannot express
    /// "unset". It was a <c>bool</c>, which is why <see cref="DetermineEnabled" /> re-read the
    /// environment itself and this property went unused — the value on the config object had no
    /// effect on whether ServiceEvents ran, so constructing a config in a test proved nothing about
    /// enablement. One source of truth now, and enablement is testable without mutating process
    /// state.
    /// </remarks>
    public bool? Enabled { get; init; }

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

    /// <summary>Gets deployment environment, from <c>OTEL_RESOURCE_ATTRIBUTES[deployment.environment(.name)]</c> or <c>ENVIRONMENT</c>. Empty when unset — omitted from signals, no sentinel.</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>
    /// Gets the AWS distro version stamped onto <c>telemetry.distro.version</c>. Supplied by the
    /// distro's plugin, which hosts ServiceEvents and owns the authoritative version string, so
    /// both the main telemetry resource and ServiceEvents' own resource report the same value.
    /// Empty when ServiceEvents is constructed outside the distro (e.g. unit tests), in which case
    /// the attribute is omitted rather than reporting a wrong version.
    /// </summary>
    public string DistroVersion { get; init; } = string.Empty;

    /// <summary>Gets functionCall flush cadence in milliseconds.</summary>
    public int FunctionCallFlushInterval { get; init; } = 30_000;

    /// <summary>Gets endpointSummary flush cadence in milliseconds.</summary>
    public int EndpointFlushInterval { get; init; } = 30_000;

    /// <summary>Gets incidentSnapshot flush cadence in milliseconds.</summary>
    public int IncidentSnapshotFlushInterval { get; init; } = 10_000;

    /// <summary>
    /// Gets the maximum number of incident snapshots per minute. The window is fixed at one minute
    /// and is deliberately not configurable, per the env-vars spec.
    /// </summary>
    public int IncidentSnapshotMaxPerMinute { get; init; } = 100;

    /// <summary>Gets default duration threshold (ms) for latency-triggered snapshots.</summary>
    public int IncidentSnapshotDurationThresholdMs { get; init; } = 5_000;

    /// <summary>
    /// Gets the per-error dedup ceiling for incident snapshots: at most this many snapshots per
    /// distinct error per minute.
    /// </summary>
    public int IncidentSnapshotMaxSameError { get; init; } = 1;

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

    /// <summary>Gets functionCall sampling mode: <c>"always"</c> (default), <c>"auto"</c>, or <c>"never"</c>. (<c>adaptive</c> is no longer accepted.)</summary>
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
    /// (no <c>SERVICE_EVENTS</c> infix — the name is identical across the Java, Python and JS distros).
    /// Empty here means "unset"; the default is applied at init time (4316 when bundled with
    /// App Signals; required when force-enabled without App Signals).
    /// </summary>
    public string LogsEndpoint { get; init; } = string.Empty;

    /// <summary>Gets the OTLP metrics endpoint, from the shared <c>OTEL_AWS_OTLP_METRICS_ENDPOINT</c>. Same defaulting rule as <see cref="LogsEndpoint" />.</summary>
    public string MetricsEndpoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the CloudWatch log group, sent as the <c>x-aws-log-group</c> header on every OTLP log
    /// request. Consumed by the collector / CloudWatch agent to route records to a log group, so it
    /// is sent regardless of whether the endpoint is a collector or CloudWatch directly — the same
    /// as Java and JS.
    /// </summary>
    public string LogGroup { get; init; } = "/serviceevents/telemetry";

    /// <summary>
    /// Gets the CloudWatch log stream, sent as the <c>x-aws-log-stream</c> header.
    /// <see cref="FromEnvironment" /> falls back to <see cref="ServiceName" /> when the env var is
    /// unset. The Java distro resolves its log stream to the same value and in the same place —
    /// while building config, not in the exporter — though it takes it from an internal default
    /// rather than a customer-facing env var. Directly constructed instances keep the empty
    /// default, in which case the header is omitted rather than sent empty.
    /// </summary>
    public string LogStream { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether per-function instrumentation is enabled. Defaults to <c>true</c> per spec
    /// v2.5 — FunctionCall still requires a non-empty <see cref="PackagesToInstrument" />
    /// allowlist, so in practice only <c>PACKAGES_INCLUDE</c> must be set to turn it on.
    /// </summary>
    public bool FunctionInstrumentEnabled { get; init; } = true;

    /// <summary>
    /// Gets the root namespace filter for FunctionCall instrumentation, read from
    /// <c>OTEL_AWS_SERVICE_EVENTS_DOTNET_SERVICE_CODE_NAMESPACE</c>. The env override is .NET-only:
    /// the Java distro carries the same concept as an internal field with no env var of its own.
    /// </summary>
    public string DotnetServiceCodeNamespace { get; init; } = string.Empty;

    /// <summary>
    /// Gets the VCS commit SHA for the running build, surfaced as deployment provenance on the
    /// DeploymentEvent and as a resource attribute. Empty when the deployment pipeline does not
    /// supply it.
    /// </summary>
    public string GitCommitSha { get; init; } = string.Empty;

    /// <summary>Gets the VCS repository URL for the running build. Empty when not supplied.</summary>
    public string GitRepoUrl { get; init; } = string.Empty;

    /// <summary>Gets the deployment identifier supplied by the pipeline. Empty when not supplied.</summary>
    public string DeploymentId { get; init; } = string.Empty;

    /// <summary>Gets a link to the deployment record. Empty when not supplied.</summary>
    public string DeploymentUrl { get; init; } = string.Empty;

    /// <summary>Gets the deployment timestamp supplied by the pipeline, passed through as given
    /// rather than parsed, since the backend accepts the pipeline's own format. Empty when not
    /// supplied.</summary>
    public string DeploymentTimestamp { get; init; } = string.Empty;

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

        // Resolved once because the log stream falls back to it — the same fallback the Java
        // distro applies while building its own config.
        var serviceName = GetServiceName(defaults.ServiceName);

        return new ServiceEventsConfig
        {
            Enabled = GetNullableBool("OTEL_AWS_SERVICE_EVENTS_ENABLED"),
            ApplicationSignalsEnabled = GetBool("OTEL_AWS_APPLICATION_SIGNALS_ENABLED", defaults.ApplicationSignalsEnabled),
            OutputFile = GetString("OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE", defaults.OutputFile),
            ServiceName = serviceName,
            Environment = GetEnvironment(defaults.Environment),

            FunctionCallFlushInterval = GetInt("OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL", defaults.FunctionCallFlushInterval),
            EndpointFlushInterval = GetInt("OTEL_AWS_SERVICE_EVENTS_ENDPOINT_FLUSH_INTERVAL", defaults.EndpointFlushInterval),
            IncidentSnapshotFlushInterval = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_FLUSH_INTERVAL", defaults.IncidentSnapshotFlushInterval),

            IncidentSnapshotMaxPerMinute = GetInt("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_PER_MINUTE", defaults.IncidentSnapshotMaxPerMinute),
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
            LogStream = GetString("OTEL_AWS_SERVICE_EVENTS_LOG_STREAM", serviceName),

            FunctionInstrumentEnabled = GetBool("OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED", defaults.FunctionInstrumentEnabled),

            DotnetServiceCodeNamespace = GetString("OTEL_AWS_SERVICE_EVENTS_DOTNET_SERVICE_CODE_NAMESPACE", defaults.DotnetServiceCodeNamespace),

            GitCommitSha = GetString("OTEL_AWS_SERVICE_EVENTS_GIT_COMMIT_SHA", defaults.GitCommitSha),
            GitRepoUrl = GetString("OTEL_AWS_SERVICE_EVENTS_GIT_REPO_URL", defaults.GitRepoUrl),
            DeploymentId = GetString("OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_ID", defaults.DeploymentId),
            DeploymentUrl = GetString("OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_URL", defaults.DeploymentUrl),
            DeploymentTimestamp = GetString("OTEL_AWS_SERVICE_EVENTS_DEPLOYMENT_TIMESTAMP", defaults.DeploymentTimestamp),

            ResourceAttributes = resourceAttributes ?? new ResourceAttributes(),
        };
    }

    /// <summary>
    /// Decide whether ServiceEvents should run, applying the bundling
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

        // Read from the config rather than the environment. This used to re-read
        // OTEL_AWS_SERVICE_EVENTS_ENABLED directly, which meant config.Enabled was dead and the
        // decision could not be exercised without mutating process-global state.
        if (config.Enabled is bool explicitFlag)
        {
            return explicitFlag;
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
    /// <remarks>
    /// Re-parses <see cref="LatencyThresholds" /> on each call. Deliberate: the expensive part was
    /// compiling a regex per pattern per call, and <see cref="GlobMatches" /> now caches those, so
    /// what remains is splitting a short configured string. Memoizing the parse on the instance would
    /// mean caching derived state on a record, where a <c>with</c> expression copies the cache while
    /// replacing the source list — a stale-cache trap worse than the work it saves. If this ever
    /// shows up in a profile, the fix belongs in the caller, which can parse once at construction.
    /// </remarks>
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
        var endpointStr = HttpOperationResolver.ResolveOperation(method, route);

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

    /// <summary>
    /// Read a tri-state boolean flag: <c>true</c>/<c>false</c> when explicitly set (case-insensitive,
    /// matching <see cref="GetBool" /> and the distro's own flag parsing), <c>null</c> when unset or
    /// unrecognised so the caller can distinguish "off" from "not specified".
    /// </summary>
    private static bool? GetNullableBool(string envVar)
    {
        var raw = System.Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An unrecognised value is not a decision. Treated as unset so the bundling rule applies,
        // rather than silently reading as false and disabling the feature on a typo.
        return null;
    }

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
    /// fnmatch-style glob match for endpoint patterns, against a regex compiled once per distinct
    /// pattern. See <see cref="GlobCache" /> for why the compilation is cached statically rather
    /// than pre-computed per config instance.
    /// </summary>
    /// <remarks>
    /// Called once per configured pattern per request from <see cref="ShouldTrackEndpoint" />,
    /// <see cref="GetLatencyThresholdMs" /> and the function-name filters, so it is on the
    /// <c>OnEnd</c> hot path. The previous implementation rebuilt the pattern string and used the
    /// static <c>Regex.IsMatch</c> overload, whose cache holds 15 entries behind a lock — past that
    /// every pattern was recompiled on every request, and the lock itself became contended.
    /// </remarks>
    private static bool GlobMatches(string pattern, string input)
    {
        var regex = GlobCache.GetOrAdd(pattern, static p =>
        {
            // Translate fnmatch glob to regex: * → .*, ? → ., escape regex metas.
            var regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";

            try
            {
                return new Regex(regexPattern, RegexOptions.None, GlobMatchTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });

        if (regex is null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // Treated as "did not match". For an exclude pattern that leaves the endpoint tracked;
            // for an include pattern it leaves the endpoint untracked. Neither outcome touches the
            // request, which is the property worth protecting here.
            return false;
        }
    }
}
