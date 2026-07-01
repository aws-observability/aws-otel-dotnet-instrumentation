// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation;

/// <summary>
/// Dynamic Instrumentation plugin loaded via OTEL_DOTNET_AUTO_PLUGINS.
/// No-ops if OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED != true.
/// Skipped in Lambda environments (no CloudWatch Agent available).
/// </summary>
public class DynamicInstrumentationPlugin
{
    public void Initializing()
    {
        if (IsLambda())
            return;

        var config = DynamicInstrumentationConfig.FromEnvironment();
        if (!config.Enabled)
            return;

        DynamicInstrumentationManager.Instance.Initialize(config);
    }

    public void ConfigureResource(ResourceBuilder builder)
    {
    }

    public void BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
    }

    public void AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
    }

    public void TracerProviderInitialized(TracerProvider provider)
    {
        DynamicInstrumentationManager.Instance.OnTracerProviderInitialized(provider);
    }

    public void AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
    }

    public void ConfigureLogsOptions(OpenTelemetryLoggerOptions options)
    {
    }

    private static bool IsLambda() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));
}
