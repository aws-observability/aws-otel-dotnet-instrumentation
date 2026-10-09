// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Outcome of a line-probe resolution attempt: a location on success, or a status explaining why not.
/// </summary>
/// <param name="Status">The typed outcome.</param>
/// <param name="Location">The resolved location; non-null only when <paramref name="Status"/> is
/// <see cref="LineProbeResolutionStatus.Resolved"/>.</param>
/// <param name="Detail">Optional human-readable detail for logs (e.g. the nearest executable line).
/// Never sent to the backend as-is; the backend receives the mapped ErrorCause.</param>
internal sealed record LineProbeResolution(
    LineProbeResolutionStatus Status,
    LineProbeLocation? Location = null,
    string? Detail = null)
{
    /// <summary>
    /// Gets every probe applied for this configuration, one per captured local. Empty for a failure, and
    /// for a resolution that has not been applied yet.
    /// </summary>
    // Multi-local capture applies N probes at one offset, each with its OWN id — and the woven callback
    // carries nothing but that id, so the caller must register all of them to attribute a hit to a variable.
    // `Location` remains the FIRST applied probe so single-local callers and existing tests are unaffected.
    public IReadOnlyList<LineProbeProbeLocation> Locations { get; init; } =
        Array.Empty<LineProbeProbeLocation>();

    /// <summary>Gets a value indicating whether the resolution succeeded.</summary>
    public bool IsResolved => this.Status == LineProbeResolutionStatus.Resolved && this.Location != null;

    /// <summary>Creates a successful resolution.</summary>
    /// <param name="location">The resolved location.</param>
    /// <returns>A resolved outcome.</returns>
    public static LineProbeResolution Success(LineProbeLocation location) =>
        new(LineProbeResolutionStatus.Resolved, location);

    /// <summary>Creates a successful resolution carrying every applied probe.</summary>
    /// <param name="location">The first applied probe's location.</param>
    /// <param name="locations">All applied probes, in emission order.</param>
    /// <returns>A resolved outcome.</returns>
    public static LineProbeResolution Success(
        LineProbeLocation location, IReadOnlyList<LineProbeProbeLocation> locations) =>
        new(LineProbeResolutionStatus.Resolved, location) { Locations = locations };

    /// <summary>Creates a failed resolution.</summary>
    /// <param name="status">The failure status.</param>
    /// <param name="detail">Optional detail for logs.</param>
    /// <returns>A failed outcome.</returns>
    public static LineProbeResolution Fail(LineProbeResolutionStatus status, string? detail = null) =>
        new(status, null, detail);
}
