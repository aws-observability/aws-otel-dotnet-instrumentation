// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static OpenTelemetry.Trace.TraceSemanticConventions;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// This exporter will update a span with metric attributes before exporting. It depends on a
/// <see cref="BaseExporter<Activity>"/> being provided on instantiation, which the AwsSpanMetricsExporter will delegate
/// export to. Also, a <see cref="IMetricAttributeGenerator"/> must be provided, which will provide a means
/// to determine attributes which should be applied to the span. Finally, a <see cref="Resource"/> must be
/// provided, which is used to generate metric attributes.
///
/// <p>This exporter should be coupled with the <see cref="AwsSpanMetricsProcessor"/> using the same {@link
/// MetricAttributeGenerator}. This will result in metrics and spans being produced with common
/// attributes.
/// </summary>
public class AwsMetricAttributesSpanExporter : BaseExporter<Activity>
{
    private readonly BaseExporter<Activity> exporterDelegate;
    private readonly IMetricAttributeGenerator generator;
    private readonly Resource resource;

    private AwsMetricAttributesSpanExporter(BaseExporter<Activity> exporterDelegate, IMetricAttributeGenerator generator, Resource resource)
    {
        this.exporterDelegate = exporterDelegate;
        this.generator = generator;
        this.resource = resource;
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        Batch<Activity> modifiedSpans = this.AddMetricAttributes(batch);
        return this.exporterDelegate.Export(modifiedSpans);
    }

    /// Use <see cref="AwsMetricAttributesSpanExporterBuilder"/> to construct this exporter.
    internal static AwsMetricAttributesSpanExporter Create(
        BaseExporter<Activity> exporterDelegate, IMetricAttributeGenerator generator, Resource resource)
    {
        return new AwsMetricAttributesSpanExporter(exporterDelegate, generator, resource);
    }

    /// <inheritdoc/>
    protected override bool OnShutdown(int timeoutMilliseconds = -1)
    {
        return this.exporterDelegate.Shutdown(timeoutMilliseconds);
    }

    /// <inheritdoc/>
    protected override bool OnForceFlush(int timeoutMilliseconds = -1)
    {
        return this.exporterDelegate.ForceFlush(timeoutMilliseconds);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        this.exporterDelegate.Dispose();
    }

    private static Activity WrapSpanWithAttributes(Activity span, ActivityTagsCollection attributes)
    {
        foreach (KeyValuePair<string, object?> attribute in attributes)
        {
            span.SetTag(attribute.Key, attribute.Value);
        }

        return span;
    }

    private Batch<Activity> AddMetricAttributes(Batch<Activity> batch)
    {
        List<Activity> modifiedSpans = new List<Activity>();
        foreach (Activity span in batch)
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
                modifiedSpans.Add(modifiedSpan);
            }
            else
            {
                modifiedSpans.Add(span);
            }
        }

        return new Batch<Activity>(modifiedSpans.ToArray(), modifiedSpans.Count);
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
