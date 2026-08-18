// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Reflection;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudWatchPluginOtel.Contract;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace CloudWatchPluginOtel;

public static class Program
{
    internal const string ActivitySourceName = "CloudWatchPluginOtel.Contract";
    internal const string DependenciesReadyMessage = "CloudWatchPluginOtel dependencies ready.";
    internal const string ServiceName = "cloudwatch-plugin-otel-contract-test";

    private const string LocalStackEndpoint = "http://localstack:4566";
    private const string RedisEndpoint = "redis:6379,abortConnect=false";
    private const string RegionName = "us-west-2";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static async Task Main(string[] args)
    {
        var mode = ParseMode(Environment.GetEnvironmentVariable("SPAN_METRICS_MODE"));
        LogModeAndActivationEnvironment(mode);
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var redisConnection = await ConnectToRedisAsync();
        var builder = WebApplication.CreateBuilder(args);
        ConfigureKestrel(builder);
        ConfigureApplicationServices(builder.Services, redisConnection);

        ManualProviders? manualProviders = null;
        switch (mode)
        {
            case SpanMetricsMode.Auto:
                ConfigureAuto();
                break;
            case SpanMetricsMode.Manual:
                manualProviders = ConfigureManualRawSdk(redisConnection);
                break;
            case SpanMetricsMode.ManualGlobal:
                ConfigureManualGlobal(builder.Services);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported span metrics mode.");
        }

        var app = builder.Build();
        MapEndpoints(app);

        try
        {
            await app.StartAsync();
            await InitializeDependenciesAsync(app.Services);
            LogInstrumentationAssemblies();
            Console.WriteLine(DependenciesReadyMessage);
            await app.WaitForShutdownAsync();
        }
        finally
        {
            await app.DisposeAsync();
            manualProviders?.Dispose();
            await redisConnection.CloseAsync();
            redisConnection.Dispose();
            ActivitySource.Dispose();
        }
    }

    private static void ConfigureAuto()
    {
        // The startup hook builds both providers. The application entry point registers no OpenTelemetry components.
        Console.WriteLine("SPAN_METRICS_MODE=auto -> ConfigureAuto");
    }

    private static ManualProviders ConfigureManualRawSdk(IConnectionMultiplexer redisConnection)
    {
        // Raw SDK mode owns both providers and explicitly registers every instrumentation and exporter.
        Console.WriteLine("SPAN_METRICS_MODE=manual -> ConfigureManualRawSdk");
        var rootSampler = CreateRootSampler();
        var tracerBuilder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(CreateResourceBuilder())
            .AddSource(ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddGrpcClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddAWSInstrumentation()
            .AddRedisInstrumentation(
                redisConnection,
                options => options.FlushInterval = TimeSpan.FromMilliseconds(50))
            .AddOtlpExporter();
        SpanMetricsTracerProviderBuilderExtensions.AddCloudWatchSpanMetrics(
            tracerBuilder,
            rootSampler);
        var tracerProvider = tracerBuilder.Build();

        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(CreateResourceBuilder())
            .AddOtlpExporter();
        SpanMetricsMeterProviderBuilderExtensions.AddCloudWatchSpanMetrics(meterBuilder);
        var meterProvider = meterBuilder.Build();

        return new ManualProviders(tracerProvider, meterProvider);
    }

    private static void ConfigureManualGlobal(IServiceCollection services)
    {
        // Hosting mode lets dependency injection own both providers; no startup hook participates.
        Console.WriteLine("SPAN_METRICS_MODE=manual-global -> ConfigureManualGlobal");
        var rootSampler = CreateRootSampler();
        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddGrpcClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddAWSInstrumentation()
                    .AddRedisInstrumentation(
                        options => options.FlushInterval = TimeSpan.FromMilliseconds(50))
                    .AddOtlpExporter();
                SpanMetricsTracerProviderBuilderExtensions.AddCloudWatchSpanMetrics(
                    tracing,
                    rootSampler);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddOtlpExporter();
                SpanMetricsMeterProviderBuilderExtensions.AddCloudWatchSpanMetrics(metrics);
            });
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(8080, listener => listener.Protocols = HttpProtocols.Http1);
            options.ListenAnyIP(8081, listener => listener.Protocols = HttpProtocols.Http2);
        });
    }

    private static void ConfigureApplicationServices(
        IServiceCollection services,
        IConnectionMultiplexer redisConnection)
    {
        var credentials = new BasicAWSCredentials(
            Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? "test",
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? "test");

        services.AddSingleton(redisConnection);
        services.AddSingleton<IConnectionMultiplexer>(redisConnection);
        services.AddDbContext<ContractDbContext>(
            options => options.UseSqlite("Data Source=/tmp/cloudwatch-plugin-otel.db"));
        services.AddHttpClient("downstream", client => client.BaseAddress = new Uri("http://127.0.0.1:8080"));
        services.AddGrpc();
        services.AddSingleton(_ => GrpcChannel.ForAddress("http://127.0.0.1:8081"));
        services.AddSingleton(provider => new Health.HealthClient(provider.GetRequiredService<GrpcChannel>()));
        services.AddSingleton<DependencyState>();
        services.AddSingleton<IAmazonS3>(
            _ => new AmazonS3Client(
                credentials,
                new AmazonS3Config
                {
                    ServiceURL = LocalStackEndpoint,
                    ForcePathStyle = true,
                    AuthenticationRegion = RegionName,
                }));
        services.AddSingleton<IAmazonSQS>(
            _ => new AmazonSQSClient(
                credentials,
                new AmazonSQSConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = RegionName,
                }));
        services.AddSingleton<IAmazonDynamoDB>(
            _ => new AmazonDynamoDBClient(
                credentials,
                new AmazonDynamoDBConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = RegionName,
                }));
        services.AddSingleton<IAmazonSimpleNotificationService>(
            _ => new AmazonSimpleNotificationServiceClient(
                credentials,
                new AmazonSimpleNotificationServiceConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = RegionName,
                }));
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGrpcService<ContractHealthService>();
        app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        app.MapGet("/downstream", () => Results.Ok(new { status = "downstream-ready" }));
        app.MapGet(
            "/exercise",
            async (
                IHttpClientFactory httpClientFactory,
                ContractDbContext database,
                IConnectionMultiplexer redis,
                Health.HealthClient grpcClient,
                IAmazonS3 s3,
                IAmazonSQS sqs,
                IAmazonDynamoDB dynamoDb,
                IAmazonSimpleNotificationService sns,
                DependencyState dependencyState,
                CancellationToken cancellationToken) =>
            {
                using (var internalActivity = ActivitySource.StartActivity("internal-work", ActivityKind.Internal))
                {
                    await Task.Yield();
                }

                using var downstreamResponse = await httpClientFactory
                    .CreateClient("downstream")
                    .GetAsync("/downstream", cancellationToken);
                downstreamResponse.EnsureSuccessStatusCode();

                _ = await database.Users
                    .AsNoTracking()
                    .OrderBy(user => user.Id)
                    .FirstAsync(cancellationToken);
                _ = await redis.GetDatabase().StringGetAsync("contract-key");
                _ = await grpcClient.CheckAsync(
                    new HealthCheckRequest(),
                    cancellationToken: cancellationToken);
                _ = await s3.ListBucketsAsync(cancellationToken);
                _ = await sqs.SendMessageAsync(
                    new SendMessageRequest
                    {
                        QueueUrl = dependencyState.QueueUrl,
                        MessageBody = "contract-message",
                    },
                    cancellationToken);
                _ = await dynamoDb.GetItemAsync(
                    new GetItemRequest
                    {
                        TableName = DependencyState.TableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["Id"] = new("contract-user"),
                        },
                    },
                    cancellationToken);
                _ = await sns.PublishAsync(
                    new PublishRequest
                    {
                        TopicArn = dependencyState.TopicArn,
                        Message = "contract-message",
                    },
                    cancellationToken);

                using (var consumerActivity = ActivitySource.StartActivity("orders receive", ActivityKind.Consumer))
                {
                    consumerActivity?.SetTag("messaging.system", "contract-broker");
                    consumerActivity?.SetTag("messaging.operation.name", "receive");
                    consumerActivity?.SetTag("messaging.destination.name", "orders");
                }

                return Results.Ok(new { status = "exercised" });
            });
        app.MapGet("/error", ThrowContractError);
    }

    private static IResult ThrowContractError()
    {
        throw new InvalidOperationException("CloudWatch span metrics contract error.");
    }

    private static async Task InitializeDependenciesAsync(IServiceProvider services)
    {
        const int maxAttempts = 30;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await InitializeDependenciesOnceAsync(services);
                return;
            }
            catch (Exception exception) when (attempt < maxAttempts)
            {
                lastException = exception;
                Console.WriteLine(
                    $"Dependency initialization attempt {attempt} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException("Dependency initialization did not complete.", lastException);
    }

    private static async Task InitializeDependenciesOnceAsync(IServiceProvider services)
    {
        await using var connection = new SqliteConnection("Data Source=/tmp/cloudwatch-plugin-otel.db");
        await connection.OpenAsync();
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText =
            """
            CREATE TABLE IF NOT EXISTS users (
                Id INTEGER NOT NULL CONSTRAINT PK_users PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            );
            INSERT OR IGNORE INTO users (Id, Name) VALUES (1, 'contract-user');
            """;
        await createCommand.ExecuteNonQueryAsync();

        var redis = services.GetRequiredService<IConnectionMultiplexer>();
        await redis.GetDatabase().StringSetAsync("contract-key", "contract-value");

        var healthClient = services.GetRequiredService<Health.HealthClient>();
        var healthResponse = await healthClient.ReadyAsync(new HealthCheckRequest());
        if (!string.Equals(healthResponse.Status, "SERVING", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected gRPC health response '{healthResponse.Status}'.");
        }

        var s3 = services.GetRequiredService<IAmazonS3>();
        try
        {
            await s3.PutBucketAsync(
                new PutBucketRequest
                {
                    BucketName = DependencyState.BucketName,
                    BucketRegionName = RegionName,
                });
        }
        catch (AmazonS3Exception exception)
            when (exception.StatusCode == HttpStatusCode.Conflict ||
                  string.Equals(exception.ErrorCode, "BucketAlreadyOwnedByYou", StringComparison.Ordinal))
        {
        }

        var sqs = services.GetRequiredService<IAmazonSQS>();
        var queue = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = DependencyState.QueueName });

        var dynamoDb = services.GetRequiredService<IAmazonDynamoDB>();
        try
        {
            await dynamoDb.CreateTableAsync(
                new CreateTableRequest
                {
                    TableName = DependencyState.TableName,
                    AttributeDefinitions =
                    [
                        new AttributeDefinition("Id", ScalarAttributeType.S),
                    ],
                    KeySchema =
                    [
                        new KeySchemaElement("Id", KeyType.HASH),
                    ],
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                });
        }
        catch (ResourceInUseException)
        {
        }

        await dynamoDb.PutItemAsync(
            new PutItemRequest
            {
                TableName = DependencyState.TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["Id"] = new("contract-user"),
                },
            });

        var sns = services.GetRequiredService<IAmazonSimpleNotificationService>();
        var topic = await sns.CreateTopicAsync(new CreateTopicRequest { Name = DependencyState.TopicName });

        var dependencyState = services.GetRequiredService<DependencyState>();
        dependencyState.QueueUrl = queue.QueueUrl;
        dependencyState.TopicArn = topic.TopicArn;
    }

    private static async Task<IConnectionMultiplexer> ConnectToRedisAsync()
    {
        const int maxAttempts = 30;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var connection = await ConnectionMultiplexer.ConnectAsync(RedisEndpoint);
                await connection.GetDatabase().PingAsync();
                return connection;
            }
            catch (Exception exception) when (attempt < maxAttempts)
            {
                lastException = exception;
                Console.WriteLine(
                    $"Redis connection attempt {attempt} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException("Redis did not become available.", lastException);
    }

    private static ResourceBuilder CreateResourceBuilder()
    {
        return ResourceBuilder.CreateDefault().AddService(ServiceName);
    }

    private static Sampler CreateRootSampler()
    {
        var configuredSampler = Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER");
        return configuredSampler?.Trim().ToLowerInvariant() switch
        {
            "always_on" => new AlwaysOnSampler(),
            "always_off" => new AlwaysOffSampler(),
            _ => throw new InvalidOperationException(
                $"Unsupported OTEL_TRACES_SAMPLER '{configuredSampler}'. Expected always_on or always_off."),
        };
    }

    private static SpanMetricsMode ParseMode(string? configuredMode)
    {
        return configuredMode?.Trim().ToLowerInvariant() switch
        {
            "auto" => SpanMetricsMode.Auto,
            "manual" => SpanMetricsMode.Manual,
            "manual-global" => SpanMetricsMode.ManualGlobal,
            _ => throw new InvalidOperationException(
                $"Unsupported SPAN_METRICS_MODE '{configuredMode}'. " +
                "Expected auto, manual, or manual-global."),
        };
    }

    private static void LogModeAndActivationEnvironment(SpanMetricsMode mode)
    {
        Console.WriteLine($"Resolved SpanMetricsMode={mode}.");
        foreach (var key in new[]
                 {
                     "CORECLR_ENABLE_PROFILING",
                     "CORECLR_PROFILER_PATH",
                     "DOTNET_STARTUP_HOOKS",
                     "OTEL_DOTNET_AUTO_HOME",
                     "DOTNET_ADDITIONAL_DEPS",
                     "DOTNET_SHARED_STORE",
                     "OTEL_DOTNET_AUTO_PLUGINS",
                 })
        {
            var value = Environment.GetEnvironmentVariable(key);
            Console.WriteLine($"{key}={value ?? string.Empty}");
        }
    }

    private static void LogInstrumentationAssemblies()
    {
        foreach (var assemblyName in new[]
                 {
                     "OpenTelemetry.Instrumentation.AspNetCore",
                     "OpenTelemetry.Instrumentation.Http",
                     "OpenTelemetry.Instrumentation.EntityFrameworkCore",
                     "OpenTelemetry.Instrumentation.GrpcNetClient",
                     "OpenTelemetry.Instrumentation.StackExchangeRedis",
                     "OpenTelemetry.Instrumentation.AWS",
                     "OpenTelemetry.Extensions.AWS",
                 })
        {
            var assembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
            Console.WriteLine(
                assembly is null
                    ? $"Instrumentation assembly {assemblyName}=not-loaded"
                    : $"Instrumentation assembly {assembly.FullName} location={GetAssemblyLocation(assembly)}");
        }
    }

    private static string GetAssemblyLocation(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (NotSupportedException)
        {
            return "<dynamic>";
        }
    }

    internal enum SpanMetricsMode
    {
        Auto,
        Manual,
        ManualGlobal,
    }

    private sealed class ManualProviders : IDisposable
    {
        private readonly TracerProvider tracerProvider;
        private readonly MeterProvider meterProvider;

        public ManualProviders(TracerProvider tracerProvider, MeterProvider meterProvider)
        {
            this.tracerProvider = tracerProvider;
            this.meterProvider = meterProvider;
        }

        public void Dispose()
        {
            this.tracerProvider.Dispose();
            this.meterProvider.Dispose();
        }
    }
}

public sealed class ContractHealthService : Health.HealthBase
{
    public override Task<HealthCheckResponse> Check(
        HealthCheckRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new HealthCheckResponse { Status = "SERVING" });
    }

    public override Task<HealthCheckResponse> Ready(
        HealthCheckRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new HealthCheckResponse { Status = "SERVING" });
    }
}

public sealed class ContractDbContext : DbContext
{
    public ContractDbContext(DbContextOptions<ContractDbContext> options)
        : base(options)
    {
    }

    public DbSet<ContractUser> Users => this.Set<ContractUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContractUser>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Name).IsRequired();
        });
    }
}

public sealed class ContractUser
{
    public int Id { get; set; }

    public required string Name { get; set; }
}

public sealed class DependencyState
{
    public const string BucketName = "cloudwatch-plugin-otel-contract";
    public const string QueueName = "orders";
    public const string TableName = "contract_users";
    public const string TopicName = "orders";

    public string QueueUrl { get; set; } = string.Empty;

    public string TopicArn { get; set; } = string.Empty;
}
