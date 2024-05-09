// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AttributePropagatingSpanProcessor handles the propagation of attributes from parent spans to
/// child spans, specified in {@link #attributesKeysToPropagate}. AttributePropagatingSpanProcessor
/// also propagates configurable data from parent spans to child spans, as a new attribute specified
/// by {@link #propagationDataKey}. Propagated data can be configured via the {@link
/// #propagationDataExtractor}. Span data propagation only starts from local root server/consumer
/// spans, but from there will be propagated to any descendant spans. If the span is a CONSUMER
/// PROCESS with the parent also a CONSUMER, it will set attribute AWS_CONSUMER_PARENT_SPAN_KIND as
/// CONSUMER to indicate that dependency metrics should not be generated for this span.
/// </summary>
public class AttributePropagatingSpanProcessor : BaseProcessor<Activity>
{
    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
        // TODO: Implement this function
        Console.WriteLine($"OnStart: {activity.DisplayName}");
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
        // TODO: Implement this function
        Console.WriteLine($"OnEnd: {activity.DisplayName}");
    }
}
