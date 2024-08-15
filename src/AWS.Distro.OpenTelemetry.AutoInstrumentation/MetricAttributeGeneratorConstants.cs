// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

// Utility class holding attribute keys with special meaning to AWS components
internal sealed class MetricAttributeGeneratorConstants
{
    internal static readonly string ServiceMetric = "Service";
    internal static readonly string DependencyMetric = "Dependency";
}
