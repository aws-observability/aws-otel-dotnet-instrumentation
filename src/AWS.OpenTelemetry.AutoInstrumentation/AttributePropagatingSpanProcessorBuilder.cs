// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AttributePropagatingSpanProcessorBuilder is used to construct a {@link
/// AttributePropagatingSpanProcessor}. If {@link #setPropagationDataExtractor}, {@link
/// #setPropagationDataKey} or {@link #setAttributesKeysToPropagate} are not invoked, the builder
/// defaults to using specific propagation targets.
/// </summary>
public class AttributePropagatingSpanProcessorBuilder
{
    private AttributePropagatingSpanProcessorBuilder()
    {
    }

    public static AttributePropagatingSpanProcessorBuilder Create()
    {
        return new AttributePropagatingSpanProcessorBuilder();
    }

    public AttributePropagatingSpanProcessorBuilder SetPropagationDataExtractor(Func<Activity, string> propagationDataExtractor)
    {
        // TODO: Implement this
        return this;
    }

    public AttributePropagatingSpanProcessorBuilder SetPropagationDataKey(string propagationDataKey)
    {
        // TODO: Implement this
        return this;
    }

    public AttributePropagatingSpanProcessorBuilder SetAttributesKeysToPropagate(List<string> attributesKeysToPropagate)
    {
        // TODO: Implement this
        return this;
    }

    public AttributePropagatingSpanProcessor Build()
    {
        // TODO implement this function
        throw new NotImplementedException();
    }
}
