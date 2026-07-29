// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Derives <c>AWS::S3</c> attribution from a presigned S3 URL by recognizing S3 endpoint hostnames.
///
/// <para>Because the signing service cannot be read from the (redacted) credential scope, S3 is
/// identified purely from the endpoint host. Only the standard virtual-hosted and path-style S3
/// endpoint forms are recognized. Anything else — custom CNAMEs, access points, unknown endpoints —
/// fails closed (returns null) so we never mis-attribute a non-S3 or unverifiable request.</para>
///
/// <para>The remote operation is derived from the HTTP method, whether an object key is present
/// (bucket- vs object-level), and the S3 subresource/multipart query parameters. Operation names
/// follow the S3 REST API.</para>
///
/// <para><b>.NET-specific divergence:</b> the Java/Python/JS ports read the <c>list-type</c> value to
/// confirm it equals <c>2</c> before mapping a bucket-level GET to <c>ListObjectsV2</c>. This distro's
/// URL sanitization blanks every query value (see PresignedAwsUrlParser remarks), so the value is
/// unavailable — this port keys on the <b>presence</b> of the <c>list-type</c> parameter instead. That
/// is safe because <c>list-type</c> is unique to ListObjectsV2 in the S3 REST API (its only defined
/// value is <c>2</c>, and no other operation uses the parameter), so its presence alone is an
/// unambiguous marker. All other operation markers are valueless flags (e.g. <c>acl</c>,
/// <c>tagging</c>, <c>uploads</c>) or presence-only keys (<c>uploadId</c>, <c>partNumber</c>), which
/// survive sanitization intact.</para>
///
/// <para>References:
/// <list type="bullet">
///   <item>Endpoints: https://docs.aws.amazon.com/general/latest/gr/s3.html</item>
///   <item>Virtual-hosted vs path-style: https://docs.aws.amazon.com/AmazonS3/latest/userguide/VirtualHosting.html</item>
///   <item>S3 REST API operations: https://docs.aws.amazon.com/AmazonS3/latest/API/API_Operations.html</item>
/// </list></para>
/// </summary>
internal sealed class S3PresignedUrlAttributor
{
    private const string NormalizedS3ServiceName = "AWS::S3";
    private const string S3BucketResourceType = NormalizedS3ServiceName + "::Bucket";

    // Standard S3 endpoint host forms, including global, regional, legacy regional, dual-stack,
    // transfer acceleration, FIPS (incl. FIPS dual-stack), and China (.com.cn). The optional segment
    // after "s3" covers the mutually exclusive endpoint styles.
    //
    // The legacy "-<label>" alternative is intentionally broad: besides legacy regional hosts
    // (s3-us-west-2) it also matches other s3-prefixed AWS hosts such as s3-website-<region>. This is
    // accepted deliberately as low risk — all such hosts are S3-owned domains anchored to
    // amazonaws.com, and presigned object requests do not target website/other endpoints.
    // https://docs.aws.amazon.com/general/latest/gr/s3.html
    // https://docs.aws.amazon.com/AmazonS3/latest/userguide/dual-stack-endpoints.html
    private const string S3EndpointSuffix =
        "s3(?:" +
        @"\.(?:dualstack\.)?[a-z0-9-]+" + // s3.<region> | s3.dualstack.<region>
        @"|-fips(?:\.dualstack)?\.[a-z0-9-]+" + // s3-fips.<region> | s3-fips.dualstack.<region>
        @"|-accelerate(?:\.dualstack)?" + // s3-accelerate | s3-accelerate.dualstack
        "|-[a-z0-9-]+" + // s3-<region> (legacy regional)
        @")?\.amazonaws\.com(?:\.cn)?";

    private static readonly Regex VirtualHostedS3Endpoint =
        new Regex("^(.+)\\." + S3EndpointSuffix + "$", RegexOptions.IgnoreCase);

    private static readonly Regex PathStyleS3Endpoint =
        new Regex("^" + S3EndpointSuffix + "$", RegexOptions.IgnoreCase);

    private S3PresignedUrlAttributor()
    {
    }

    internal static PresignedUrlAttribution? Attribute(PresignedAwsUrl presignedAwsUrl)
    {
        string host = presignedAwsUrl.GetHost();
        bool pathStyle = PathStyleS3Endpoint.IsMatch(host);

        string? bucket;
        if (pathStyle)
        {
            bucket = GetPathStyleBucket(presignedAwsUrl);
        }
        else
        {
            bucket = GetVirtualHostedStyleBucket(host);
            if (bucket == null)
            {
                // Not a recognized S3 endpoint (custom CNAME, access point, unknown host). Fail
                // closed: the signing service cannot be recovered from a redacted credential scope.
                return null;
            }
        }

        RemoteResource? remoteResource = bucket == null ? null : new RemoteResource(S3BucketResourceType, bucket);

        return new PresignedUrlAttribution(
            NormalizedS3ServiceName,
            GetRemoteOperation(presignedAwsUrl, pathStyle),
            remoteResource);
    }

    private static string GetRemoteOperation(PresignedAwsUrl presignedAwsUrl, bool pathStyle)
    {
        string? httpMethod = presignedAwsUrl.GetHttpMethod();
        if (httpMethod == null)
        {
            return UnknownRemoteOperation;
        }

        string normalizedMethod = httpMethod.ToUpperInvariant();
        bool hasObjectKeyPresent = HasObjectKey(presignedAwsUrl, pathStyle);

        // ListObjectsV2 is a bucket-level GET (no object key). Presence of `list-type` is a unique,
        // unambiguous marker (see class remarks); its value is unavailable after redaction.
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html
        if (normalizedMethod == "GET" && !hasObjectKeyPresent && presignedAwsUrl.HasQueryParameter("list-type"))
        {
            return "ListObjectsV2";
        }

        // S3 multipart REST API operations.
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_CreateMultipartUpload.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_UploadPart.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListParts.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_CompleteMultipartUpload.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_AbortMultipartUpload.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListMultipartUploads.html
        if (presignedAwsUrl.HasQueryParameter("uploadId"))
        {
            if (normalizedMethod == "PUT" && presignedAwsUrl.HasQueryParameter("partNumber"))
            {
                return "UploadPart";
            }

            if (normalizedMethod == "GET")
            {
                return "ListParts";
            }

            if (normalizedMethod == "POST")
            {
                return "CompleteMultipartUpload";
            }

            if (normalizedMethod == "DELETE")
            {
                return "AbortMultipartUpload";
            }
        }

        if (presignedAwsUrl.HasQueryParameter("uploads"))
        {
            if (normalizedMethod == "POST" && hasObjectKeyPresent)
            {
                return "CreateMultipartUpload";
            }

            if (normalizedMethod == "GET" && !hasObjectKeyPresent)
            {
                return "ListMultipartUploads";
            }
        }

        // Subresource operations selected by a query parameter. They are object-level when an object
        // key is present and bucket-level otherwise.
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectAcl.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectTagging.html
        if (presignedAwsUrl.HasQueryParameter("acl"))
        {
            if (normalizedMethod == "GET")
            {
                return hasObjectKeyPresent ? "GetObjectAcl" : "GetBucketAcl";
            }

            if (normalizedMethod == "PUT")
            {
                return hasObjectKeyPresent ? "PutObjectAcl" : "PutBucketAcl";
            }
        }

        if (presignedAwsUrl.HasQueryParameter("tagging"))
        {
            if (normalizedMethod == "GET")
            {
                return hasObjectKeyPresent ? "GetObjectTagging" : "GetBucketTagging";
            }

            if (normalizedMethod == "PUT")
            {
                return hasObjectKeyPresent ? "PutObjectTagging" : "PutBucketTagging";
            }

            if (normalizedMethod == "DELETE")
            {
                return hasObjectKeyPresent ? "DeleteObjectTagging" : "DeleteBucketTagging";
            }
        }

        // Object-only subresources. These operate on an object, so they require an object key.
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectRetention.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectLegalHold.html
        // https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObjectTorrent.html
        if (hasObjectKeyPresent)
        {
            if (presignedAwsUrl.HasQueryParameter("retention"))
            {
                if (normalizedMethod == "GET")
                {
                    return "GetObjectRetention";
                }

                if (normalizedMethod == "PUT")
                {
                    return "PutObjectRetention";
                }
            }

            if (presignedAwsUrl.HasQueryParameter("legal-hold"))
            {
                if (normalizedMethod == "GET")
                {
                    return "GetObjectLegalHold";
                }

                if (normalizedMethod == "PUT")
                {
                    return "PutObjectLegalHold";
                }
            }

            if (normalizedMethod == "GET" && presignedAwsUrl.HasQueryParameter("torrent"))
            {
                return "GetObjectTorrent";
            }
        }

        if (!hasObjectKeyPresent)
        {
            return UnknownRemoteOperation;
        }

        switch (normalizedMethod)
        {
            case "GET":
                return "GetObject";
            case "HEAD":
                return "HeadObject";
            case "PUT":
                return "PutObject";
            case "DELETE":
                return "DeleteObject";
            default:
                return UnknownRemoteOperation;
        }
    }

    private static bool HasObjectKey(PresignedAwsUrl presignedAwsUrl, bool pathStyle)
    {
        string[] pathSegments = GetPathSegments(presignedAwsUrl.GetPath());
        if (pathStyle)
        {
            // Path-style URLs carry the bucket as the first path segment, so an object key requires
            // a second segment.
            return pathSegments.Length > 1;
        }

        return pathSegments.Length > 0;
    }

    private static string? GetPathStyleBucket(PresignedAwsUrl presignedAwsUrl)
    {
        string[] pathSegments = GetPathSegments(presignedAwsUrl.GetPath());
        if (pathSegments.Length == 0)
        {
            return null;
        }

        return pathSegments[0];
    }

    private static string? GetVirtualHostedStyleBucket(string host)
    {
        Match match = VirtualHostedS3Endpoint.Match(host);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value;
    }

    private static string[] GetPathSegments(string path)
    {
        string normalizedPath = (path ?? string.Empty).TrimStart('/');
        if (normalizedPath.Length == 0)
        {
            return Array.Empty<string>();
        }

        // Drop empty segments so a trailing slash (e.g. path-style "/bucket/") is not misread as an
        // object key. Java's String.split already discards trailing empties; C#'s String.Split does
        // not.
        return normalizedPath.Split('/').Where(segment => segment.Length > 0).ToArray();
    }
}
