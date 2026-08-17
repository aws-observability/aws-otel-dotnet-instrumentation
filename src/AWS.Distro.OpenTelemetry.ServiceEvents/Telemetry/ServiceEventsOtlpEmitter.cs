// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AWS.Distro.OpenTelemetry.ServiceEvents.Models;
using Microsoft.Extensions.Logging;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// Emits ServiceEvents signals as OTel log records (via the public
/// <see cref="ILogger"/> bridge) and OTel metric data points.
/// </summary>
/// <remarks>
/// <para>
/// This is the wire-format contract. Each <c>Emit*</c> method maps an in-memory
/// model from <c>Models/</c> into the exact attribute and body shape ServiceEvents
/// puts on the wire, so a change in here is a change to the published signal format.
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

    /// <summary>
    /// Serialization for the structured body. Carries a converter that keeps integral doubles
    /// recognisably float once the body has been through JSON, so they reach the wire as
    /// <c>double_value</c> rather than <c>int_value</c>.
    /// </summary>
    internal static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Converters = { new PreserveFloatDoubleConverter() },
    };

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

    /// <summary>Emit an <see cref="IncidentSnapshot"/> as an OTLP log record (with trace context).</summary>
    /// <param name="snapshot">The snapshot to emit.</param>
    public void EmitIncidentSnapshot(IncidentSnapshot snapshot)
    {
        var attrs = new List<KeyValuePair<string, object?>>
        {
            new("event.name", IncidentSnapshotEventName),
            new("aws.service_events.snapshot_id", snapshot.SnapshotId),
            new("aws.service_events.trigger_type", snapshot.TriggerType),
            new("aws.service_events.operation", snapshot.Operation),
            new("aws.service_events.duration_ms", snapshot.DurationMs),
            new("aws.service_events.is_partial", snapshot.IsPartial),
            new("http.request.method", snapshot.Method),
            new("url.route", snapshot.Route),
            new("http.response.status_code", snapshot.StatusCode),
            new("aws.service_events.request.type", "http"),
        };
        this.AppendVcsAndDeploymentAttributes(attrs);

        var body = new Dictionary<string, object?>();
        if (snapshot.ExceptionInfo.Count > 0)
        {
            body["exception_info"] = snapshot.ExceptionInfo.Select(ExceptionInfoToWireDictionary).ToArray();
        }

        if (snapshot.RequestContext is not null)
        {
            body["request_context"] = RequestContextToWireDictionary(snapshot.RequestContext);
        }

        // Trace context: parse hex and stash on a synthetic Activity so
        // the OpenTelemetry log bridge picks it up automatically. Falls
        // back to no trace context when parsing fails or fields are unset.
        Activity? activity = null;
        if (TryBuildTraceActivity(snapshot.TraceId, snapshot.SpanId, out var built))
        {
            activity = built;
        }

        try
        {
            EmitLog(this.generalLogger, attrs, body.Count > 0 ? body : null, activity);
        }
        finally
        {
            activity?.Stop();
            activity?.Dispose();
        }
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

        // DeploymentEvent has no body.
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

            // deployment.environment is omitted from the data point when unset — no sentinel.
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
    /// ExponentialHistogram. Service-level context rides on the OTel
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
            // CamelCase keys here — NOT snake_case, unlike the rest of the payload.
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
            attributes.Add(new("body", System.Text.Json.JsonSerializer.Serialize(body, BodyJsonOptions)));
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

    private static IDictionary<string, object?> ExceptionInfoToWireDictionary(ExceptionInfo info) =>
        new Dictionary<string, object?>
        {
            ["exception_type"] = info.ExceptionType,
            ["exception_message"] = info.ExceptionMessage,
            ["stack_trace"] = info.StackTrace,
            ["call_path"] = info.CallPath.Select(CallPathEntryToWireDictionary).ToArray(),
        };

    private static IDictionary<string, object?> CallPathEntryToWireDictionary(CallPathEntry entry)
    {
        var d = new Dictionary<string, object?>
        {
            ["function_name"] = entry.FunctionName,
            ["caller_function_name"] = entry.CallerFunctionName,
            ["error"] = entry.Error,
        };

        if (entry.DurationNs > 0)
        {
            d["duration_ns"] = entry.DurationNs;
        }

        if (entry.IsAsync)
        {
            d["is_async"] = true;
        }

        return d;
    }

    private static IDictionary<string, object?> RequestContextToWireDictionary(RequestContext ctx)
    {
        // Payload fields (request_body, query_params, path_params, request_headers)
        // are no longer emitted per spec §5 — payload capture was removed. Only the
        // non-payload context fields remain.
        return new Dictionary<string, object?>
        {
            ["type"] = ctx.Type,
            ["timestamp"] = ctx.Timestamp,
            ["status_code"] = ctx.StatusCode,
        };
    }

    /// <summary>
    /// Build a synthetic <see cref="Activity"/> carrying the supplied trace
    /// + span IDs so the OpenTelemetry logger bridge can pull them onto the
    /// emitted log record. Returns null when the IDs are missing or invalid.
    /// </summary>
    private static bool TryBuildTraceActivity(string? traceIdHex, string? spanIdHex, out Activity? activity)
    {
        activity = null;
        if (string.IsNullOrEmpty(traceIdHex) || traceIdHex.Length != 32 ||
            string.IsNullOrEmpty(spanIdHex) || spanIdHex.Length != 16)
        {
            return false;
        }

        try
        {
            var traceId = ActivityTraceId.CreateFromString(traceIdHex);
            var spanId = ActivitySpanId.CreateFromString(spanIdHex);

            // Use a plain Activity rather than ActivitySource.CreateActivity — the latter
            // returns null unless a matching ActivityListener is registered (nothing listens
            // to the "serviceevents" source), which would silently drop trace context.
            // Setting the W3C parent makes the started activity adopt the trace id, which the
            // OpenTelemetry log bridge then copies onto the LogRecord.
            //
            // Limitation: the LogRecord's SpanId is this activity's own span (a child of the
            // original), since the .NET ILogger bridge can't stamp an arbitrary SpanId. The
            // trace id — the backend join key per spec §5 — is preserved exactly.
            var built = new Activity("service_events.snapshot");
            built.SetIdFormat(ActivityIdFormat.W3C);
            built.SetParentId(traceId, spanId, ActivityTraceFlags.Recorded);
            built.Start();
            activity = built;
            return true;
        }
        catch
        {
            return false;
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

    /// <summary>
    /// Writes <see cref="double" /> values so that whole numbers keep a decimal point: the double
    /// 2000.0 is written as <c>2000.0</c> rather than <c>2000</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body's float-typed fields — a duration's <c>Values</c>, <c>Max</c>, <c>Min</c> and
    /// <c>Sum</c> — are <c>double</c> in the model, but the default writer emits an integral double as
    /// a bare integer token. The body is packed into a JSON string and reparsed on the way to the wire
    /// (OTel .NET's LogRecord body is string-only), and by then <c>2000</c> is indistinguishable from
    /// an integer, so the field went out as OTLP <c>int_value</c> while Java and Python emit a double
    /// for the same field. A consumer switching on the AnyValue case sees a different type depending
    /// on which SDK produced the record.
    /// </para>
    /// <para>
    /// Keeping the decimal point preserves the distinction the CLR types already made, with no list of
    /// field names anywhere: fields declared <c>long</c> — a duration's <c>Counts</c> and
    /// <c>Count</c> — take the default integer path and stay integers.
    /// </para>
    /// </remarks>
    private sealed class PreserveFloatDoubleConverter : JsonConverter<double>
    {
        /// <inheritdoc />
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDouble();

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            // NaN and infinity are not valid JSON numbers. Defer to the default writer rather than
            // inventing a representation for them here.
            if (!double.IsFinite(value))
            {
                writer.WriteNumberValue(value);
                return;
            }

            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0)
            {
                text += ".0";
            }

            writer.WriteRawValue(text);
        }
    }
}
