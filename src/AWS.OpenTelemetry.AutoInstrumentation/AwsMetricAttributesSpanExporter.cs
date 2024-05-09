// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// This exporter will update a span with metric attributes before exporting. It depends on a {@link
/// SpanExporter} being provided on instantiation, which the AwsSpanMetricsExporter will delegate
/// export to. Also, a {@link MetricAttributeGenerator} must be provided, which will provide a means
/// to determine attributes which should be applied to the span. Finally, a {@link Resource} must be
/// provided, which is used to generate metric attributes.
///
/// <p>This exporter should be coupled with the {@link AwsSpanMetricsProcessor} using the same {@link
/// MetricAttributeGenerator}. This will result in metrics and spans being produced with common
/// attributes.
/// </summary>
public class AwsMetricAttributesSpanExporter : BaseExporter<Activity>
{
    private AwsMetricAttributesSpanExporter(BaseExporter<Activity> exporterDelegate, IMetricAttributeGenerator generator, Resource resource)
    {
        // TODO: implement this
    }

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        throw new NotImplementedException();
    }

    /// Use <see cref="AwsMetricAttributesSpanExporterBuilder"/> to construct this exporter.
    internal static AwsMetricAttributesSpanExporter Create(
        BaseExporter<Activity> exporterDelegate, IMetricAttributeGenerator generator, Resource resource)
    {
        return new AwsMetricAttributesSpanExporter(exporterDelegate, generator, resource);
    }
}
