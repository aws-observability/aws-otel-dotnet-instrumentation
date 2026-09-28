// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// One captured exception inside an <see cref="IncidentSnapshot"/>.
/// </summary>
/// <param name="ExceptionType">Exception type name, e.g. <c>"TypeError"</c>.</param>
/// <param name="ExceptionMessage">Exception message.</param>
/// <param name="StackTrace">Formatted stack trace.</param>
/// <param name="CallPath">Ordered call path (innermost frame first).</param>
public sealed record ExceptionInfo(
    string ExceptionType,
    string ExceptionMessage,
    string StackTrace,
    IReadOnlyList<CallPathEntry> CallPath);
