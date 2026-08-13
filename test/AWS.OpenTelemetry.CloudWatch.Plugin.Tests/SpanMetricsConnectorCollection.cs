// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpanMetricsConnectorCollection
{
    public const string Name = "SpanMetricsConnector";
}
