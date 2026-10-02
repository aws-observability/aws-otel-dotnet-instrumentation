// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Drives a REAL <see cref="OtlpMetricExporter"/> built by the production factory, through a real
/// <see cref="MeterProvider"/>, down to a recording transport.
///
/// This layer exists because unit-testing the gate and the signing handler separately missed two
/// defects that both lived in the seam between them:
///
/// 1. The handler overrode only <c>SendAsync</c>, while upstream's OTLP/HTTP client calls the
///    synchronous <c>HttpClient.Send</c>. Every handler test drove the async path through
///    <c>PostAsync</c> and passed, while the real exporter sent unsigned requests.
/// 2. Replacing <c>HttpClientFactory</c> also replaced upstream's default factory, which is where
///    <c>TimeoutMilliseconds</c> becomes <c>HttpClient.Timeout</c>.
///
/// So these tests assert on what the transport actually received, and deliberately record which of
/// the two send paths was used.
/// </summary>
[Collection("EnvironmentVariables")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class CollectorlessMetricsExporterTest : IDisposable
{
    private const string MetricsEndpoint = "https://monitoring.us-east-1.amazonaws.com/v1/metrics";
    private const string MetricsHeadersVar = "OTEL_EXPORTER_OTLP_METRICS_HEADERS";
    private const string MetricsTimeoutVar = "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT";
    private const string GlobalHeadersVar = "OTEL_EXPORTER_OTLP_HEADERS";

    public CollectorlessMetricsExporterTest() => Clear();

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The regression guard for the defect that mattered: the exporter's own send path must carry a
    /// SigV4 signature. Asserted through the full pipeline, not through HttpClient directly.
    /// </summary>
    [Fact]
    public void TestRealExporterSignsItsRequest()
    {
        var transport = new RecordingHandler();

        ExportThroughRealExporter(transport, CollectorlessMetricsAuthMode.SigV4);

        HttpRequestMessage sent = FirstRequest(transport);
        string[] authorizations = sent.Headers.GetValues("Authorization").ToArray();
        Assert.Single(authorizations);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", authorizations[0]);
        Assert.Contains("/us-east-1/monitoring/aws4_request", authorizations[0]);
    }

    /// <summary>
    /// Pins the specific mechanism of the original defect. Upstream uses the synchronous path for
    /// OTLP/HTTP, so if this ever reports the async path the equivalence of the two overrides is no
    /// longer being exercised and the sync override could silently rot.
    /// </summary>
    [Fact]
    public void TestExporterUsesTheSynchronousSendPathAndStillSigns()
    {
        var transport = new RecordingHandler();

        ExportThroughRealExporter(transport, CollectorlessMetricsAuthMode.SigV4);

        // Upstream uses the synchronous path for OTLP/HTTP. If this ever flips to the async
        // path, the Send override stops being exercised and could silently rot.
        Assert.True(transport.SyncCount >= 1, "expected the synchronous send path to be used");
        Assert.Equal(0, transport.AsyncCount);
        Assert.True(FirstRequest(transport).Headers.Contains("Authorization"));
    }

    /// <summary>
    /// Bearer mode must export, carrying the customer's header and no signature. Previously this
    /// configuration registered no reader at all, so nothing was exported.
    /// </summary>
    [Fact]
    public void TestBearerModeExportsWithTheConfiguredHeaderAndNoSignature()
    {
        Environment.SetEnvironmentVariable(MetricsHeadersVar, "Authorization=Bearer abc123,x-custom=value");
        var transport = new RecordingHandler();

        ExportThroughRealExporter(transport, CollectorlessMetricsAuthMode.BearerToken);

        HttpRequestMessage sent = FirstRequest(transport);
        Assert.Equal("Bearer abc123", sent.Headers.GetValues("Authorization").Single());
        Assert.Equal("value", sent.Headers.GetValues("x-custom").Single());
    }

    /// <summary>
    /// Signal-specific headers reach the wire. new OtlpExporterOptions() resolves only the generic
    /// OTEL_EXPORTER_OTLP_* variables, so without explicit plumbing these were parsed by the gate
    /// and then dropped.
    /// </summary>
    [Fact]
    public void TestSignalSpecificHeadersReachTheWire()
    {
        Environment.SetEnvironmentVariable(MetricsHeadersVar, "x-custom=value,x-another=second");
        var transport = new RecordingHandler();

        ExportThroughRealExporter(transport, CollectorlessMetricsAuthMode.SigV4);

        HttpRequestMessage sent = FirstRequest(transport);
        Assert.Equal("value", sent.Headers.GetValues("x-custom").Single());
        Assert.Equal("second", sent.Headers.GetValues("x-another").Single());

        // Still signed: these headers carry no Authorization.
        Assert.StartsWith("AWS4-HMAC-SHA256 ", sent.Headers.GetValues("Authorization").Single());
    }

    /// <summary>
    /// Signal-specific headers replace the global set rather than merging, which is the precedence
    /// the OTLP specification defines.
    /// </summary>
    [Fact]
    public void TestSignalSpecificHeadersReplaceGlobalHeaders()
    {
        Environment.SetEnvironmentVariable(GlobalHeadersVar, "x-global=globalvalue");
        Environment.SetEnvironmentVariable(MetricsHeadersVar, "x-custom=value");
        var transport = new RecordingHandler();

        ExportThroughRealExporter(transport, CollectorlessMetricsAuthMode.SigV4);

        HttpRequestMessage sent = FirstRequest(transport);
        Assert.Equal("value", sent.Headers.GetValues("x-custom").Single());
        Assert.False(sent.Headers.Contains("x-global"));
    }

    /// <summary>
    /// The configured timeout must survive replacing HttpClientFactory. Upstream's default factory
    /// is what normally applies it, so overriding the factory without setting Timeout left clients
    /// on the 100-second default.
    /// </summary>
    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("45000", 45000)]
    [InlineData(null, 10000)]
    public void TestConfiguredTimeoutReachesTheHttpClient(string? configured, int expectedMilliseconds)
    {
        Environment.SetEnvironmentVariable(MetricsTimeoutVar, configured);

        OtlpExporterOptions options = BuildOptions(CollectorlessMetricsAuthMode.SigV4);

        Assert.Equal(expectedMilliseconds, options.TimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), options.HttpClientFactory().Timeout);
    }

    [Fact]
    public void TestBearerModeAlsoHonorsTheConfiguredTimeout()
    {
        Environment.SetEnvironmentVariable(MetricsHeadersVar, "Authorization=Bearer abc123");
        Environment.SetEnvironmentVariable(MetricsTimeoutVar, "4321");

        OtlpExporterOptions options = BuildOptions(CollectorlessMetricsAuthMode.BearerToken, new RecordingHandler());

        Assert.Equal(4321, options.TimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(4321), options.HttpClientFactory().Timeout);
    }

    /// <summary>
    /// Returns the first request the transport saw. Disposing a MeterProvider triggers a shutdown
    /// export in addition to the explicit flush, so the count is not fixed at one; every request is
    /// equivalent for these assertions.
    /// </summary>
    private static HttpRequestMessage FirstRequest(RecordingHandler transport)
    {
        Assert.NotEmpty(transport.Requests);
        return transport.Requests[0];
    }

    private static void Clear()
    {
        foreach (string name in new[] { MetricsHeadersVar, MetricsTimeoutVar, GlobalHeadersVar })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>
    /// Invokes the production option-construction path. Nothing about the wiring is reimplemented
    /// here, which is the point: the defects this class guards against were invisible to tests that
    /// rebuilt the chain themselves.
    /// </summary>
    private static OtlpExporterOptions BuildOptions(
        CollectorlessMetricsAuthMode mode,
        HttpMessageHandler? innerHandler = null,
        IAwsAuthenticator? authenticator = null)
    {
        MethodInfo method = typeof(Plugin).GetMethod(
            "CreateCollectorlessExporterOptions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateCollectorlessExporterOptions not found");

        return (OtlpExporterOptions)method.Invoke(
            null, new object?[] { MetricsEndpoint, mode, innerHandler, authenticator })!;
    }

    /// <summary>
    /// Exports one real counter through a real OtlpMetricExporter built from the production options,
    /// terminating in the supplied recording transport.
    /// </summary>
    private static void ExportThroughRealExporter(RecordingHandler transport, CollectorlessMetricsAuthMode mode)
    {
        OtlpExporterOptions options = BuildOptions(transport, mode);

        var exporter = new OtlpMetricExporter(options);
        var reader = new PeriodicExportingMetricReader(exporter, 600000)
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Cumulative,
        };

        string meterName = "CollectorlessMetricsExporterTest." + Guid.NewGuid();
        using var meter = new Meter(meterName);
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            .AddReader(reader)
            .Build();

        meter.CreateCounter<long>("probe").Add(1);

        Assert.True(provider.ForceFlush(10000), "metric export did not complete");
    }

    private static OtlpExporterOptions BuildOptions(RecordingHandler transport, CollectorlessMetricsAuthMode mode)
        => BuildOptions(mode, transport, new FixedCredentialsAuthenticator());

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

        internal int SyncCount { get; private set; }

        internal int AsyncCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.SyncCount++;
            this.Requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.AsyncCount++;
            this.Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FixedCredentialsAuthenticator : IAwsAuthenticator
    {
        private static readonly ImmutableCredentials Credentials =
            new ImmutableCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", null);

        public Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(Credentials);

        public void Sign(IRequest request, IClientConfig config, ImmutableCredentials credentials)
            => new DefaultAwsAuthenticator().Sign(request, config, credentials);
    }
}
