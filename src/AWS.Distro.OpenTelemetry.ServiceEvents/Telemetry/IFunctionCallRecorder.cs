// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;

/// <summary>
/// Seam for recording FunctionCall data points. Lets the FunctionCallProcessor
/// be unit-tested with a fake recorder; the production implementation is
/// <see cref="ServiceEventsOtlpEmitter" />, which owns the histogram instrument.
/// </summary>
internal interface IFunctionCallRecorder
{
    /// <summary>Record one FunctionCall on the <c>service.function.duration</c> histogram.</summary>
    /// <param name="durationMicros">Call duration in microseconds.</param>
    /// <param name="functionName">Derived function name.</param>
    /// <param name="status"><c>"success"</c> or <c>"error"</c>.</param>
    /// <param name="caller">Optional caller function name.</param>
    /// <param name="operation">Optional owning endpoint operation (<c>"METHOD /route"</c>).</param>
    void RecordFunctionCall(double durationMicros, string functionName, string status, string? caller, string? operation);
}
