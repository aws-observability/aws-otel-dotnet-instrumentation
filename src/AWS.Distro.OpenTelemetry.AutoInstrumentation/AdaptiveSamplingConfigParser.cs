// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation.Logging;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation;

internal static class AdaptiveSamplingConfigParser
{
    internal static readonly string EnvVar = "AWS_XRAY_ADAPTIVE_SAMPLING_CONFIG";

#pragma warning disable CS0436
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(new ConsoleLoggerProvider()));
#pragma warning restore CS0436
    private static readonly ILogger Logger = Factory.CreateLogger("AdaptiveSamplingConfig");

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal static AdaptiveSamplingConfig? Parse(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return null;
        }

        string content = envValue!.Trim();

        // If it looks like a file path, read the file
        if (!content.StartsWith("{") && File.Exists(content))
        {
            try
            {
                content = File.ReadAllText(content);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to read adaptive sampling config file: {Error}", e.Message);
                return null;
            }
        }

        try
        {
            var yaml = Deserializer.Deserialize<YamlConfig>(content);
            if (yaml == null)
            {
                Logger.LogWarning("AWS_XRAY_ADAPTIVE_SAMPLING_CONFIG: config must be a YAML mapping");
                return null;
            }

            return Validate(yaml);
        }
        catch (Exception e)
        {
            Logger.LogWarning("Failed to parse AWS_XRAY_ADAPTIVE_SAMPLING_CONFIG: {Error}", e.Message);
            return null;
        }
    }

    private static AdaptiveSamplingConfig? Validate(YamlConfig yaml)
    {
        if (yaml.Version < 1.0 || yaml.Version >= 2.0)
        {
            Logger.LogWarning("AWS_XRAY_ADAPTIVE_SAMPLING_CONFIG: version must be >= 1.0 and < 2.0, got: {Version}", yaml.Version);
            return null;
        }

        var result = new AdaptiveSamplingConfig { Version = yaml.Version };

        if (yaml.AnomalyConditions != null)
        {
            result.AnomalyConditions = new List<AnomalyCondition>();
            foreach (var cond in yaml.AnomalyConditions)
            {
                var usage = ParseUsage(cond.Usage);
                if (usage == null)
                {
                    return null;
                }

                result.AnomalyConditions.Add(new AnomalyCondition
                {
                    ErrorCodeRegex = cond.ErrorCodeRegex,
                    Operations = cond.Operations,
                    HighLatencyMs = cond.HighLatencyMs,
                    Usage = usage.Value,
                });
            }
        }

        if (yaml.AnomalyCaptureLimit != null)
        {
            if (yaml.AnomalyCaptureLimit.AnomalyTracesPerSecond < 0)
            {
                Logger.LogWarning("AWS_XRAY_ADAPTIVE_SAMPLING_CONFIG: anomalyTracesPerSecond must be non-negative");
                return null;
            }

            result.AnomalyCaptureLimit = new AnomalyCaptureLimit
            {
                AnomalyTracesPerSecond = yaml.AnomalyCaptureLimit.AnomalyTracesPerSecond,
            };
        }

        return result;
    }

    private static UsageType? ParseUsage(string? usageStr)
    {
        return usageStr?.ToLowerInvariant() switch
        {
            "both" or null => UsageType.Both,
            "sampling-boost" => UsageType.SamplingBoost,
            "anomaly-span-capture" or "anomaly_trace_capture" => UsageType.AnomalyTraceCapture,
            "neither" => UsageType.Neither,
            _ => LogAndReturnNull(usageStr),
        };
    }

    private static UsageType? LogAndReturnNull(string? usageStr)
    {
        Logger.LogWarning("AWS_XRAY_ADAPTIVE_SAMPLING_CONFIG: invalid usage type: {Usage}", usageStr);
        return null;
    }

    private sealed class YamlConfig
    {
        [YamlMember(Alias = "version")]
        public double Version { get; set; }

        [YamlMember(Alias = "anomalyConditions")]
        public List<YamlCondition>? AnomalyConditions { get; set; }

        [YamlMember(Alias = "anomalyCaptureLimit")]
        public YamlCaptureLimit? AnomalyCaptureLimit { get; set; }
    }

    private sealed class YamlCondition
    {
        [YamlMember(Alias = "errorCodeRegex")]
        public string? ErrorCodeRegex { get; set; }

        [YamlMember(Alias = "operations")]
        public List<string>? Operations { get; set; }

        [YamlMember(Alias = "highLatencyMs")]
        public int? HighLatencyMs { get; set; }

        [YamlMember(Alias = "usage")]
        public string? Usage { get; set; }
    }

    private sealed class YamlCaptureLimit
    {
        [YamlMember(Alias = "anomalyTracesPerSecond")]
        public int AnomalyTracesPerSecond { get; set; } = 1;
    }
}
