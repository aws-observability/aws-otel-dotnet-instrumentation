// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Tracing;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Utils;

/// <summary>
/// Self-diagnostics channel for ServiceEvents. Reports the feature's own failures and dropped data
/// to whoever is listening, without touching the customer's application or telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Why an <see cref="EventSource" /> rather than a logger. ServiceEvents has no logger it can safely
/// write diagnostics to: the only <c>ILogger</c> it holds is the one that <i>emits its own signals</i>,
/// so logging a failure there would turn a dropped record into an emitted record — feeding the very
/// pipeline that just failed. Writing to the customer's logging pipeline instead is worse: enabling
/// a telemetry feature is not consent to have its internal diagnostics appear in your application
/// logs. An EventSource is off unless somebody attaches a listener, costs nothing
/// when nobody is, and is the mechanism the rest of this repository already uses — see
/// <c>CloudWatchPluginEventSource</c> and <c>AWSSamplerEventSource</c>.
/// </para>
/// <para>
/// Consumed with the standard .NET tooling: <c>dotnet-trace collect --providers
/// OpenTelemetry-AWS-ServiceEvents</c>, or any in-process <c>EventListener</c>.
/// </para>
/// <para>
/// <b>Event IDs are a permanent contract.</b> Once an ID ships, a listener may be filtering on it, so
/// IDs are never renumbered and never reused for a different meaning. New events take the next free
/// number; retired events leave their number retired. The full set is declared here rather than grown
/// piecemeal so the numbering stays coherent.
/// </para>
/// <para>
/// A malformed EventSource fails <i>silently</i> — the runtime disables it rather than throwing, so a
/// mistake here would look exactly like "nothing went wrong". <c>ServiceEventsEventSourceTests</c>
/// therefore round-trips every event through a real listener.
/// </para>
/// </remarks>
[EventSource(Name = EventSourceName)]
internal sealed class ServiceEventsEventSource : EventSource
{
    /// <summary>The singleton instance. Cheap when no listener is attached.</summary>
    public static readonly ServiceEventsEventSource Log = new();

    /// <summary>The provider name listeners subscribe to.</summary>
    internal const string EventSourceName = "OpenTelemetry-AWS-ServiceEvents";

    /// <summary>Report a failed export of ServiceEvents' own records.</summary>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void ExportFailed(string endpoint, Exception exception)
    {
        if (this.IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            this.ExportFailed(endpoint, exception.ToString());
        }
    }

    /// <summary>
    /// Report a failed export of ServiceEvents' own records.
    /// </summary>
    /// <remarks>
    /// <paramref name="detail" /> is a stringified exception or a rejection description — an export can
    /// fail without throwing, which is the case a misconfigured endpoint produces: the request
    /// completes and returns a non-success status.
    /// </remarks>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="detail">Stringified exception, or a description of the rejection.</param>
    [Event(
        1,
        Message = "ServiceEvents export to '{0}' failed; records for this batch are lost: {1}",
        Level = EventLevel.Error)]
    public void ExportFailed(string endpoint, string detail)
    {
        this.WriteEvent(1, endpoint, detail);
    }

    /// <summary>Report a failed write to the local output file.</summary>
    /// <param name="path">Target path.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void FileWriteFailed(string path, Exception exception)
    {
        if (this.IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            this.FileWriteFailed(path, exception.ToString());
        }
    }

    /// <summary>Report a failed write to the local output file.</summary>
    /// <param name="path">Target path.</param>
    /// <param name="exception">Stringified failure.</param>
    [Event(
        2,
        Message = "ServiceEvents write to '{0}' failed; records for this flush are lost: {1}",
        Level = EventLevel.Error)]
    public void FileWriteFailed(string path, string exception)
    {
        this.WriteEvent(2, path, exception);
    }

    /// <summary>Report a collector's flush cycle throwing.</summary>
    /// <param name="collector">Collector name.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void CollectFailed(string collector, Exception exception)
    {
        if (this.IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            this.CollectFailed(collector, exception.ToString());
        }
    }

    /// <summary>Report a collector's flush cycle throwing.</summary>
    /// <param name="collector">Collector name.</param>
    /// <param name="exception">Stringified failure.</param>
    [Event(
        3,
        Message = "ServiceEvents collector '{0}' failed during flush; this window is lost: {1}",
        Level = EventLevel.Error)]
    public void CollectFailed(string collector, string exception)
    {
        this.WriteEvent(3, collector, exception);
    }

    /// <summary>
    /// Report an incident that was suppressed rather than emitted.
    /// </summary>
    /// <remarks>
    /// Suppression is normal and expected — it is how volume stays bounded. This exists so an operator
    /// investigating "why do I see no incidents" can tell suppression apart from absence, which is
    /// otherwise indistinguishable. Verbose, because at high error rates this fires often.
    /// </remarks>
    /// <param name="reason">One of <see cref="DropReason" />.</param>
    /// <param name="operation">The operation the incident belonged to.</param>
    [Event(
        4,
        Message = "ServiceEvents suppressed an incident for '{1}' ({0}).",
        Level = EventLevel.Verbose)]
    public void IncidentDropped(string reason, string operation)
    {
        this.WriteEvent(4, reason, operation);
    }

    /// <summary>
    /// Report an export abandoned because the shutdown budget ran out.
    /// </summary>
    /// <remarks>
    /// Losing the final batch is the deliberate trade: overrunning the host's shutdown window risks
    /// the process being killed, which loses the data anyway and delays the customer's shutdown.
    /// </remarks>
    /// <param name="endpoint">Target endpoint.</param>
    /// <param name="budgetMs">Milliseconds that remained when the attempt was abandoned.</param>
    [Event(
        5,
        Message = "ServiceEvents abandoned an export to '{0}' with {1}ms of shutdown budget left; the final batch is lost.",
        Level = EventLevel.Warning)]
    public void ExportAbandonedOnShutdown(string endpoint, int budgetMs)
    {
        this.WriteEvent(5, endpoint, budgetMs);
    }

    /// <summary>Report the output file being rotated because it reached its size cap.</summary>
    /// <param name="path">The file that was rotated.</param>
    /// <param name="sizeBytes">Size at rotation.</param>
    [Event(
        6,
        Message = "ServiceEvents rotated output file '{0}' at {1} bytes.",
        Level = EventLevel.Informational)]
    public void OutputFileRotated(string path, long sizeBytes)
    {
        this.WriteEvent(6, path, sizeBytes);
    }

    /// <summary>Report a component swallowing a failure, having dropped the data it was handling.</summary>
    /// <param name="component">Component and phase, e.g. <c>"EndpointActivityProcessor.OnEnd"</c>.</param>
    /// <param name="exception">The failure.</param>
    [NonEvent]
    public void ComponentFailed(string component, Exception exception)
    {
        if (this.IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            this.ComponentFailed(component, exception.ToString());
        }
    }

    /// <summary>
    /// Report a component swallowing a failure, having dropped the data it was handling.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>CollectFailed</c>, which loses a whole flush window. This loses one
    /// request's or one emission's contribution. Fired from per-request paths, so it can be frequent
    /// when something is systematically broken — which is the situation it exists to make visible, and
    /// it costs nothing while no listener is attached.
    /// </remarks>
    /// <param name="component">Component and phase.</param>
    /// <param name="exception">Stringified failure.</param>
    [Event(
        7,
        Message = "ServiceEvents component '{0}' failed and dropped the data it was handling: {1}",
        Level = EventLevel.Error)]
    public void ComponentFailed(string component, string exception)
    {
        this.WriteEvent(7, component, exception);
    }

    /// <summary>Reasons an incident was not turned into a snapshot. Stable strings — listeners may match on them.</summary>
    internal static class DropReason
    {
        /// <summary>Another snapshot for this error hash was already produced in this flush cycle.</summary>
        internal const string BatchDuplicate = "batch_duplicate";

        /// <summary>This error hash has reached its per-window ceiling.</summary>
        internal const string PerErrorLimit = "per_error_limit";

        /// <summary>The global per-minute cap is exhausted.</summary>
        internal const string RateLimit = "rate_limit";

        /// <summary>The per-window distinct-hash table is full.</summary>
        internal const string CardinalityGuard = "cardinality_guard";
    }
}
