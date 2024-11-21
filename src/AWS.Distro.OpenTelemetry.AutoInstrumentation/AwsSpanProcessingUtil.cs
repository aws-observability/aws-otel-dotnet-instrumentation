// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
#if NETFRAMEWORK
using System.Web;
#else
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
#endif
using Newtonsoft.Json.Linq;
using OpenTelemetry;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/** Utility class designed to support shared logic across AWS Span Processors. */
internal sealed class AwsSpanProcessingUtil
{
    // v1.21.0
    // https://github.com/open-telemetry/semantic-conventions/blob/v1.21.0/docs/http/http-metrics.md#http-server
    // TODO: Use TraceSemanticConventions once the below is officially released.
    public const string AttributeHttpRequestMethod = "http.request.method"; // replaces: "http.method" (AttributeHttpMethod)
    public const string AttributeHttpRequestMethodOriginal = "http.request.method_original";
    public const string AttributeHttpResponseStatusCode = "http.response.status_code"; // replaces: "http.status_code" (AttributeHttpStatusCode)
    public const string AttributeUrlScheme = "url.scheme"; // replaces: "http.scheme" (AttributeHttpScheme)
    public const string AttributeUrlFull = "url.full"; // replaces: "http.url" (AttributeHttpUrl)
    public const string AttributeUrlPath = "url.path"; // replaces: "http.target" (AttributeHttpTarget)

    // TODO: Check whether the query part of the url is included in the path or not.
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/f1fd71fdb60146be6399ecfd0dd90243e4c8cf1b/src/OpenTelemetry.Instrumentation.AspNet/Implementation/HttpInListener.cs#L94
    public const string AttributeUrlQuery = "url.query"; // replaces: "http.target" (AttributeHttpTarget)
    public const string AttributeServerSocketAddress = "server.socket.address"; // replaces: "net.peer.ip" (AttributeNetPeerIp)

    // Default attribute values if no valid span attribute value is identified
    internal static readonly string UnknownService = "UnknownService";
    internal static readonly string UnknownOperation = "UnknownOperation";
    internal static readonly string UnknownRemoteService = "UnknownRemoteService";
    internal static readonly string UnknownRemoteOperation = "UnknownRemoteOperation";
    internal static readonly string InternalOperation = "InternalOperation";
    internal static readonly string LocalRoot = "LOCAL_ROOT";
    internal static readonly string SqsReceiveMessageSpanName = "Sqs.ReceiveMessage";

    // This was gotten from the OpenTelemetry.Instrumentation.AWS. Might need to change after
    // AWS SDK auto instrumentation is developed.
    internal static readonly string ActivitySourceName = "Amazon.AWS.AWSClientInstrumentation";

    // This was copied over from
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/1d20bf70ebc6809f3e401d4cc5c72e8fe7f6581f/src/OpenTelemetry.Instrumentation.AWS/Implementation/AWSServiceType.cs#L8
    // This will be used to determine the ServiceName which is set using the attribute: AttributeAWSServiceName
    internal static readonly string DynamoDbService = "DynamoDB";
    internal static readonly string SQSService = "SQS";
    internal static readonly string SNSService = "SNS";

    // Max keyword length supported by parsing into remote_operation from DB_STATEMENT.
    // The current longest command word is DATETIME_INTERVAL_PRECISION at 27 characters.
    // If we add a longer keyword to the sql dialect keyword list, need to update the constant below.
    internal static readonly int MaxKeywordLength = 27;

    // TODO: remove once supported by Semantic Conventions
    internal static readonly string AttributeGenAiModelId = "gen_ai.request.model";
    internal static readonly string AttributeGenAiSystem = "gen_ai.system";

    internal static readonly string SqlDialectPattern = "^(?:" + string.Join("|", GetDialectKeywords()) + ")\\b";

    private static readonly string HttpRouteDataParsingEnabledConfig = "HTTP_ROUTE_DATA_PARSING_ENABLED";
    private static readonly string HttpRouteDataParsingEnabled = System.Environment.GetEnvironmentVariable(HttpRouteDataParsingEnabledConfig) ?? "false";

    internal static List<string> GetDialectKeywords()
    {
        try
        {
            string sqlDialectKeywordsJsonFullPath = "configuration/sql_dialect_keywords.json";
            string otelDotnetAutoHome = Environment.GetEnvironmentVariable("OTEL_DOTNET_AUTO_HOME") ?? string.Empty;
            if (!string.IsNullOrEmpty(otelDotnetAutoHome))
            {
                sqlDialectKeywordsJsonFullPath = Path.Combine(otelDotnetAutoHome, "configuration", "sql_dialect_keywords.json");
            }

            using (StreamReader r = new StreamReader(sqlDialectKeywordsJsonFullPath))
            {
                string json = r.ReadToEnd();
                JObject jObject = JObject.Parse(json);
                JArray? keywordArray = (JArray?)jObject["keywords"];
                if (keywordArray == null)
                {
                    return new List<string>();
                }

#pragma warning disable CS8619 // Nullability of reference types in value doesn't match target type.
                List<string> keywordList = keywordArray.Values<string>().ToList();
#pragma warning restore CS8619 // Nullability of reference types in value doesn't match target type.
                return keywordList;
            }
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    // Ingress operation (i.e. operation for Server and Consumer spans) will be generated from
    // "http.method + http.target/with the first API path parameter" if the default span name equals
    // null, UnknownOperation or http.method value.
    internal static string GetIngressOperation(Activity span)
    {
        if (ShouldUseInternalOperation(span))
        {
            return InternalOperation;
        }
        else if (span.GetCustomProperty("HttpContextWeakRef") != null)
        {
            return GetRouteTemplate(span);
        }

        return RouteFallback(span);
    }

    internal static string GetRouteTemplate(Activity span)
    {
        string operation = span.DisplayName;

        // Access the HttpContext object to get the route data.
        if (span.GetCustomProperty("HttpContextWeakRef") is WeakReference<HttpContext> httpContextWeakRef &&
            httpContextWeakRef.TryGetTarget(out var httpContext))
        {
#if !NETFRAMEWORK
            // This is copied from upstream to maintain the same retrieval logic
            // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AspNetCore/Implementation/HttpInListener.cs#L246C13-L247C83
            var routePattern = (httpContext.Features.Get<IExceptionHandlerPathFeature>()?.Endpoint as RouteEndpoint ??
                    httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
#else
            string? routePattern = GetHttpRouteData(httpContext);
#endif
            if (!string.IsNullOrEmpty(routePattern))
            {
                string? httpMethod = (string?)span.GetTagItem(AttributeHttpRequestMethod);
                operation = httpMethod + " " + routePattern;
            }
            else
            {
                operation = GenerateIngressOperation(span);
            }

            return operation;
        }
        else
        {
            return RouteFallback(span);
        }
    }

    internal static string RouteFallback(Activity span)
    {
        string operation = span.DisplayName;

        // workaround for now so that both Server and Consumer spans have same operation
        // TODO: Update this and other languages so that all of them set the operation during propagation.
        if (!IsValidOperation(span, operation) || (IsKeyPresent(span, AttributeUrlPath) && HttpRouteDataParsingEnabled == "false"))
        {
            operation = GenerateIngressOperation(span);
        }

        return operation;
    }

#if NETFRAMEWORK
    // Uses reflection to the get the HttpRequestRouteHelper.GetRouteTemplate to get the 
    // route template from NETFRAMEWORK applications.
    // https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AspNet/Implementation/HttpRequestRouteHelper.cs#L12
    internal static string? GetHttpRouteData(HttpContext httpContext)
    {
        Type? httpRouteHelper = Type.GetType("OpenTelemetry.Instrumentation.AspNet.Implementation.HttpRequestRouteHelper, OpenTelemetry.Instrumentation.AspNet");

        if (httpRouteHelper == null)
        {
            Console.WriteLine("HttpRequestRouteHelper Type was not found");
            return null;
        }

        // Create an instance of HttpRequestRouteHelper using the default parameterless constructor
        object? httpRouteHelperInstance = Activator.CreateInstance(httpRouteHelper);

        MethodInfo getRouteTemplateMethod = httpRouteHelper.GetMethod(
            "GetRouteTemplate",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new Type[] { typeof(HttpRequest) },
            modifiers: null);

        if (getRouteTemplateMethod == null)
        {
            Console.WriteLine("getRouteTemplateMethod was not found");
            return null;
        }

        return (string)getRouteTemplateMethod.Invoke(httpRouteHelperInstance, new object[] { httpContext.Request });
    }
#endif

    internal static string? GetEgressOperation(Activity span)
    {
        if (ShouldUseInternalOperation(span))
        {
            return InternalOperation;
        }
        else
        {
            return (string?)span.GetTagItem(AttributeAWSLocalOperation);
        }
    }

    /// <summary>
    /// Extract the first part from API http target if it exists
    /// </summary>
    /// <param name="httpTarget"><see cref="string"/>http request target string value. Eg, /payment/1234.</param>
    /// <returns>the first part from the http target. Eg, /payment.</returns>
    internal static string ExtractAPIPathValue(string httpTarget)
    {
        if (string.IsNullOrEmpty(httpTarget))
        {
            return "/";
        }

        string[] paths = httpTarget.Split('/');
        if (paths.Length > 1)
        {
            return "/" + paths[1];
        }

        return "/";
    }

    internal static bool IsKeyPresent(Activity span, string key)
    {
        return span.GetTagItem(key) != null;
    }

    internal static bool IsAwsSDKSpan(Activity span)
    {
        // https://opentelemetry.io/docs/specs/semconv/cloud-providers/aws-sdk/
        // TODO workaround for AWS SDK span
        return "aws-api".Equals((string?)span.GetTagItem(AttributeRpcSystem)) || ((string?)span.GetTagItem(AttributeAWSServiceName)) != null;
    }

    internal static bool IsDBSpan(Activity span)
    {
        return IsKeyPresent(span, AttributeDbSystem) || IsKeyPresent(span, AttributeDbOperation) ||
               IsKeyPresent(span, AttributeDbStatement);
    }

    internal static bool ShouldGenerateServiceMetricAttributes(Activity span)
    {
        return (IsLocalRoot(span) && !IsSqsReceiveMessageConsumerSpan(span))
            || ActivityKind.Server.Equals(span.Kind);
    }

    internal static bool ShouldGenerateDependencyMetricAttributes(Activity span)
    {
        return ActivityKind.Client.Equals(span.Kind)
            || ActivityKind.Producer.Equals(span.Kind)
            || (IsDependencyConsumerSpan(span) && !IsSqsReceiveMessageConsumerSpan(span));
    }

    internal static bool IsConsumerProcessSpan(Activity spanData)
    {
        string? messagingOperation = (string?)spanData.GetTagItem(AttributeMessagingOperation);
        return ActivityKind.Consumer.Equals(spanData.Kind) && MessagingOperationValues.Process.Equals(messagingOperation);
    }

    // Any spans that are Local Roots and also not SERVER should have aws.local.operation renamed to
    // InternalOperation.
    internal static bool ShouldUseInternalOperation(Activity span)
    {
        return IsLocalRoot(span) && !ActivityKind.Server.Equals(span.Kind);
    }

    // A span is a local root if it has no parent or if the parent is remote. This function checks the
    // parent context and returns true if it is a local root.
    internal static bool IsLocalRoot(Activity span)
    {
        return span.Parent == null || !span.Parent.Context.IsValid() || span.HasRemoteParent;
    }

    // To identify the SQS consumer spans produced by AWS SDK instrumentation
    // TODO: Verify this after AWS SDK AutoInstrumentation
    // Can also use this instead to check the service name
    // https://opentelemetry.io/docs/specs/semconv/cloud-providers/aws-sdk/
    private static bool IsSqsReceiveMessageConsumerSpan(Activity span)
    {
        string? messagingOperation = (string?)span.GetTagItem(AttributeMessagingOperation);

        ActivityKind spanKind = span.Kind;
        ActivitySource spanActivitySource = span.Source;

        string? serviceName = (string?)span.GetTagItem(AttributeAWSServiceName);

        return !string.IsNullOrEmpty(serviceName)
            && serviceName.Equals(SQSService)
            && ActivityKind.Consumer.Equals(spanKind)
            && spanActivitySource != null
            && spanActivitySource.Name.StartsWith(ActivitySourceName)
            && (messagingOperation == null || messagingOperation.Equals(MessagingOperationValues.Process));
    }

    private static bool IsDependencyConsumerSpan(Activity span)
    {
        if (!ActivityKind.Consumer.Equals(span.Kind))
        {
            return false;
        }
        else if (IsConsumerProcessSpan(span))
        {
            if (IsLocalRoot(span))
            {
                return true;
            }

            object? parentSpanKind = span.GetTagItem(AttributeAWSConsumerParentSpanKind);
            return !ActivityKind.Consumer.ToString().Equals((string?)parentSpanKind);
        }

        return true;
    }

    // When Span name is null, UnknownOperation or HttpMethod value, it will be treated as invalid
    // local operation value that needs to be further processed
    private static bool IsValidOperation(Activity span, string operation)
    {
        if (string.IsNullOrEmpty(operation))
        {
            return false;
        }

        if (IsKeyPresent(span, AttributeHttpRequestMethod))
        {
            object? httpMethod = span.GetTagItem(AttributeHttpRequestMethod);
            return !operation.Equals((string?)httpMethod);
        }

        return true;
    }

    // When span name is not meaningful(null, unknown or http_method value) as operation name for http
    // use cases. Will try to extract the operation name from http target string
    private static string GenerateIngressOperation(Activity span)
    {
        string operation = UnknownOperation;
        if (IsKeyPresent(span, AttributeUrlPath))
        {
            object? httpTarget = span.GetTagItem(AttributeUrlPath);

            // get the first part from API path string as operation value
            // the more levels/parts we get from API path the higher chance for getting high cardinality
            // data
            if (httpTarget != null)
            {
                operation = ExtractAPIPathValue((string)httpTarget);
                if (IsKeyPresent(span, AttributeHttpRequestMethod))
                {
                    string? httpMethod = (string?)span.GetTagItem(AttributeHttpRequestMethod);
                    if (httpMethod != null)
                    {
                        operation = httpMethod + " " + operation;
                    }
                }
            }
        }

        return operation;
    }
}
