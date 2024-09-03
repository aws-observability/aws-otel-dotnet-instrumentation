using System.Text;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class BedrockTests(ILogger<BedrockTests> logger) : ContractTest(logger)
{
    public Task GetGuardrail()
    {
        Console.Log("GetGuardrail");
        return Task.CompletedTask;
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