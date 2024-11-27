using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class StepFunctionsTests(
    IAmazonStepFunctions stepFunctions,
    [FromKeyedServices("fault-stepfunctions")] IAmazonStepFunctions faultClient,
    [FromKeyedServices("error-stepfunctions")] IAmazonStepFunctions errorClient,
    ILogger<StepFunctionsTests> logger) : ContractTest(logger)
{
    public Task<CreateStateMachineResponse> CreateStateMachine()
    {
        return stepFunctions.CreateStateMachineAsync(new CreateStateMachineRequest
        {
            Name = "test-state-machine",
            Definition = "{\"StartAt\":\"TestState\",\"States\":{\"TestState\":{\"Type\":\"Pass\",\"End\":true,\"Result\":\"Result\"}}}",
            RoleArn = "arn:aws:iam::000000000000:role/stepfunctions-role"
        });
    }

    public Task<DescribeStateMachineResponse> DescribeStateMachine()
    {
        return stepFunctions.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:test-state-machine" });
    }

    public Task<CreateActivityResponse> CreateActivity()
    {
        return stepFunctions.CreateActivityAsync(new CreateActivityRequest { Name = "test-activity" });
    }

    public Task<DescribeActivityResponse> DescribeActivity()
    {
        return stepFunctions.DescribeActivityAsync(new DescribeActivityRequest { ActivityArn = "arn:aws:states:us-east-1:000000000000:activity:test-activity" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultClient.CreateStateMachineAsync(new CreateStateMachineRequest
        {
            Name = "test-state-machine",
            Definition = "{\"StartAt\":\"TestState\",\"States\":{\"TestState\":{\"Type\":\"Pass\",\"End\":true,\"Result\":\"Result\"}}}",
            RoleArn = "arn:aws:iam::000000000000:role/stepfunctions-role"
        }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorClient.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:error-state-machine" }, cancellationToken);
    }
}