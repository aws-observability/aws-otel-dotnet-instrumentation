// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using OpenTelemetry.Trace;
using Xunit.Repeat;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class TracerConfigurerTest
{
    private TracerProvider tracerProvider;
    private ActivitySource testSource = new ActivitySource("test");

    public TracerConfigurerTest()
    {
        Environment.SetEnvironmentVariable(SamplerUtil.OtelTracesSampler, "traceidratio");
        Environment.SetEnvironmentVariable(SamplerUtil.OtelTracesSamplerArg, "0.01");
        Plugin plugin = new Plugin();
        TracerProviderBuilder tracerProviderBuilder = new TracerProviderBuilderBase();
        var listener = new ActivityListener
        {
            ShouldListenTo = (activitySource) => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        tracerProviderBuilder.AddSource("test");
        Environment.SetEnvironmentVariable(Plugin.ApplicationSignalsEnabledConfig, "true");
        tracerProviderBuilder = plugin.AfterConfigureTracerProvider(tracerProviderBuilder);
        this.tracerProvider = tracerProviderBuilder.Build();
    }

    [Fact]
    [Repeat(20)]
    public void ProviderGeneratesXrayIds()
    {
        var startTimeSecs = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TelemetrySpan telemetrySpan = this.tracerProvider.GetTracer("test").StartActiveSpan("test");
        FieldInfo fieldInfo = typeof(TelemetrySpan).GetField(
            "Activity",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Activity telemetryActivity = (Activity)fieldInfo.GetValue(telemetrySpan);
        var hexString = telemetryActivity.TraceId.ToHexString();
        var epoch = Convert.ToUInt32(hexString.Substring(0, 8), 16);
        Assert.True(epoch >= startTimeSecs);
        telemetrySpan.Dispose();
    }

    [Fact]
    [Repeat(20)]
    public void TraceIdRatioSampler()
    {
        int numSpans = 100000;
        int numSampled = 0;
        Tracer tracer = this.tracerProvider.GetTracer("test");
        for (int i = 0; i < numSpans; i++)
        {
            TelemetrySpan telemetrySpan = tracer.StartActiveSpan("test");
            FieldInfo fieldInfo = typeof(TelemetrySpan).GetField(
                "Activity",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Activity telemetryActivity = (Activity)fieldInfo.GetValue(telemetrySpan);
            if (telemetryActivity.ActivityTraceFlags == ActivityTraceFlags.Recorded)
            {
                numSampled++;
            }

            telemetrySpan.Dispose();
        }

        Assert.True((double)numSampled / numSpans < 0.05);
    }
}
