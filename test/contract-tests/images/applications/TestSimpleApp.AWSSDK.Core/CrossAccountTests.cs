using Amazon.S3;
using Amazon.S3.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class CrossAccountTests(
    [FromKeyedServices("cross-account-s3")] IAmazonS3 crossAccountS3Client,
    ILogger<CrossAccountTests> logger) :
    ContractTest(logger)
{
    public Task<PutBucketResponse> CreateBucketCrossAccount()
    {
        return crossAccountS3Client.PutBucketAsync(new PutBucketRequest 
        { 
            BucketName = "cross-account-bucket",
            BucketRegion = "eu-central-1"
        });
    }
    
    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
