using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class SNSTests(
    IAmazonSimpleNotificationService sns,
    [FromKeyedServices("fault-sns")] IAmazonSimpleNotificationService faultSns,
    [FromKeyedServices("error-sns")] IAmazonSimpleNotificationService errorSns,
    ILogger<SNSTests> logger) : ContractTest(logger)
{
    public Task<CreateTopicResponse> CreateTopic(string name)
    {
        return sns.CreateTopicAsync(new CreateTopicRequest { Name = name });
    }

    public Task<PublishResponse> Publish()
    {
        return sns.PublishAsync(new PublishRequest { TopicArn = "arn:aws:sns:us-east-1:000000000000:test-topic", Message = "test-message" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultSns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = "arn:aws:sns:us-east-1:000000000000:invalid-topic" }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorSns.PublishAsync(new PublishRequest { TopicArn = "arn:aws:sns:us-east-1:000000000000:test-topic-error" });
    }
}