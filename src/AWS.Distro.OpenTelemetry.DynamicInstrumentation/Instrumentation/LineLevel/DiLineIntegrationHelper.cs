// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Implementation behind <see cref="DiLineIntegration"/>'s woven callbacks.
/// </summary>
// Separated from DiLineIntegration for the same reason DiIntegrationHelper is separate from DiIntegrationN:
// the woven type's surface must contain ONLY what the injected IL calls, so that adding a helper can never
// accidentally change the public contract the native MemberRefs bind against.
//
// Internal is correct here — nothing in customer IL references this type, only DiLineIntegration.
internal static class DiLineIntegrationHelper
{
    // Set by the manager once configuration is known. Volatile because probes fire on arbitrary customer
    // threads while the manager may be swapping the sink from its polling thread; without it a thread could
    // observe a torn or stale reference indefinitely.
    private static volatile ILineProbeSink? sink;

    /// <summary>
    /// Installs the sink that receives line-probe hits, or clears it when <paramref name="value"/> is null.
    /// </summary>
    /// <param name="value">The sink, or null to disable capture.</param>
    internal static void Configure(ILineProbeSink? value)
    {
        sink = value;
    }

    /// <summary>
    /// Called when injected IL reaches an instrumented line.
    /// </summary>
    /// <param name="probeId">Id baked into the injected IL.</param>
    /// <param name="hasValue">Whether <paramref name="value"/> carries a captured local.</param>
    /// <param name="value">The captured local, already boxed by the injected IL; null when none.</param>
    // EVERY PATH IS SWALLOWED. This runs at an arbitrary interior IL offset inside customer code, on the
    // customer's thread, often inside their try/catch or loop. An exception escaping here does not merely
    // lose a snapshot — it changes the customer's control flow at a point their code never anticipated, and
    // can surface as an impossible exception from a line that cannot throw. Losing telemetry is strictly
    // preferable, so the catch is unconditional and deliberately silent on the hot path.
    //
    // This is the same discipline the function-level engine needed, and it was a real finding there: the
    // hot path originally had no try/catch at all.
    internal static void OnLineReached(int probeId, bool hasValue, object? value)
    {
        try
        {
            // Read once. A second read could see a different sink mid-call if the manager reconfigures.
            var current = sink;
            if (current == null)
            {
                // Probe is woven but capture is off (not yet configured, or disabled). The woven IL stays in
                // place; making this a cheap no-op is what allows disabling without re-weaving.
                return;
            }

            current.OnLineProbeHit(probeId, hasValue, value);
        }
        catch
        {
            // Intentionally empty: see the note above. No logging either — a logger call here would itself
            // be arbitrary managed code on the customer's hot path.
        }
    }

    /// <summary>
    /// Rate-limit gate for <see cref="LineProbeEmissionMode.GatedBox"/>.
    /// </summary>
    /// <param name="probeId">Id baked into the injected IL.</param>
    /// <returns>True to proceed with capture; false to skip.</returns>
    // FAIL CLOSED, and note the asymmetry with OnLineReached: that one swallows and continues because the
    // work is already done, while this one swallows and returns FALSE, suppressing the capture. Returning
    // true on failure would run the capture path with an unknown gate state.
    internal static bool ShouldCapture(int probeId)
    {
        try
        {
            var current = sink;
            return current != null && current.ShouldCapture(probeId);
        }
        catch
        {
            return false;
        }
    }
}
