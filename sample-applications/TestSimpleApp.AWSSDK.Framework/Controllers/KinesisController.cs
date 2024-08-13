using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Unity;

namespace TestSimpleApp.AWSSDK.Framework
{
    [RoutePrefix("kinesis")]
    public class KinesisController : ContractTestController
    {
        private readonly IAmazonKinesis kinesis;
        private readonly IAmazonKinesis faultKinesis;
        private readonly IAmazonKinesis errorKinesis;

        public KinesisController(
            [Dependency("kinesis")] IAmazonKinesis kinesis,
            [Dependency("fault-kinesis")] IAmazonKinesis faultKinesis,
            [Dependency("error-kinesis")] IAmazonKinesis errorKinesis)
        {
            this.kinesis = kinesis;
            this.faultKinesis = faultKinesis;
            this.errorKinesis = errorKinesis;
        }

        [HttpGet]
        [Route("createstream/my-stream")]
        public async Task<IHttpActionResult> CreateStream()
        {
            var response = await kinesis.CreateStreamAsync(new CreateStreamRequest { StreamName = "test_stream" });
            return Ok(response);
        }

        [HttpGet]
        [Route("putrecord/my-stream")]
        public async Task<IHttpActionResult> PutRecord()
        {
            var response = await kinesis.PutRecordAsync(new PutRecordRequest
            {
                StreamName = "test_stream", Data = new MemoryStream(Encoding.UTF8.GetBytes("test_data")), PartitionKey =
                    "partition_key"
            });

            return Ok(response);
        }

        [HttpGet]
        [Route("deletestream/my-stream")]
        public async Task<IHttpActionResult> DeleteStream()
        {
            var response = await kinesis.DeleteStreamAsync(new DeleteStreamRequest { StreamName = "test_stream" });
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
            return faultKinesis.CreateStreamAsync(new CreateStreamRequest { StreamName = "test_stream" }, cancellationToken);
        }

        protected override Task CreateError(CancellationToken cancellationToken)
        {
            return errorKinesis.CreateStreamAsync(new CreateStreamRequest { StreamName = "test_stream" }, cancellationToken);
        }
    }
}