// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Records a FunctionCall (the <c>service.function.duration</c> ExponentialHistogram)
/// for each completed non-server <see cref="Activity" /> that passes the
/// package allowlist and the sampler. Registered on the customer's
/// <c>TracerProvider</c> via the plugin's <c>AfterConfigureTracerProvider</c> hook, so
/// ServiceEvents observes the same Activities the upstream auto-instrumentation already
/// produces — there is no user-method wrapping in v1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActivityKind.Server" /> spans are skipped: the root server span is the
/// endpoint itself, already captured by EndpointSummary/Metrics (M3) — recording it here
/// would double-count. FunctionCall therefore covers downstream calls (HttpClient, AWS
/// SDK, internal spans) made while handling a request.
/// </para>
/// <para>
/// The derived <c>function.name</c> (<c>{Source.Name}.{OperationName}</c>, de-duplicated
/// when the operation name already starts with the source name) is matched against the
/// allowlist and recorded as a datapoint attribute; the OTel SDK owns aggregation + export
/// via the histogram instrument — there is no LogRecord path.
/// </para>
/// </remarks>
internal sealed class FunctionCallProcessor : BaseProcessor<Activity>
{
    private readonly IFunctionCallRecorder recorder;
    private readonly ServiceEventsConfig config;
    private readonly FunctionCallSampler sampler;

    public FunctionCallProcessor(IFunctionCallRecorder recorder, ServiceEventsConfig config, FunctionCallSampler sampler)
    {
        this.recorder = recorder;
        this.config = config;
        this.sampler = sampler;
    }

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        // Skip the endpoint's own server span (already covered by M3) — decision #6.
        if (activity.Kind == ActivityKind.Server)
        {
            return;
        }

        var functionName = BuildName(activity.Source.Name, activity.OperationName);

        if (!this.config.ShouldInstrumentFunction(functionName))
        {
            return;
        }

        if (!this.sampler.ShouldSample(functionName))
        {
            return;
        }

        var status = activity.Status == ActivityStatusCode.Error ? "error" : "success";
        var caller = ResolveCaller(activity);
        var operation = ResolveOperation(activity);

        // Activity.Duration is a TimeSpan; 1 tick = 100 ns ⇒ microseconds = ticks / 10.
        var durationMicros = activity.Duration.Ticks / 10.0;

        this.recorder.RecordFunctionCall(durationMicros, functionName, status, caller, operation);

        // Capture this frame for the IncidentSnapshot call_path (Option A, latency incidents).
        // Appends to the buffer on the request's server span; no-op if the request isn't tracked.
        CallPathCapture.Append(
            activity,
            new Models.CallPathEntry(
                FunctionName: functionName,
                CallerFunctionName: caller,
                DurationNs: activity.Duration.Ticks * 100L,
                Error: activity.Status == ActivityStatusCode.Error,
                IsAsync: false));
    }

    /// <summary>
    /// Build the composite name <c>{sourceName}.{operationName}</c>, de-duplicated: when the
    /// operation name already starts with the source name (e.g. the HttpClient instrumentation's
    /// <c>System.Net.Http</c> source + <c>System.Net.Http.HttpRequestOut</c> operation), the
    /// source prefix is not repeated.
    /// </summary>
    private static string BuildName(string sourceName, string operationName) =>
        string.IsNullOrEmpty(sourceName) || operationName.StartsWith(sourceName, StringComparison.Ordinal)
            ? operationName
            : $"{sourceName}.{operationName}";

    /// <summary>Caller = the parent Activity's composite name; null when there is no parent.</summary>
    private static string? ResolveCaller(Activity activity)
    {
        var parent = activity.Parent;
        return parent is null ? null : BuildName(parent.Source.Name, parent.OperationName);
    }

    /// <summary>
    /// Find the owning endpoint operation by walking the parent chain to the nearest HTTP
    /// server span, formatted as <c>"METHOD /route"</c> (matching EndpointMetricCollector's
    /// operation key). Null when none is found (e.g. a background call with no request ancestor).
    /// </summary>
    private static string? ResolveOperation(Activity activity)
    {
        for (var a = activity; a is not null; a = a.Parent)
        {
            if (a.Kind != ActivityKind.Server)
            {
                continue;
            }

            if (a.GetTagItem("http.request.method") is string method && !string.IsNullOrEmpty(method))
            {
                return $"{method.ToUpperInvariant()} {ResolveRoute(a)}";
            }
        }

        return null;
    }

    /// <summary>Resolve the route template: <c>http.route</c> → <c>url.path</c> → DisplayName.</summary>
    /// <remarks>
    /// The HTTP attribute names here are deliberately literals, unlike the resource attribute keys
    /// in <c>ResourceAttributes</c>, which come from
    /// <c>OpenTelemetry.Resources.ResourceSemanticConventions</c>. The semconv package this repo
    /// pins (<c>OpenTelemetry.SemanticConventions</c> 1.0.0-rc9.9, the only version ever published)
    /// predates the HTTP convention stabilisation: it defines <c>http.method</c>,
    /// <c>http.target</c> and <c>http.status_code</c>, and has no constant at all for
    /// <c>http.request.method</c>, <c>url.path</c>, <c>http.response.status_code</c> or
    /// <c>error.type</c>. Only <c>http.route</c> is shared. Sourcing these from the package would
    /// therefore switch us to the pre-stabilisation names and stop matching what the ASP.NET Core
    /// instrumentation actually emits, so they stay written out here.
    /// </remarks>
    private static string ResolveRoute(Activity activity)
    {
        if (activity.GetTagItem("http.route") is string route && !string.IsNullOrEmpty(route))
        {
            return route;
        }

        if (activity.GetTagItem("url.path") is string path && !string.IsNullOrEmpty(path))
        {
            return path;
        }

        return activity.DisplayName;
    }
}
