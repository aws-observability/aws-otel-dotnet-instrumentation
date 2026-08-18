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
    /// <remarks>
    /// <para>
    /// The whole body is wrapped for the same reason <c>EndpointActivityProcessor.OnEnd</c> is, and
    /// more urgently: this runs from <c>ActivityListener.ActivityStopped</c> on <b>every</b>
    /// non-server span, not just the request's own server span, so it sits on the busiest path
    /// ServiceEvents touches. Anything thrown here surfaces in the customer's code at whatever point
    /// they stopped an activity.
    /// </para>
    /// <para>
    /// Bare <c>catch</c> with no logging, matching <c>CollectorBase.RunCollectSafely</c> and
    /// <c>EndpointActivityProcessor.OnEnd</c>: the only logger factory reachable from this assembly
    /// is the one that emits ServiceEvents' own signals, so logging here would inject our internal
    /// errors into the customer's telemetry. The cost is that a systematic failure is silent —
    /// function metrics would simply stop.
    /// </para>
    /// </remarks>
    public override void OnEnd(Activity activity)
    {
        try
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

            // Capture this frame for the IncidentSnapshot call_path (latency incidents).
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
        catch
        {
            // Telemetry must never crash the host. Drop and continue.
        }
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
    /// server span, formatted as <c>"METHOD /route"</c>. Null when none is found (e.g. a background
    /// call with no request ancestor).
    /// </summary>
    /// <remarks>
    /// Resolved through <see cref="HttpOperationResolver" /> rather than locally, so this key is
    /// byte-identical to the one the endpoint summary uses. The two must match for the FunctionCall
    /// metric's <c>operation</c> attribute to be joinable with the endpoint signal for the same
    /// request, and separate copies had already drifted on the unmatched-route path.
    /// </remarks>
    private static string? ResolveOperation(Activity activity)
    {
        for (var a = activity; a is not null; a = a.Parent)
        {
            if (a.Kind != ActivityKind.Server)
            {
                continue;
            }

            var operation = HttpOperationResolver.ResolveOperation(a);
            if (operation is not null)
            {
                return operation;
            }
        }

        return null;
    }
}
