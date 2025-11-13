// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.Distro.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AttributePropagatingSpanProcessorBuilder is used to construct a {@link
/// AttributePropagatingSpanProcessor}. If {@link #setPropagationDataExtractor}, {@link
/// #setPropagationDataKey} or {@link #setAttributesKeysToPropagate} are not invoked, the builder
/// defaults to using specific propagation targets.
/// </summary>
public class AttributePropagatingSpanProcessorBuilder
{
    private Func<Activity, string> propagationDataExtractor = GetIngressOperation;
    private string propagationDataKey = AttributeAWSLocalOperation;
    private ReadOnlyCollection<string> attributesKeysToPropagate =
        new ReadOnlyCollection<string>(new List<string> { AttributeAWSRemoteService, AttributeAWSRemoteOperation });

    private AttributePropagatingSpanProcessorBuilder()
    {
    }

    /// <summary>
    /// Creates a new AttributePropagatingSpanProcessorBuilder instance.
    /// </summary>
    /// <returns>A new builder instance.</returns>
    public static AttributePropagatingSpanProcessorBuilder Create()
    {
        return new AttributePropagatingSpanProcessorBuilder();
    }

    /// <summary>
    /// Sets the propagation data extractor function.
    /// </summary>
    /// <param name="propagationDataExtractor">Function to extract propagation data from activity.</param>
    /// <returns>This builder instance.</returns>
    public AttributePropagatingSpanProcessorBuilder SetPropagationDataExtractor(Func<Activity, string> propagationDataExtractor)
    {
        if (propagationDataExtractor == null)
        {
            throw new ArgumentNullException(nameof(propagationDataExtractor), "propagationDataExtractor must not be null");
        }

        this.propagationDataExtractor = propagationDataExtractor;
        return this;
    }

    /// <summary>
    /// Sets the propagation data key.
    /// </summary>
    /// <param name="propagationDataKey">The key for propagation data.</param>
    /// <returns>This builder instance.</returns>
    public AttributePropagatingSpanProcessorBuilder SetPropagationDataKey(string propagationDataKey)
    {
        if (propagationDataKey == null)
        {
            throw new ArgumentNullException(nameof(propagationDataKey), "propagationDataKey must not be null");
        }

        this.propagationDataKey = propagationDataKey;
        return this;
    }

    /// <summary>
    /// Sets the attribute keys to propagate.
    /// </summary>
    /// <param name="attributesKeysToPropagate">List of attribute keys to propagate.</param>
    /// <returns>This builder instance.</returns>
    public AttributePropagatingSpanProcessorBuilder SetAttributesKeysToPropagate(List<string> attributesKeysToPropagate)
    {
        if (attributesKeysToPropagate == null)
        {
            throw new ArgumentNullException(nameof(attributesKeysToPropagate), "propagationDataKey must not be null");
        }

        this.attributesKeysToPropagate = attributesKeysToPropagate.AsReadOnly();

        return this;
    }

    /// <summary>
    /// Builds the AttributePropagatingSpanProcessor.
    /// </summary>
    /// <returns>The configured processor.</returns>
    public AttributePropagatingSpanProcessor Build()
    {
        return AttributePropagatingSpanProcessor
            .Create(this.propagationDataExtractor, this.propagationDataKey, this.attributesKeysToPropagate);
    }
}
