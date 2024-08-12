using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using Amazon.SQS;
using Amazon.SQS.Model;
using Unity;

namespace TestSimpleApp.AWSSDK.Framework.Controllers
{
    [RoutePrefix("sqs")]
    public class SqsController : ContractTestController
    {
        private readonly IAmazonSQS sqs;
        private readonly IAmazonSQS faultSqs;
        private readonly IAmazonSQS errorSqs;

        public SqsController(
            [Dependency("sqs")] IAmazonSQS sqs,
            [Dependency("fault-sqs")] IAmazonSQS faultSqs,
            [Dependency("error-sqs")] IAmazonSQS errorSqs)
        {
            this.sqs = sqs;
            this.faultSqs = faultSqs;
            this.errorSqs = errorSqs;
        }

        [HttpGet]
        [Route("createqueue/some-queue")]
        public async Task<IHttpActionResult> CreateQueue()
        {
            var response = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test_queue" });

            return Ok(response);
        }

        [HttpGet]
        [Route("publishqueue/some-queue")]
        public async Task<IHttpActionResult> SendMessage([FromUri] string queueUrl = "http://localstack:4566/000000000000/test_put_get_queue")
        {
            var response = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = HttpUtility.UrlDecode(queueUrl), MessageBody = "test_message" });
            return Ok(response);
        }

        [HttpGet]
        [Route("consumequeue/some-queue")]
        public async Task<IHttpActionResult> ReceiveMessage([FromUri] string queueUrl = "http://localstack:4566/000000000000/test_put_get_queue")
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = HttpUtility.UrlDecode(queueUrl) });
            return Ok(response);
        }

        [HttpGet]
        [Route("deletequeue/some-queue")]
        public async Task<IHttpActionResult> DeleteQueue([FromUri] string queueUrl = "http://localstack:4566/000000000000/test_put_get_queu")
        {
            var response = await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = HttpUtility.UrlDecode(queueUrl) });
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
            return faultSqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test_queue" }, cancellationToken);
        }

        protected override Task CreateError(CancellationToken cancellationToken)
        {
            return errorSqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test_queue" }, cancellationToken);
        }
    }
}