// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using OpenTelemetry.Resources;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// A builder for <see cref="AwsSpanMetricsProcessor"/>
/// </summary>
public class AwsSpanMetricsProcessorBuilder
{
    // Metric instrument configuration constants
    private static readonly string Error = "Error";
    private static readonly string Fault = "Fault";
    private static readonly string Latency = "Latency";
    private static readonly string LatencyUnits = "Milliseconds";

    // Defaults
    private static readonly IMetricAttributeGenerator DefaultGenerator = new AwsMetricAttributeGenerator();

    // ActivitySource.Name?
    private static readonly string DefaultScopeName = "AwsSpanMetricsProcessor";
    private readonly Resource resource;

    // Optional builder elements
    private IMetricAttributeGenerator generator = DefaultGenerator;
    private string scopeName = DefaultScopeName;

    private AwsSpanMetricsProcessorBuilder(Resource resource)
    {
        this.resource = resource;
    }

    /// <summary>
    /// Configure new AwsSpanMetricsProcessorBuilder
    /// </summary>
    /// <param name="resource"><see cref="Resource"/>Resource to store</param>
    /// <returns>New AwsSpanMetricsProcessorBuilder</returns>
    public static AwsSpanMetricsProcessorBuilder Create(Resource resource)
    {
        return new AwsSpanMetricsProcessorBuilder(resource);
    }

    /// <summary>
    /// Sets the generator used to generate attributes used in metrics produced by span metrics
    /// processor. If unset, defaults to <see cref="DefaultGenerator"/>. Must not be null.
    /// </summary>
    /// <param name="generator"><see cref="IMetricAttributeGenerator"/>generator to store</param>
    /// <returns>Returns this instance of the builder</returns>
    public AwsSpanMetricsProcessorBuilder SetGenerator(IMetricAttributeGenerator generator)
    {
        if (generator == null)
        {
            throw new ArgumentNullException("generator must not be null", nameof(generator));
        }

        this.generator = generator;
        return this;
    }

    /// <summary>
    /// Sets the scope name used in the creation of metrics by the span metrics processor. If unset,
    /// defaults to <see cref="DefaultScopeName"/>. Must not be null.
    /// </summary>
    /// <param name="scopeName"><see cref="string"/>scopeName to store</param>
    /// <returns>Returns this instance of the builder</returns>
    public AwsSpanMetricsProcessorBuilder SetScopeName(string scopeName)
    {
        if (scopeName == null)
        {
            throw new ArgumentNullException("scopeName must not be null", nameof(scopeName));
        }

        this.scopeName = scopeName;
        return this;
    }

    /// <summary>
    /// Creates AwsSpanMetricsProcessor with Histograms subscribed the meter with this.scopeName
    /// </summary>
    /// <returns>Returns AwsSpanMetricsProcessor</returns>
    public AwsSpanMetricsProcessor Build()
    {
        Meter meter = new Meter(this.scopeName);
        Histogram<long> errorHistogram = meter.CreateHistogram<long>(Error);
        Histogram<long> faultHistogram = meter.CreateHistogram<long>(Fault);
        Histogram<double> latencyHistogram = meter.CreateHistogram<double>(Latency, LatencyUnits);
        return AwsSpanMetricsProcessor.Create(
            errorHistogram, faultHistogram, latencyHistogram, this.generator, this.resource);
    }

    /// <summary>
    /// Returns the scopeName that will be used to register a meter
    /// </summary>
    /// <returns>Returns this scope name set in the meter</returns>
    public string GetScopeName()
    {
        return this.scopeName;
    }
}
