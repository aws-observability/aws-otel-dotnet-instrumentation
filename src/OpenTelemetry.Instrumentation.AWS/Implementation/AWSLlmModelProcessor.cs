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
                        case "cohere.command":
                            ProcessCommandModelRequestAttributes(activity, jsonObject);
                            break;
                        case "ai21.jamba":
                            ProcessJambaModelRequestAttributes(activity, jsonObject);
                            break;
                        case "mistral.mistral":
                            ProcessMistralModelRequestAttributes(activity, jsonObject);
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
        // Currently, the .NET SDK does not expose "X-Amzn-Bedrock-*" HTTP headers in the response metadata, as per
        // https://github.com/aws/aws-sdk-net/issues/3171. As a result, we can only extract attributes given what is in
        // the response body. For the Claude, Command, and Mistral models, the input and output tokens are not provided
        // in the response body, so we approximate their values by dividing the input and output lengths by 6, based on
        // the Bedrock documentation here: https://docs.aws.amazon.com/bedrock/latest/userguide/model-customization-prepare.html

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
                        case "cohere.command":
                            ProcessCommandModelResponseAttributes(activity, jsonObject);
                            break;
                        case "ai21.jamba":
                            ProcessJambaModelResponseAttributes(activity, jsonObject);
                            break;
                        case "mistral.mistral":
                            ProcessMistralModelResponseAttributes(activity, jsonObject);
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

            // input tokens not provided in Claude response body, so we estimate the value based on input length
            if (jsonBody.TryGetValue("prompt", out var input))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiInputTokens, Math.Ceiling((double) input.GetString().Length / 6));
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
                activity.SetTag(AWSSemanticConventions.AttributeGenAiFinishReasons, new string[] { finishReasons.GetString() });
            }

            // output tokens not provided in Claude response body, so we estimate the value based on output length
            if (jsonBody.TryGetValue("completion", out var output))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiOutputTokens, Math.Ceiling((double) output.GetString().Length / 6));
            }
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessCommandModelRequestAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessCommandModelResponseAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessJambaModelRequestAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
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

            if (jsonBody.TryGetValue("max_tokens", out var maxTokens))
            {
                activity.SetTag(AWSSemanticConventions.AttributeGenAiMaxTokens, maxTokens.GetInt32());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessJambaModelResponseAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessMistralModelRequestAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }

    private static void ProcessMistralModelResponseAttributes(Activity activity, Dictionary<string, JsonElement> jsonBody)
    {
        try
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
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex.Message);
        }
    }
}
