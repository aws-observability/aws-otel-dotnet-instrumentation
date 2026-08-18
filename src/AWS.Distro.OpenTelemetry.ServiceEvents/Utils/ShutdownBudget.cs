// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Utils;

/// <summary>
/// A single wall-clock deadline shared by every wait performed while ServiceEvents shuts down.
/// </summary>
/// <remarks>
/// <para>
/// Teardown runs sequentially: each collector waits for an in-flight timer callback, the endpoint
/// collector waits for in-flight writes before its final drain, the deployment emitter waits for its
/// own callback, and only then do the OTLP providers flush over the network. Bounding each of those
/// waits individually is not enough — the bounds add up, and the process-exit window they all share
/// does not. Exceeding it is worse than waiting less, because the runtime then terminates the
/// process mid-flush and the telemetry the final drain existed to save is lost anyway.
/// </para>
/// <para>
/// So the waits share one deadline. Each asks for what it would ideally wait, and receives the
/// smaller of that and whatever remains, which keeps the total inside the window and stops an early
/// step from starving the network flush that actually ships the data.
/// </para>
/// <para>
/// Uses <see cref="Environment.TickCount64" /> rather than <c>DateTime</c>: it is monotonic, so a
/// clock adjustment mid-shutdown cannot produce a negative or absurd remaining time.
/// </para>
/// </remarks>
internal readonly struct ShutdownBudget
{
    /// <summary>
    /// Total time all ServiceEvents shutdown waits may consume between them. Deliberately well
    /// inside the runtime's process-exit allowance (roughly two seconds) so the exporter flushes
    /// that follow still have room to complete.
    /// </summary>
    internal static readonly TimeSpan Default = TimeSpan.FromMilliseconds(1000);

    private readonly long deadlineMs;

    private ShutdownBudget(long deadlineMs) => this.deadlineMs = deadlineMs;

    /// <summary>Gets the time left before the deadline, never negative.</summary>
    internal TimeSpan Remaining
    {
        get
        {
            var left = this.deadlineMs - Environment.TickCount64;
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
        }
    }

    /// <summary>Start a budget of <paramref name="total" /> from now.</summary>
    /// <param name="total">Total time all shutdown waits may consume.</param>
    /// <returns>A budget whose deadline is <paramref name="total" /> from now.</returns>
    internal static ShutdownBudget FromNow(TimeSpan total) =>
        new(Environment.TickCount64 + (long)total.TotalMilliseconds);

    /// <summary>
    /// The lesser of <paramref name="requested" /> and <see cref="Remaining" />. Returns
    /// <see cref="TimeSpan.Zero" /> once the deadline has passed, so a caller that has run out of
    /// budget stops waiting rather than blocking.
    /// </summary>
    /// <param name="requested">What the caller would wait if it had the whole window to itself.</param>
    /// <returns>How long the caller may actually wait.</returns>
    internal TimeSpan Clamp(TimeSpan requested)
    {
        var remaining = this.Remaining;
        return requested < remaining ? requested : remaining;
    }
}
