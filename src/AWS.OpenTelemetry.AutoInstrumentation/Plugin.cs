// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// TODO: Add documentation here
/// </summary>
public class Plugin
{
    /// <summary>
    /// To configure plugin, before OTel SDK configuration is called.
    /// </summary>public void Initializing()
    public void Initializing()
    {
        // My custom logic here
    }

    /// <summary>
    /// To access TracerProvider right after TracerProviderBuilder.Build() is executed.
    /// </summary>
    /// <param name="tracerProvider"><see cref="TracerProvider"/> Provider to configure</param>
    public void TracerProviderInitialized(TracerProvider tracerProvider)
    {
        // My custom logic here
    }

    /// <summary>
    /// To access MeterProvider right after MeterProviderBuilder.Build() is executed.
    /// </summary>
    /// <param name="meterProvider"><see cref="MeterProvider"/> Provider to configure</param>
    public void MeterProviderInitialized(MeterProvider meterProvider)
    {
        // My custom logic here
    }

    /// <summary>
    /// To configure tracing SDK before Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public TracerProviderBuilder BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
        // My custom logic here
        return builder;
    }

    /// <summary>
    /// To configure tracing SDK after Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        // My custom logic here
        AwsSpanMetricsProcessor testProcessor = new AwsSpanMetricsProcessor();
        builder.AddProcessor(testProcessor);
        return builder;
    }

    /// <summary>
    /// To configure any traces options used by OpenTelemetry .NET Automatic Instrumentation
    /// We can set the OTLP endpoint configs to point to the cloudwatch agent here as default so that we don't need
    /// to use env vars.
    /// </summary>
    /// <param name="options"><see cref="OtlpExporterOptions"/> options to configure</param>
    public void ConfigureTracesOptions(OtlpExporterOptions options)
    {
        // My custom logic here
        // Find supported options below
    }

    /// <summary>
    /// To configure metrics SDK before Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public MeterProviderBuilder BeforeConfigureMeterProvider(MeterProviderBuilder builder)
    {
        // My custom logic here
        return builder;
    }

    /// <summary>
    /// To configure metrics SDK after Auto Instrumentation configured SDK
    /// </summary>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> Provider to configure</param>
    /// <returns>Returns configured builder</returns>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        // My custom logic here
        return builder;
    }

    /// <summary>
    /// To configure any metrics options used by OpenTelemetry .NET Automatic Instrumentation
    /// We can set the OTLP endpoint configs to point to the cloudwatch agent here as default so that we don't need
    /// to use env vars.
    /// </summary>
    /// <param name="options"><see cref="OtlpExporterOptions"/> options to configure</param>
    public void ConfigureMetricsOptions(OtlpExporterOptions options)
    {
        // My custom logic here
        // Find supported options below
    }

    /// <summary>
    /// To configure Resource
    /// </summary>
    /// <param name="builder"><see cref="ResourceBuilder"/> Builder to configure</param>
    /// <returns>Returns configured builder</returns>
    public ResourceBuilder ConfigureResource(ResourceBuilder builder)
    {
        // My custom logic here
        // Please note this method is common to set the resource for trace, logs and metrics.
        // This method could be overridden by ConfigureTracesOptions, ConfigureMeterProvider and ConfigureLogsOptions
        // by calling SetResourceBuilder with new object.
        return builder;
    }
}
