// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Tests for how Plugin recognizes the AWS X-Ray and CloudWatch Logs OTLP endpoints, which decides whether
// traces and logs are exported with SigV4 signing. The AWS China partition (cn-north-1, cn-northwest-1)
// serves these endpoints under amazonaws.com.cn rather than amazonaws.com.
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class PluginTest
{
    [Theory]
    [InlineData("https://xray.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("https://xray.us-gov-west-1.amazonaws.com/v1/traces")]
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn/v1/traces")]
    [InlineData("https://xray.cn-northwest-1.amazonaws.com.cn/v1/traces")]
    public void TestIsXrayOtlpEndpointAcceptsXrayEndpoints(string endpoint)
    {
        Assert.True(Plugin.IsXrayOtlpEndpoint(endpoint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://localhost:4318/v1/traces")]
    [InlineData("http://xray.us-east-1.amazonaws.com/v1/traces")]
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/traces")]

    // Near misses of the China partition's amazonaws.com.cn suffix
    [InlineData("https://xray.cn-north-1.amazonaws.cn/v1/traces")]
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn.example.com/v1/traces")]
    public void TestIsXrayOtlpEndpointRejectsOtherEndpoints(string? endpoint)
    {
        Assert.False(Plugin.IsXrayOtlpEndpoint(endpoint));
    }

    [Theory]
    [InlineData("https://logs.us-west-2.amazonaws.com/v1/logs")]
    [InlineData("https://logs.us-gov-east-1.amazonaws.com/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/logs")]
    [InlineData("https://logs.cn-northwest-1.amazonaws.com.cn/v1/logs")]
    public void TestIsCloudWatchLogsOtlpEndpointAcceptsLogsEndpoints(string endpoint)
    {
        Assert.True(Plugin.IsCloudWatchLogsOtlpEndpoint(endpoint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://localhost:4318/v1/logs")]
    [InlineData("http://logs.us-west-2.amazonaws.com/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn/v1/traces")]
    [InlineData("https://xray.cn-north-1.amazonaws.com.cn/v1/logs")]

    // Near misses of the China partition's amazonaws.com.cn suffix
    [InlineData("https://logs.cn-north-1.amazonaws.cn/v1/logs")]
    [InlineData("https://logs.cn-north-1.amazonaws.com.cn.example.com/v1/logs")]
    public void TestIsCloudWatchLogsOtlpEndpointRejectsOtherEndpoints(string? endpoint)
    {
        Assert.False(Plugin.IsCloudWatchLogsOtlpEndpoint(endpoint));
    }
}
