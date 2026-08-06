// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// Emits ServiceEvents signals as OTel log records (via the public
/// <see cref="ILogger"/> bridge) and OTel metric data points.
/// </summary>
/// <remarks>
/// <para>
/// This is the wire-format contract — the spec attribute and body
/// mappings live here. Each <c>Emit*</c> method maps an in-memory model
/// from <c>Models/</c> into the exact attribute / body shape defined in
/// <see href="../../../SERVICE_EVENTS_OTLP_SIGNALS_SPEC.md">SERVICE_EVENTS_OTLP_SIGNALS_SPEC.md</see>,
/// cross-referenced in
/// <see href="../../../docs/design-docs/TELEMEND_DOTNET_PHASE1_DESIGN.md">Phase 1 design doc §6</see>.
/// </para>
/// <para>
/// One injected logger and one meter:
/// <list type="bullet">
/// <item><description><b>General logger</b> — EndpointSummary, IncidentSnapshot, DeploymentEvent.</description></item>
/// <item><description><b>Meter</b> — EndpointErrorMetrics counter.</description></item>
/// </list>
/// </para>
/// <para>
/// On the OTel API choice: OTel .NET 1.15.0 keeps the direct
/// <c>Logger.EmitLog()</c> API marked internal as a stability gate.
/// The recommended public path is <see cref="ILogger"/>, which the
/// OpenTelemetry SDK bridges into <c>LogRecord</c>s. Attribute keys
/// flow via the structured-log <c>state</c> payload using the
/// <c>{OriginalFormat}</c> sentinel that <see cref="LoggerExtensions" />
/// uses internally.
/// </para>
/// </remarks>
internal sealed class ServiceEventsOtlpEmitter : IFunctionCallRecorder
{
    internal const string InstrumentationScopeName = "serviceevents";
    internal const string InstrumentationScopeVersion = "1.0";

    internal const string EndpointSummaryEventName = "aws.service_events.endpoint_summary";
    internal const string IncidentSnapshotEventName = "aws.service_events.incident_snapshot";
    internal const string DeploymentEventEventName = "aws.service_events.deployment_event";

    private readonly ILogger generalLogger;
    private readonly Counter<long> errorCounter;
    private readonly Histogram<double> functionDuration;
    private readonly string deploymentId;
    private readonly string gitCommitSha;
    private readonly string gitRepoUrl;

    public ServiceEventsOtlpEmitter(
        ILogger generalLogger,
        Meter meter,
        string deploymentId,
        string gitCommitSha,
        string gitRepoUrl)
    {
        this.generalLogger = generalLogger;
        this.deploymentId = deploymentId ?? string.Empty;
        this.gitCommitSha = gitCommitSha ?? string.Empty;
        this.gitRepoUrl = gitRepoUrl ?? string.Empty;
        this.errorCounter = meter.CreateCounter<long>(name: "count", unit: "Count");
        this.functionDuration = meter.CreateHistogram<double>(
            name: "service.function.duration",
            unit: "Microseconds",
            description: "Function call duration");
    }

    /// <summary>Emit an <see cref="EndpointMetricEvent"/> as an OTLP log record.</summary>
    public void EmitEndpointSummary(EndpointMetricEvent evt)
    {
        var attrs = new List<KeyValuePair<string, object?>>
        {
            new("event.name", EndpointSummaryEventName),
            new("http.request.method", evt.Method),
            new("url.route", evt.Route),
            new("aws.service_events.operation", evt.Operation),
            new("aws.service_events.request.count", evt.Count),
            new("aws.service_events.request.faults", evt.Faults),
            new("aws.service_events.request.errors", evt.Errors),
            new("aws.service_events.incident.count", evt.IncidentCount),
        };
        this.AppendVcsAndDeploymentAttributes(attrs);

        var body = new Dictionary<string, object?>
        {
            ["duration"] = DurationToWireDictionary(evt.Duration),
            ["exception_breakdown"] = evt.ExceptionBreakdown.Select(BreakdownToWireDictionary).ToArray(),
            ["incidents_exemplar"] = evt.IncidentsExemplar.Select(ExemplarToWireDictionary).ToArray(),
        };

        EmitLog(this.generalLogger, attrs, body);
    }

    /// <summary>Emit a <see cref="DeploymentEvent"/> as an OTLP log record (no body).</summary>
    public void EmitDeploymentEvent(DeploymentEvent evt)
    {
        var attrs = new List<KeyValuePair<string, object?>>
        {
            new("event.name", DeploymentEventEventName),
            new("aws.service_events.deployment.trigger", evt.Trigger),
        };
        if (!string.IsNullOrEmpty(evt.GitCommitSha))
        {
            attrs.Add(new("vcs.ref.head.revision", evt.GitCommitSha!));
        }

        if (!string.IsNullOrEmpty(evt.GitRepoUrl))
        {
            attrs.Add(new("vcs.repository.url.full", evt.GitRepoUrl!));
        }

        if (!string.IsNullOrEmpty(evt.DeploymentId))
        {
            attrs.Add(new("aws.service_events.deployment.id", evt.DeploymentId!));
        }

        if (!string.IsNullOrEmpty(evt.DeploymentUrl))
        {
            attrs.Add(new("aws.service_events.deployment.url", evt.DeploymentUrl!));
        }

        if (!string.IsNullOrEmpty(evt.DeploymentTimestamp))
        {
            attrs.Add(new("aws.service_events.deployment.timestamp", evt.DeploymentTimestamp!));
        }

        // DeploymentEvent has no body per spec §6.
        EmitLog(this.generalLogger, attrs, body: null);
    }

    /// <summary>Emit one or more <see cref="EndpointErrorMetric"/> data points.</summary>
    public void EmitEndpointErrorMetrics(IEnumerable<EndpointErrorMetric> metrics)
    {
        foreach (var metric in metrics)
        {
            if (metric.Count <= 0)
            {
                continue;
            }

            var tags = new TagList
            {
                { "Telemetry.Source", "ServiceEvents" },
                { "service_name", metric.ServiceName },
            };

            // deployment.environment is omitted from the data point when unset — no sentinel (spec v2.5 §7).
            if (!string.IsNullOrEmpty(metric.Environment))
            {
                tags.Add("environment", metric.Environment);
            }

            tags.Add("operation", metric.Operation);
            tags.Add("exception", metric.Exception);

            this.errorCounter.Add(metric.Count, tags);
        }
    }

    /// <summary>
    /// Record one FunctionCall data point on the <c>service.function.duration</c>
    /// ExponentialHistogram (spec §4). Service-level context rides on the OTel
    /// Resource, so only the per-call dimensions are attached here.
    /// </summary>
    /// <param name="durationMicros">Call duration in microseconds.</param>
    /// <param name="functionName">Derived function name (<c>{Source.Name}.{OperationName}</c>).</param>
    /// <param name="status"><c>"success"</c> or <c>"error"</c>.</param>
    /// <param name="caller">Optional caller function name; omitted when null/empty.</param>
    /// <param name="operation">Optional owning endpoint operation; omitted when null/empty.</param>
    public void RecordFunctionCall(double durationMicros, string functionName, string status, string? caller, string? operation)
    {
        var tags = new TagList
        {
            { "Telemetry.Source", "ServiceEvents" },
            { "function.name", functionName },
            { "status", status },
        };

        if (!string.IsNullOrEmpty(operation))
        {
            tags.Add("operation", operation);
        }

        if (!string.IsNullOrEmpty(caller))
        {
            tags.Add("aws.service_events.caller", caller);
        }

        this.functionDuration.Record(durationMicros, tags);
    }

    private static IDictionary<string, object?> DurationToWireDictionary(DurationMetrics duration) =>
        new Dictionary<string, object?>
        {
            // CamelCase keys per spec §3 — NOT snake_case.
            ["Values"] = duration.Values.ToArray(),
            ["Counts"] = duration.Counts.ToArray(),
            ["Max"] = duration.Max,
            ["Min"] = duration.Min,
            ["Count"] = duration.Count,
            ["Sum"] = duration.Sum,
        };

    private static IDictionary<string, object?> BreakdownToWireDictionary(ErrorBreakdownEntry entry) =>
        new Dictionary<string, object?>
        {
            ["failure_type"] = entry.FailureType,
            ["count"] = entry.Count,
            ["exceptions"] = entry.Exceptions.Select(e => (object?)new Dictionary<string, object?>
            {
                ["exception_type"] = e.ExceptionType,
                ["function_name"] = e.FunctionName,
            }).ToArray(),
        };

    private static IDictionary<string, object?> ExemplarToWireDictionary(IncidentExemplar e) =>
        new Dictionary<string, object?>
        {
            ["snapshot_id"] = e.SnapshotId,
            ["trigger_type"] = e.TriggerType,
            ["timestamp"] = e.Timestamp,
        };

    /// <summary>
    /// Emit a structured log record through the public ILogger bridge. The
    /// OpenTelemetry SDK's logger provider observes the call and produces
    /// an OTLP <c>LogRecord</c> with the supplied attributes; the bridge
    /// also pulls trace context from <see cref="Activity.Current"/> when
    /// present.
    /// </summary>
    /// <param name="logger">Target logger (general or profile pipeline).</param>
    /// <param name="attributes">Flat attribute key/value pairs.</param>
    /// <param name="body">Nested body dict; serialized to JSON since OTLP body strings are the most portable shape across SDK versions.</param>
    /// <param name="activity">Optional activity carrying trace context. When null, <see cref="Activity.Current"/> is used.</param>
    private static void EmitLog(
        ILogger logger,
        List<KeyValuePair<string, object?>> attributes,
        IDictionary<string, object?>? body,
        Activity? activity = null)
    {
        if (body is not null)
        {
            attributes.Add(new("body", System.Text.Json.JsonSerializer.Serialize(body)));
        }

        // Use a synthetic event id so log filters can target ServiceEvents events
        // distinctly. The structured-log "state" is the attribute list itself.
        var eventName = attributes
            .FirstOrDefault(kv => string.Equals(kv.Key, "event.name", StringComparison.Ordinal))
            .Value as string ?? "service_events.event";

        var prevActivity = Activity.Current;
        if (activity is not null)
        {
            Activity.Current = activity;
        }

        try
        {
            logger.Log(
                logLevel: LogLevel.Information,
                eventId: new EventId(0, eventName),
                state: new ServiceEventsLogState(attributes),
                exception: null,
                formatter: ServiceEventsLogState.Formatter);
        }
        finally
        {
            if (activity is not null)
            {
                Activity.Current = prevActivity;
            }
        }
    }

    private void AppendVcsAndDeploymentAttributes(List<KeyValuePair<string, object?>> attrs)
    {
        if (!string.IsNullOrEmpty(this.gitCommitSha))
        {
            attrs.Add(new("vcs.ref.head.revision", this.gitCommitSha));
        }

        if (!string.IsNullOrEmpty(this.gitRepoUrl))
        {
            attrs.Add(new("vcs.repository.url.full", this.gitRepoUrl));
        }

        if (!string.IsNullOrEmpty(this.deploymentId))
        {
            attrs.Add(new("aws.service_events.deployment.id", this.deploymentId));
        }
    }

    /// <summary>
    /// Structured-log state object the OpenTelemetry logger bridge unwraps
    /// into <c>LogRecord.Attributes</c>. Mirrors the
    /// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> shape
    /// the bridge pattern-matches against.
    /// </summary>
    private sealed class ServiceEventsLogState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public static readonly Func<ServiceEventsLogState, Exception?, string> Formatter = (state, _) =>
            state.attributes.FirstOrDefault(kv => kv.Key == "event.name").Value as string ?? "service_events";

        private readonly List<KeyValuePair<string, object?>> attributes;

        public ServiceEventsLogState(List<KeyValuePair<string, object?>> attributes)
        {
            this.attributes = attributes;
        }

        public int Count => this.attributes.Count;

        public KeyValuePair<string, object?> this[int index] => this.attributes[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => this.attributes.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
