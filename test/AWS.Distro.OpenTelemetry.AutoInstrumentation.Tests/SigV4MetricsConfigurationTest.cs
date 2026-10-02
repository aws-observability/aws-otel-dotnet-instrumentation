// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Selection-layer coverage for collector-less OTLP metrics export.
///
/// Every prerequisite is asserted in both directions, because a missing one produces no metrics
/// rather than an error: the customer sees silence. The wire-level behavior of the signature itself
/// lives in <see cref="SigV4SigningHandlerTest"/>.
///
/// These tests mutate process environment variables, so they run in a single collection to stop
/// xUnit parallelising them against each other.
/// </summary>
[Collection("EnvironmentVariables")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class SigV4MetricsConfigurationTest : IDisposable
{
    private const string MetricsEndpointVar = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
    private const string MetricsHeadersVar = "OTEL_EXPORTER_OTLP_METRICS_HEADERS";
    private const string MetricsProtocolVar = "OTEL_EXPORTER_OTLP_METRICS_PROTOCOL";
    private const string MetricsTimeoutVar = "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT";
    private const string MetricsTemporalityVar = "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE";
    private const string MetricsExporterVar = "OTEL_METRICS_EXPORTER";
    private const string SigV4EnabledVar = "OTEL_AWS_SIG_V4_ENABLED";
    private const string ExportIntervalVar = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string GlobalTimeoutVar = "OTEL_EXPORTER_OTLP_TIMEOUT";
    private const string GlobalProtocolVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string LambdaVar = "AWS_LAMBDA_FUNCTION_NAME";

    private const string ValidEndpoint = "https://monitoring.us-east-1.amazonaws.com/v1/metrics";
    private const string ValidChinaEndpoint = "https://monitoring.cn-north-1.amazonaws.com.cn/v1/metrics";

    private static readonly string[] ManagedVars =
    {
        MetricsEndpointVar, MetricsHeadersVar, MetricsProtocolVar, MetricsTimeoutVar,
        MetricsTemporalityVar, MetricsExporterVar, SigV4EnabledVar, ExportIntervalVar,
        GlobalTimeoutVar, GlobalProtocolVar, LambdaVar,
    };

    public SigV4MetricsConfigurationTest() => Clear();

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TestEnabledWithCompleteConfiguration()
    {
        ConfigureValid(ValidEndpoint);

        Assert.True(InvokeGate());
    }

    [Fact]
    public void TestEnabledForChinaEndpoint()
    {
        ConfigureValid(ValidChinaEndpoint);

        Assert.True(InvokeGate());
    }

    [Fact]
    public void TestDisabledWhenEndpointIsNotConfigured()
    {
        Environment.SetEnvironmentVariable(SigV4EnabledVar, "true");
        Environment.SetEnvironmentVariable(MetricsExporterVar, "none");

        Assert.False(InvokeGate());
    }

    [Theory]
    [InlineData("http://localhost:4318/v1/metrics")]
    [InlineData("https://example.com/v1/metrics")]
    [InlineData("https://monitoring.us-east-1.amazonaws.com.evil/v1/metrics")]
    [InlineData("https://cloudwatch.us-east-1.amazonaws.com/v1/metrics")]
    public void TestDisabledForNonCloudWatchEndpoint(string endpoint)
    {
        ConfigureValid(endpoint);

        Assert.False(InvokeGate());
    }

    [Fact]
    public void TestDisabledWhenSigV4FlagIsNotSet()
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(SigV4EnabledVar, null);

        Assert.False(InvokeGate());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("TRUE")]
    public void TestSigV4FlagCasingAndFalseValue(string value)
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(SigV4EnabledVar, value);

        // The flag is matched case-insensitively, so "TRUE" enables and "false" does not.
        Assert.Equal(string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase), InvokeGate());
    }

    /// <summary>
    /// Without OTEL_METRICS_EXPORTER=none the exporter upstream already configured stays in place,
    /// so metrics would be exported twice, once unsigned.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("otlp")]
    public void TestDisabledUnlessUpstreamMetricsExporterIsNone(string? exporter)
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(MetricsExporterVar, exporter);

        Assert.False(InvokeGate());
    }

    [Fact]
    public void TestDisabledInLambda()
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(LambdaVar, "my-function");

        Assert.False(InvokeGate());
    }

    [Theory]
    [InlineData(MetricsProtocolVar)]
    [InlineData(GlobalProtocolVar)]
    public void TestDisabledForGrpcProtocol(string protocolVar)
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(protocolVar, "grpc");

        Assert.False(InvokeGate());
    }

    [Fact]
    public void TestEnabledForHttpProtobufProtocol()
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(MetricsProtocolVar, "http/protobuf");

        Assert.True(InvokeGate());
    }

    /// <summary>
    /// A signal-specific Authorization header means the customer chose bearer-token authentication,
    /// which CloudWatch Metrics also accepts. It must be preserved rather than replaced by SigV4.
    /// </summary>
    /// <summary>
    /// A signal-specific Authorization header selects bearer-token authentication, which CloudWatch
    /// Metrics accepts. It must still EXPORT: collector-less export requires
    /// OTEL_METRICS_EXPORTER=none, so there is no upstream reader to fall back on, and treating
    /// bearer as "disabled" would export nothing while appearing to honor the header.
    /// </summary>
    [Theory]
    [InlineData("Authorization=Bearer abc123")]
    [InlineData("authorization=Bearer abc123")]
    [InlineData("x-custom=value,Authorization=Bearer abc123")]
    public void TestBearerConfigurationStillExportsWithoutSigning(string headers)
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(MetricsHeadersVar, headers);

        Assert.Equal(CollectorlessMetricsAuthMode.BearerToken, InvokeMode());
        Assert.True(InvokeGate(), "bearer mode must still register a reader");
    }

    [Fact]
    public void TestEnabledWhenSignalSpecificHeadersCarryNoAuthorization()
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable(MetricsHeadersVar, "x-custom=value,x-another=value2");

        Assert.Equal(CollectorlessMetricsAuthMode.SigV4, InvokeMode());
    }

    /// <summary>
    /// Only the signal-specific variable selects bearer mode. This matches the cross-SDK contract,
    /// and the signing handler replaces rather than duplicates any Authorization already present,
    /// so a global header cannot produce two Authorization values.
    /// </summary>
    [Fact]
    public void TestGlobalAuthorizationHeaderDoesNotSuppressSigning()
    {
        ConfigureValid(ValidEndpoint);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "Authorization=Bearer abc123");

        try
        {
            Assert.True(InvokeGate());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", null);
        }
    }

    /// <summary>
    /// Temporality is honored rather than forced, so metric meaning is not changed on the
    /// customer's behalf. Upstream's default is cumulative.
    /// </summary>
    [Theory]
    [InlineData(null, MetricReaderTemporalityPreference.Cumulative)]
    [InlineData("cumulative", MetricReaderTemporalityPreference.Cumulative)]
    [InlineData("delta", MetricReaderTemporalityPreference.Delta)]
    [InlineData("DELTA", MetricReaderTemporalityPreference.Delta)]
    [InlineData("lowmemory", MetricReaderTemporalityPreference.LowMemory)]
    [InlineData("nonsense", MetricReaderTemporalityPreference.Cumulative)]
    public void TestTemporalityPreference(string? configured, MetricReaderTemporalityPreference expected)
    {
        Environment.SetEnvironmentVariable(MetricsTemporalityVar, configured);

        Assert.Equal(expected, Invoke<MetricReaderTemporalityPreference>("GetMetricsTemporalityPreference"));
    }

    /// <summary>
    /// The Application Signals path caps the interval at 60s. That rule must not leak onto the
    /// collector-less path, where an explicit customer value is used as given.
    /// </summary>
    [Theory]
    [InlineData("120000", 120000)]
    [InlineData("300000", 300000)]
    [InlineData("5000", 5000)]
    [InlineData(null, 60000)]
    [InlineData("0", 60000)]
    [InlineData("not-a-number", 60000)]
    public void TestExportIntervalIsNotCapped(string? configured, int expected)
    {
        Environment.SetEnvironmentVariable(ExportIntervalVar, configured);

        Assert.Equal(expected, Invoke<int>("GetCollectorlessMetricExportInterval"));
    }

    [Theory]
    [InlineData("3000", null, 3000)]
    [InlineData(null, "7000", 7000)]
    [InlineData("3000", "7000", 3000)]
    [InlineData(null, null, 10000)]
    public void TestTimeoutPrefersTheSignalSpecificVariable(string? signalSpecific, string? global, int expected)
    {
        Environment.SetEnvironmentVariable(MetricsTimeoutVar, signalSpecific);
        Environment.SetEnvironmentVariable(GlobalTimeoutVar, global);

        Assert.Equal(expected, Invoke<int>("GetCollectorlessMetricTimeout"));
    }

    private static void Clear()
    {
        foreach (string name in ManagedVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static void ConfigureValid(string endpoint)
    {
        Environment.SetEnvironmentVariable(MetricsEndpointVar, endpoint);
        Environment.SetEnvironmentVariable(SigV4EnabledVar, "true");
        Environment.SetEnvironmentVariable(MetricsExporterVar, "none");
    }

    private static CollectorlessMetricsAuthMode InvokeMode()
    {
        MethodInfo method = typeof(Plugin).GetMethod(
            "DetermineCollectorlessMetricsAuthMode", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DetermineCollectorlessMetricsAuthMode not found");

        return (CollectorlessMetricsAuthMode)method.Invoke(new Plugin(), null)!;
    }

    private static bool InvokeGate() => InvokeMode() != CollectorlessMetricsAuthMode.Disabled;

    private static T Invoke<T>(string name)
    {
        MethodInfo method = typeof(Plugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} not found");

        return (T)method.Invoke(null, null)!;
    }
}
