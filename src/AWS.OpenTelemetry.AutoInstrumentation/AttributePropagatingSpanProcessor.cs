// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using OpenTelemetry;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AttributePropagatingSpanProcessor handles the propagation of attributes from parent spans to
/// child spans, specified in <see cref="attributesKeysToPropagate"/>. AttributePropagatingSpanProcessor
/// also propagates configurable data from parent spans to child spans, as a new attribute specified
/// by <see cref="propagationDataKey"/>. Propagated data can be configured via the
/// <see cref="propagationDataExtractor"/>. Span data propagation only starts from local root server/consumer
/// spans, but from there will be propagated to any descendant spans. If the span is a CONSUMER
/// PROCESS with the parent also a CONSUMER, it will set attribute AttributeAWSConsumerParentSpanKind as
/// CONSUMER to indicate that dependency metrics should not be generated for this span.
/// </summary>
public class AttributePropagatingSpanProcessor : BaseProcessor<Activity>
{
    private readonly Func<Activity, string> propagationDataExtractor;
    private readonly string propagationDataKey;
    private readonly ReadOnlyCollection<string> attributesKeysToPropagate;

    private AttributePropagatingSpanProcessor(
        Func<Activity, string> propagationDataExtractor,
        string propagationDataKey,
        ReadOnlyCollection<string> attributesKeysToPropagate)
    {
        this.propagationDataExtractor = propagationDataExtractor;
        this.propagationDataKey = propagationDataKey;
        this.attributesKeysToPropagate = attributesKeysToPropagate;
    }

    /// <summary>
    /// Creates a AttributePropagatingSpanProcessor
    /// </summary>
    /// <param name="propagationDataExtractor">propagationDataExtractor function</param>
    /// <param name="propagationDataKey">propagationDataKey string</param>
    /// <param name="attributesKeysToPropagate">attributesKeysToPropagate list</param>
    /// <returns>new AttributePropagatingSpanProcessor</returns>
    public static AttributePropagatingSpanProcessor Create(
      Func<Activity, string> propagationDataExtractor,
      string propagationDataKey,
      ReadOnlyCollection<string> attributesKeysToPropagate)
    {
        return new AttributePropagatingSpanProcessor(
            propagationDataExtractor, propagationDataKey, attributesKeysToPropagate);
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="span"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity span)
    {
        Activity? parentSpan = span.Parent;
        if (parentSpan != null)
        {
            // Add the AttributeAWSSdkDescendant attribute to the immediate child spans of AWS SDK span.
            // This attribute helps the backend differentiate between SDK spans and their immediate children.
            // It's assumed that the HTTP spans are immediate children of the AWS SDK span
            // TODO: we should have a contract test to check the immediate children are HTTP span
            if (IsAwsSDKSpan(parentSpan))
            {
                span.SetTag(AttributeAWSSdkDescendant, "true");
            }

            if (ActivityKind.Internal.Equals(parentSpan.Kind))
            {
                foreach (string keyToPropagate in this.attributesKeysToPropagate)
                {
                    string? valueToPropagate = (string?)parentSpan.GetTagItem(keyToPropagate);
                    if (valueToPropagate != null)
                    {
                        span.SetTag(keyToPropagate, valueToPropagate);
                    }
                }
            }

            // We cannot guarantee that messaging.operation is set onStart, it could be set after the
            // fact. To work around this, add the AttributeAWSConsumerParentSpanKind attribute if parent and
            // child are both CONSUMER then check later if a metric should be generated.
            if (IsConsumerKind(span) && IsConsumerKind(parentSpan))
            {
                span.SetTag(AttributeAWSConsumerParentSpanKind, parentSpan.Kind.GetType().Name);
            }
        }

        string? propagationData = null;
        if (IsLocalRoot(span))
        {
            if (!IsServerKind(span))
            {
                propagationData = this.propagationDataExtractor(span);
            }
        }
        else if (parentSpan != null && IsServerKind(parentSpan))
        {
            propagationData = this.propagationDataExtractor(parentSpan);
        }
        else
        {
            propagationData = parentSpan != null ? (string?)parentSpan.GetTagItem(this.propagationDataKey) : null;
        }

        if (propagationData != null)
        {
            span.SetTag(this.propagationDataKey, propagationData);
        }
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
    }

    private static bool IsConsumerKind(Activity span)
    {
        return ActivityKind.Consumer.Equals(span.Kind);
    }

    private static bool IsServerKind(Activity span)
    {
        return ActivityKind.Server.Equals(span.Kind);
    }
}
