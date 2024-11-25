using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class SNSTests(
    IAmazonSimpleNotificationService sns,
    [FromKeyedServices("fault-sns")] IAmazonSimpleNotificationService faultSns,
    [FromKeyedServices("error-sns")] IAmazonSimpleNotificationService errorSns,
    ILogger<SNSTests> logger) : ContractTest(logger)
{
    public Task<CreateTopicResponse> CreateTopic()
    {
        return sns.CreateTopicAsync(new CreateTopicRequest { Name = "test-topic" });
    }

    public Task<PublishResponse> Publish()
    {
        return sns.PublishAsync(new PublishRequest { TopicArn = "arn:aws:sns:us-east-1:000000000000:test-topic", Message = "test-message" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultSns.CreateTopicAsync(new CreateTopicRequest { Name = "test-topic" }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorSns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = "arn:aws:sns:us-east-1:000000000000:test-topic-error" });
    }
}