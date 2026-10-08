// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Covers the AWS OTLP endpoint patterns that gate SigV4 signing for traces
/// (<see cref="AwsOtlpEndpoint.TracesPattern"/>) and logs
/// (<see cref="AwsOtlpEndpoint.LogsPattern"/>).
///
/// These patterns decide whether a request is signed at all, so a false negative silently disables
/// authentication and a false positive would sign a host that is not AWS. Both directions are
/// asserted below, including the lookalike hosts the anchoring is there to reject.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class AwsOtlpEndpointPatternTest
{
    [Theory]

    // Commercial partition: unchanged behavior.
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("https://xray.eu-west-1.amazonaws.com/v1/traces")]

    // AWS China partition: the regression this change fixes.
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn/v1/traces")]
    [InlineData("https://xray.cn-northwest-1.amazonaws.com.cn/v1/traces")]
    public void TestTracesPatternMatchesSupportedEndpoints(string endpoint)
    {
        Assert.Matches(AwsOtlpEndpoint.TracesPattern, endpoint);
    }

    [Theory]

    // A suffix after the partition domain must not match; this is what the trailing anchor is for.
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn.evil/v1/traces")]
    [InlineData("https://xray.us-east-1.amazonaws.com.evil/v1/traces")]

    // ".cn" is optional, not free-floating: it is only accepted directly after ".amazonaws.com".
    [InlineData("https://xray.cn-north-1.amazonaws.cn/v1/traces")]
    [InlineData("https://xray.cn-north-1.cn.amazonaws.com/v1/traces")]

    // Wrong signal path, wrong service host, wrong scheme, or extra path segments.
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/metrics")]
    [InlineData("https://logs.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("http://xray.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/traces/extra")]

    // Not an AWS endpoint at all.
    [InlineData("https://example.com/v1/traces")]
    [InlineData("http://localhost:4318/v1/traces")]
    public void TestTracesPatternRejectsUnsupportedEndpoints(string endpoint)
    {
        Assert.DoesNotMatch(AwsOtlpEndpoint.TracesPattern, endpoint);
    }

    [Theory]
    [InlineData("https://logs.us-east-1.amazonaws.com/v1/logs")]
    [InlineData("https://logs.eu-west-1.amazonaws.com/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/logs")]
    [InlineData("https://logs.cn-northwest-1.amazonaws.com.cn/v1/logs")]
    public void TestLogsPatternMatchesSupportedEndpoints(string endpoint)
    {
        Assert.Matches(AwsOtlpEndpoint.LogsPattern, endpoint);
    }

    [Theory]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn.evil/v1/logs")]
    [InlineData("https://logs.us-east-1.amazonaws.com.evil/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.cn/v1/logs")]
    [InlineData("https://logs.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/logs")]
    [InlineData("http://logs.us-east-1.amazonaws.com/v1/logs")]
    [InlineData("https://example.com/v1/logs")]
    public void TestLogsPatternRejectsUnsupportedEndpoints(string endpoint)
    {
        Assert.DoesNotMatch(AwsOtlpEndpoint.LogsPattern, endpoint);
    }

    /// <summary>
    /// The capture group feeds nothing today, but the same second-label value is what both call
    /// sites use as the signing region (via <c>Split('.')[1]</c>). Asserting it here records that
    /// China hosts yield a real region rather than a DNS fragment, which is the reason the China
    /// fix needed no change to region parsing.
    /// </summary>
    [Theory]
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/traces", "us-east-1")]
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn/v1/traces", "cn-north-1")]
    [InlineData("https://xray.cn-northwest-1.amazonaws.com.cn/v1/traces", "cn-northwest-1")]
    public void TestTracesPatternCapturesRegion(string endpoint, string expectedRegion)
    {
        Match match = Regex.Match(endpoint, AwsOtlpEndpoint.TracesPattern);

        Assert.True(match.Success);
        Assert.Equal(expectedRegion, match.Groups[1].Value);
        Assert.Equal(expectedRegion, new Uri(endpoint).Host.Split('.')[1]);
    }

    [Theory]
    [InlineData("https://logs.us-east-1.amazonaws.com/v1/logs", "us-east-1")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/logs", "cn-north-1")]
    public void TestLogsPatternCapturesRegion(string endpoint, string expectedRegion)
    {
        Match match = Regex.Match(endpoint, AwsOtlpEndpoint.LogsPattern);

        Assert.True(match.Success);
        Assert.Equal(expectedRegion, match.Groups[1].Value);
        Assert.Equal(expectedRegion, new Uri(endpoint).Host.Split('.')[1]);
    }

    [Theory]
    [InlineData("https://monitoring.us-east-1.amazonaws.com/v1/metrics")]
    [InlineData("https://monitoring.eu-west-1.amazonaws.com/v1/metrics")]
    [InlineData("https://monitoring.cn-north-1.amazonaws.com.cn/v1/metrics")]
    [InlineData("https://monitoring.cn-northwest-1.amazonaws.com.cn/v1/metrics")]
    public void TestMetricsPatternMatchesSupportedEndpoints(string endpoint)
    {
        Assert.True(AwsOtlpEndpoint.IsMetricsEndpoint(endpoint));
    }

    [Theory]
    [InlineData("https://monitoring.cn-north-1.amazonaws.com.cn.evil/v1/metrics")]
    [InlineData("https://monitoring.us-east-1.amazonaws.com.evil/v1/metrics")]
    [InlineData("https://monitoring.us-east-1.amazonaws.cn/v1/metrics")]

    // "cloudwatch" and "metrics" are not the metrics OTLP host; only "monitoring" is.
    [InlineData("https://cloudwatch.us-east-1.amazonaws.com/v1/metrics")]
    [InlineData("https://metrics.us-east-1.amazonaws.com/v1/metrics")]

    // Right host, wrong signal path.
    [InlineData("https://monitoring.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("http://monitoring.us-east-1.amazonaws.com/v1/metrics")]
    [InlineData("http://localhost:4318/v1/metrics")]
    public void TestMetricsPatternRejectsUnsupportedEndpoints(string endpoint)
    {
        Assert.False(AwsOtlpEndpoint.IsMetricsEndpoint(endpoint));
    }

    /// <summary>
    /// Each signal's matcher must recognize only its own endpoint. Signing a request with the wrong
    /// service name produces a credential scope the service rejects, so a cross-signal match would
    /// fail at the service with an authentication error rather than anything self-explanatory.
    /// </summary>
    [Fact]
    public void TestMatchersDoNotOverlapAcrossSignals()
    {
        const string traces = "https://xray.us-east-1.amazonaws.com/v1/traces";
        const string logs = "https://logs.us-east-1.amazonaws.com/v1/logs";
        const string metrics = "https://monitoring.us-east-1.amazonaws.com/v1/metrics";

        Assert.True(AwsOtlpEndpoint.IsTracesEndpoint(traces));
        Assert.False(AwsOtlpEndpoint.IsLogsEndpoint(traces));
        Assert.False(AwsOtlpEndpoint.IsMetricsEndpoint(traces));

        Assert.True(AwsOtlpEndpoint.IsLogsEndpoint(logs));
        Assert.False(AwsOtlpEndpoint.IsTracesEndpoint(logs));
        Assert.False(AwsOtlpEndpoint.IsMetricsEndpoint(logs));

        Assert.True(AwsOtlpEndpoint.IsMetricsEndpoint(metrics));
        Assert.False(AwsOtlpEndpoint.IsTracesEndpoint(metrics));
        Assert.False(AwsOtlpEndpoint.IsLogsEndpoint(metrics));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TestNullAndEmptyEndpointsAreNotRecognized(string? endpoint)
    {
        Assert.False(AwsOtlpEndpoint.IsTracesEndpoint(endpoint));
        Assert.False(AwsOtlpEndpoint.IsLogsEndpoint(endpoint));
        Assert.False(AwsOtlpEndpoint.IsMetricsEndpoint(endpoint));
        Assert.Null(AwsOtlpEndpoint.GetRegion(endpoint));
    }

    [Theory]
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/traces", "us-east-1")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/logs", "cn-north-1")]
    [InlineData("https://monitoring.cn-northwest-1.amazonaws.com.cn/v1/metrics", "cn-northwest-1")]
    public void TestGetRegionReturnsTheCapturedRegion(string endpoint, string expectedRegion)
    {
        Assert.Equal(expectedRegion, AwsOtlpEndpoint.GetRegion(endpoint));
    }

    [Fact]
    public void TestGetRegionReturnsNullForUnrecognizedEndpoint()
    {
        Assert.Null(AwsOtlpEndpoint.GetRegion("https://example.com/v1/metrics"));
    }

    /// <summary>
    /// These names go into the SigV4 credential scope verbatim, and the service rejects a request
    /// whose scope names the service differently, including by casing. A live cn-north-1 probe with
    /// "XRay" returned "Credential should be scoped to correct service: 'xray'", so all three are
    /// pinned here in the exact casing the services expect.
    /// </summary>
    [Fact]
    public void TestSigningServiceNames()
    {
        // "monitoring" is the CloudWatch Metrics signing service; "cloudwatch" and "metrics" are not.
        Assert.Equal("monitoring", AwsOtlpEndpoint.MetricsSigningServiceName);
        Assert.Equal("logs", AwsOtlpEndpoint.LogsSigningServiceName);
        Assert.Equal("xray", AwsOtlpEndpoint.TracesSigningServiceName);
    }
}
