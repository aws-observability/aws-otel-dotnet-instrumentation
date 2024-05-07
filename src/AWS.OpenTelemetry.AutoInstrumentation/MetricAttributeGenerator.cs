// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Metric attribute generator defines an interface for classes that can generate specific attributes
/// to be used by an {@link AwsSpanMetricsProcessor} to produce metrics and by {@link
/// AwsMetricAttributesSpanExporter} to wrap the original span.
/// </summary>
public interface IMetricAttributeGenerator
{
    const string ServiceMetric = "Service";
    const string DependencyMetric = "Dependency";

    /// <summary>
    /// Given a span and associated resource, produce meaningful metric attributes for metrics produced
    /// from the span. If no metrics should be generated from this span, return empty attributes map.
    /// </summary>
    /// <param name="span"><see cref="TelemetrySpan"/>SpanData to be used to generate metric attributes.</param>
    /// <param name="resource"><see cref="Resource"/>Resource associated with Span to be used to generate metric attributes.</param>
    /// <returns>A map of Attributes (Activity Tags) with values assigned to key "Service" or "Dependency". It will contain either 0, 1, or 2 items.</returns>
    Dictionary<string, ActivityTagsCollection> GenerateMetricAttributeMapFromSpan(TelemetrySpan span, Resource resource);
}
