using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

// TODO: MockCollectorClient is not fully implemented
// https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/23

/// <summary>
/// The mock collector client is used to interact with the Mock collector image, used in the tests.
/// </summary>
public class MockCollectorClient(HttpClient client)
{
    private readonly TimeSpan TIMEOUT_DELAY = TimeSpan.FromSeconds(20);
    private static readonly ILogger logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MockCollectorClient>();
    private static readonly JsonSerializerSettings EXPORT_TRACE_SERVICE_REQUEST_LIST = new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.All
    };
    private static readonly JsonSerializerSettings EXPORT_METRICS_SERVICE_REQUEST_LIST = new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.All
    };
    private const int WAIT_INTERVAL_MS = 100;

    static MockCollectorClient()
    {
        // Setup Serializer for protobuf
    }

    private readonly HttpClient client = client;

    /// <summary>
    /// Get all traces that are currently stored in the collector
    /// </summary>
    /// <returns>List of `object`.</returns>
    public async Task<List<object>> GetTracesAsync()
    {
        Thread.Sleep(3000);
        var res = await this.client.GetAsync("/get-traces");
        var responseBody = await res.Content.ReadAsStringAsync();
        var items = JsonConvert.DeserializeObject<List<object>>(responseBody);
        return items!;
    }
    
    public async Task ClearSignalsAsync()
    {
        await this.client.GetAsync("/clear");
    }
}