// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

/// <summary>
/// The kind of dynamic instrumentation: a permanent probe or a temporary breakpoint.
/// </summary>
public enum InstrumentationType
{
    /// <summary>A permanent instrumentation that persists until explicitly removed.</summary>
    PROBE,

    /// <summary>A temporary instrumentation that auto-expires.</summary>
    BREAKPOINT,
}
