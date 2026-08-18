// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Feeds completed HTTP server spans into the <see cref="EndpointMetricCollector" />.
/// Registered on the customer's <c>TracerProvider</c> via the plugin's
/// <c>AfterConfigureTracerProvider</c> hook, so ServiceEvents observes the same
/// <see cref="Activity" /> instances the upstream ASP.NET Core instrumentation
/// already produces — no separate instrumentation of our own.
/// </summary>
/// <remarks>
/// <para>
/// Reads OTel HTTP semantic-convention tags (v1.21+, confirmed against the AWS
/// distro's <c>AwsSpanProcessingUtil</c>): <c>http.request.method</c>,
/// <c>http.response.status_code</c>, and the route via <c>http.route</c> →
/// <c>url.path</c> → <see cref="Activity.DisplayName" /> fallback chain.
/// </para>
/// <para>
/// Only <see cref="ActivityKind.Server" /> spans with an HTTP method are
/// considered (incoming requests). Endpoint include/exclude filters from config
/// are applied here so filtered endpoints never reach the collector.
/// </para>
/// </remarks>
internal sealed class EndpointActivityProcessor : BaseProcessor<Activity>
{
    private readonly IEndpointRecorder recorder;
    private readonly ServiceEventsConfig config;
    private readonly IIncidentTrigger? incidentTrigger;

    public EndpointActivityProcessor(IEndpointRecorder recorder, ServiceEventsConfig config, IIncidentTrigger? incidentTrigger = null)
    {
        this.recorder = recorder;
        this.config = config;
        this.incidentTrigger = incidentTrigger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Wrapped whole, for the reason given on <see cref="OnEnd" />: this runs inside
    /// <c>Activity.Start()</c> on the customer's request path.
    /// </remarks>
    public override void OnStart(Activity activity)
    {
        try
        {
            // Create the per-request call_path frame buffer on the server span so instrumented
            // child spans can append to it (used by latency incidents; see CallPathCapture).
            //
            // Gated on there being an incident trigger, because it is the only thing that ever drains the
            // buffer. Without one this allocated a queue and a custom property on every single server
            // span and then threw them away — per-request hot-path cost for data nobody read.
            if (this.incidentTrigger is not null && activity.Kind == ActivityKind.Server)
            {
                CallPathCapture.Begin(activity);
            }
        }
        catch
        {
            // Telemetry must never crash the host. Drop and continue.
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The whole body is wrapped because there is no framework boundary between this method and the
    /// customer's request. The call chain is
    /// <c>ActivityListener.ActivityStopped</c> ← <c>Activity.Stop()</c> ←
    /// ASP.NET Core's <c>HostingApplicationDiagnostics.StopActivity</c>, so anything thrown here
    /// surfaces in the customer's request rather than being absorbed on the way out.
    /// </para>
    /// <para>
    /// Deliberately a bare <c>catch</c> with no logging, matching <c>CollectorBase</c>'s
    /// <c>RunCollectSafely</c>. The only logger factory reachable from this assembly is the one
    /// that emits ServiceEvents' own signals, so logging a failure here would inject our internal
    /// errors into the customer's telemetry stream. The cost is that a systematic failure is
    /// silent — endpoint metrics would simply stop.
    /// </para>
    /// </remarks>
    public override void OnEnd(Activity activity)
    {
        try
        {
            if (activity.Kind != ActivityKind.Server)
            {
                return;
            }

            if (activity.GetTagItem("http.request.method") is not string method || string.IsNullOrEmpty(method))
            {
                // Not an HTTP server span — nothing to record.
                return;
            }

            var route = ResolveRoute(activity);

            if (!this.config.ShouldTrackEndpoint(route, method))
            {
                return;
            }

            var statusCode = ReadStatusCode(activity);

            // Activity.Duration is a TimeSpan; 1 tick = 100 ns.
            var durationNs = activity.Duration.Ticks * 100L;

            var (errorType, functionName) = ReadError(activity, statusCode);

            this.recorder.RecordRequest(route, method, statusCode, durationNs, errorType, functionName);

            if (this.incidentTrigger is not null)
            {
                this.FeedIncidentTrigger(activity, route, method, statusCode, durationNs);
            }
        }
        catch
        {
            // Telemetry must never crash the host. Drop and continue.
        }
    }

    /// <summary>
    /// Read the exception type/message/stack trace for a failed request. Unlike
    /// <see cref="ReadError" />, this returns the real exception type (no <c>HTTP{status}</c>
    /// fallback) and null when no exception information is available at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources, in order of richness:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The span's <c>exception</c> event, which carries type, message and stack. It exists only when
    /// something enabled <c>RecordException</c> — the customer's own configuration, or another
    /// instrumentation. ServiceEvents deliberately does not enable it (see
    /// <c>Plugin.ConfigureTracesOptions</c>): that event lands on the customer's exported spans, and
    /// messages and stacks carry secrets and PII. When a customer has opted into it themselves we
    /// read it, because then the data is already in their telemetry by their own choice.
    /// </description></item>
    /// <item><description>
    /// The <c>error.type</c> tag, which the ASP.NET Core instrumentation sets on the error path
    /// independently of <c>RecordException</c>. Type only, which is all
    /// <c>EndpointErrorMetrics</c>' <c>exception</c> dimension needs, and a type name carries no
    /// payload data. This is the source in the default configuration.
    /// </description></item>
    /// </list>
    /// <para>
    /// Nothing here reads a message or stack from <c>error.type</c> because it has none; incident
    /// snapshots that need them will have to capture the exception through a private channel rather
    /// than by mutating the customer's span.
    /// </para>
    /// </remarks>
    private static (string? Type, string? Message, string? StackTrace) ReadExceptionDetails(Activity activity)
    {
        foreach (var evt in activity.Events)
        {
            if (!string.Equals(evt.Name, "exception", StringComparison.Ordinal))
            {
                continue;
            }

            string? type = null;
            string? message = null;
            string? stack = null;
            foreach (var tag in evt.Tags)
            {
                switch (tag.Key)
                {
                    case "exception.type" when tag.Value is string t:
                        type = t;
                        break;
                    case "exception.message" when tag.Value is string m:
                        message = m;
                        break;
                    case "exception.stacktrace" when tag.Value is string s:
                        stack = s;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(type))
            {
                return (type, message, stack);
            }
        }

        // No exception event, so fall back to error.type. The instrumentation sets it to
        // exc.GetType().FullName — the same string the exception event's exception.type carries — so
        // the dimension value is identical either way.
        if (activity.GetTagItem("error.type") is string errorType && IsExceptionTypeName(errorType))
        {
            return (errorType, null, null);
        }

        return (null, null, null);
    }

    /// <summary>
    /// Whether an <c>error.type</c> value is an exception type name rather than a status code.
    /// </summary>
    /// <remarks>
    /// The semantic conventions allow <c>error.type</c> to hold a protocol-level error code when no
    /// exception was involved, so on a hand-returned 500 it can be the literal <c>"500"</c>. Feeding
    /// that through as an exception type would produce <c>exception="500"</c>, which is neither the
    /// real type nor the <c>HTTP{status}</c> shape callers expect — so digit-only values are rejected
    /// and left to <see cref="ReadError" />'s fallback.
    /// </remarks>
    /// <param name="errorType">The raw <c>error.type</c> tag value.</param>
    /// <returns><c>true</c> when the value looks like an exception type name.</returns>
    private static bool IsExceptionTypeName(string errorType)
    {
        if (string.IsNullOrEmpty(errorType))
        {
            return false;
        }

        foreach (var c in errorType)
        {
            if (!char.IsDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolve the route template: <c>http.route</c> → first segment of <c>url.path</c> → DisplayName.</summary>
    private static string ResolveRoute(Activity activity)
    {
        if (activity.GetTagItem("http.route") is string route && !string.IsNullOrEmpty(route))
        {
            return route;
        }

        // No route matched (404 / scanner traffic): collapse to the first path segment to reduce
        // metric cardinality — e.g. "/wp-admin/setup.php" → "/wp-admin".
        //
        // This reduces cardinality but does not bound it: traffic spread across many distinct first
        // segments still yields one aggregation per segment per flush window. The window resets on
        // every flush so nothing accumulates, and no SDK caps this today, so the behaviour is
        // deliberately consistent across SDKs rather than capped here unilaterally.
        if (activity.GetTagItem("url.path") is string path && !string.IsNullOrEmpty(path))
        {
            return FirstPathSegment(path);
        }

        return activity.DisplayName;
    }

    /// <summary>Return the first path segment with a leading slash (e.g. <c>"/wp-admin/x" → "/wp-admin"</c>, <c>"/" → "/"</c>).</summary>
    private static string FirstPathSegment(string path)
    {
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var first = slash >= 0 ? trimmed.Substring(0, slash) : trimmed;
        return "/" + first;
    }

    /// <summary>Read the HTTP status code tag, tolerating int/long/string encodings.</summary>
    private static int ReadStatusCode(Activity activity)
    {
        var raw = activity.GetTagItem("http.response.status_code");
        return raw switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
            _ => 0,
        };
    }

    /// <summary>
    /// Extract the exception type from the span when it errored, for the
    /// <c>EndpointErrorMetrics</c> <c>exception</c> dimension. Function name is not available at the
    /// HTTP-span level, so it is reported as <c>"unknown"</c>.
    /// </summary>
    /// <remarks>
    /// Two sources, richest first: the <c>exception</c> event's <c>exception.type</c>, which exists
    /// only when something enabled <c>RecordException</c>, then the <c>error.type</c> tag, which the
    /// ASP.NET Core instrumentation sets on the error path regardless. ServiceEvents deliberately does
    /// not enable <c>RecordException</c> — it would attach exception messages and stack traces to the
    /// customer's exported spans (see <c>Plugin.ConfigureTracesOptions</c>) — so in the default
    /// configuration <c>error.type</c> is the source. Both carry <c>GetType().FullName</c>, so the
    /// dimension value is the same either way.
    /// </remarks>
    private static (string? ErrorType, string? FunctionName) ReadError(Activity activity, int statusCode)
    {
        var isError = statusCode >= 400 || activity.Status == ActivityStatusCode.Error;
        if (!isError)
        {
            return (null, null);
        }

        foreach (var evt in activity.Events)
        {
            if (!string.Equals(evt.Name, "exception", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var tag in evt.Tags)
            {
                if (string.Equals(tag.Key, "exception.type", StringComparison.Ordinal) &&
                    tag.Value is string exceptionType && !string.IsNullOrEmpty(exceptionType))
                {
                    return (exceptionType, "unknown");
                }
            }
        }

        if (activity.GetTagItem("error.type") is string errorType && IsExceptionTypeName(errorType))
        {
            return (errorType, "unknown");
        }

        // No exception type from either source. We emit NO synthetic exception
        // (no "HTTP{code}" / "UnknownError"): a 5xx that returned a status without raising
        // increments request.faults but produces no breakdown entry or count data point.
        return (null, null);
    }

    /// <summary>
    /// Feed the completed span into the IncidentSnapshot trigger and, if it produces a
    /// snapshot, link the exemplar back onto the endpoint window.
    /// </summary>
    private void FeedIncidentTrigger(Activity activity, string route, string method, int statusCode, long durationNs)
    {
        var (exceptionType, exceptionMessage, stackTrace) = ReadExceptionDetails(activity);
        var durationMs = durationNs / 1_000_000.0;
        var requestTimestampMs = new DateTimeOffset(activity.StartTimeUtc).ToUnixTimeMilliseconds();

        // Only surface trace context when the span was actually sampled — i.e. the W3C
        // "sampled" flag is set (exposed in .NET as Activity.Recorded). An unsampled span
        // is never exported to the trace backend, so emitting its trace/span id would be a
        // dead link in the console. AlwaysRecordSampler records every span for metrics but
        // only sets Recorded on sampled spans, so Recorded is the correct "is a sample
        // available" signal here.
        var sampled = activity.Recorded;
        var traceId = sampled && activity.TraceId != default ? activity.TraceId.ToHexString() : null;
        var spanId = sampled && activity.SpanId != default ? activity.SpanId.ToHexString() : null;

        // Append the endpoint (entry) frame last — outermost frame, ordered after the
        // inner instrumented frames — then drain the per-request call_path buffer. Used by latency
        // incidents; exception incidents derive their call_path from the stack trace instead.
        CallPathCapture.Append(
            activity,
            new Models.CallPathEntry($"{method} {route}", null, durationNs, statusCode >= 500, false));
        var spanFrames = CallPathCapture.Drain(activity);

        var result = this.incidentTrigger!.ProcessPotentialIncident(
            route,
            method,
            statusCode,
            durationMs,
            exceptionType,
            exceptionMessage,
            stackTrace,
            traceId,
            spanId,
            requestTimestampMs,
            spanFrames);

        if (result is not null)
        {
            this.recorder.RecordIncidentExemplar(
                result.Operation,
                result.SnapshotId,
                result.TriggerType,
                result.Severity,
                result.Timestamp);
        }
    }
}
