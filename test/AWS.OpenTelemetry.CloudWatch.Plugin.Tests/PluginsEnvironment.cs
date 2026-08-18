// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.OpenTelemetry.CloudWatch.Plugin.Tests;

internal sealed class PluginsEnvironment : IDisposable
{
    private const string AutoPluginsEnvironmentVariable = "OTEL_DOTNET_AUTO_PLUGINS";
    private readonly string? originalPlugins;

    public PluginsEnvironment(string? plugins)
    {
        this.originalPlugins = Environment.GetEnvironmentVariable(AutoPluginsEnvironmentVariable);
        Environment.SetEnvironmentVariable(AutoPluginsEnvironmentVariable, plugins);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AutoPluginsEnvironmentVariable, this.originalPlugins);
    }
}
