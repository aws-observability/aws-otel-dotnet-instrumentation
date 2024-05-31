// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using OpenTelemetry.Resources;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// A builder for <see cref="AwsMetricAttributesSpanProcessor"/>
/// </summary>
public class AwsMetricAttributesSpanProcessorBuilder
{
    // Defaults
    private static readonly IMetricAttributeGenerator DefaultGenerator = new AwsMetricAttributeGenerator();

    private readonly Resource resource;

    // Optional builder elements
    private IMetricAttributeGenerator generator = DefaultGenerator;

    private AwsMetricAttributesSpanProcessorBuilder(Resource resource)
    {
        this.resource = resource;
    }

    /// <summary>
    /// Configure new AwsMetricAttributesSpanProcessorBuilder
    /// </summary>
    /// <param name="resource"><see cref="Resource"/>Resource to store</param>
    /// <returns>New AwsMetricAttributesSpanProcessorBuilder</returns>
    public static AwsMetricAttributesSpanProcessorBuilder Create(Resource resource)
    {
        return new AwsMetricAttributesSpanProcessorBuilder(resource);
    }

    /// <summary>
    /// Sets the generator used to generate attributes used in metrics produced by span metrics
    /// processor. If unset, defaults to <see cref="DefaultGenerator"/>. Must not be null.
    /// </summary>
    /// <param name="generator"><see cref="IMetricAttributeGenerator"/>generator to store</param>
    /// <returns>Returns this instance of the builder</returns>
    public AwsMetricAttributesSpanProcessorBuilder SetGenerator(IMetricAttributeGenerator generator)
    {
        if (generator == null)
        {
            throw new ArgumentNullException("generator must not be null", nameof(generator));
        }

        this.generator = generator;
        return this;
    }

    /// <summary>
    /// Creates an instance of AwsMetricAttributesSpanProcessor
    /// </summary>
    /// <returns>Returns AwsMetricAttributesSpanProcessor</returns>
    public AwsMetricAttributesSpanProcessor Build()
    {
        return AwsMetricAttributesSpanProcessor.Create(this.generator, this.resource);
    }
}
