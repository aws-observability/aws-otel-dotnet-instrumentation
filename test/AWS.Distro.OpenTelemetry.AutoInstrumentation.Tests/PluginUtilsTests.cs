// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class PluginUtilsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , invalid, =value")]
    public void TestParseOtlpHeadersEnvVariableShouldReturnEmptyMapForMissingOrMalformedHeaders(string? headers)
    {
        Assert.Empty(PluginUtils.ParseOtlpHeadersEnvVariable(headers));
    }

    [Fact]
    public void TestParseOtlpHeadersEnvVariableShouldParseHeadersWithWhitespaceAndEncodedValues()
    {
        var headers = PluginUtils.ParseOtlpHeadersEnvVariable(" Authorization = Bearer%20token== , x-test = value%2Cwith%3Dequals ");

        Assert.Equal(2, headers.Count);
        Assert.Equal("Bearer%20token==", headers["authorization"]);
        Assert.Equal("value%2Cwith%3Dequals", headers["X-TEST"]);
    }

    [Fact]
    public void TestParseOtlpHeadersEnvVariableShouldKeepEmptyValuesAndIgnoreMalformedEntries()
    {
        var headers = PluginUtils.ParseOtlpHeadersEnvVariable("invalid,Authorization=, =ignored,x-test=value");

        Assert.Equal(2, headers.Count);
        Assert.Equal(string.Empty, headers["Authorization"]);
        Assert.Equal("value", headers["x-test"]);
    }

    [Fact]
    public void TestParseOtlpHeadersEnvVariableShouldUseLastValueForDuplicateHeaderNames()
    {
        var headers = PluginUtils.ParseOtlpHeadersEnvVariable("Authorization=first,authorization=second");

        Assert.Single(headers);
        Assert.Equal("second", headers["Authorization"]);
    }

    [Theory]
    [InlineData("x-test=Authorization=Bearer%20token")]
    [InlineData("Proxy-Authorization=Bearer%20token")]
    [InlineData("X-Authorization=Bearer%20token")]
    public void TestParseOtlpHeadersEnvVariableShouldKeepOtherHeaderNamesDistinctFromAuthorization(string headers)
    {
        var parsedHeaders = PluginUtils.ParseOtlpHeadersEnvVariable(headers);

        Assert.Single(parsedHeaders);
        Assert.False(parsedHeaders.ContainsKey("Authorization"));
    }
}
