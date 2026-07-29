// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation;
using Xunit;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

// Tests for S3PresignedUrlAttributor.
//
// S3 attribution is driven purely by the endpoint hostname (the signing service cannot be read from
// the redacted credential scope). Operation resolution keys on query-parameter PRESENCE only,
// because this distro's URL sanitization blanks every query value to "Redacted" — so the tests set
// subresource markers with redacted values, matching runtime.
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class S3PresignedUrlAttributorTest
{
    private static PresignedAwsUrl? PresignedUrl(string? method, string host, string path, string extraQueryParameters = "")
    {
        return PresignedAwsUrlParser.Parse(
            "https://" + host + path +
            "?X-Amz-Algorithm=Redacted" +
            "&X-Amz-Credential=Redacted" +
            "&X-Amz-Signature=Redacted" +
            "&X-Amz-Date=Redacted" +
            "&X-Amz-Expires=Redacted" +
            "&X-Amz-SignedHeaders=Redacted" +
            extraQueryParameters,
            method);
    }

    private static PresignedUrlAttribution Attribute(PresignedAwsUrl? url)
    {
        Assert.NotNull(url);
        PresignedUrlAttribution? attribution = S3PresignedUrlAttributor.Attribute(url!);
        Assert.NotNull(attribution);
        return attribution!;
    }

    [Fact]
    public void TestResolvesBucketForEndpointVariant()
    {
        // host, path, expected bucket
        (string Host, string Path, string ExpectedBucket)[] cases =
        {
            // Virtual-hosted style
            ("example-bucket.s3.amazonaws.com", "/object", "example-bucket"),
            ("example-bucket.s3.us-west-2.amazonaws.com", "/object", "example-bucket"),
            ("example-bucket.s3-us-west-2.amazonaws.com", "/object", "example-bucket"),
            ("example.s3.bucket.s3.us-west-2.amazonaws.com", "/object", "example.s3.bucket"),
            ("example-bucket.s3.cn-north-1.amazonaws.com.cn", "/object", "example-bucket"),
            ("example-bucket.s3.dualstack.us-west-2.amazonaws.com", "/object", "example-bucket"),
            ("example-bucket.s3-accelerate.amazonaws.com", "/object", "example-bucket"),
            ("example-bucket.s3-accelerate.dualstack.amazonaws.com", "/object", "example-bucket"),
            ("example-bucket.s3-fips.us-west-2.amazonaws.com", "/object", "example-bucket"),
            ("example-bucket.s3-fips.dualstack.us-east-1.amazonaws.com", "/object", "example-bucket"),
            // Path-style: bucket is the first path segment
            ("s3.amazonaws.com", "/example-bucket/object", "example-bucket"),
            ("s3.us-west-2.amazonaws.com", "/example-bucket/object", "example-bucket"),
            ("s3.cn-north-1.amazonaws.com.cn", "/example-bucket/object", "example-bucket"),
            ("s3-fips.us-west-2.amazonaws.com", "/example-bucket/object", "example-bucket"),
            ("s3-fips.dualstack.us-east-1.amazonaws.com", "/example-bucket/object", "example-bucket"),
        };

        foreach ((string host, string path, string expectedBucket) in cases)
        {
            PresignedUrlAttribution attribution = Attribute(PresignedUrl("GET", host, path));
            Assert.Equal("AWS::S3", attribution.RemoteService);
            Assert.NotNull(attribution.RemoteResource);
            Assert.Equal("AWS::S3::Bucket", attribution.RemoteResource!.Type);
            Assert.Equal(expectedBucket, attribution.RemoteResource.Identifier);
        }
    }

    [Fact]
    public void TestResolvesOperation()
    {
        // method, path, extra query params, expected operation
        (string Method, string Path, string ExtraQuery, string ExpectedOperation)[] cases =
        {
            ("GET", "/object", string.Empty, "GetObject"),
            ("PUT", "/object", string.Empty, "PutObject"),
            ("HEAD", "/object", string.Empty, "HeadObject"),
            ("DELETE", "/object", string.Empty, "DeleteObject"),
            ("PATCH", "/object", string.Empty, "UnknownRemoteOperation"),
            // ListObjectsV2 is bucket-level only. Presence of list-type is the marker (value blanked).
            ("GET", "/", "&list-type=Redacted", "ListObjectsV2"),
            ("GET", "/object", "&list-type=Redacted", "GetObject"),
            ("PUT", "/object", "&list-type=Redacted", "PutObject"),
            // Multipart
            ("PUT", "/object", "&partNumber=Redacted&uploadId=Redacted", "UploadPart"),
            ("PUT", "/object", "&uploadId=Redacted", "PutObject"),
            ("GET", "/object", "&uploadId=Redacted", "ListParts"),
            ("POST", "/object", "&uploadId=Redacted", "CompleteMultipartUpload"),
            ("DELETE", "/object", "&uploadId=Redacted", "AbortMultipartUpload"),
            ("POST", "/object", "&uploads", "CreateMultipartUpload"),
            ("GET", "/", "&uploads", "ListMultipartUploads"),
            ("GET", "/object", "&uploads", "GetObject"),
            // ACL / tagging (object- and bucket-level). These are valueless flags.
            ("GET", "/object", "&acl", "GetObjectAcl"),
            ("PUT", "/object", "&acl", "PutObjectAcl"),
            ("GET", "/", "&acl", "GetBucketAcl"),
            ("PUT", "/", "&acl", "PutBucketAcl"),
            ("GET", "/object", "&tagging", "GetObjectTagging"),
            ("PUT", "/object", "&tagging", "PutObjectTagging"),
            ("DELETE", "/object", "&tagging", "DeleteObjectTagging"),
            ("GET", "/", "&tagging", "GetBucketTagging"),
            ("PUT", "/", "&tagging", "PutBucketTagging"),
            ("DELETE", "/", "&tagging", "DeleteBucketTagging"),
            // Object-only subresources
            ("GET", "/object", "&retention", "GetObjectRetention"),
            ("PUT", "/object", "&retention", "PutObjectRetention"),
            ("GET", "/object", "&legal-hold", "GetObjectLegalHold"),
            ("PUT", "/object", "&legal-hold", "PutObjectLegalHold"),
            ("GET", "/object", "&torrent", "GetObjectTorrent"),
        };

        foreach ((string method, string path, string extraQuery, string expectedOperation) in cases)
        {
            PresignedUrlAttribution attribution =
                Attribute(PresignedUrl(method, "example-bucket.s3.us-west-2.amazonaws.com", path, extraQuery));
            Assert.Equal(expectedOperation, attribution.RemoteOperation);
        }
    }

    [Fact]
    public void TestResolvesPathStyleOperation()
    {
        // method, path, extra query params, expected operation
        (string Method, string Path, string ExtraQuery, string ExpectedOperation)[] cases =
        {
            ("GET", "/example-bucket", "&list-type=Redacted", "ListObjectsV2"),
            // Trailing slash after the bucket is bucket-level, not an object key.
            ("GET", "/example-bucket/", "&list-type=Redacted", "ListObjectsV2"),
            ("GET", "/example-bucket/", string.Empty, "UnknownRemoteOperation"),
            ("GET", "/example-bucket/object", string.Empty, "GetObject"),
            ("DELETE", "/example-bucket/object", string.Empty, "DeleteObject"),
            ("GET", "/example-bucket", "&acl", "GetBucketAcl"),
            ("GET", "/example-bucket/", "&acl", "GetBucketAcl"),
            ("GET", "/example-bucket/object", "&acl", "GetObjectAcl"),
        };

        foreach ((string method, string path, string extraQuery, string expectedOperation) in cases)
        {
            PresignedUrlAttribution attribution =
                Attribute(PresignedUrl(method, "s3.us-west-2.amazonaws.com", path, extraQuery));
            Assert.Equal(expectedOperation, attribution.RemoteOperation);
        }
    }

    [Fact]
    public void TestFailsClosedForUnrecognizedEndpoint()
    {
        string[] hosts =
        {
            // Access point host (bucket not identifiable from the endpoint form)
            "example-bucket.s3-accesspoint.us-west-2.amazonaws.com",
            // Custom CNAME
            "s3.mycompany.com",
            // Non-S3 AWS service endpoint
            "sqs.us-west-2.amazonaws.com",
        };

        foreach (string host in hosts)
        {
            PresignedAwsUrl? url = PresignedUrl("GET", host, "/object");
            Assert.NotNull(url);
            Assert.Null(S3PresignedUrlAttributor.Attribute(url!));
        }
    }

    [Fact]
    public void TestUsesUnknownOperationForAmbiguousBucketOperation()
    {
        PresignedUrlAttribution attribution =
            Attribute(PresignedUrl("GET", "example-bucket.s3.us-west-2.amazonaws.com", "/"));

        Assert.Equal("AWS::S3", attribution.RemoteService);
        Assert.Equal("UnknownRemoteOperation", attribution.RemoteOperation);
        Assert.NotNull(attribution.RemoteResource);
    }

    [Fact]
    public void TestMissingHttpMethodUsesUnknownOperation()
    {
        PresignedUrlAttribution attribution =
            Attribute(PresignedUrl(null, "example-bucket.s3.us-west-2.amazonaws.com", "/object"));

        Assert.Equal("AWS::S3", attribution.RemoteService);
        Assert.Equal("UnknownRemoteOperation", attribution.RemoteOperation);
    }

    [Fact]
    public void TestPathStyleWithoutBucketAttributesS3WithoutResource()
    {
        PresignedUrlAttribution attribution =
            Attribute(PresignedUrl("GET", "s3.us-west-2.amazonaws.com", "/"));

        Assert.Equal("AWS::S3", attribution.RemoteService);
        Assert.Null(attribution.RemoteResource);
    }
}
