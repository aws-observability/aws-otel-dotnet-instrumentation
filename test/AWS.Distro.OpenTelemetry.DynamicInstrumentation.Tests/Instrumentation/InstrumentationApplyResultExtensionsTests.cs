// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Tests.Instrumentation;

// Pins the instrumentation-failed error taxonomy: which apply results are reportable ERRORs and how
// each maps to the backend InstrumentationErrorCause wire value. This is distinct from capture-failed
// (NotCapturedReason), which is never an ERROR on the configuration.
//
// The cases live inside [Fact] bodies rather than [Theory]/[InlineData] because InstrumentationApplyResult
// is internal and cannot appear in a public test method signature (CS0051).
public class InstrumentationApplyResultExtensionsTests
{
    [Fact]
    public void IsReportableFailure_ClassifiesPermanentFailuresOnly()
    {
        InstrumentationApplyResult.MethodNotFound.IsReportableFailure().Should().BeTrue();
        InstrumentationApplyResult.NoSupportedArity.IsReportableFailure().Should().BeTrue();
        InstrumentationApplyResult.RuntimeError.IsReportableFailure().Should().BeTrue();

        InstrumentationApplyResult.Applied.IsReportableFailure().Should().BeFalse();
        InstrumentationApplyResult.Skipped.IsReportableFailure().Should().BeFalse();
        InstrumentationApplyResult.TypeNotLoaded.IsReportableFailure().Should().BeFalse(); // transient: retried, never reported
    }

    [Fact]
    public void MapErrorCause_ReturnsBackendCause_ForReportableFailures()
    {
        InstrumentationApplyResult.MethodNotFound.MapErrorCause().Should().Be("METHOD_NOT_FOUND");
        InstrumentationApplyResult.NoSupportedArity.MapErrorCause().Should().Be("RUNTIME_ERROR"); // >9 params: no bespoke cause
        InstrumentationApplyResult.RuntimeError.MapErrorCause().Should().Be("RUNTIME_ERROR");
    }

    [Fact]
    public void MapErrorCause_ReturnsNull_ForNonFailures()
    {
        InstrumentationApplyResult.Applied.MapErrorCause().Should().BeNull();
        InstrumentationApplyResult.Skipped.MapErrorCause().Should().BeNull();
        InstrumentationApplyResult.TypeNotLoaded.MapErrorCause().Should().BeNull();
    }

    [Fact]
    public void MapErrorCause_AndIsReportable_AgreeOnEveryResult()
    {
        // Invariant the PR3 emission relies on: a result has a non-null cause exactly when it is reportable.
        foreach (InstrumentationApplyResult result in Enum.GetValues<InstrumentationApplyResult>())
        {
            (result.MapErrorCause() != null).Should().Be(
                result.IsReportableFailure(),
                "cause presence must match reportability for {0}", result);
        }
    }
}
