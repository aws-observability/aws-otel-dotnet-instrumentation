// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Models;

/// <summary>
/// One entry in a call path — the shape of one <c>call_path[]</c> element on the wire.
/// </summary>
/// <param name="FunctionName">Function name, e.g. <c>"MyApp.UserService.GetUser"</c>.</param>
/// <param name="CallerFunctionName">Caller function name. Null if outermost.</param>
/// <param name="DurationNs">Function duration in nanoseconds. Zero when timing unavailable.</param>
/// <param name="Error">Whether this frame is the one that threw the exception.</param>
/// <param name="IsAsync">Whether the function is async.</param>
public sealed record CallPathEntry(
    string FunctionName,
    string? CallerFunctionName,
    long DurationNs,
    bool Error,
    bool IsAsync);
