// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace AWS.Distro.Opentelemetry.AutoInstrumentation.ContractTests;

public abstract class TestBase: IDisposable
{
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddConsole());
    private readonly ILogger<TestBase> CollectorLogger = Factory.CreateLogger<TestBase>();
    private readonly ILogger<TestBase> ApplicationLogger = Factory.CreateLogger<TestBase>();
    
    protected readonly INetwork Network;
    
    private const string CollectorHostname = "collector";
    private const int CollectorGRPCPort = 4317;
    private const int CollectorHTTPPort = 4318;
    protected abstract int ApplicationPort { get; }
    
    protected readonly IContainer MockCollector;
    protected readonly IContainer Application;

    protected MockCollectorClient? MockCollectorClient;
    protected HttpClient? AppClient;

    protected TestBase(string testApplicationName, ITestOutputHelper output, string testApplicationType = "contract")
    {
        this.CollectorLogger.Log(LogLevel.Information, $"collector {GetApplicationOtelServiceName()}");
        this.ApplicationLogger.Log(LogLevel.Information, $"application {GetApplicationOtelServiceName()}");
        this.Network = new NetworkBuilder().Build();
        
        // Setup Mocked Collector Service
        MockCollector = new ContainerBuilder()
            .WithImage("aws-appsignals-mock-collector")
            .WithPortBinding(CollectorGRPCPort, CollectorGRPCPort)
            .WithPortBinding(CollectorHTTPPort, CollectorHTTPPort)
            .WithLogger(this.CollectorLogger)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(CollectorHTTPPort).ForPath("/health")))
            .WithNetwork(Network)
            .WithNetworkAliases(CollectorHostname)
            .Build();

        // Setup Test Application
        Application = new ContainerBuilder()
            .WithImage(GetApplicationImageName())
            .WithPortBinding(ApplicationPort, ApplicationPort)
            .WithNetwork(Network)
            .WithLogger(this.ApplicationLogger)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)ApplicationPort)))
            .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "100") // 100 ms
            .WithEnvironment("OTEL_AWS_APP_SIGNALS_ENABLED", "false")
            .WithEnvironment("OTEL_METRICS_EXPORTER", "none")
            .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "0")
            .WithEnvironment("OTEL_AWS_APP_SIGNALS_EXPORTER_ENDPOINT", $"http://{CollectorHostname}:{CollectorGRPCPort}")
            .WithEnvironment("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",$"http://{CollectorHostname}:{CollectorGRPCPort}")
            .WithEnvironment("OTEL_RESOURCE_ATTRIBUTES", GetApplicationOtelResourceAttributes())
            .WithEnvironment(GetApplicationExtraEnvironmentVariables())
            .WithNetworkAliases([.. GetApplicationNetworkAliases()])
            .Build();
    }

    public async Task Setup()
    {
        await StartCollector();
    }

    protected async Task StartCollector()
    {
        await this.MockCollector.StartAsync();
    }

    protected async Task StopCollector()
    {
        await this.MockCollector.StopAsync();
    }
    public async void Dispose()
    {
        // Stop Collector after all tests
        await StopCollector();
    }

    protected async Task SetupClients()
    {
        await Application.StartAsync();

        AppClient = new()
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        MockCollectorClient =
            new MockCollectorClient(new()
            {
                BaseAddress = new UriBuilder(Uri.UriSchemeHttp, this.MockCollector.Hostname, this.MockCollector.GetMappedPublicPort(4318), "uuid").Uri
            });
    }

    protected abstract String GetApplicationImageName();
    protected String GetApplicationOtelServiceName() {
        return GetApplicationImageName();
    }

    protected String GetApplicationOtelResourceAttributes() {
        return "service.name=" + GetApplicationOtelServiceName();
    }

    protected abstract Dictionary<string, string> GetApplicationExtraEnvironmentVariables();
    protected abstract List<String> GetApplicationNetworkAliases();
}
