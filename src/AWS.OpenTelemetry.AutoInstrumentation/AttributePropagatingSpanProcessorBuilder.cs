// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Diagnostics;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsAttributeKeys;
using static AWS.OpenTelemetry.AutoInstrumentation.AwsSpanProcessingUtil;

namespace AWS.OpenTelemetry.AutoInstrumentation;

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
        new ReadOnlyCollection<string>([AttributeAWSRemoteService, AttributeAWSRemoteOperation]);

    private AttributePropagatingSpanProcessorBuilder()
    {
    }

    public static AttributePropagatingSpanProcessorBuilder Create()
    {
        return new AttributePropagatingSpanProcessorBuilder();
    }

    public AttributePropagatingSpanProcessorBuilder SetPropagationDataExtractor(Func<Activity, string> propagationDataExtractor)
    {
        if (propagationDataExtractor == null)
        {
            throw new ArgumentNullException(nameof(propagationDataExtractor), "propagationDataExtractor must not be null");
        }

        this.propagationDataExtractor = propagationDataExtractor;
        return this;
    }

    public AttributePropagatingSpanProcessorBuilder SetPropagationDataKey(string propagationDataKey)
    {
        if (propagationDataKey == null)
        {
            throw new ArgumentNullException(nameof(propagationDataKey), "propagationDataKey must not be null");
        }

        this.propagationDataKey = propagationDataKey;
        return this;
    }

    public AttributePropagatingSpanProcessorBuilder SetAttributesKeysToPropagate(List<string> attributesKeysToPropagate)
    {
        if (attributesKeysToPropagate == null)
        {
            throw new ArgumentNullException(nameof(attributesKeysToPropagate), "propagationDataKey must not be null");
        }

        this.attributesKeysToPropagate = attributesKeysToPropagate.AsReadOnly();

        return this;
    }

    public AttributePropagatingSpanProcessor Build()
    {
        return AttributePropagatingSpanProcessor
            .Create(this.propagationDataExtractor, this.propagationDataKey, this.attributesKeysToPropagate);
    }
}
