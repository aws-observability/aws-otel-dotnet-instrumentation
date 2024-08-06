using System.Web;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class SQSTests(
    IAmazonSQS sqs,
    [FromKeyedServices("fault-sqs")] IAmazonSQS faultSqs,
    [FromKeyedServices("error-sqs")] IAmazonSQS errorSqs,
    ILogger<SQSTests> logger) : ContractTest(logger)
{
    public Task<CreateQueueResponse> CreateQueue()
    {
        return sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test_queue" });
    }

    public Task<SendMessageResponse> SendMessage()
    {
        return sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = "http://sqs.us-east-1.localstack:4566/000000000000/test_queue", MessageBody = "test_message" });
    }

    public Task ReceiveMessage()
    {
        return sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = "http://sqs.us-east-1.localstack:4566/000000000000/test_queue" });
    }

    public Task<DeleteQueueResponse> DeleteQueue()
    {
        return sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = "http://sqs.us-east-1.localstack:4566/000000000000/test_queue" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultSqs.CreateQueueAsync(new CreateQueueRequest { QueueName = "test_queue" }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorSqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = "http://sqs.us-east-1.localstack:4566/000000000000/test_queue_error" });
    }
}