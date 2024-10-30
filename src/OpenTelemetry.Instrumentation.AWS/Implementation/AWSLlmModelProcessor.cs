// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;

namespace OpenTelemetry.Instrumentation.AWS.Implementation;

internal class AWSLlmModelProcessor
{
    internal static void ProcessRequestModelAttributes(Activity activity, AmazonWebServiceRequest request, string model)
    {
        var requestBodyProperty = request.GetType().GetProperty("Body");
        if (requestBodyProperty != null)
        {
            var body = requestBodyProperty.GetValue(request) as MemoryStream;
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
                            ProcessTitanModelRequestAttributes(activity, jsonObject);
                            break;
                        case "anthropic.claude":
                            ProcessClaudeModelRequestAttributes(activity, jsonObject);
                            break;
                        case "meta.llama3":
                            ProcessLlamaModelRequestAttributes(activity, jsonObject);
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

    internal static void ProcessResponseModelAttributes(Activity activity, AmazonWebServiceResponse response, string model)
    {
        // currently, the .NET SDK does not expose "X-Amzn-Bedrock-*" HTTP headers in the response metadata,
        // as per https://github.com/aws/aws-sdk-net/issues/3171. Unless the Bedrock team decides to change the
        // public interface of the APIs, we can only extract Bedrock attributes that exist in the response body.

        var responseBodyProperty = response.GetType().GetProperty("Body");
        if (responseBodyProperty != null)
        {
            var body = responseBodyProperty.GetValue(response) as MemoryStream;
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
                            ProcessTitanModelResponseAttributes(activity, jsonObject);
                            break;
                        case "anthropic.claude":
                            ProcessClaudeModelResponseAttributes(activity, jsonObject);
                            break;
                        case "meta.llama3":
                            ProcessLlamaModelResponseAttributes(activity, jsonObject);
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

    private static void ProcessTitanModelRequestAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessTitanModelResponseAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
        {
            if (jsonBody.TryGetValue("inputTextTokenCount", out var promptTokens))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiPromptTokens, promptTokens.GetInt32());
            }

            if (jsonBody.TryGetValue("results", out var resultsArray))
            {
                var results = resultsArray[0];
                if (results.TryGetProperty("tokenCount", out var completionTokens))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiCompletionTokens, completionTokens.GetInt32());
                }

                if (results.TryGetProperty("completionReason", out var finishReasons))
                {
                    activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, finishReasons.GetString());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessClaudeModelRequestAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessClaudeModelResponseAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
        {
            if (jsonBody.TryGetValue("stop_reason", out var finishReasons))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, finishReasons.GetString());
            }

            // prompt_tokens and completion_tokens not provided in Claude response body.
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessLlamaModelRequestAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessLlamaModelResponseAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
        {
            if (jsonBody.TryGetValue("prompt_token_count", out var promptTokens))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiPromptTokens, promptTokens.GetInt32());
            }

            if (jsonBody.TryGetValue("generation_token_count", out var completionTokens))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiCompletionTokens, completionTokens.GetInt32());
            }

            if (jsonBody.TryGetValue("stop_reason", out var finishReasons))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, finishReasons.GetString());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }
}
