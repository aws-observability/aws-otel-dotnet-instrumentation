// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Exporter;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

[Collection("AWS credential providers")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpSigV4ExporterSelectionTests : PluginTests
{
    public OtlpSigV4ExporterSelectionTests()
        : base(CreateEnvironment())
    {
    }

    public static IEnumerable<object?[]> ExporterSelections()
    {
        foreach (var signal in new[] { "TRACES", "METRICS", "LOGS" })
        {
            foreach (var (selection, expected, signed) in new (string?, string?, bool)[]
            {
                (null, null, false),
                (string.Empty, string.Empty, false),
                ("  ", "  ", false),
                ("none", "none", false),
                ("otlp", "otlp", false),
                (" otlp , console ", " otlp , console ", false),
                ("otlp/SIGV4", "otlp/SIGV4", false),
                ("otlp/sigv4", "otlp", true),
                (" otlp/sigv4 , console ", "otlp,console", true),
                ("otlp,otlp/sigv4", "otlp", true),
                ("otlp/sigv4,otlp", "otlp", true),
                ("console,otlp/sigv4,otlp/sigv4,otlp", "console,otlp", true),
            })
            {
                yield return new object?[] { signal, selection, expected, signed };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ExporterSelections))]
    public void Initializing_NormalizesSignedSelection(
        string signal, string? selection, string? expected, bool signed)
    {
        var variable = $"OTEL_{signal}_EXPORTER";
        Environment.SetEnvironmentVariable(variable, selection);
        var originalValue = Environment.GetEnvironmentVariable(variable);

        new Plugin().Initializing();
        Assert.Equal(signed ? expected : originalValue, Environment.GetEnvironmentVariable(variable));
    }

    [Theory]
    [InlineData("TRACES")]
    [InlineData("METRICS")]
    [InlineData("LOGS")]
    public void Initializing_PreservesSigningSelectionAndUpstreamOptions(string selectedSignal)
    {
        Environment.SetEnvironmentVariable($"OTEL_{selectedSignal}_EXPORTER", "otlp,otlp/sigv4");
        var plugin = new Plugin();
        plugin.Initializing();
        plugin.Initializing();

        Assert.Equal("otlp", Environment.GetEnvironmentVariable($"OTEL_{selectedSignal}_EXPORTER"));
        foreach (var signal in new[] { "TRACES", "METRICS", "LOGS" })
        {
            var endpoint = new Uri($"http://localhost:4318/v1/{signal.ToLowerInvariant()}");
            var options = new OtlpExporterOptions
            {
                Endpoint = endpoint,
                Headers = "x-test=upstream",
                TimeoutMilliseconds = 1234,
                Compression = OtlpExportCompression.GZip,
                Protocol = OtlpExportProtocol.HttpProtobuf,
            };
            var originalFactory = options.HttpClientFactory;

            ConfigureExporter(plugin, signal, options);

            Assert.Equal(endpoint, options.Endpoint);
            Assert.Equal("x-test=upstream", options.Headers);
            Assert.Equal(1234, options.TimeoutMilliseconds);
            Assert.Equal(OtlpExportCompression.GZip, options.Compression);
            Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.Protocol);
            if (signal == selectedSignal)
            {
                Assert.NotSame(originalFactory, options.HttpClientFactory);
                using var client = options.HttpClientFactory();
                Assert.Equal(TimeSpan.FromMilliseconds(1234), client.Timeout);
            }
            else
            {
                Assert.Same(originalFactory, options.HttpClientFactory);
            }
        }
    }

    private static Dictionary<string, string?> CreateEnvironment()
    {
        var environment = new Dictionary<string, string?>
        {
            ["AWS_REGION"] = "us-east-1",
            ["OTEL_AWS_SIG_V4_ENABLED"] = null,
            ["OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED"] = "false",
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "false",
            ["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false",
            ["AWS_LAMBDA_FUNCTION_NAME"] = null,
        };
        foreach (var signal in new[] { "TRACES", "METRICS", "LOGS" })
        {
            environment[$"OTEL_{signal}_EXPORTER"] = null;
            environment[$"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"] = null;
        }

        return environment;
    }

    private static void ConfigureExporter(Plugin plugin, string signal, OtlpExporterOptions options)
    {
        switch (signal)
        {
            case "TRACES":
                plugin.ConfigureTracesOptions(options);
                break;
            case "METRICS":
                plugin.ConfigureMetricsOptions(options);
                break;
            case "LOGS":
                plugin.ConfigureLogsOptions(options);
                break;
        }
    }
}
