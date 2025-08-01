// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Util;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Internal;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.AWS.Implementation;

/// <summary>
/// Wraps the outgoing AWS SDK Request in a Span and adds additional AWS specific Tags.
/// Depending on the target AWS Service, additional request specific information may be injected as well.
/// <para />
/// This <see cref="PipelineHandler"/> must execute early in the AWS SDK pipeline
/// in order to manipulate outgoing requests objects before they are marshalled (ie serialized).
/// </summary>
internal sealed class AWSTracingPipelineHandler : PipelineHandler
{
    internal const string ActivitySourceName = "Amazon.AWS.AWSClientInstrumentation";

    private static readonly ActivitySource AWSSDKActivitySource = new(ActivitySourceName, typeof(AWSTracingPipelineHandler).Assembly.GetPackageVersion());

    private readonly AWSClientInstrumentationOptions options;

    public AWSTracingPipelineHandler(AWSClientInstrumentationOptions options)
    {
        this.options = options;
    }

    public Activity? Activity { get; private set; }

    public override void InvokeSync(IExecutionContext executionContext)
    {
        this.Activity = this.ProcessBeginRequest(executionContext);
        try
        {
            base.InvokeSync(executionContext);
        }
        catch (Exception ex)
        {
            if (this.Activity != null)
            {
                ProcessException(this.Activity, ex);
            }

            throw;
        }
        finally
        {
            if (this.Activity != null)
            {
                ProcessEndRequest(executionContext, this.Activity);
            }
        }
    }

    public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        T? ret = null;

        this.Activity = this.ProcessBeginRequest(executionContext);
        try
        {
            ret = await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (this.Activity != null)
            {
                ProcessException(this.Activity, ex);
            }

            throw;
        }
        finally
        {
            if (this.Activity != null)
            {
                ProcessEndRequest(executionContext, this.Activity);
            }
        }

        return ret;
    }

    private static void ProcessEndRequest(IExecutionContext executionContext, Activity activity)
    {
        var responseContext = executionContext.ResponseContext;
        var requestContext = executionContext.RequestContext;
        var service = AWSServiceHelper.GetAWSServiceName(requestContext);

        if (activity.IsAllDataRequested)
        {
            if (Utils.GetTagValue(activity, AWSSemanticConventions.AttributeAWSRequestId) == null)
            {
                activity.SetTag(AWSSemanticConventions.AttributeAWSRequestId, FetchRequestId(requestContext, responseContext));
            }

            var httpResponse = responseContext.HttpResponse;
            if (httpResponse != null)
            {
                int statusCode = (int)httpResponse.StatusCode;

                string? accessKey = requestContext.ImmutableCredentials.AccessKey;
                string? determinedSigningRegion = requestContext.Request.DeterminedSigningRegion;
                if (accessKey != null && determinedSigningRegion != null)
                {
                    activity.SetTag(AWSSemanticConventions.AttributeAWSAuthAccessKey, accessKey);
                    activity.SetTag(AWSSemanticConventions.AttributeAWSAuthRegion, determinedSigningRegion);
                }

                AddStatusCodeToActivity(activity, statusCode);
                activity.SetTag(AWSSemanticConventions.AttributeHttpResponseContentLength, httpResponse.ContentLength);

                AddResponseSpecificInformation(activity, responseContext, service);
            }
        }

        activity.Stop();
    }

    private static void ProcessException(Activity activity, Exception ex)
    {
        if (activity.IsAllDataRequested)
        {
            activity.RecordException(ex);

            activity.SetStatus(Status.Error.WithDescription(ex.Message));

            if (ex is AmazonServiceException amazonServiceException)
            {
                AddStatusCodeToActivity(activity, (int)amazonServiceException.StatusCode);
                activity.SetTag(AWSSemanticConventions.AttributeAWSRequestId, amazonServiceException.RequestId);
            }
        }
    }

#if NET
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "The reflected properties were already used by the AWS SDK's marshallers so the properties could not have been trimmed.")]
#endif
    private static void AddRequestSpecificInformation(Activity activity, IRequestContext requestContext, string service)
    {
        if (AWSServiceHelper.ServiceRequestParameterMap.TryGetValue(service, out var parameters))
        {
            AmazonWebServiceRequest request = requestContext.OriginalRequest;

            foreach (var parameter in parameters)
            {
                try
                {
                    var property = request.GetType().GetProperty(parameter);
                    if (property != null)
                    {
                        // for bedrock runtime, LLM specific attributes are extracted based on the model ID.
                        if (AWSServiceType.IsBedrockRuntimeService(service) && parameter == "ModelId")
                        {
                            var model = property.GetValue(request);
                            if (model != null)
                            {
                                var modelString = model.ToString();
                                if (modelString != null)
                                {
                                    AWSLlmModelProcessor.ProcessGenAiAttributes(activity, request, modelString, true);
                                }
                            }
                        }

                        // for secrets manager, only extract SecretId from request if it is a secret ARN.
                        if (AWSServiceType.IsSecretsManagerService(service) && parameter == "SecretId")
                        {
                            var secretId = property.GetValue(request);
                            if (secretId != null)
                            {
                                var secretIdString = secretId.ToString();
                                if (secretIdString != null && !secretIdString.StartsWith("arn:aws:secretsmanager:"))
                                {
                                    continue;
                                }
                            }
                        }

                        // for Lambda, FunctionName can be passed as arn, partial arn, or name. Standardize to name.
                        if (AWSServiceType.IsLambdaService(service) && parameter == "FunctionName")
                        {
                            var functionName = property.GetValue(request);
                            if (functionName != null)
                            {
                                var functionNameString = functionName.ToString();
                                if (functionNameString != null)
                                {
                                    string[] parts = functionNameString.Split(':');
                                    functionNameString = parts.Length > 0 ? parts[parts.Length - 1] : null;
                                    activity.SetTag(AWSSemanticConventions.AttributeAWSLambdaFunctionName, functionNameString);
                                    continue;
                                }
                            }
                        }

                        if (AWSServiceHelper.ParameterAttributeMap.TryGetValue(parameter, out var attribute))
                        {
                            activity.SetTag(attribute, property.GetValue(request));
                        }
                    }
                }
                catch (Exception)
                {
                    // Guard against any reflection-related exceptions when running in AoT.
                    // See https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/1543#issuecomment-1907667722.
                }
            }
        }

        if (AWSServiceType.IsDynamoDbService(service))
        {
            activity.SetTag(SemanticConventions.AttributeDbSystem, AWSSemanticConventions.AttributeValueDynamoDb);
        }
        else if (AWSServiceType.IsSqsService(service))
        {
            SqsRequestContextHelper.AddAttributes(
                requestContext, AWSMessagingUtils.InjectIntoDictionary(new PropagationContext(activity.Context, Baggage.Current)));
        }
        else if (AWSServiceType.IsSnsService(service))
        {
            SnsRequestContextHelper.AddAttributes(
                requestContext, AWSMessagingUtils.InjectIntoDictionary(new PropagationContext(activity.Context, Baggage.Current)));
        }
        else if (AWSServiceType.IsBedrockRuntimeService(service))
        {
            activity.SetTag(AWSSemanticConventions.AttributeGenAiSystem, "aws.bedrock");
        }
    }

    private static void AddResponseSpecificInformation(Activity activity, IResponseContext responseContext, string service)
    {
        AmazonWebServiceResponse response = responseContext.Response;
        if (AWSServiceHelper.ServiceResponseParameterMap.TryGetValue(service, out var parameters))
        {
            foreach (var parameter in parameters)
            {
                try
                {
                    var property = response.GetType().GetProperty(parameter);
                    if (property != null)
                    {
                        if (AWSServiceHelper.ParameterAttributeMap.TryGetValue(parameter, out var attribute))
                        {
                            activity.SetTag(attribute, property.GetValue(response));
                        }
                    }
                }
                catch (Exception)
                {
                    // Guard against any reflection-related exceptions when running in AoT.
                    // See https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/1543#issuecomment-1907667722.
                }
            }
        }
        // for bedrock runtime, LLM specific attributes are extracted based on the model ID.
        if (AWSServiceType.IsBedrockRuntimeService(service))
        {
            var model = activity.GetTagItem(AWSSemanticConventions.AttributeGenAiModelId);
            if (model != null)
            {
                var modelString = model.ToString();
                if (modelString != null)
                {
                    AWSLlmModelProcessor.ProcessGenAiAttributes(activity, responseContext.Response, modelString, false);
                }
            }
        }
        // for Lambda, extract function ARN from response Configuration object.
        if (AWSServiceType.IsLambdaService(service))
        {
            var configuration = response.GetType().GetProperty("Configuration");
            if (configuration != null)
            {
                var configObject = configuration.GetValue(response);
                if (configObject != null)
                {
                    var functionArn = configObject.GetType().GetProperty("FunctionArn");
                    if (functionArn != null)
                    {
                        activity.SetTag(AWSSemanticConventions.AttributeAWSLambdaFunctionArn, functionArn.GetValue(configObject));
                    }
                }
            }
        }

        // for DynamoDb, extract table ARN from response Table object. 
        if (AWSServiceType.IsDynamoDbService(service))
        {
            AddDynamoTableArnAttribute(activity, response);
        }
    }

    private static void AddDynamoTableArnAttribute(Activity activity, AmazonWebServiceResponse response)
    {
        var responseObject = response.GetType().GetProperty("Table");
        if (responseObject != null)
        {
            var tableObject = responseObject.GetValue(response);
            if (tableObject != null)
            {
                var property = tableObject.GetType().GetProperty("TableArn");
                if (property != null)
                {
                    if (AWSServiceHelper.ParameterAttributeMap.TryGetValue("TableArn", out var attribute))
                    {
                        activity.SetTag(attribute, property.GetValue(tableObject));
                    }
                }
            }
        }
    }

    private static void AddBedrockAgentResponseAttribute(Activity activity, AmazonWebServiceResponse response, string parameter)
    {
        var responseObject = response.GetType().GetProperty(Utils.RemoveSuffix(parameter, "Id"));
        if (responseObject != null)
        {
            var attributeObject = responseObject.GetValue(response);
            if (attributeObject != null)
            {
                var property = attributeObject.GetType().GetProperty(parameter);
                if (property != null)
                {
                    if (AWSServiceHelper.ParameterAttributeMap.TryGetValue(parameter, out var attribute))
                    {
                        activity.SetTag(attribute, property.GetValue(attributeObject));
                    }
                }
            }
        }
    }

    private static void AddStatusCodeToActivity(Activity activity, int status_code)
    {
        activity.SetTag(AWSSemanticConventions.AttributeHttpStatusCode, status_code);
    }

    private static string FetchRequestId(IRequestContext requestContext, IResponseContext responseContext)
    {
        string request_id = string.Empty;
        var response = responseContext.Response;
        if (response != null)
        {
            request_id = response.ResponseMetadata.RequestId;
        }
        else
        {
            var request_headers = requestContext.Request.Headers;
            if (string.IsNullOrEmpty(request_id) && request_headers.TryGetValue("x-amzn-RequestId", out var req_id))
            {
                request_id = req_id;
            }

            if (string.IsNullOrEmpty(request_id) && request_headers.TryGetValue("x-amz-request-id", out req_id))
            {
                request_id = req_id;
            }

            if (string.IsNullOrEmpty(request_id) && request_headers.TryGetValue("x-amz-id-2", out req_id))
            {
                request_id = req_id;
            }
        }

        return request_id;
    }

    private Activity? ProcessBeginRequest(IExecutionContext executionContext)
    {
        var requestContext = executionContext.RequestContext;
        var service = AWSServiceHelper.GetAWSServiceName(requestContext);
        var operation = AWSServiceHelper.GetAWSOperationName(requestContext);

        Activity? activity = AWSSDKActivitySource.StartActivity(service + "." + operation, ActivityKind.Client);

        if (activity == null)
        {
            return null;
        }

        if (this.options.SuppressDownstreamInstrumentation)
        {
            SuppressInstrumentationScope.Enter();
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag(AWSSemanticConventions.AttributeAWSServiceName, service);
            activity.SetTag(AWSSemanticConventions.AttributeAWSOperationName, operation);

            // Follow: https://github.com/open-telemetry/semantic-conventions/blob/v1.26.0/docs/cloud-providers/aws-sdk.md#common-attributes
            activity.SetTag(AWSSemanticConventions.AttributeValueRPCSystem, "aws-api");
            activity.SetTag(AWSSemanticConventions.AttributeValueRPCService, service);
            activity.SetTag(AWSSemanticConventions.AttributeValueRPCMethod, operation);
            var client = executionContext.RequestContext.ClientConfig;
            if (client != null)
            {
                var region = client.RegionEndpoint?.SystemName;
                activity.SetTag(AWSSemanticConventions.AttributeAWSRegion, region ?? AWSSDKUtils.DetermineRegion(client.ServiceURL));
            }

            AddRequestSpecificInformation(activity, requestContext, service);
        }

        return activity;
    }
}
