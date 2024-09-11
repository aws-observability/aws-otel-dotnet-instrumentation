using System.Net;
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

public class BedrockTests(
    IAmazonBedrock bedrock,
    ILogger<BedrockTests> logger) :
    ContractTest(logger)
{
    public Task<GetGuardrailResponse> GetGuardrail()
    {
        return bedrock.GetGuardrailAsync(new GetGuardrailRequest
        {
            GuardrailIdentifier = "test-guardrail",
        });
    }

    public GetGuardrailResponse GetGuardrailResponse()
    {
        return new GetGuardrailResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
            GuardrailId = "test-guardrail",
        };
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