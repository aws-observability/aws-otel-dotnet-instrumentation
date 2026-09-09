// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation.LineLevel;

/// <summary>
/// Verifies the retry/report policy for line-probe resolution failures.
/// </summary>
// The distinction these tests protect is operational, not cosmetic: a status classified retryable is
// re-resolved on EVERY poll, so misclassifying a permanent failure (a stripped PDB) as retryable turns one
// failed probe into an unbounded retry loop, while misclassifying a transient one as permanent means a probe
// on a not-yet-loaded assembly never installs.
//
// Cases live inside [Fact] bodies rather than [Theory]/[InlineData] because LineProbeResolutionStatus is
// internal and cannot appear in a public test method signature (CS0051) — same convention as
// InstrumentationApplyResultExtensionsTests.
public class LineProbeResolutionExtensionsTests
{
    [Fact]
    public void TypeNotLoaded_IsTheOnlyRetryableStatus()
    {
        // Asserted exhaustively over the enum rather than one-by-one, so a NEW status added later cannot
        // default to retryable without this test noticing.
        foreach (var status in Enum.GetValues<LineProbeResolutionStatus>())
        {
            var expected = status == LineProbeResolutionStatus.TypeNotLoaded;
            status.IsRetryable().Should().Be(expected, $"{status} retryability");
        }
    }

    [Fact]
    public void MapErrorCause_MapsFailuresToBackendCauses()
    {
        LineProbeResolutionStatus.LineNotExecutable.MapErrorCause().Should().Be("LINE_NOT_EXECUTABLE");

        // An out-of-scope local is a property of the requested LINE, so the operator's fix is to move the
        // probe — not to retry or file a bug. Hence LINE_NOT_EXECUTABLE, not RUNTIME_ERROR.
        LineProbeResolutionStatus.LocalOutOfScope.MapErrorCause().Should().Be("LINE_NOT_EXECUTABLE");

        // The backend enum has no "debug info missing" member, so these collapse onto RUNTIME_ERROR and the
        // Detail string carries the precision. A deliberate lossy mapping.
        LineProbeResolutionStatus.DebugInfoUnavailable.MapErrorCause().Should().Be("RUNTIME_ERROR");
        LineProbeResolutionStatus.DebugInfoMismatch.MapErrorCause().Should().Be("RUNTIME_ERROR");
        LineProbeResolutionStatus.ProfilerMissingLineProbeSupport.MapErrorCause().Should().Be("RUNTIME_ERROR");
    }

    [Fact]
    public void DebugInfoUnavailable_IsReported_NotSilentlyDropped()
    {
        // The most likely line-level failure in production: containers routinely strip PDBs. It fails CLOSED
        // and quietly, so if it mapped to null the operator would see a probe that never fires and no reason
        // why — indistinguishable from the feature being broken.
        LineProbeResolutionStatus.DebugInfoUnavailable.MapErrorCause().Should().NotBeNull();
    }

    [Fact]
    public void MapErrorCause_ReturnsNull_ForSuccessAndRetryableStatuses()
    {
        // Reporting on either would spam the backend: success needs no error, and a not-yet-loaded assembly
        // is expected during startup.
        LineProbeResolutionStatus.Resolved.MapErrorCause().Should().BeNull();
        LineProbeResolutionStatus.TypeNotLoaded.MapErrorCause().Should().BeNull();
    }
}
