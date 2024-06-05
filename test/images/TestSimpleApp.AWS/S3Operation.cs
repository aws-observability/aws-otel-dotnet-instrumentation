// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;

namespace TestApplication.AWS;

public class S3Operation
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;

    public S3Operation(IAmazonS3 s3, string bucketName)
    {
        _s3 = s3;
        _bucketName = bucketName;
    }

    public async Task CreateBucket()
    {
        var putBucketRequest = new PutBucketRequest
        {
            BucketName = _bucketName,
            UseClientRegion = true
        };

        await _s3.PutBucketAsync(putBucketRequest);
    }

    public async Task<S3BucketVersioningConfig> GetBucketVersioning()
    {
        GetBucketVersioningResponse response = await _s3.GetBucketVersioningAsync(_bucketName);
        return response.VersioningConfig;
    }

    public async Task PutObject()
    {
        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = "sample_key",
            ContentBody = "sample_text"
        };
        await _s3.PutObjectAsync(putRequest);
    }

    public async Task DeleteObject()
    {
        var deleteObjectRequest = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = "sample_key"
        };
        await _s3.DeleteObjectAsync(deleteObjectRequest);
    }

    public async Task DeleteBucket()
    {
        await _s3.DeleteBucketAsync(_bucketName);
    }
}
