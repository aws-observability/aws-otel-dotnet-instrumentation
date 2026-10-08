// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Covers the AWS OTLP endpoint patterns that gate SigV4 signing for traces
/// (<see cref="Plugin.XRayOtlpEndpointPattern"/>) and logs
/// (<see cref="Plugin.CloudWatchLogsOtlpEndpointPattern"/>).
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
        Assert.Matches(Plugin.XRayOtlpEndpointPattern, endpoint);
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
        Assert.DoesNotMatch(Plugin.XRayOtlpEndpointPattern, endpoint);
    }

    [Theory]
    [InlineData("https://logs.us-east-1.amazonaws.com/v1/logs")]
    [InlineData("https://logs.eu-west-1.amazonaws.com/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/logs")]
    [InlineData("https://logs.cn-northwest-1.amazonaws.com.cn/v1/logs")]
    public void TestLogsPatternMatchesSupportedEndpoints(string endpoint)
    {
        Assert.Matches(Plugin.CloudWatchLogsOtlpEndpointPattern, endpoint);
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
        Assert.DoesNotMatch(Plugin.CloudWatchLogsOtlpEndpointPattern, endpoint);
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
        Match match = Regex.Match(endpoint, Plugin.XRayOtlpEndpointPattern);

        Assert.True(match.Success);
        Assert.Equal(expectedRegion, match.Groups[1].Value);
        Assert.Equal(expectedRegion, new Uri(endpoint).Host.Split('.')[1]);
    }

    [Theory]
    [InlineData("https://logs.us-east-1.amazonaws.com/v1/logs", "us-east-1")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/logs", "cn-north-1")]
    public void TestLogsPatternCapturesRegion(string endpoint, string expectedRegion)
    {
        Match match = Regex.Match(endpoint, Plugin.CloudWatchLogsOtlpEndpointPattern);

        Assert.True(match.Success);
        Assert.Equal(expectedRegion, match.Groups[1].Value);
        Assert.Equal(expectedRegion, new Uri(endpoint).Host.Split('.')[1]);
    }
}
