// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// One data point of the <c>EndpointErrorMetrics</c> Sum metric — one
/// instance per <c>(operation, exception)</c> pair flushed per window.
/// </summary>
/// <param name="ServiceName">Service name dimension.</param>
/// <param name="Environment">Deployment environment dimension.</param>
/// <param name="Operation">HTTP operation dimension, e.g. <c>"POST /api/users"</c>.</param>
/// <param name="Exception">Exception type dimension, e.g. <c>"RuntimeError"</c>.</param>
/// <param name="Count">Increment for the window.</param>
/// <remarks>
/// Wire-format mapping per spec §7 / Phase 1 design doc §6.6.
/// <c>Telemetry.Source</c> is added by the emitter (not stored here).
/// </remarks>
public sealed record EndpointErrorMetric(
    string ServiceName,
    string Environment,
    string Operation,
    string Exception,
    long Count);
