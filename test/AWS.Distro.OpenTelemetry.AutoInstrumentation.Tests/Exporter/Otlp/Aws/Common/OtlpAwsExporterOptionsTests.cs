// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporter.Otlp.Aws.Common;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.Exporter.Otlp.Aws.Common;

[Collection("AWS credential providers")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsExporterOptionsTests : PluginTests
{
    [Theory]
    [InlineData(OtlpAwsExporterOptions.Traces, /* signalService: */ "custom-service", /* globalService: */ "global-service", /* expectedService: */ "custom-service")]
    [InlineData(OtlpAwsExporterOptions.Traces, /* signalService: */ null, /* globalService: */ "global-service", /* expectedService: */ "global-service")]
    [InlineData(OtlpAwsExporterOptions.Traces, /* signalService: */ "  ", /* globalService: */ "global-service", /* expectedService: */ "global-service")]
    [InlineData(OtlpAwsExporterOptions.Traces, /* signalService: */ null, /* globalService: */ null, /* expectedService: */ OtlpAwsExporterOptions.DefaultTracesService)]
    [InlineData(OtlpAwsExporterOptions.Traces, /* signalService: */ "  ", /* globalService: */ "  ", /* expectedService: */ OtlpAwsExporterOptions.DefaultTracesService)]
    [InlineData(OtlpAwsExporterOptions.Metrics, /* signalService: */ "custom-service", /* globalService: */ "global-service", /* expectedService: */ "custom-service")]
    [InlineData(OtlpAwsExporterOptions.Metrics, /* signalService: */ null, /* globalService: */ "global-service", /* expectedService: */ "global-service")]
    [InlineData(OtlpAwsExporterOptions.Metrics, /* signalService: */ "  ", /* globalService: */ "global-service", /* expectedService: */ "global-service")]
    [InlineData(OtlpAwsExporterOptions.Metrics, /* signalService: */ null, /* globalService: */ null, /* expectedService: */ OtlpAwsExporterOptions.DefaultMetricsService)]
    [InlineData(OtlpAwsExporterOptions.Metrics, /* signalService: */ "  ", /* globalService: */ "  ", /* expectedService: */ OtlpAwsExporterOptions.DefaultMetricsService)]
    [InlineData(OtlpAwsExporterOptions.Logs, /* signalService: */ "custom-service", /* globalService: */ "global-service", /* expectedService: */ "custom-service")]
    [InlineData(OtlpAwsExporterOptions.Logs, /* signalService: */ null, /* globalService: */ "global-service", /* expectedService: */ "global-service")]
    [InlineData(OtlpAwsExporterOptions.Logs, /* signalService: */ "  ", /* globalService: */ "global-service", /* expectedService: */ "global-service")]
    [InlineData(OtlpAwsExporterOptions.Logs, /* signalService: */ null, /* globalService: */ null, /* expectedService: */ OtlpAwsExporterOptions.DefaultLogsService)]
    [InlineData(OtlpAwsExporterOptions.Logs, /* signalService: */ "  ", /* globalService: */ "  ", /* expectedService: */ OtlpAwsExporterOptions.DefaultLogsService)]
    public void GetAwsServiceEnvironmentVariablesOverrideDefault(
        string signal, string? signalService, string? globalService, string expectedService)
    {
        Environment.SetEnvironmentVariable($"AWS_{signal}_EXPORTER_SERVICE", signalService);
        Environment.SetEnvironmentVariable("AWS_EXPORTER_SERVICE", globalService);

        Assert.Equal(expectedService, OtlpAwsExporterOptions.GetAwsService(signal));
    }

    [Theory]
    [InlineData("eu-central-1", "us-west-2", "eu-central-1")]
    [InlineData(null, "us-west-2", "us-west-2")]
    [InlineData("  ", "us-west-2", "us-west-2")]
    [InlineData(null, null, "us-east-1")]
    [InlineData("  ", "  ", "us-east-1")]
    public void GetAwsRegionEnvironmentVariablesOverrideEndpoint(
        string? region, string? defaultRegion, string expectedRegion)
    {
        Environment.SetEnvironmentVariable("AWS_REGION", region);
        Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", defaultRegion);

        Assert.Equal(
            expectedRegion,
            OtlpAwsExporterOptions.GetAwsRegion(
                new Uri("https://xray.us-east-1.amazonaws.com"),
                () => throw new InvalidOperationException("The SDK provider should not be called.")));
    }

    [Theory]
    [InlineData("xray.us-east-1.amazonaws.com", "us-east-1")]
    [InlineData("logs.eu-central-1.amazonaws.com", "eu-central-1")]
    [InlineData("monitoring.cn-north-1.amazonaws.com.cn", "cn-north-1")]
    [InlineData("logs.cn-northwest-1.amazonaws.com.cn", "cn-northwest-1")]
    [InlineData("xray.us-gov-west-1.amazonaws.com", "us-gov-west-1")]
    [InlineData("xray-fips.us-gov-east-1.amazonaws.com", "us-gov-east-1")]
    [InlineData("vpce-123.logs.us-east-1.vpce.amazonaws.com", "us-east-1")]
    [InlineData("pipeline.us-west-2.osis.amazonaws.com", "us-west-2")]
    [InlineData("monitoring.eu-west-1.api.aws", "eu-west-1")]
    [InlineData("xray.us-iso-east-1.c2s.ic.gov", "us-iso-east-1")]
    [InlineData("logs.us-isob-east-1.sc2s.sgov.gov", "us-isob-east-1")]
    [InlineData("monitoring.eu-isoe-west-1.cloud.adc-e.uk", "eu-isoe-west-1")]
    [InlineData("logs.us-isof-south-1.csp.hci.ic.gov", "us-isof-south-1")]
    [InlineData("XRAY.US-EAST-1.AMAZONAWS.COM", "us-east-1")]
    public void GetAwsRegionRecognizedAwsEndpoint(string host, string expectedRegion)
    {
        Assert.Equal(
            expectedRegion,
            OtlpAwsExporterOptions.GetAwsRegion(
                new Uri($"https://{host}"),
                () => throw new InvalidOperationException("The SDK provider should not be called.")));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("example.com")]
    [InlineData("xray.us-east-1.amazonaws.com.evil.com")]
    public void GetAwsRegionUnrecognizedEndpointUsesSdkRegionProvider(string host)
    {
        var providerCalls = 0;
        var region = OtlpAwsExporterOptions.GetAwsRegion(new Uri($"https://{host}"), () =>
        {
            providerCalls++;
            return "eu-north-1";
        });

        Assert.Equal("eu-north-1", region);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public void GetAwsRegionNoRegionSourceReturnsNull()
    {
        Assert.Null(OtlpAwsExporterOptions.GetAwsRegion(new Uri("http://localhost"), () => null));
    }

    protected override Dictionary<string, string?> CreateEnvironment() => new()
    {
        ["AWS_REGION"] = null,
        ["AWS_DEFAULT_REGION"] = null,
        ["AWS_EXPORTER_SERVICE"] = null,
        ["AWS_TRACES_EXPORTER_SERVICE"] = null,
        ["AWS_METRICS_EXPORTER_SERVICE"] = null,
        ["AWS_LOGS_EXPORTER_SERVICE"] = null,
    };
}
