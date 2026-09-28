// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// Per-exception detail inside an <see cref="ErrorBreakdownEntry"/>.
/// </summary>
/// <param name="ExceptionType">Exception type name, e.g. <c>"TypeError"</c>.</param>
/// <param name="FunctionName">Function that threw the exception.</param>
public sealed record ErrorDetail(string ExceptionType, string FunctionName);
