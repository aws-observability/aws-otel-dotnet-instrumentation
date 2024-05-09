// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Resources;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// A builder for <see cref="AwsSpanMetricsProcessor"/>
/// </summary>
public class AwsSpanMetricsProcessorBuilder
{
    private AwsSpanMetricsProcessorBuilder(Resource resource)
    {
        // TODO: implement this
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
        // TODO: implement this
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
        // TODO: implement this
        return this;
    }

    /// <summary>
    /// Sets the scope name used in the creation of metrics by the span metrics processor. If unset,
    /// defaults to <see cref="DefaultScopeName"/>. Must not be null.
    /// </summary>
    /// <returns>Returns this instance of the builder</returns>
    public AwsSpanMetricsProcessor Build()
    {
        // TODO: implement this
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the scopeName that will be used to register a meter
    /// </summary>
    /// <returns>Returns this scope name set in the meter</returns>
    public string GetScopeName()
    {
        // TODO: implement this
        return string.Empty;
    }
}
