using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class StepFunctionsTests(
    IAmazonStepFunctions stepFunctions,
    [FromKeyedServices("fault-stepfunctions")] IAmazonStepFunctions faultClient,
    [FromKeyedServices("error-stepfunctions")] IAmazonStepFunctions errorClient,
    ILogger<StepFunctionsTests> logger) : ContractTest(logger)
{
    public Task<CreateStateMachineResponse> CreateStateMachine(string name)
    {
        return stepFunctions.CreateStateMachineAsync(new CreateStateMachineRequest
        {
            Name = name,
            Definition = "{\"StartAt\":\"TestState\",\"States\":{\"TestState\":{\"Type\":\"Pass\",\"End\":true,\"Result\":\"Result\"}}}",
            RoleArn = "arn:aws:iam::000000000000:role/stepfunctions-role"
        });
    }

    public Task<CreateActivityResponse> CreateActivity(string name)
    {
        return stepFunctions.CreateActivityAsync(new CreateActivityRequest { Name = name });
    }

    public Task<DescribeStateMachineResponse> DescribeStateMachine()
    {
        return stepFunctions.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:test-state-machine" });
    }

    public Task<DescribeActivityResponse> DescribeActivity()
    {
        return stepFunctions.DescribeActivityAsync(new DescribeActivityRequest { ActivityArn = "arn:aws:states:us-east-1:000000000000:activity:test-activity" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultClient.ListStateMachineVersionsAsync(new ListStateMachineVersionsRequest
        {
            StateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:invalid-state-machine"
        }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorClient.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:error-state-machine" }, cancellationToken);
    }
}