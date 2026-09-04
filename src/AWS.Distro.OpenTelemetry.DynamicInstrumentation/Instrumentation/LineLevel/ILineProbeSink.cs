// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Receives line-probe hits from <see cref="DiLineIntegration"/>'s woven callbacks.
/// </summary>
// An interface, rather than wiring the snapshot layer directly into the callbacks, for one concrete reason:
// the callbacks run inside injected IL and cannot be invoked from a unit test without the native profiler.
// A seam here is what lets the hot-path contract — swallow everything, fail closed, no-op when unconfigured
// — be tested with a throwing/counting fake instead of only in a live E2E.
//
// IMPLEMENTATIONS MUST NOT THROW, and must assume they are called on arbitrary customer threads at an
// arbitrary interior IL offset. DiLineIntegrationHelper guards every call anyway, but an implementation that
// relies on that guard is one refactor away from breaking a customer's control flow.
internal interface ILineProbeSink
{
    /// <summary>
    /// Called when an instrumented line is reached.
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL.</param>
    /// <param name="hasValue">Whether <paramref name="value"/> carries a captured local.</param>
    /// <param name="value">
    /// The captured local, already boxed by the injected IL; null when <paramref name="hasValue"/> is false.
    /// Note that a null value with <paramref name="hasValue"/> true is legitimate — the local itself was null
    /// — which is why the two are separate parameters rather than a null check.
    /// </param>
    void OnLineProbeHit(int probeId, bool hasValue, object? value);

    /// <summary>
    /// Decides whether a gated probe should capture on this hit.
    /// </summary>
    /// <param name="probeId">The id baked into the injected IL.</param>
    /// <returns>True to capture; false to skip.</returns>
    bool ShouldCapture(int probeId);
}
