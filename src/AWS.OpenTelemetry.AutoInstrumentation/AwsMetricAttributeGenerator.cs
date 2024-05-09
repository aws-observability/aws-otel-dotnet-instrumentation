// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// AwsMetricAttributeGenerator generates very specific metric attributes based on low-cardinality
/// span and resource attributes. If such attributes are not present, we fallback to default values.
/// <p>The goal of these particular metric attributes is to get metrics for incoming and outgoing
/// traffic for a service. Namely, <see cref="SpanKind.Server"/> and <see cref="SpanKind.Consumer"/> spans
/// represent "incoming" traffic, {<see cref="SpanKind.Client"/> and <see cref="SpanKind.Producer"/> spans
/// represent "outgoing" traffic, and <see cref="SpanKind.Internal"/> spans are ignored.
/// </summary>
internal sealed class AwsMetricAttributeGenerator : IMetricAttributeGenerator
{
    /// <inheritdoc/>
    public Dictionary<string, ActivityTagsCollection> GenerateMetricAttributeMapFromSpan(Activity span, Resource resource)
    {
        throw new NotImplementedException();
    }
}
