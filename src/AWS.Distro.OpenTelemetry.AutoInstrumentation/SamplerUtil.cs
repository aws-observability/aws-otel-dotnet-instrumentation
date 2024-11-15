// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Sampler.AWS;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

/// <summary>
/// Class for getting sampler for instrumentation
/// </summary>
public class SamplerUtil
{
    /// <summary>
    /// OTEL_TRACES_SAMPLER
    /// </summary>
    public static readonly string OtelTracesSampler = "OTEL_TRACES_SAMPLER";

    /// <summary>
    /// OTEL_TRACES_SAMPLER_ARG
    /// </summary>
    public static readonly string OtelTracesSamplerArg = "OTEL_TRACES_SAMPLER_ARG";

    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
    private static readonly ILogger Logger = Factory.CreateLogger<SamplerUtil>();

    // These default values are based on values from the python implementation:
    // https://github.com/aws-observability/aws-otel-python-instrumentation/blob/main/aws-opentelemetry-distro/src/amazon/opentelemetry/distro/sampler/aws_xray_remote_sampler.py#L23-L24
    private static readonly double DefaultRulesPollingIntervalSeconds = 300;
    private static readonly string DefaultSamplingProxyEndpoint = "http://127.0.0.1:2000";

    /// <summary>
    /// This function is based on an internal function in Otel:
    /// https://github.com/open-telemetry/opentelemetry-dotnet/blob/1bbafaa7b7bed6470ff52fc76b6e881cd19692a5/src/OpenTelemetry/Trace/TracerProviderSdk.cs#L408
    /// Unfortunately, that function is private.
    /// </summary>
    /// <param name="resource">Resource to be used for XraySampler</param>
    /// <returns>Sampler to wrap AlwaysRecordSampler around</returns>
    public static Sampler GetSampler(Resource resource)
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
            case "xray":
                return ConfigureXraySampler(tracesSamplerArg, resource);
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

    private static AWSXRayRemoteSampler ConfigureXraySampler(string? tracesSamplerArg, Resource resource)
    {
        // Example env var value
        // OTEL_TRACES_SAMPLER_ARG=endpoint=http://localhost:2000,polling_interval=360
        string endpoint = DefaultSamplingProxyEndpoint;
        double pollingInterval = DefaultRulesPollingIntervalSeconds;
        if (tracesSamplerArg != null)
        {
            var args = tracesSamplerArg.Split(',');
            foreach (string arg in args)
            {
                char[] charSeparators = new char[] { '=' };
                var keyValue = arg.Split(charSeparators, 2);
                if (keyValue.Length != 2)
                {
                    continue;
                }

                if (keyValue[0] == "endpoint")
                {
                    try
                    {
                        Uri url = new Uri(keyValue[1]);
                        endpoint = url.ToString();

                        // Remove the trailing slash since it will be added by the sampler when calling APIs such as
                        // "/SamplingTargets".
                        int lastSlash = endpoint.LastIndexOf('/');
                        endpoint = (lastSlash > -1 && lastSlash == endpoint.Length - 1) ? endpoint.Substring(0, endpoint.Length - 1) : endpoint;
                    }
                    catch (UriFormatException e)
                    {
                        Logger.Log(LogLevel.Error, "Invalid endpoint in OTEL_TRACES_SAMPLER_ARG: {0}. Going to use the default endpoint: http://127.0.0.1:2000", e);
                    }
                }
                else if (keyValue[0] == "polling_interval")
                {
                    try
                    {
                        pollingInterval = Convert.ToDouble(keyValue[1]);
                    }
                    catch (Exception e)
                    {
                        Logger.Log(LogLevel.Error, "polling_interval in OTEL_TRACES_SAMPLER_ARG must be a number: {0}", e);
                    }
                }
            }
        }

        Logger.Log(LogLevel.Information, "XRay Sampler Endpoint: {0}", endpoint);
        Logger.Log(LogLevel.Information, "XRay Sampler Polling Interval:: {0}", pollingInterval);

        return AWSXRayRemoteSampler.Builder(resource) // you must provide a resource
            .SetPollingInterval(TimeSpan.FromSeconds(pollingInterval))
            .SetEndpoint(endpoint)
            .Build();
    }
}
