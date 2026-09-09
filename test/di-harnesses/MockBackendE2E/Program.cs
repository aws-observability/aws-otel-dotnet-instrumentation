// Mock-backend E2E: stands up a real HTTP server serving the two production DI endpoints
// (list-instrumentation-configurations, report-instrumentation-configuration-status) with a
// Dotnet-language config, then drives the REAL DynamicInstrumentationClient against it.
//
// Proves the agent<->backend wire loop:
//   1. client fetches configs -> backend returns a Dotnet PROBE config -> client parses it
//   2. client reports status -> backend receives the READY/ACTIVE/etc. payload
//
// Exit code = number of failed checks (0 = all pass).
using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

// The client speaks PascalCase on the wire (case-sensitive). ASP.NET's Results.Json defaults
// to camelCase, which the client would silently fail to bind — so serialize PascalCase here.
var pascalJson = new JsonSerializerOptions { PropertyNamingPolicy = null };

int failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok)
    {
        Console.WriteLine($"[PASS] {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"[FAIL] {name}{(detail != null ? ": " + detail : string.Empty)}");
    }
}

// --- Mock backend ---------------------------------------------------------
// Captures what the client POSTs so we can assert on the status report.
var receivedStatusBodies = new List<string>();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5199");
var app = builder.Build();

// The exact JSON shape InstrumentationConfiguration.Parse expects, with Language=Dotnet.
app.MapPost("/list-instrumentation-configurations", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var requestBody = await reader.ReadToEndAsync();
    Console.WriteLine($"[backend] list request: {requestBody}");

    var payload = new
    {
        Changed = true,
        // Numeric epoch seconds — matches the LIVE backend (application-signals.*.api.aws), which
        // emits SyncedAt as a JSON number, NOT the ISO string the spec documents.
        SyncedAt = 1783975100.0,
        SyncInterval = 60,
        NextToken = (string?)null,
        LatestConfigurations = new object[]
        {
            new
            {
                InstrumentationType = "PROBE",
                LocationHash = "dotnet-loc-hash-1",
                Location = new
                {
                    CodeLocation = new
                    {
                        Language = "Dotnet",
                        CodeUnit = "MyApp",
                        ClassName = "OrderService",
                        MethodName = "Process",
                        FilePath = "OrderService.cs",
                        LineNumber = 0,
                    },
                },
                CaptureConfiguration = new
                {
                    CodeCapture = new
                    {
                        CaptureArguments = new[] { "orderId" },
                        CaptureReturn = true,
                        CaptureStackTrace = false,
                    },
                },
            },
        },
    };
    return Results.Json(payload, pascalJson);
});

app.MapPost("/report-instrumentation-configuration-status", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    receivedStatusBodies.Add(body);
    Console.WriteLine($"[backend] status report: {body}");
    return Results.Ok();
});

await app.StartAsync();
Console.WriteLine("=== Mock Backend E2E ===\n[backend] listening on http://127.0.0.1:5199\n");

// --- Drive the REAL client ------------------------------------------------
using var http = new HttpClient();
var client = new DynamicInstrumentationClient(http, "http://127.0.0.1:5199", "test-service", "test-env");

// 1. Fetch configs
var result = await client.FetchConfigurationsAsync(InstrumentationType.PROBE);
Check("fetch succeeded", result.Success, $"Success={result.Success}");
Check("changed flag latched", result.Changed);
Check("syncedAt round-tripped (numeric)",
    result.SyncedAt is { } sa && sa.ValueKind == System.Text.Json.JsonValueKind.Number,
    result.SyncedAt?.GetRawText() ?? "(null)");
Check("one configuration returned", result.Configurations.Length == 1, $"count={result.Configurations.Length}");

// 2. Parse the config the way the Manager would
InstrumentationConfiguration? parsed = result.Configurations.Length == 1
    ? InstrumentationConfiguration.Parse(result.Configurations[0])
    : null;
Check("Dotnet config parsed (not rejected)", parsed != null);
if (parsed != null)
{
    Check("type = PROBE", parsed.Type == InstrumentationType.PROBE);
    Check("TypeName resolved", parsed.TypeName == "MyApp.OrderService", parsed.TypeName);
    Check("method resolved", parsed.MethodName == "Process", parsed.MethodName);
    Check("locationHash resolved", parsed.LocationHash == "dotnet-loc-hash-1", parsed.LocationHash);
    Check("method-level (LineNumber 0)", parsed.IsMethodLevel);
    Check("CaptureReturn parsed true", parsed.Capture.CaptureReturn);
    Check("CaptureArguments parsed", parsed.Capture.CaptureArguments is { Length: 1 } a && a[0] == "orderId");
}

// 3. Report status back
await client.ReportStatusAsync(new List<StatusEntry>
{
    new()
    {
        InstrumentationType = "PROBE",
        LocationHash = "dotnet-loc-hash-1",
        Status = "READY",
        Time = 1_783_000_005,
    },
});
Check("backend received a status report", receivedStatusBodies.Count == 1, $"count={receivedStatusBodies.Count}");
if (receivedStatusBodies.Count == 1)
{
    var doc = JsonDocument.Parse(receivedStatusBodies[0]);
    var root = doc.RootElement;
    Check("status: service echoed", root.GetProperty("Service").GetString() == "test-service");
    var entry = root.GetProperty("Configurations")[0];
    Check("status: READY reported", entry.GetProperty("Status").GetString() == "READY");
    Check("status: locationHash reported", entry.GetProperty("LocationHash").GetString() == "dotnet-loc-hash-1");
    // Wire shape parity with Java/Python/JS + the backend status-report type.
    Check("status: SignalType=SNAPSHOT", entry.TryGetProperty("SignalType", out var st) && st.GetString() == "SNAPSHOT");
    Check("status: Time is epoch seconds (numeric)", entry.TryGetProperty("Time", out var tm) && tm.ValueKind == JsonValueKind.Number);
    Check("status: no DisableReason field", !entry.TryGetProperty("DisableReason", out _));
}

await app.StopAsync();
Console.WriteLine($"\n=== {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
return failures;
