// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using OpenTelemetry.Trace;
using Xunit.Repeat;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
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

    [Theory]
    [Repeat(20)]
    [SuppressMessage("xUnit", "xUnit1026", Justification = "Parameter use as require by Theory to perform Repeat")]
    public void TraceIdRatioSampler(int iter)
    {
        int numSpans = 100000;
        int numSampled = 0;
        Tracer tracer = this.tracerProvider.GetTracer("test");
        for (int i = 0; i < numSpans; i++)
        {
            TelemetrySpan telemetrySpan = tracer.StartActiveSpan("test");
            FieldInfo? fieldInfo = typeof(TelemetrySpan).GetField(
                "Activity",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldInfo != null)
            {
                Activity? telemetryActivity = (Activity?)fieldInfo.GetValue(telemetrySpan);
                if (telemetryActivity != null && telemetryActivity.ActivityTraceFlags == ActivityTraceFlags.Recorded)
                {
                    numSampled++;
                }
            }

            telemetrySpan.Dispose();
        }

        Assert.True((double)numSampled / numSpans < 0.05);
    }
}
