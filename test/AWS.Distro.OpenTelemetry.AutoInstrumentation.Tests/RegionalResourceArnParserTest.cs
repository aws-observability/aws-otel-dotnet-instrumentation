// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class RegionalResourceArnParserTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(":", null)]
    [InlineData("::::::", null)]
    [InlineData("not:an:arn:string", null)]
    [InlineData("arn:aws:ec2:us-west-2:123456", null)]
    [InlineData("arn:aws:ec2:us-west-2:1234567xxxxx", null)]
    [InlineData("arn:aws:ec2:us-west-2:123456789012", null)]
    [InlineData("arn:aws:dynamodb:us-west-2:123456789012:table/test_table", "123456789012")]
    [InlineData("arn:aws:acm:us-east-1:123456789012:certificate:abc-123", "123456789012")]
    public void ValidateAccountId(string arn, string? expectedAccountId)
    {
        Assert.Equal(RegionalResourceArnParser.GetAccountId(arn), expectedAccountId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData(":", null)]
    [InlineData("::::::", null)]
    [InlineData("not:an:arn:string", null)]
    [InlineData("arn:aws:ec2:us-west-2:123456789012", null)]
    [InlineData("arn:aws:dynamodb:us-west-2:123456789012:table/test_table", "us-west-2")]
    [InlineData("arn:aws:acm:us-east-1:123456789012:certificate:abc-123", "us-east-1")]
    public void ValidateRegion(string arn, string? expectedRegion)
    {
        Assert.Equal(RegionalResourceArnParser.GetRegion(arn), expectedRegion);
    }
}
