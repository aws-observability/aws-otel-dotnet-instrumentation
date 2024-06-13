// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class SqlUrlParserTest
{
    private const string MaxLength80As = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]

    // testSqsClientSpanBasicUrls
    [InlineData("https://sqs.us-east-1.amazonaws.com/123412341234/Q_Name-5", "Q_Name-5")]
    [InlineData("https://sqs.af-south-1.amazonaws.com/999999999999/-_ThisIsValid", "-_ThisIsValid")]
    [InlineData("http://sqs.eu-west-3.amazonaws.com/000000000000/FirstQueue", "FirstQueue")]
    [InlineData("sqs.sa-east-1.amazonaws.com/123456781234/SecondQueue", "SecondQueue")]

    // testSqsClientSpanCustomUrls
    [InlineData("http://127.0.0.1:1212/123456789012/MyQueue", "MyQueue")]
    [InlineData("https://127.0.0.1:1212/123412341234/RRR", "RRR")]
    [InlineData("127.0.0.1:1212/123412341234/QQ", "QQ")]
    [InlineData("https://amazon.com/123412341234/BB", "BB")]

    // testSqsClientSpanLegacyFormatUrls
    [InlineData("https://ap-northeast-2.queue.amazonaws.com/123456789012/MyQueue", "MyQueue")]
    [InlineData("http://cn-northwest-1.queue.amazonaws.com/123456789012/MyQueue", "MyQueue")]
    [InlineData("http://cn-north-1.queue.amazonaws.com/123456789012/MyQueue", "MyQueue")]

    // testSqsClientSpanLongUrls
    [InlineData("ap-south-1.queue.amazonaws.com/123412341234/MyLongerQueueNameHere", "MyLongerQueueNameHere")]
    [InlineData("https://queue.amazonaws.com/123456789012/MyQueue", "MyQueue")]
    [InlineData("http://127.0.0.1:1212/123456789012/" + MaxLength80As, MaxLength80As)]
    [InlineData("http://127.0.0.1:1212/123456789012/" + MaxLength80As + "a", null)]

    // testClientSpanSqsInvalidOrEmptyUrls
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("/", null)]
    [InlineData("//", null)]
    [InlineData("///", null)]
    [InlineData("//asdf", null)]
    [InlineData("/123412341234/as&df", null)]
    [InlineData("invalidUrl", null)]
    [InlineData("https://www.amazon.com", null)]
    [InlineData("https://sqs.us-east-1.amazonaws.com/123412341234/.", null)]
    [InlineData("https://sqs.us-east-1.amazonaws.com/12/Queue", null)]
    [InlineData("https://sqs.us-east-1.amazonaws.com/A/A", null)]
    [InlineData("https://sqs.us-east-1.amazonaws.com/123412341234/A/ThisShouldNotBeHere", null)]
    public void ValidateUrls(string url, string? expectedName)
    {
        Assert.Equal(SqsUrlParser.GetQueueName(url), expectedName);
    }
}
