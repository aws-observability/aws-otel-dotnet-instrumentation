// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace AWS.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Class for getting sampler for instrumentation
/// </summary>
public class SamplerUtil
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddConsole());
    private static readonly ILogger Logger = Factory.CreateLogger<SamplerUtil>();
    public static readonly string OtelTracesSampler = "OTEL_TRACES_SAMPLER";
    public static readonly string OtelTracesSamplerArg = "OTEL_TRACES_SAMPLER_ARG";

    /// <summary>
    /// This function is based on an internal function in Otel:
    /// https://github.com/open-telemetry/opentelemetry-dotnet/blob/1bbafaa7b7bed6470ff52fc76b6e881cd19692a5/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L408
    /// Unfortunately, that function is private.
    /// </summary>
    /// <returns>Sampler to wrap AlwaysRecordSampler around</returns>
    public static Sampler GetSampler()
    {
        string? tracesSampler = System.Environment.GetEnvironmentVariable(OtelTracesSampler);
        string? tracesSamplerArg = System.Environment.GetEnvironmentVariable(OtelTracesSamplerArg);
        double samplerProbability = 1.0;
        if (tracesSampler != null)
        {
            try
            {
                samplerProbability = Convert.ToDouble(tracesSamplerArg);
            }
            catch (Exception)
            {
                Logger.Log(LogLevel.Trace, "Could not convert OTEL_TRACES_SAMPLER_ARG to double. Using default value 1.0.");
            }
        }

        // based on the list of available samplers:
        // https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/blob/77256e3a9666ee0f1f72fec5f4ca1a6d8500f229/docs/config.md#samplers
        // Currently, this is the only way to get the sampler as there is no factory and we can't get the sampler
        // that was already set in the TracerProviderBuilder
        // TODO: Add case for X-Ray Sampler when implemented and tested
        switch (tracesSampler)
        {
            case "always_on":
                return new AlwaysOnSampler();
            case "always_off":
                return new AlwaysOffSampler();
            case "traceidratio":
                return new TraceIdRatioBasedSampler(samplerProbability);
            case "parentbased_always_off":
                Sampler alwaysOffSampler = new AlwaysOffSampler();
                return new ParentBasedSampler(alwaysOffSampler);
            case "parentbased_traceidratio":
                Sampler traceIdRatioSampler = new TraceIdRatioBasedSampler(samplerProbability);
                return new ParentBasedSampler(traceIdRatioSampler);
            case "parentbased_always_on":
            default:
                Sampler alwaysOnSampler = new AlwaysOnSampler();
                return new ParentBasedSampler(alwaysOnSampler);
        }
    }
}
