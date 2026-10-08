// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;
using Moq;
using Moq.Protected;
using OpenTelemetry;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Common;

/// <summary>
/// Verifies delegated exporter lifecycle results, timeouts, and disposal.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class BaseOtlpAwsExporterTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldDelegateFlushResultAndTimeout(bool expectedResult)
    {
        var upstream = new Mock<BaseExporter<object>>();
        upstream.Protected().Setup<bool>("OnForceFlush", 1234).Returns(expectedResult);
        using var exporter = new DelegatingExporter(upstream.Object);

        Assert.Equal(expectedResult, exporter.ForceFlush(1234));

        upstream.Protected().Verify("OnForceFlush", Times.Once(), 1234);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldDelegateShutdownResultAndTimeoutOnce(bool expectedResult)
    {
        var upstream = new Mock<BaseExporter<object>>();
        upstream.Protected().Setup<bool>("OnShutdown", 2345).Returns(expectedResult);
        using var exporter = new DelegatingExporter(upstream.Object);

        Assert.Equal(expectedResult, exporter.Shutdown(2345));
        Assert.False(exporter.Shutdown(2345));

        upstream.Protected().Verify("OnShutdown", Times.Once(), 2345);
    }

    [Fact]
    public void ShouldDisposeUpstreamExporterOnce()
    {
        var upstream = new Mock<BaseExporter<object>>();
        var exporter = new DelegatingExporter(upstream.Object);

        exporter.Dispose();
        exporter.Dispose();

        upstream.Protected().Verify("Dispose", Times.Once(), new object[] { true });
    }

    private sealed class DelegatingExporter : BaseOtlpAwsExporter<object>
    {
        public DelegatingExporter(BaseExporter<object> exporter)
            : base(exporter)
        {
        }
    }
}
