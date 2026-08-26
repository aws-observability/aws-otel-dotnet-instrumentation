// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpanMetricsTestsCollection
{
    public const string Name = "CloudWatch span metrics";
}
