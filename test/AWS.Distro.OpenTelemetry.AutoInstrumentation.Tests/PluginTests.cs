// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Restores plugin configuration after each test and verifies upstream exporter customization.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public abstract class PluginTests : IDisposable
{
    private readonly Dictionary<string, string?> originalEnvironment;

    protected PluginTests(IReadOnlyDictionary<string, string?> environment)
    {
        this.originalEnvironment = environment.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        ApplyConfiguration(environment);
    }

    public void Dispose()
    {
        ApplyConfiguration(this.originalEnvironment);
        GC.SuppressFinalize(this);
    }

    protected static void CustomizeExporterTest(
        IReadOnlyDictionary<string, string?> config,
        string endpointConfigName,
        Action<OtlpExporterOptions> configureExporter,
        bool expectSigning)
    {
        ApplyConfiguration(config);
        var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
        var originalFactory = options.HttpClientFactory;
        if (config[endpointConfigName] is string endpoint)
        {
            options.Endpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute);
        }

        configureExporter(options);

        // Java checks the customized exporter type. .NET customizes upstream's HTTP client factory.
        if (expectSigning)
        {
            Assert.NotSame(originalFactory, options.HttpClientFactory);
        }
        else
        {
            Assert.Same(originalFactory, options.HttpClientFactory);
        }
    }

    protected static void ApplyConfiguration(IReadOnlyDictionary<string, string?> config)
    {
        foreach (var entry in config)
        {
            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}

/// <summary>
/// Validates SigV4 trace exporter customization using ADOT Java's valid and invalid configuration cases.
/// </summary>
[Collection("AWS credential providers")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class OtlpAwsSpanExporterConfigurationTests : PluginTests
{
    private const string XRayEndpoint = "https://xray.us-east-1.amazonaws.com/v1/traces";
    private static readonly Uri Endpoint = new(XRayEndpoint);

    public OtlpAwsSpanExporterConfigurationTests()
        : base(CreateEnvironment())
    {
    }

    // Endpoint cases mirror AwsApplicationSignalsCustomizerProviderTest in ADOT Java.
    public static IEnumerable<object[]> ValidSigV4TracesConfigs()
    {
        string[] endpoints =
        {
            "https://xray.us-east-1.amazonaws.com/v1/traces",
            "https://XRAY.US-EAST-1.AMAZONAWS.COM/V1/TRACES",
            "https://XRAY.US-EAST-1.amazonaws.com/v1/traces",
            "https://xray.US-EAST-1.AMAZONAWS.com/v1/traces",
            "https://Xray.Us-East-1.amazonaws.com/v1/traces",
            "https://xRAY.us-EAST-1.amazonaws.com/v1/traces",
            "https://XRAY.us-EAST-1.AMAZONAWS.com/v1/TRACES",
            "https://xray.US-EAST-1.amazonaws.com/V1/Traces",
            "https://xray.us-east-1.AMAZONAWS.COM/v1/traces",
            "https://XrAy.Us-EaSt-1.AmAzOnAwS.cOm/V1/TrAcEs",
            "https://xray.US-EAST-1.amazonaws.com/v1/traces",
            "https://xray.us-east-1.amazonaws.com/V1/TRACES",
            "https://xray.us-east-1.AMAZONAWS.COM/V1/traces",
        };
        foreach (var endpoint in endpoints)
        {
            yield return new object[] { CreateSigV4TracesConfig(endpoint) };
        }

        foreach (var exporter in new string?[] { null, "otlp,console" })
        {
            var config = CreateSigV4TracesConfig(Endpoint.OriginalString);
            config["OTEL_TRACES_EXPORTER"] = exporter;
            yield return new object[] { config };
        }

        var legacyConfig = CreateSigV4TracesConfig(Endpoint.OriginalString);
        legacyConfig["OTEL_TRACES_EXPORTER"] = "none";
        legacyConfig["OTEL_AWS_SIG_V4_ENABLED"] = "true";
        yield return new object[] { legacyConfig };

        foreach (var exporter in new[] { "otlp", "none" })
        {
            foreach (var protocols in new (string? Trace, string? Global)[]
            {
                ("http/protobuf", null),
                (null, "http/protobuf"),
                ("http/protobuf", "grpc"),
            })
            {
                var config = CreateSigV4TracesConfig(Endpoint.OriginalString);
                config["OTEL_TRACES_EXPORTER"] = exporter;
                config["OTEL_AWS_SIG_V4_ENABLED"] = exporter == "none" ? "true" : null;
                config["OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"] = protocols.Trace;
                config["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocols.Global;
                yield return new object[] { config };
            }
        }
    }

    public static IEnumerable<object[]> InvalidSigV4TracesConfigs()
    {
        // The empty hostname label in Java's xray..amazonaws.com case is rejected by .NET's Uri before reaching the plugin.
        string[] endpoints =
        {
            "http://localhost:4318/v1/traces",
            "http://xray.us-east-1.amazonaws.com/v1/traces",
            "ftp://xray.us-east-1.amazonaws.com/v1/traces",
            "https://ray.us-east-1.amazonaws.com/v1/traces",
            "https://xra.us-east-1.amazonaws.com/v1/traces",
            "https://x-ray.us-east-1.amazonaws.com/v1/traces",
            "https://xray.amazonaws.com/v1/traces",
            "https://xray.us-east-1.amazon.com/v1/traces",
            "https://xray.us-east-1.aws.com/v1/traces",
            "https://xray.us_east_1.amazonaws.com/v1/traces",
            "https://xray.us.east.1.amazonaws.com/v1/traces",
            "https://xray.us-east-1.amazonaws.com/traces",
            "https://xray.us-east-1.amazonaws.com/v2/traces",
            "https://xray.us-east-1.amazonaws.com/v1/trace",
            "https://xray.us-east-1.amazonaws.com/v1/traces/",
            "https://xray.us-east-1.amazonaws.com//v1/traces",
            "https://xray.us-east-1.amazonaws.com/v1//traces",
            "https://xray.us-east-1.amazonaws.com/v1/traces?param=value",
            "https://xray.us-east-1.amazonaws.com/v1/traces#fragment",
            "https://xray.us-east-1.amazonaws.com:443/v1/traces",
            "https:/xray.us-east-1.amazonaws.com/v1/traces",
            "https:://xray.us-east-1.amazonaws.com/v1/traces",
            "https://logs.us-east-1.amazonaws.com/v1/logs",
        };
        foreach (var endpoint in endpoints)
        {
            yield return new object[] { CreateSigV4TracesConfig(endpoint) };
        }

        foreach (var exporter in new[] { "console", "none", "zipkin" })
        {
            var config = CreateSigV4TracesConfig(Endpoint.OriginalString);
            config["OTEL_TRACES_EXPORTER"] = exporter;
            yield return new object[] { config };
        }

        var disabledSigV4Config = CreateSigV4TracesConfig(Endpoint.OriginalString);
        disabledSigV4Config["OTEL_TRACES_EXPORTER"] = "none";
        disabledSigV4Config["OTEL_AWS_SIG_V4_ENABLED"] = "false";
        yield return new object[] { disabledSigV4Config };

        foreach (var exporter in new[] { "otlp", "none" })
        {
            foreach (var protocols in new (string? Trace, string? Global)[]
            {
                ("grpc", null),
                (null, "grpc"),
                ("grpc", "http/protobuf"),
                ("http/json", null),
            })
            {
                var config = CreateSigV4TracesConfig(Endpoint.OriginalString);
                config["OTEL_TRACES_EXPORTER"] = exporter;
                config["OTEL_AWS_SIG_V4_ENABLED"] = exporter == "none" ? "true" : null;
                config["OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"] = protocols.Trace;
                config["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocols.Global;
                yield return new object[] { config };
            }
        }

        yield return new object[] { CreateSigV4TracesConfig(null) };
    }

    [Theory]
    [MemberData(nameof(ValidSigV4TracesConfigs))]
    public void ShouldEnableSigV4SpanExporterIfConfigIsCorrect(Dictionary<string, string?> config)
    {
        CustomizeExporterTest(config, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", new Plugin().ConfigureTracesOptions, expectSigning: true);
    }

    [Theory]
    [MemberData(nameof(InvalidSigV4TracesConfigs))]
    public void ShouldNotUseSigV4SpanExporterIfConfigIsIncorrect(Dictionary<string, string?> config)
    {
        CustomizeExporterTest(config, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", new Plugin().ConfigureTracesOptions, expectSigning: false);
    }

    [Fact]
    public void ShouldPreserveUpstreamTraceAndBatchSettings()
    {
        var plugin = new Plugin();
        var options = new OtlpExporterOptions
        {
            Endpoint = Endpoint,
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers = "x-trace-header=trace-value",
            TimeoutMilliseconds = 4321,
            Compression = OtlpExportCompression.GZip,
            ExportProcessorType = ExportProcessorType.Batch,
            BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
            {
                MaxQueueSize = 32,
                MaxExportBatchSize = 4,
                ScheduledDelayMilliseconds = 12345,
                ExporterTimeoutMilliseconds = 6789,
            },
        };
        var batchOptions = options.BatchExportProcessorOptions;
        var originalFactory = options.HttpClientFactory;

        plugin.ConfigureTracesOptions(options);

        Assert.Equal(Endpoint, options.Endpoint);
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, options.Protocol);
        Assert.Equal("x-trace-header=trace-value", options.Headers);
        Assert.Equal(4321, options.TimeoutMilliseconds);
        Assert.Equal(OtlpExportCompression.GZip, options.Compression);
        Assert.Equal(ExportProcessorType.Batch, options.ExportProcessorType);
        Assert.Same(batchOptions, options.BatchExportProcessorOptions);
        Assert.Equal(32, batchOptions.MaxQueueSize);
        Assert.Equal(4, batchOptions.MaxExportBatchSize);
        Assert.Equal(12345, batchOptions.ScheduledDelayMilliseconds);
        Assert.Equal(6789, batchOptions.ExporterTimeoutMilliseconds);
        Assert.NotSame(originalFactory, options.HttpClientFactory);
        using var client = options.HttpClientFactory();
        Assert.Equal(TimeSpan.FromMilliseconds(4321), client.Timeout);
    }

    [Theory]
    [InlineData(OtlpExportProtocol.Grpc)]
    [InlineData((OtlpExportProtocol)byte.MaxValue)]
    public void ShouldThrowIfSpanExporterProtocolIsIncorrect(OtlpExportProtocol protocol)
    {
        var plugin = new Plugin();
        var options = new OtlpExporterOptions { Endpoint = Endpoint, Protocol = protocol };
        var originalFactory = options.HttpClientFactory;

        Assert.Throws<ArgumentException>(() => plugin.ConfigureTracesOptions(options));

        Assert.Equal(protocol, options.Protocol);
        Assert.Same(originalFactory, options.HttpClientFactory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    public void ShouldSignXRayExportsBasedOnTheEndpoint(string? sigV4Enabled)
    {
        var config = CreateSigV4TracesConfig(XRayEndpoint);
        config["OTEL_AWS_SIG_V4_ENABLED"] = sigV4Enabled;
        CustomizeExporterTest(config, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", new Plugin().ConfigureTracesOptions, expectSigning: true);
    }

    [Fact]
    public void ShouldDescribeBothSupportedConfigurationsWhenUsingTheSigV4Flag()
    {
        var plugin = new Plugin();
        var options = new OtlpExporterOptions { Endpoint = Endpoint, Protocol = OtlpExportProtocol.HttpProtobuf };
        var originalFactory = options.HttpClientFactory;
        using var output = new StringWriter();
        var originalOutput = Console.Out;

        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "none");
        Environment.SetEnvironmentVariable("OTEL_AWS_SIG_V4_ENABLED", "true");
        try
        {
            Console.SetOut(output);
            plugin.ConfigureTracesOptions(options);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var warning = Assert.Single(output.ToString().Split(Environment.NewLine)
            .Where(line => line.Contains("OTEL_AWS_SIG_V4_ENABLED is deprecated", StringComparison.Ordinal)));
        Assert.Contains("will be ignored in future releases", warning);
        Assert.Contains("OTEL_TRACES_EXPORTER=none with OTEL_AWS_SIG_V4_ENABLED=true", warning);
        Assert.Contains("recommended OTEL_TRACES_EXPORTER=otlp", warning);
        Assert.Contains("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", warning);
        Assert.Equal("none", Environment.GetEnvironmentVariable("OTEL_TRACES_EXPORTER"));
        Assert.Equal("true", Environment.GetEnvironmentVariable("OTEL_AWS_SIG_V4_ENABLED"));
        Assert.NotSame(originalFactory, options.HttpClientFactory);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "http/protobuf")]
    [InlineData("http/protobuf", null)]
    [InlineData("http/protobuf", "grpc")]
    [InlineData(null, "grpc")]
    [InlineData("grpc", null)]
    [InlineData("grpc", "http/protobuf")]
    public void ShouldRetainTheNoneConfigurationAndItsProtocolSettings(string? traceProtocol, string? globalProtocol)
    {
        var plugin = new Plugin();
        using var provider = Sdk.CreateTracerProviderBuilder().Build();
        using var output = new StringWriter();
        var originalOutput = Console.Out;

        Environment.SetEnvironmentVariable("OTEL_TRACES_EXPORTER", "none");
        Environment.SetEnvironmentVariable("OTEL_AWS_SIG_V4_ENABLED", "true");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", traceProtocol);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", globalProtocol);
        try
        {
            Console.SetOut(output);
            plugin.TracerProviderInitialized(provider);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var warnings = output.ToString().Split(Environment.NewLine)
            .Where(line => line.Contains("OTEL_AWS_SIG_V4_ENABLED is deprecated", StringComparison.Ordinal));
        var protocol = traceProtocol ?? globalProtocol;
        if (protocol == null || protocol == "http/protobuf")
        {
            Assert.Single(warnings);
        }
        else
        {
            Assert.Empty(warnings);
        }

        Assert.Equal("none", Environment.GetEnvironmentVariable("OTEL_TRACES_EXPORTER"));
        Assert.Equal("true", Environment.GetEnvironmentVariable("OTEL_AWS_SIG_V4_ENABLED"));
        Assert.Equal(traceProtocol, Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"));
        Assert.Equal(globalProtocol, Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL"));
        Assert.True(provider.ForceFlush(5000));
    }

    private static Dictionary<string, string?> CreateSigV4TracesConfig(string? endpoint) => new()
    {
        ["OTEL_AWS_SIG_V4_ENABLED"] = null,
        ["OTEL_TRACES_EXPORTER"] = "otlp",
        ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = endpoint,
    };

    private static Dictionary<string, string?> CreateEnvironment()
    {
        var environment = CreateSigV4TracesConfig(Endpoint.OriginalString);
        environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = null;
        environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = null;
        environment["OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"] = null;
        environment["OTEL_AWS_APPLICATION_SIGNALS_ENABLED"] = "false";
        environment["OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED"] = "false";
        environment["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "false";
        environment["AWS_LAMBDA_FUNCTION_NAME"] = null;
        return environment;
    }
}
