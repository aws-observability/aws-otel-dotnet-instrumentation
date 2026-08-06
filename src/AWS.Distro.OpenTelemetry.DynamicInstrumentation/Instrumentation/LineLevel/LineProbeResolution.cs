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
    /// <summary>Gets a value indicating whether the resolution succeeded.</summary>
    public bool IsResolved => this.Status == LineProbeResolutionStatus.Resolved && this.Location != null;

    /// <summary>Creates a successful resolution.</summary>
    /// <param name="location">The resolved location.</param>
    /// <returns>A resolved outcome.</returns>
    public static LineProbeResolution Success(LineProbeLocation location) =>
        new(LineProbeResolutionStatus.Resolved, location);

    /// <summary>Creates a failed resolution.</summary>
    /// <param name="status">The failure status.</param>
    /// <param name="detail">Optional detail for logs.</param>
    /// <returns>A failed outcome.</returns>
    public static LineProbeResolution Fail(LineProbeResolutionStatus status, string? detail = null) =>
        new(status, null, detail);
}
