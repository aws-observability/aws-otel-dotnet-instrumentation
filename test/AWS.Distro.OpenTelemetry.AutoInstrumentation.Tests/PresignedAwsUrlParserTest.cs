// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Tests for PresignedAwsUrlParser.
//
// Detection is presence-only: this distro's URL sanitization blanks every query value to the literal
// "Redacted" before attribution runs, so the parser cannot read the X-Amz-Algorithm value (nor any
// other value). The tests use realistic sanitized URLs (redacted credential and signature) to reflect
// what the parser actually sees at runtime.
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class PresignedAwsUrlParserTest
{
    // A realistic sanitized presigned URL: the agent redacts the credential and signature values
    // before attribution runs. The non-redacted presigned parameters remain (also redacted here,
    // matching runtime behavior where every value is blanked).
    private static string PresignedUrl(string host, string path)
    {
        return "https://" + host + path +
            "?X-Amz-Algorithm=Redacted" +
            "&X-Amz-Credential=Redacted" +
            "&X-Amz-Signature=Redacted" +
            "&X-Amz-Date=Redacted" +
            "&X-Amz-Expires=Redacted" +
            "&X-Amz-SignedHeaders=Redacted";
    }

    [Fact]
    public void TestParsesPresignedUrl()
    {
        PresignedAwsUrl? presignedAwsUrl =
            PresignedAwsUrlParser.Parse(PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", "/object"), "GET");

        Assert.NotNull(presignedAwsUrl);
        Assert.Equal("example-bucket.s3.us-west-2.amazonaws.com", presignedAwsUrl!.GetHost());
        Assert.Equal("/object", presignedAwsUrl.GetPath());
        Assert.Equal("GET", presignedAwsUrl.GetHttpMethod());
    }

    [Fact]
    public void TestRejectsUrlsMissingRequiredParameters()
    {
        // description -> URL expected to be rejected (returns null).
        Dictionary<string, string?> cases = new Dictionary<string, string?>
        {
            ["null url"] = null,
            ["empty url"] = string.Empty,
            ["not a url"] = "not-a-url",
            ["no query parameters"] = "https://example-bucket.s3.us-west-2.amazonaws.com/object",
            ["missing X-Amz-Algorithm"] =
                "https://example-bucket.s3.us-west-2.amazonaws.com/object" +
                "?X-Amz-Credential=Redacted&X-Amz-Signature=Redacted&X-Amz-Date=Redacted" +
                "&X-Amz-Expires=Redacted&X-Amz-SignedHeaders=Redacted",
            ["missing X-Amz-Expires"] =
                "https://example-bucket.s3.us-west-2.amazonaws.com/object" +
                "?X-Amz-Algorithm=Redacted&X-Amz-Credential=Redacted&X-Amz-Signature=Redacted" +
                "&X-Amz-Date=Redacted&X-Amz-SignedHeaders=Redacted",
            ["missing X-Amz-SignedHeaders"] =
                "https://example-bucket.s3.us-west-2.amazonaws.com/object" +
                "?X-Amz-Algorithm=Redacted&X-Amz-Credential=Redacted&X-Amz-Signature=Redacted" +
                "&X-Amz-Date=Redacted&X-Amz-Expires=Redacted",
        };

        foreach (KeyValuePair<string, string?> testCase in cases)
        {
            Assert.Null(PresignedAwsUrlParser.Parse(testCase.Value, "GET"));
        }
    }

    [Fact]
    public void TestAcceptsPresignedRequestRegardlessOfAlgorithmValue()
    {
        // Presence-only detection: because sanitization blanks the X-Amz-Algorithm value, the parser
        // must accept the request no matter what value the parameter carries (including "Redacted"
        // or a non-SigV4 string). This is the key .NET divergence from the value-allowlist ports.
        string[] algorithmValues = { "Redacted", "AWS4-HMAC-SHA256", "AWS4-ECDSA-P256-SHA256", "anything" };
        foreach (string algorithm in algorithmValues)
        {
            string url =
                "https://example-bucket.s3.us-west-2.amazonaws.com/object" +
                "?X-Amz-Algorithm=" + algorithm +
                "&X-Amz-Credential=Redacted&X-Amz-Signature=Redacted&X-Amz-Date=Redacted" +
                "&X-Amz-Expires=Redacted&X-Amz-SignedHeaders=Redacted";
            Assert.NotNull(PresignedAwsUrlParser.Parse(url, "GET"));
        }
    }

    [Fact]
    public void TestAcceptsValuelessRequiredParameters()
    {
        // A presigned parameter present with no value (e.g. "X-Amz-Expires" with an empty value)
        // still counts: presence is the only signal. The value-allowlist ports reject empty values,
        // but under .NET sanitization values are never trustworthy, so presence alone gates.
        string url =
            "https://example-bucket.s3.us-west-2.amazonaws.com/object" +
            "?X-Amz-Algorithm=&X-Amz-Credential=&X-Amz-Signature=&X-Amz-Date=&X-Amz-Expires=&X-Amz-SignedHeaders=";
        Assert.NotNull(PresignedAwsUrlParser.Parse(url, "GET"));
    }

    [Fact]
    public void TestPreservesHttpMethodAndDefaultsPath()
    {
        PresignedAwsUrl? presignedAwsUrl =
            PresignedAwsUrlParser.Parse(PresignedUrl("example-bucket.s3.us-west-2.amazonaws.com", string.Empty), null);

        Assert.NotNull(presignedAwsUrl);
        // An empty path defaults to "/".
        Assert.Equal("/", presignedAwsUrl!.GetPath());
        Assert.Null(presignedAwsUrl.GetHttpMethod());
    }
}
