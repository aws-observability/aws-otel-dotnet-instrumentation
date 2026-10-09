// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Pins <see cref="LineProbeEmissionMode"/>'s numeric values to the native <c>LineProbeEmissionMode</c>.
/// </summary>
// These cross the managed/native boundary as bare integers with no type checking on either side. If the
// two enums drift, the rewriter emits a DIFFERENT IL sequence than intended and the failure is silent:
// the wrong callback is called, or a value arrives unboxed. No compiler and no marshaler can catch it,
// and a fire-count test cannot either — the removal-under-load spike had four probes firing the expected 200 times each
// while all silently using probe[0]'s callback. Hence an explicit value lock.
//
// Source of truth (line_probe.h, verified in the fork):
//   LINE_EMIT_LEGACY = 0, LINE_EMIT_GATED_BOX = 1, LINE_EMIT_UNGATED_BOX = 2, LINE_EMIT_LOCAL_CAPTURE = 3
public class LineProbeEmissionModeTests
{
    // Named rather than passed as the enum itself: LineProbeEmissionMode is internal, and an internal type
    // cannot appear in a public [Theory] signature.
    [Theory]
    [InlineData(nameof(LineProbeEmissionMode.Legacy), 0)]
    [InlineData(nameof(LineProbeEmissionMode.GatedBox), 1)]
    [InlineData(nameof(LineProbeEmissionMode.UngatedBox), 2)]
    [InlineData(nameof(LineProbeEmissionMode.LocalCapture), 3)]
    public void Values_MatchTheNativeEnum(string modeName, int nativeValue)
    {
        var mode = Enum.Parse<LineProbeEmissionMode>(modeName);

        ((int)mode).Should().Be(
            nativeValue, "{0} is compared against a hardcoded integer in line_probe.cpp", modeName);
    }

    [Fact]
    public void Legacy_IsZero_SoADefaultConstructedDefinitionTakesTheProvenPath()
    {
        // The native side documents 0/null as "LEGACY, unchanged Phase-2 behavior". A zeroed struct must
        // therefore land on the emission path that is already proven, not on a box/gate path.
        ((int)default(LineProbeEmissionMode)).Should().Be(0);
        default(LineProbeEmissionMode).Should().Be(LineProbeEmissionMode.Legacy);
    }

    [Fact]
    public void NoModeWasAddedWithoutPinningItsValue()
    {
        // A mode added managed-side without a matching native value would otherwise slip through: the
        // Theory above only checks the four it names. This fails when the enum grows, forcing the author
        // to pin the new value deliberately.
        Enum.GetValues<LineProbeEmissionMode>().Should().HaveCount(
            4, "every mode must have a pinned native counterpart in line_probe.h");
    }
}
