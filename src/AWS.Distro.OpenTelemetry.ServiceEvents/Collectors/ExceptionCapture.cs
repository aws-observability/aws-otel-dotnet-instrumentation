// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Per-request capture of the exception that failed a request, held privately on the request's
/// <see cref="Activity" /> for IncidentSnapshot to read.
/// </summary>
/// <remarks>
/// <para>
/// This exists so ServiceEvents can report an exception's message and stack trace <b>without</b>
/// putting them on the customer's own span. The obvious route — enabling
/// <c>AspNetCoreTraceInstrumentationOptions.RecordException</c> — makes OTel attach an
/// <c>exception</c> event carrying <c>exception.message</c> and <c>exception.stacktrace</c> to the
/// customer's server span, which their trace pipeline then exports. Messages and stacks routinely
/// contain connection strings, tokens and user identifiers, and because ServiceEvents is enabled by
/// default alongside Application Signals, switching it on would silently change what a customer's
/// spans contain on upgrade. Self-telemetry must not do that.
/// </para>
/// <para>
/// Instead the exception is stashed via <c>EnrichWithException</c>, which hands us the live
/// <see cref="Exception" /> without mutating the span, and is read back only by ServiceEvents'
/// own collectors. Same destination as Java and Python — a private incident channel — reached by the
/// route .NET actually offers.
/// </para>
/// <para>
/// Scoped to the Activity via <see cref="Activity.SetCustomProperty" />, so it is naturally
/// per-request and garbage-collected with the Activity: no shared dictionary and nothing to clean up.
/// </para>
/// <para>
/// The captured text is <b>not</b> redacted or length-capped, matching Java and Python, which store
/// the full message and formatted trace. Unbounded content is a deliberate parity choice rather than
/// an oversight. A redaction policy for this text is a separate, still-open deliverable; the length
/// bound that <b>is</b> applied lives at <c>IncidentSnapshotCollector.BuildExceptionInfo</c>, the
/// single point both exception sources converge on.
/// </para>
/// </remarks>
internal static class ExceptionCapture
{
    internal const string PropertyKey = "aws.service_events.exception_capture";

    /// <summary>
    /// Record the exception that failed this request. Call from <c>EnrichWithException</c>.
    /// </summary>
    /// <param name="activity">The request's activity.</param>
    /// <param name="exception">The exception being reported.</param>
    public static void Stash(Activity activity, Exception exception)
    {
        if (activity is null || exception is null)
        {
            return;
        }

        // ToString() rather than the StackTrace property: it carries the type, the message and the
        // inner-exception chain in the same shape activity.RecordException would have produced, which
        // is the format IncidentSnapshotCollector.ParseStackTrace already parses (including the
        // "--->" inner-exception markers).
        activity.SetCustomProperty(
            PropertyKey,
            new CapturedException(
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                exception.ToString()));
    }

    /// <summary>
    /// Read the stashed exception, if this request failed with one.
    /// </summary>
    /// <param name="activity">The request's activity.</param>
    /// <returns>The captured type, message and stack trace; all null when nothing was stashed.</returns>
    public static (string? Type, string? Message, string? StackTrace) TryRead(Activity activity)
    {
        if (activity.GetCustomProperty(PropertyKey) is CapturedException captured)
        {
            return (captured.Type, captured.Message, captured.StackTrace);
        }

        return (null, null, null);
    }

    /// <summary>The captured detail, kept immutable so the reader cannot disturb it.</summary>
    /// <param name="Type">Fully-qualified exception type name.</param>
    /// <param name="Message">Exception message.</param>
    /// <param name="StackTrace">Formatted trace, including any inner-exception chain.</param>
    private sealed record CapturedException(string Type, string Message, string StackTrace);
}
