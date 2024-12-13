using System.IO;
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
        return new GetGuardrailResponse
        {
            HttpStatusCode = HttpStatusCode.OK,
            GuardrailId = "test-guardrail",
        };
    }

    // 7 InvokeModel test calls and responses, one for each supported model
    // The manual responses are automatically serialized to a MemoryStream and used as the response body

    public void InvokeModelAmazonNova()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "us.amazon.nova-micro-v1:0",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                text = "sample input text",
                            }
                        }
                    },
                },
                inferenceConfig = new
                {
                    temperature = 0.123,
                    top_p = 0.456,
                    max_new_tokens = 123,
                },
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelAmazonNovaResponse()
    {
        return new
        {
            usage = new
            {
                inputTokens = 456,
                outputTokens = 789,
            },
            stopReason = "finish_reason",
        };
    }

    public void InvokeModelAmazonTitan()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "amazon.titan-text-express-v1",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                inputText = "sample input text",
                textGenerationConfig = new
                {
                    temperature = 0.123,
                    topP = 0.456,
                    maxTokenCount = 123,
                },
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelAmazonTitanResponse()
    {
        return new
        {
            inputTextTokenCount = 456,
            results = new object[]
            {
                new
                {
                    outputText = "sample output text",
                    tokenCount = 789,
                    completionReason = "finish_reason"
                },
            },
        };
    }

    public void InvokeModelAnthropicClaude()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "us.anthropic.claude-3-5-haiku-20241022-v1:0",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = "sample input text",
                            }
                        }
                    },
                },
                temperature = 0.123,
                top_p = 0.456,
                max_tokens = 123,
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelAnthropicClaudeResponse()
    {
        return new
        {
            usage = new
            {
                input_tokens = 456,
                output_tokens = 789,
            },
            stop_reason = "finish_reason",
        };
    }

    public void InvokeModelMetaLlama()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "meta.llama3-8b-instruct-v1:0",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                prompt = "sample input text",
                temperature = 0.123,
                top_p = 0.456,
                max_gen_len = 123,
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelMetaLlamaResponse()
    {
        return new
        {
            generation = "sample output text",
            prompt_token_count = 456,
            generation_token_count = 789,
            stop_reason = "finish_reason",
        };
    }

    public void InvokeModelCohereCommand()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "cohere.command-r-v1:0",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                // prompt is 72 chars long, input_tokens should be estimated as ceil(72/6) = 12
                message = "sample input text sample input text sample input text sample input text ",
                temperature = 0.123,
                p = 0.456,
                max_tokens = 123,
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelCohereCommandResponse()
    {
        return new
        {
            // response is 56 chars long, output_tokens should be estimated as ceil(56/6) = 10
            text = "sample output text sample output text sample output text",
            finish_reason = "finish_reason",
        };
    }

    public void InvokeModelAi21Jamba()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "ai21.jamba-1-5-large-v1:0",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                messages = new object[]
                {
                    new
                    {
                        role = "USER",
                        content = "sample input text",
                    },
                },
                temperature = 0.123,
                top_p = 0.456,
                max_tokens = 123,
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelAi21JambaResponse()
    {
        return new
        {
            choices = new object[]
            {
                new
                {
                    finish_reason = "finish_reason",
                },
            },
            usage = new
            {
                prompt_tokens = 456,
                completion_tokens = 789,
            },
        };
    }

    public void InvokeModelMistralAi()
    {
        bedrockRuntime.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = "mistral.mistral-7b-instruct-v0:2",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                // prompt is 72 chars long, input_tokens should be estimated as ceil(72/6) = 12
                prompt = "sample input text sample input text sample input text sample input text ",
                temperature = 0.123,
                top_p = 0.456,
                max_tokens = 123,
            }))),
            ContentType = "application/json",
        });
        return;
    }

    public object InvokeModelMistralAiResponse()
    {
        return new
        {
            outputs = new object[]
            {
                new
                {
                    // response is 56 chars long, output_tokens should be estimated as ceil(56/6) = 10
                    text = "sample output text sample output text sample output text",
                    stop_reason = "finish_reason",
                },
            },
        };
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

    public void InvokeAgent()
    {
        bedrockAgentRuntime.InvokeAgentAsync(new InvokeAgentRequest
        {
            AgentId = "test-agent",
            AgentAliasId = "test-agent-alias",
            SessionId = "test-session",
        });
        return;
    }

    public void InvokeAgentResponse()
    {
        return;
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