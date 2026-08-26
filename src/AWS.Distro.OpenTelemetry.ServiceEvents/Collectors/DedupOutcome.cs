// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;

/// <summary>
/// Result of the per-error deduplication check.
/// </summary>
/// <remarks>
/// A boolean would do for the control flow — the caller only proceeds on <see cref="Admitted" /> — but
/// the two rejections are worth telling apart when reporting a suppressed incident. Hitting the
/// per-error ceiling means one error is noisy and raising the ceiling would admit more of it. Hitting
/// the cardinality guard means the service is producing more <i>distinct</i> errors in one window than
/// the table tracks, so raising the per-error ceiling changes nothing. Collapsing both into "false"
/// makes those indistinguishable to an operator, which is what this type exists to prevent.
/// </remarks>
internal enum DedupOutcome
{
    /// <summary>The occurrence was recorded and the caller may emit.</summary>
    Admitted,

    /// <summary>This error hash has already reached its per-window ceiling.</summary>
    PerErrorLimit,

    /// <summary>The window's distinct-hash table is full, so this hash was never tracked.</summary>
    CardinalityGuard,
}
