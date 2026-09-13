// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Tracing;

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Implementation;

[EventSource(Name = "OpenTelemetry-AWS-CloudWatchPluginOtel")]
internal sealed class CloudWatchPluginEventSource : EventSource
{
    public static readonly CloudWatchPluginEventSource Log = new();

    [NonEvent]
    public void SpanProcessingException(string callback, Exception exception)
    {
        if (this.IsEnabled(EventLevel.Error, EventKeywords.All))
        {
            this.SpanProcessingException(callback, exception.ToString());
        }
    }

    [Event(
        1,
        Message = "An exception occurred in the span metrics {0} callback: {1}",
        Level = EventLevel.Error)]
    public void SpanProcessingException(string callback, string exception)
    {
        this.WriteEvent(1, callback, exception);
    }

    [Event(
        2,
        Message = "Unsupported OTEL_TRACES_SAMPLER value '{0}'. CloudWatch span metrics were not enabled.",
        Level = EventLevel.Error)]
    public void UnsupportedSamplerConfiguration(string samplerName)
    {
        this.WriteEvent(2, samplerName);
    }
}
