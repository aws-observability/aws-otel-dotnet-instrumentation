// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// TODO: Add Description
/// </summary>
public class AwsMetricAttributesSpanExporterBuilder
{
    // Defaults
    private static readonly IMetricAttributeGenerator DefaultGenerator = new AwsMetricAttributeGenerator();

    // Required builder elements
    private readonly BaseExporter<Activity> exporterDelegate;
    private readonly Resource resource;

    // Optional builder elements
    private IMetricAttributeGenerator generator = DefaultGenerator;

    private AwsMetricAttributesSpanExporterBuilder(BaseExporter<Activity> exporterDelegate, Resource resource)
    {
        this.exporterDelegate = exporterDelegate;
        this.resource = resource;
    }

    public static AwsMetricAttributesSpanExporterBuilder Create(
        BaseExporter<Activity> exporterDelegate, Resource resource)
    {
        return new AwsMetricAttributesSpanExporterBuilder(exporterDelegate, resource);
    }

    /// <summary>
    /// Sets the generator used to generate attributes used spancs exported by the exporter. If unset,
    /// defaults to {@link #DEFAULT_GENERATOR}. Must not be null.
    /// </summary>
    /// <param name="generator"><see cref="IMetricAttributeGenerator"/>generator to set</param>
    /// <returns>Returns the builder</returns>
    public AwsMetricAttributesSpanExporterBuilder SetGenerator(IMetricAttributeGenerator generator)
    {
        this.generator = generator;
        return this;
    }

    public AwsMetricAttributesSpanExporter Build()
    {
        return AwsMetricAttributesSpanExporter.Create(this.exporterDelegate, this.generator, this.resource);
    }
}
