// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Reflection;
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

    private const string LocalStackEndpoint = "http://localstack:4566";
    private const string RedisEndpoint = "redis:6379,abortConnect=false";
    private const string RegionName = "us-east-1";
    private const string DatabaseConnectionString = "Data Source=/tmp/span-metrics-contract.db";
    private const string RedisKey = "contract-test";
    private const string RedisValue = "ok";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static string ServiceName =>
        Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ??
        "cloudwatch-plugin-otel-contract-test";

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
            case SpanMetricsMode.ManualGlobalProviders:
                ConfigureManualGlobalProviders(builder.Services);
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

    private static void ConfigureManualGlobalProviders(IServiceCollection services)
    {
        // Hosting mode lets dependency injection own both providers; no startup hook participates.
        Console.WriteLine(
            "SPAN_METRICS_MODE=manual-global-providers -> ConfigureManualGlobalProviders");
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
            Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? "testing",
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? "testing");

        services.AddSingleton<IConnectionMultiplexer>(redisConnection);
        services.AddDbContext<ContractDbContext>(
            options => options.UseSqlite(DatabaseConnectionString));
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
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/downstream", () => Results.Ok(new { status = "ok" }));
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

                using (var databaseActivity = ActivitySource.StartActivity("SELECT users", ActivityKind.Client))
                {
                    databaseActivity?.SetTag("db.system", "sqlite");
                    databaseActivity?.SetTag("db.operation", "SELECT");
                    databaseActivity?.SetTag("db.sql.table", "users");
                }

                _ = await s3.ListBucketsAsync(cancellationToken);
                _ = await sqs.SendMessageAsync(
                    new SendMessageRequest
                    {
                        QueueUrl = dependencyState.QueueUrl,
                        MessageBody = "contract test",
                    },
                    cancellationToken);
                _ = await sns.PublishAsync(
                    new PublishRequest
                    {
                        TopicArn = dependencyState.TopicArn,
                        Message = "contract test",
                    },
                    cancellationToken);
                _ = await dynamoDb.GetItemAsync(
                    new GetItemRequest
                    {
                        TableName = DependencyState.TableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["id"] = new("1"),
                        },
                    },
                    cancellationToken);

                var redisDatabase = redis.GetDatabase();
                _ = await redisDatabase.StringSetAsync(RedisKey, RedisValue);
                _ = await redisDatabase.StringGetAsync(RedisKey);
                _ = await grpcClient.CheckAsync(
                    new HealthCheckRequest(),
                    cancellationToken: cancellationToken);

                using (var consumerActivity = ActivitySource.StartActivity("orders receive", ActivityKind.Consumer))
                {
                    consumerActivity?.SetTag("messaging.system", "contract-broker");
                    consumerActivity?.SetTag("messaging.operation.name", "receive");
                    consumerActivity?.SetTag("messaging.operation.type", "receive");
                    consumerActivity?.SetTag("messaging.destination.name", "orders");
                }

                return Results.Ok(new { status = "ok" });
            });
        app.MapGet("/error", ThrowContractError);
    }

    private static IResult ThrowContractError()
    {
        Activity.Current?.SetStatus(ActivityStatusCode.Error);
        throw new InvalidOperationException("expected contract-test error");
    }

    private static Task InitializeDependenciesAsync(IServiceProvider services)
    {
        return RetryAsync(
            () => InitializeDependenciesOnceAsync(services),
            "Dependency initialization",
            "Dependency initialization did not complete.");
    }

    private static async Task InitializeDependenciesOnceAsync(IServiceProvider services)
    {
        await using var connection = new SqliteConnection(DatabaseConnectionString);
        await connection.OpenAsync();
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText =
            """
            CREATE TABLE IF NOT EXISTS users (
                Id INTEGER NOT NULL CONSTRAINT PK_users PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            );
            INSERT OR IGNORE INTO users (Id, Name) VALUES (1, 'contract-test');
            """;
        await createCommand.ExecuteNonQueryAsync();

        var redis = services.GetRequiredService<IConnectionMultiplexer>();
        await redis.GetDatabase().StringSetAsync(RedisKey, RedisValue);

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
                        new AttributeDefinition("id", ScalarAttributeType.S),
                    ],
                    KeySchema =
                    [
                        new KeySchemaElement("id", KeyType.HASH),
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
                    ["id"] = new("1"),
                    ["name"] = new("contract-test"),
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
        return await RetryAsync(
            async () =>
            {
                var connection = await ConnectionMultiplexer.ConnectAsync(RedisEndpoint);
                await connection.GetDatabase().PingAsync();
                return connection;
            },
            "Redis connection",
            "Redis did not become available.");
    }

    private static async Task RetryAsync(
        Func<Task> operation,
        string operationName,
        string failureMessage)
    {
        await RetryAsync(
            async () =>
            {
                await operation();
                return true;
            },
            operationName,
            failureMessage);
    }

    private static async Task<T> RetryAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        string failureMessage)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception)
            {
                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException(failureMessage, exception);
                }

                Console.WriteLine(
                    $"{operationName} attempt {attempt} failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException(failureMessage);
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
            null or "" => new AlwaysOnSampler(),
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
            null or "" => SpanMetricsMode.Auto,
            "auto" => SpanMetricsMode.Auto,
            "manual" => SpanMetricsMode.Manual,
            "manual-global-providers" => SpanMetricsMode.ManualGlobalProviders,
            _ => throw new InvalidOperationException(
                $"Unsupported SPAN_METRICS_MODE '{configuredMode}'. " +
                "Expected auto, manual, or manual-global-providers."),
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
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
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
            var assembly = loadedAssemblies.FirstOrDefault(candidate =>
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
        ManualGlobalProviders,
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
    public const string BucketName = "contract-test";
    public const string QueueName = "orders";
    public const string TableName = "users";
    public const string TopicName = "orders";

    public string QueueUrl { get; set; } = string.Empty;

    public string TopicArn { get; set; } = string.Empty;
}
