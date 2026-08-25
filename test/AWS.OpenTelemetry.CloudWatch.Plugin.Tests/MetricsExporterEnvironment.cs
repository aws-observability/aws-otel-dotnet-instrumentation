// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatchPluginOtel.Tests;

internal sealed class MetricsExporterEnvironment : IDisposable
{
    private const string MetricsExporterEnvironmentVariable = "OTEL_METRICS_EXPORTER";

    private readonly string? originalExporters;

    public MetricsExporterEnvironment(string? exporters)
    {
        this.originalExporters = Environment.GetEnvironmentVariable(MetricsExporterEnvironmentVariable);
        Environment.SetEnvironmentVariable(MetricsExporterEnvironmentVariable, exporters);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            MetricsExporterEnvironmentVariable,
            this.originalExporters);
    }
}
