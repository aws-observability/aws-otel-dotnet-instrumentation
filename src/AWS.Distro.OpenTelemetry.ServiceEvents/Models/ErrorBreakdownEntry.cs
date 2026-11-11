// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// One row of the <c>exception_breakdown</c> body field — a group of
/// errors sharing the same HTTP failure type.
/// </summary>
/// <param name="FailureType">HTTP status code as a string, e.g. <c>"500"</c>.</param>
/// <param name="Count">Number of occurrences in the window.</param>
/// <param name="Exceptions">Per-exception detail for the group.</param>
public sealed record ErrorBreakdownEntry(
    string FailureType,
    long Count,
    IReadOnlyList<ErrorDetail> Exceptions);
