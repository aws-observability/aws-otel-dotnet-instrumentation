using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Amazon.S3;
using Amazon.S3.Model;
using Unity;

namespace TestSimpleApp.AWSSDK.Framework.Controllers
{
    [RoutePrefix("s3")]
    public class S3Controller : ContractTestController
    {
        private readonly IAmazonS3 s3;
        private readonly IAmazonS3 faultClient;
        private readonly IAmazonS3 errorClient;

        public S3Controller(
            [Dependency("s3")] IAmazonS3 s3,
            [Dependency("fault-s3")] IAmazonS3 faultClient,
            [Dependency("error-s3")] IAmazonS3 errorClient)
        {
            this.s3 = s3;
            this.faultClient = faultClient;
            this.errorClient = errorClient;
        }

        [HttpGet]
        [Route("createbucket/create-bucket/{bucketName}")]
        public async Task<IHttpActionResult> CreateBucket([FromUri] string bucketName = "test-bucket-name")
        {
            var response = await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucketName });
            return Ok(response);
        }

        [HttpGet]
        [Route("createobject/put-object/some-object/{bucketName}")]
        public async Task<IHttpActionResult> PutObject([FromUri] string bucketName = "test-bucket-name")
        {
            var response = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketName, Key = "my-object", ContentBody = "test_object" });
            return Ok(response);
        }

        [HttpGet]
        [Route("deleteobject/delete-object/some-object/{bucketName}")]
        public async Task<IHttpActionResult> DeleteObject([FromUri] string bucketName = "test-bucket-name")
        {
            var response = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucketName, Key = "my-object" });
            return Ok(response);
        }

        [HttpGet]
        [Route("deletebucket/delete-bucket/{bucketName}")]
        public async Task<IHttpActionResult> DeleteBucket([FromUri] string bucketName = "test-bucket-name")
        {
            var response = await s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucketName });
            return Ok(response);
        }

        [HttpGet]
        [Route("error")]
        public override Task<IHttpActionResult> Error()
        {
            return base.Error();
        }

        [HttpGet]
        [Route("fault")]
        public override Task<IHttpActionResult> Fault()
        {
            return base.Fault();
        }

        protected override Task CreateFault(CancellationToken cancellationToken)
        {
            return faultClient.PutBucketAsync(new PutBucketRequest { BucketName = "valid-bucket-name" }, cancellationToken);
        }

        protected override Task CreateError(CancellationToken cancellationToken)
        {
            return errorClient.PutBucketAsync(new PutBucketRequest { BucketName = "valid-bucket-name" }, cancellationToken);
        }
    }
}