using MockCollector.Services;
using Newtonsoft.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton(new MockCollectorTraceService()); 
builder.Services.AddSingleton(new MockCollectorMetricsService());
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<MockCollectorMetricsService>();
app.MapGrpcService<MockCollectorTraceService>();
// Retrieve the singleton instances from the service provider
var traceCollector = app.Services.GetRequiredService<MockCollectorTraceService>();
var metricsCollector = app.Services.GetRequiredService<MockCollectorMetricsService>();

if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.MapGet("/clear", async context =>
{
    traceCollector.ClearRequests();
    metricsCollector.ClearRequests();
    await context.Response.WriteAsync("OK");
});
                            
app.MapGet("/get-traces", async context =>
{
    // TODO: Implement serialization for MockCollector with Protobuf
    // https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/23
    var requests = traceCollector.GetRequests();
    var res = JsonConvert.SerializeObject(requests);
    await context.Response.WriteAsync(res);
});

app.MapGet("/get-metrics", async context =>
{
    var requests = metricsCollector.GetRequests();
    var res = JsonConvert.SerializeObject(requests);
    await context.Response.WriteAsync(res);
});
    
app.MapGet("/health", async context =>
{
    await context.Response.WriteAsync("OK");
});

app.Run();