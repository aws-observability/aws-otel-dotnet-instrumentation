// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsMetricAttributeGenerator generates very specific metric attributes based on low-cardinality
/// span and resource attributes. If such attributes are not present, we fallback to default values.
/// <p>The goal of these particular metric attributes is to get metrics for incoming and outgoing
/// traffic for a service. Namely, {@link SpanKind#SERVER} and {@link SpanKind#CONSUMER} spans
/// represent "incoming" traffic, {@link SpanKind#CLIENT} and {@link SpanKind#PRODUCER} spans
/// represent "outgoing" traffic, and {@link SpanKind#INTERNAL} spans are ignored.
/// </summary>
internal sealed class AwsMetricAttributeGenerator : IMetricAttributeGenerator
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddConsole());
    private static readonly ILogger Logger = Factory.CreateLogger<AwsMetricAttributeGenerator>();

    // Special DEPENDENCY attribute value if GRAPHQL_OPERATION_TYPE attribute key is present.
    private static readonly string GraphQL = "graphql";

    // As per
    // https://github.com/open-telemetry/opentelemetry-java/tree/main/sdk-extensions/autoconfigure#opentelemetry-resource
    // If service name is not specified, SDK defaults the service name to unknown_service
    private static readonly string OtelUnknownService = "unknown_service";

    /// <inheritdoc/>
    public Dictionary<string, ActivityTagsCollection> GenerateMetricAttributeMapFromSpan(TelemetrySpan span, Resource resource)
    {
        throw new NotImplementedException();
    }

    private static string GetDBStatementRemoteOperation(
      Activity span, string remoteOperationKey)
    {
        string remoteOperation;
        object? remoteOperationObject = span.GetTagItem(remoteOperationKey);
        if (remoteOperationObject == null)
        {
            remoteOperation = "Unknown";
        }
        else
        {
            remoteOperation = (string)remoteOperationObject;
        }

        // Remove all whitespace and newline characters from the beginning of remote_operation
        // and retrieve the first MAX_KEYWORD_LENGTH characters
        remoteOperation = remoteOperation.TrimStart();
        if (remoteOperation.Length > MaxKeywordLength)
        {
            remoteOperation = remoteOperation.Substring(0, MaxKeywordLength);
        }

        Regex regex = new Regex(SqlDialectPattern);
        Match match = regex.Match(remoteOperation.ToUpper());
        if (match.Success && !string.IsNullOrEmpty(match.Value))
        {
            remoteOperation = match.Value;
        }
        else
        {
            remoteOperation = UnknownRemoteOperation;
        }

        return remoteOperation;
    }

    private static void LogUnknownAttribute(string attributeKey, Activity span)
    {
        string[] logParams = { attributeKey, span.Kind.GetType().Name, span.Context.SpanId.ToString() };
        Logger.Log(LogLevel.Trace, "No valid {0} value found for {1} span {2}", logParams);
    }
}
