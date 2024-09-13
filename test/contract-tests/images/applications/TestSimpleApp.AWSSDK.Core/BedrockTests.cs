using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Microsoft.AspNetCore.Mvc;

namespace TestSimpleApp.AWSSDK.Core;

public class BedrockTests(
    IAmazonBedrock bedrock,
    IAmazonBedrockRuntime bedrockRuntime,
    IAmazonBedrockAgent bedrockAgent,
    IAmazonBedrockAgentRuntime bedrockAgentRuntime,
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
        Console.WriteLine("GetGuardrailResponse");
        return new GetGuardrailResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
            GuardrailId = "test-guardrail",
        };
    }

    public async Task<InvokeModelResponse> InvokeModel()
    {
        try
        {
            var result = await bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
            {
                ModelId = "amazon.titan-text-express-v1",
            });
            Console.WriteLine("HTTPStatusCode:" + result.HttpStatusCode);
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine("Exception occured in InvokeModel: " + e.Message);
            throw;
        }
    }

    public object InvokeModelResponse()
    {
        Console.WriteLine("InvokeModelResponse");
        try
        {
            var result = new
            {
                HttpStatusCode = HttpStatusCode.OK,
            };
            Console.WriteLine(result);
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine("Exception occured in InvokeModelResponse: " + e.Message);
            throw;
        }
    }

    public Task<GetAgentResponse> GetAgent()
    {
        return bedrockAgent.GetAgentAsync(new GetAgentRequest
        {
            AgentId = "test-agent",
        });
    }

    public GetAgentResponse GetAgentResponse()
    {
        return new GetAgentResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
        };
    }

    public Task<GetKnowledgeBaseResponse> GetKnowledgeBase()
    {
        return bedrockAgent.GetKnowledgeBaseAsync(new GetKnowledgeBaseRequest
        {
            KnowledgeBaseId = "test-knowledge-base",
        });
    }

    public GetKnowledgeBaseResponse GetKnowledgeBaseResponse()
    {
        return new GetKnowledgeBaseResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
        };
    }

    public Task<GetDataSourceResponse> GetDataSource()
    {
        return bedrockAgent.GetDataSourceAsync(new GetDataSourceRequest
        {
            KnowledgeBaseId = "test-knowledge-base",
            DataSourceId = "test-data-source",
        });
    }

    public GetDataSourceResponse GetDataSourceResponse()
    {
        return new GetDataSourceResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
        };
    }

    public Task<InvokeAgentResponse> InvokeAgent()
    {
        return bedrockAgentRuntime.InvokeAgentAsync(new InvokeAgentRequest
        {
            AgentId = "test-agent",
            AgentAliasId = "test-agent-alias",
            SessionId = "test-session",
        });
    }

    public InvokeAgentResponse InvokeAgentResponse()
    {
        return new InvokeAgentResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
        };
    }

    public Task<RetrieveResponse> Retrieve()
    {
        return bedrockAgentRuntime.RetrieveAsync(new RetrieveRequest
        {
            KnowledgeBaseId = "test-knowledge-base",
            RetrievalQuery = new KnowledgeBaseQuery
            {
                Text = "test-query",
            },
        });
    }

    public RetrieveResponse RetrieveResponse()
    {
        return new RetrieveResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
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