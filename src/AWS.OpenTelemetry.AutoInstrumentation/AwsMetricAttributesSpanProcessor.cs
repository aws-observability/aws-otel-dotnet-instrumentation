// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsMetricAttributesSpanProcessor is SpanProcessor that generates metrics from spans
/// This processor will generate metrics based on span data. It depends on a MetricAttributeGenerator being provided on
/// instantiation, which will provide a means to determine attributes which should be used to create metrics. A Resource
/// must also be provided, which is used to generate metrics.Finally, three Histogram must be provided, which will be
/// used to actually create desired metrics (see below)
///
/// AwsMetricAttributesSpanProcessor produces metrics for errors (e.g.HTTP 4XX status codes), faults(e.g.HTTP 5XX status
/// codes), and latency(in Milliseconds). Errors and faults are counted, while latency is measured with a histogram.
/// Metrics are emitted with attributes derived from span attributes.
///
/// For highest fidelity metrics, this processor should be coupled with the AlwaysRecordSampler, which will result in
/// 100% of spans being sent to the processor.
/// </summary>
public class AwsMetricAttributesSpanProcessor : BaseProcessor<Activity>
{
    private IMetricAttributeGenerator generator;
    private Resource resource;

    private AwsMetricAttributesSpanProcessor(
      IMetricAttributeGenerator generator,
      Resource resource)
    {
        this.generator = generator;
        this.resource = resource;
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnStart(Activity activity)
    {
    }

    /// <summary>
    /// Configure Resource Builder for Logs, Metrics and Traces
    /// </summary>
    /// <param name="activity"><see cref="Activity"/> to configure</param>
    public override void OnEnd(Activity activity)
    {
        this.AddMetricAttributes(activity);
    }

    /// <summary>
    /// Use <see cref="AwsMetricAttributesSpanProcessorBuilder"/> to construct this processor
    /// </summary>
    /// <returns>Configured AwsMetricAttributesSpanProcessor</returns>
    internal static AwsMetricAttributesSpanProcessor Create(
        IMetricAttributeGenerator generator,
        Resource resource)
    {
        return new AwsMetricAttributesSpanProcessor(generator, resource);
    }

    private static Activity WrapSpanWithAttributes(Activity span, ActivityTagsCollection attributes)
    {
        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            span.SetTag(attribute.Key, attribute.Value);
        }

        return span;
    }

    private Activity AddMetricAttributes(Activity span)
    {
        /// If the map has no items, no modifications are required. If there is one item, it means the
        /// span either produces Service or Dependency metric attributes, and in either case we want to
        /// modify the span with them. If there are two items, the span produces both Service and
        /// Dependency metric attributes indicating the span is a local dependency root. The Service
        /// Attributes must be a subset of the Dependency, with the exception of AttributeAWSSpanKind. The
        /// knowledge that the span is a local root is more important that knowing that it is a
        /// Dependency metric, so we take all the Dependency metrics but replace AttributeAWSSpanKind with
        /// <see cref="AwsSpanProcessingUtil.LocalRoot"/>.
        Dictionary<string, ActivityTagsCollection> attributeMap =
            this.generator.GenerateMetricAttributeMapFromSpan(span, this.resource);
        ActivityTagsCollection attributes = new ActivityTagsCollection();

        bool generatesServiceMetrics = AwsSpanProcessingUtil.ShouldGenerateServiceMetricAttributes(span);
        bool generatesDependencyMetrics = AwsSpanProcessingUtil.ShouldGenerateDependencyMetricAttributes(span);

        if (generatesServiceMetrics && generatesDependencyMetrics)
        {
            attributes = this.CopyAttributesWithLocalRoot(attributeMap[IMetricAttributeGenerator.DependencyMetric]);
        }
        else if (generatesServiceMetrics)
        {
            attributes = attributeMap[IMetricAttributeGenerator.ServiceMetric];
        }
        else if (generatesDependencyMetrics)
        {
            attributes = attributeMap[IMetricAttributeGenerator.DependencyMetric];
        }

        if (attributes.Count != 0)
        {
            Activity modifiedSpan = WrapSpanWithAttributes(span, attributes);
            return modifiedSpan;
        }
        else
        {
            return span;
        }
    }

    private ActivityTagsCollection CopyAttributesWithLocalRoot(ActivityTagsCollection attributes)
    {
        ActivityTagsCollection attributeCollection = new ActivityTagsCollection();
        attributeCollection.Concat(attributes);
        attributeCollection.Remove(AttributeAWSSpanKind);
        attributeCollection.Add(AttributeAWSSpanKind, AwsSpanProcessingUtil.LocalRoot);
        return attributeCollection;
    }
}
