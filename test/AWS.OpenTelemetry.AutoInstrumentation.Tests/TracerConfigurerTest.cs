// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class TracerConfigurerTest
{
    private TracerProvider tracerProvider;
    public TracerConfigurerTest()
    {
        System.Environment.SetEnvironmentVariable(SamplerUtil.OtelTracesSampler, "traceidratio");
        System.Environment.SetEnvironmentVariable(SamplerUtil.OtelTracesSamplerArg, "0.1");
        Plugin plugin = new Plugin();
        TracerProviderBuilderSdk tracerProviderBuilder = new TracerProviderBuilderBase();
        tracerProviderBuilder = plugin.BeforeConfigureTracerProvider(tracerProviderBuilder);
        tracerProviderBuilder = plugin.AfterConfigureTracerProvider(tracerProviderBuilder);
        tracerProvider = tracerProviderBuilder.Build();
         
        plugin.TracerProviderInitialized(tracerProvider);
    }
    
    
    
}
