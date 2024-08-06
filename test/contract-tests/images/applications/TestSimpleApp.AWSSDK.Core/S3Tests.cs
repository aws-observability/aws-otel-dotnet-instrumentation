using Amazon.S3;
using Amazon.S3.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class S3Tests(
    IAmazonS3 s3,
    [FromKeyedServices("fault-s3")] IAmazonS3 faultClient,
    [FromKeyedServices("error-s3")] IAmazonS3 errorClient,
    ILogger<S3Tests> logger) : ContractTest(logger)
{
    public Task<PutBucketResponse> CreateBucket(string? bucketName = "test-bucket-name")
    {
        return s3.PutBucketAsync(new PutBucketRequest { BucketName = bucketName, UseClientRegion = true });
    }

    public Task<PutObjectResponse> PutObject(string? bucketName = "test-bucket-name")
    {
        return s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketName, Key = "my-object", ContentBody = "test_object" });
    }

    public Task<DeleteObjectResponse> DeleteObject(string? bucketName = "test-bucket-name")
    {
        return s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucketName, Key = "my-object" });
    }

    public Task<DeleteBucketResponse> DeleteBucket(string? bucketName = "test-bucket-name")
    {
        return s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucketName });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultClient.PutBucketAsync(new PutBucketRequest { BucketName = "valid-bucket-name" }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorClient.DeleteBucketAsync(new DeleteBucketRequest { BucketName = "test-bucket-error" });
    }
}