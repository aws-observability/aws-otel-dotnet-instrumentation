// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;

namespace OpenTelemetry.Instrumentation.AWS.Implementation;

internal class AWSLlmModelProcessor
{
    internal static void ProcessGenAiAttributes<T>(Activity activity, T message, string model, bool isRequest)
    {
        // message can be either a request or a response. isRequest is used by the model-specific methods to determine
        // whether to extract the request or response attributes.

        // Currently, the .NET SDK does not expose "X-Amzn-Bedrock-*" HTTP headers in the response metadata, as per
        // https://github.com/aws/aws-sdk-net/issues/3171. As a result, we can only extract attributes given what is in
        // the response body. For the Claude, Command, and Mistral models, the input and output tokens are not provided
        // in the response body, so we approximate their values by dividing the input and output lengths by 6, based on
        // the Bedrock documentation here: https://docs.aws.amazon.com/bedrock/latest/userguide/model-customization-prepare.html

        var messageBodyProperty = message.GetType().GetProperty("Body");
        if (messageBodyProperty != null)
        {
            var body = messageBodyProperty.GetValue(message) as MemoryStream;
            if (body != null)
            {
                try
                {
                    var jsonString = Encoding.UTF8.GetString(body.ToArray());
                    var jsonObject = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonString);
                    if (jsonObject == null)
                    {
                        return;
                    }

                    // extract model specific attributes based on model name
                    switch (model)
                    {
                        case "amazon.titan":
                            ProcessTitanModelAttributes(activity, jsonObject, isRequest);
                            break;
                        case "anthropic.claude":
                            ProcessClaudeModelAttributes(activity, jsonObject, isRequest);
                            break;
                        case "meta.llama3":
                            ProcessLlamaModelAttributes(activity, jsonObject, isRequest);
                            break;
                        case "cohere.command":
                            ProcessCommandModelAttributes(activity, jsonObject, isRequest);
                            break;
                        case "ai21.jamba":
                            ProcessJambaModelAttributes(activity, jsonObject, isRequest);
                            break;
                        case "mistral.mistral":
                            ProcessMistralModelAttributes(activity, jsonObject, isRequest);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Exception: " + ex.Message);
                }
            }
        }
    }

    private static void ProcessTitanModelAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody, bool isRequest)
    {
        try
        {
            if (isRequest)
            {
                if (jsonBody.TryGetValue("textGenerationConfig", out var textGenerationConfig))
                {
                    if (textGenerationConfig.TryGetProperty("topP", out var topP))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiTopP, topP.GetDouble());
                    }

                    if (textGenerationConfig.TryGetProperty("temperature", out var temperature))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiTemperature, temperature.GetDouble());
                    }

                    if (textGenerationConfig.TryGetProperty("maxTokenCount", out var maxTokens))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
                    }
                }
            }
            else
            {
                if (jsonBody.TryGetValue("inputTextTokenCount", out var inputTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, inputTokens.GetInt32());
                }

                if (jsonBody.TryGetValue("results", out var resultsArray))
                {
                    var results = resultsArray[0];
                    if (results.TryGetProperty("tokenCount", out var outputTokens))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, outputTokens.GetInt32());
                    }

                    if (results.TryGetProperty("completionReason", out var finishReasons))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessClaudeModelAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody, bool isRequest)
    {
        try
        {
            if (isRequest)
            {
                if (jsonBody.TryGetValue("top_p", out var topP))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTopP, topP.GetDouble());
                }

                if (jsonBody.TryGetValue("temperature", out var temperature))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTemperature, temperature.GetDouble());
                }

                if (jsonBody.TryGetValue("max_tokens_to_sample", out var maxTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
                }

                // input tokens not provided in Claude response body, so we estimate the value based on input length
                if (jsonBody.TryGetValue("prompt", out var input))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, Math.Ceiling((double) input.GetString().Length / 6));
                }
            }
            else
            {
                if (jsonBody.TryGetValue("stop_reason", out var finishReasons))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
                }

                // output tokens not provided in Claude response body, so we estimate the value based on output length
                if (jsonBody.TryGetValue("completion", out var output))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, Math.Ceiling((double) output.GetString().Length / 6));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessLlamaModelAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody, bool isRequest)
    {
        try
        {
            if (isRequest)
            {
                if (jsonBody.TryGetValue("top_p", out var topP))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTopP, topP.GetDouble());
                }

                if (jsonBody.TryGetValue("temperature", out var temperature))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTemperature, temperature.GetDouble());
                }

                if (jsonBody.TryGetValue("max_gen_len", out var maxTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
                }
            }
            else
            {
                if (jsonBody.TryGetValue("prompt_token_count", out var inputTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, inputTokens.GetInt32());
                }

                if (jsonBody.TryGetValue("generation_token_count", out var outputTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, outputTokens.GetInt32());
                }

                if (jsonBody.TryGetValue("stop_reason", out var finishReasons))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessCommandModelAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody, bool isRequest)
    {
        try
        {
            if (isRequest)
            {
                if (jsonBody.TryGetValue("p", out var topP))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTopP, topP.GetDouble());
                }

                if (jsonBody.TryGetValue("temperature", out var temperature))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTemperature, temperature.GetDouble());
                }

                if (jsonBody.TryGetValue("max_tokens", out var maxTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
                }

                // input tokens not provided in Command response body, so we estimate the value based on input length
                if (jsonBody.TryGetValue("prompt", out var input))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, Math.Ceiling((double) input.GetString().Length / 6));
                }
            }
            else
            {
                if (jsonBody.TryGetValue("generations", out var generationsArray))
                {
                    var generation = generationsArray[0];
                    if (generation.TryGetProperty("finish_reason", out var finishReasons))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
                    }

                    // completion tokens not provided in Command response body, so we estimate the value based on output length
                    if (generation.TryGetProperty("text", out var output))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, Math.Ceiling((double) output.GetString().Length / 6));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessJambaModelAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody, bool isRequest)
    {
        try
        {
            if (isRequest)
            {
                if (jsonBody.TryGetValue("top_p", out var topP))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTopP, topP.GetDouble());
                }

                if (jsonBody.TryGetValue("temperature", out var temperature))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTemperature, temperature.GetDouble());
                }

                if (jsonBody.TryGetValue("max_tokens", out var maxTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
                }
            }
            else
            {
                if (jsonBody.TryGetValue("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var inputTokens))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, inputTokens.GetInt32());
                    }
                    if (usage.TryGetProperty("completion_tokens", out var outputTokens))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, outputTokens.GetInt32());
                    }
                }
                if (jsonBody.TryGetValue("choices", out var choices))
                {
                    if (choices[0].TryGetProperty("finish_reason", out var finishReasons))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessMistralModelAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody, bool isRequest)
    {
        try
        {
            if (isRequest)
            {
                if (jsonBody.TryGetValue("top_p", out var topP))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTopP, topP.GetDouble());
                }

                if (jsonBody.TryGetValue("temperature", out var temperature))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiTemperature, temperature.GetDouble());
                }

                if (jsonBody.TryGetValue("max_tokens", out var maxTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
                }

                // input tokens not provided in Mistral response body, so we estimate the value based on input length
                if (jsonBody.TryGetValue("prompt", out var input))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, Math.Ceiling((double) input.GetString().Length / 6));
                }
            }
            else
            {
                if (jsonBody.TryGetValue("outputs", out var outputsArray))
                {
                    var output = outputsArray[0];
                    if (output.TryGetProperty("stop_reason", out var finishReasons))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
                    }

                    // output tokens not provided in Mistral response body, so we estimate the value based on output length
                    if (output.TryGetProperty("text", out var text))
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, Math.Ceiling((double) text.GetString().Length / 6));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }
}
