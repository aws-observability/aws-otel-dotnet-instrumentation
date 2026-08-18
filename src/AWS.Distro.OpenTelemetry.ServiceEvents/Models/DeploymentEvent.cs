// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// In-memory snapshot of a DeploymentEvent record. No body — all data
/// rides on attributes.
/// </summary>
/// <param name="Trigger">Why this event fired: <c>"startup"</c>, <c>"periodic"</c>, or <c>"shutdown"</c>.</param>
/// <param name="GitCommitSha">Git commit SHA. Null when unset.</param>
/// <param name="GitRepoUrl">Git repository URL. Null when unset.</param>
/// <param name="DeploymentId">CI/CD deployment identifier. Null when unset.</param>
/// <param name="DeploymentUrl">Deployment / build URL. Null when unset.</param>
/// <param name="DeploymentTimestamp">ISO-8601 timestamp. Null when unset.</param>
public sealed record DeploymentEvent(
    string Trigger,
    string? GitCommitSha,
    string? GitRepoUrl,
    string? DeploymentId,
    string? DeploymentUrl,
    string? DeploymentTimestamp);
