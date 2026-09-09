// Full Dynamic Instrumentation E2E — drives the ENTIRE loop through the EXACT GA path.
//
// Nothing here bypasses production code except the backend, which is a local HTTP server the agent
// talks to over the real DynamicInstrumentationClient (swap OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL to
// point at the real backend instead). The flow, end to end:
//
//   1. Set the SAME env vars GA reads, point them at the in-proc mock backend + in-proc OTLP receiver.
//   2. DynamicInstrumentationConfig.FromEnvironment() + DynamicInstrumentationManager.Initialize(config)
//      — byte-for-byte the GA entry point (see Plugin.InitializeDynamicInstrumentation).
//   3. The real poller fetches configs -> real ProfilerTranslator applies -> real native profiler ReJIT
//      weaves our DiIntegrationN callbacks into the target methods.
//   4. We invoke the target methods; the woven callbacks capture into DIDataStore.
//   5. The real DISnapshotCollector drains -> the real DISnapshotOtlpEmitter exports OTLP/HTTP.
//   6. Manager.Shutdown() flushes the exporter; we decode the received protobuf LogRecords.
//   7. Assert: snapshots arrived on the OTLP wire with the right event name / aws.di.* attributes / body,
//      AND the backend received READY/ACTIVE status reports.
//
// Exit code = number of FAILED checks (0 = all pass).

using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;

const string TargetType = "E2E.OrderService";
const string TargetMethod = "Process";
const string LocationHash = "e2e-loc-process-1";
const string SnapshotEventName = "aws.dynamic_instrumentation.snapshot";

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

Console.WriteLine("=== DI Full-Loop E2E (real Manager + real ReJIT + real OTLP export) ===\n");

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Console.WriteLine("[FATAL] Native profiler not loaded. Run via run-e2e.sh.");
    return 99;
}

// ---------------------------------------------------------------------------
// In-proc OTLP/HTTP logs receiver — the ONLY telemetry sink. Receives the exact
// protobuf the DI OTLP exporter posts (OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf).
// ---------------------------------------------------------------------------
var receivedLogRecords = new List<LogRecord>();
var recvLock = new object();

// ---------------------------------------------------------------------------
// In-proc mock backend — the ONLY mocked component. Serves the two production DI
// endpoints and records status reports. Swap the API_URL env to hit the real backend.
// ---------------------------------------------------------------------------
var receivedStatusBodies = new List<string>();
var pascalJson = new JsonSerializerOptions { PropertyNamingPolicy = null };

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5299");
var app = builder.Build();

// --- Backend: list-instrumentation-configurations (Dotnet PROBE targeting our OrderService.Process) ---
app.MapPost("/list-instrumentation-configurations", () =>
{
    var payload = new
    {
        Changed = true,
        SyncedAt = 1783975100.0,
        SyncInterval = 60,
        NextToken = (string?)null,
        LatestConfigurations = new object[]
        {
            new
            {
                InstrumentationType = "PROBE",
                LocationHash,
                Location = new
                {
                    CodeLocation = new
                    {
                        Language = "Dotnet",
                        CodeUnit = "E2E",
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
    lock (recvLock)
    {
        receivedStatusBodies.Add(body);
    }

    Console.WriteLine($"[backend] status report: {body}");
    return Results.Ok();
});

// --- OTLP/HTTP logs receiver: /v1/logs (protobuf) ---
app.MapPost("/v1/logs", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var export = ExportLogsServiceRequest.Parser.ParseFrom(ms.ToArray());
    lock (recvLock)
    {
        foreach (var rl in export.ResourceLogs)
        {
            foreach (var sl in rl.ScopeLogs)
            {
                receivedLogRecords.AddRange(sl.LogRecords);
            }
        }
    }

    // OTLP expects an ExportLogsServiceResponse (empty is success).
    var resp = new ExportLogsServiceResponse().ToByteArray();
    return Results.Bytes(resp, "application/x-protobuf");
});

await app.StartAsync();
Console.WriteLine("[harness] backend + OTLP receiver on http://127.0.0.1:5299\n");

// ---------------------------------------------------------------------------
// Configure the agent EXACTLY as GA does: env vars -> FromEnvironment() -> Initialize().
// ---------------------------------------------------------------------------
Environment.SetEnvironmentVariable("OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED", "true");
Environment.SetEnvironmentVariable("OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL", "http://127.0.0.1:5299");
// Poll fast so the E2E doesn't wait the 600s/60s GA defaults (floored at 10s by the config).
Environment.SetEnvironmentVariable("OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL", "10");
Environment.SetEnvironmentVariable("OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL", "10");
// Snapshots -> our in-proc OTLP receiver, over HTTP/protobuf so we can decode them.
Environment.SetEnvironmentVariable("OTEL_AWS_OTLP_LOGS_ENDPOINT", "http://127.0.0.1:5299/v1/logs");
Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "e2e-service");

// Ensure the target type is loaded before the poller applies (in GA the app's types are already
// loaded; here we force it so apply resolves the type instead of returning TypeNotLoaded and retrying
// on the next 10s poll — past our wait window). Referencing the type triggers assembly/type load.
_ = typeof(E2E.OrderService).FullName;

// The native profiler's rewriter callback SKIPS a weave if the managed profiler assembly
// (OpenTelemetry.AutoInstrumentation) is not yet loaded into the AppDomain — and ReJIT is not retried,
// so the target would stay un-woven. In GA the profiler is fully initialized before any DI config
// applies. Here the StartupHook loads it asynchronously at process start, so wait for it before we
// Initialize DI (whose fast poll applies almost immediately). Poll the loaded assemblies for it.
Console.WriteLine("[harness] waiting for the managed profiler to load into the AppDomain...");
var profilerLoaded = false;
for (int i = 0; i < 100; i++) // up to ~10s
{
    if (AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name == "OpenTelemetry.AutoInstrumentation"))
    {
        profilerLoaded = true;
        break;
    }

    Thread.Sleep(100);
}

Check("managed profiler loaded into AppDomain (weave prerequisite)", profilerLoaded);

// THE GA ENTRY POINT — identical to Plugin.InitializeDynamicInstrumentation().
var config = AWS.Distro.OpenTelemetry.DynamicInstrumentation.Config.DynamicInstrumentationConfig.FromEnvironment();
Check("config enabled via env (GA path)", config.Enabled);
Check("config API URL points at mock backend", config.ApiUrl == "http://127.0.0.1:5299", config.ApiUrl);
Check("config logs endpoint set", config.LogsEndpoint == "http://127.0.0.1:5299/v1/logs", config.LogsEndpoint);

var manager = AWS.Distro.OpenTelemetry.DynamicInstrumentation.DynamicInstrumentationManager.Instance;
manager.Initialize(config);
Check("manager initialized", manager.IsInitialized);

// ---------------------------------------------------------------------------
// Wait for the real poll -> apply -> ReJIT weave. The poller polls immediately on Start;
// give it time to fetch, apply, and for the CLR to ReJIT the target method.
// ---------------------------------------------------------------------------
Console.WriteLine("[harness] waiting for poll -> apply -> ReJIT weave...");
var registeredInTime = false;
for (int i = 0; i < 50; i++) // up to ~5s
{
    Thread.Sleep(100);
    if (manager.Registry?.Get($"{TargetType}.{TargetMethod}") != null)
    {
        registeredInTime = true;
        break;
    }
}

Check("config fetched from backend + registered (real poll)", registeredInTime);
Thread.Sleep(700); // let ReJIT finish after registration

// ---------------------------------------------------------------------------
// Invoke the target. The woven callback captures; the collector drains; the emitter exports OTLP.
// Call it a few times over a short window in case ReJIT hasn't landed on the first invocation.
// ---------------------------------------------------------------------------
string result = string.Empty;
for (int i = 0; i < 5; i++)
{
    result = new E2E.OrderService().Process("ORD-123");
    Thread.Sleep(200);
}

Check("target method returned normally under instrumentation", result == "processed:ORD-123", result);

// DIAGNOSTIC: did the woven callback capture into DIDataStore at all? Isolates a weave failure
// (0 captures => profiler never rejit-wove) from an export failure (captured but no OTLP arrived).
var capturedCount = AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture.DIDataStore.Count;
Console.WriteLine($"[diag] DIDataStore pending capture count after invoke: {capturedCount}");

// Give the collector (10ms drain loop) + OTLP batch exporter time, then flush via the GA shutdown path.
Thread.Sleep(500);
Console.WriteLine("[harness] shutting down manager (flushes OTLP exporter)...");
manager.Shutdown();
Thread.Sleep(300);

// ---------------------------------------------------------------------------
// Assert on the REAL received OTLP wire data.
// ---------------------------------------------------------------------------
List<LogRecord> snapshots;
List<string> statuses;
lock (recvLock)
{
    snapshots = receivedLogRecords.ToList();
    statuses = receivedStatusBodies.ToList();
}

var snapshot = snapshots.FirstOrDefault(r =>
    r.Attributes.Any(a => a.Key == "event.name" && a.Value.StringValue == SnapshotEventName));

Check("a snapshot LogRecord arrived over real OTLP/HTTP", snapshot != null,
    $"received {snapshots.Count} log record(s)");

if (snapshot != null)
{
    string? Attr(string key) =>
        snapshot.Attributes.FirstOrDefault(a => a.Key == key)?.Value.StringValue;

    Check("snapshot event.name", Attr("event.name") == SnapshotEventName, Attr("event.name"));
    Check("snapshot aws.di.location_hash", Attr("aws.di.location_hash") == LocationHash, Attr("aws.di.location_hash"));
    Check("snapshot aws.di.instrumentation_level = method", Attr("aws.di.instrumentation_level") == "method", Attr("aws.di.instrumentation_level"));
    Check("snapshot aws.di.method_name", Attr("aws.di.method_name") == "Process", Attr("aws.di.method_name"));

    var body = snapshot.Body?.StringValue ?? string.Empty;
    Check("snapshot body captured the argument value", body.Contains("ORD-123"), body);
    Check("snapshot body captured the return value", body.Contains("processed:ORD-123"), body);
    Check("snapshot body uses captures.entry.arguments shape", body.Contains("\"captures\"") && body.Contains("\"arguments\""));
}

// Status: the manager reported READY for the newly-applied config (real StatusReporter -> real client -> backend).
var readyReported = statuses.Any(b =>
{
    try
    {
        var root = JsonDocument.Parse(b).RootElement;
        return root.GetProperty("Configurations").EnumerateArray().Any(e =>
            e.GetProperty("LocationHash").GetString() == LocationHash
            && e.GetProperty("Status").GetString() == "READY");
    }
    catch
    {
        return false;
    }
});
Check("backend received READY status for the config (real StatusReporter)", readyReported,
    $"{statuses.Count} status report(s)");

await app.StopAsync();

Console.WriteLine($"\n=== Full-loop E2E: {(failures == 0 ? "ALL PASS" : failures + " FAILED")} ===");
return failures;

namespace E2E
{
    // The target the backend config points at. The real profiler ReJIT-weaves DiIntegration1 into Process.
    public class OrderService
    {
        public string Process(string orderId) => $"processed:{orderId}";
    }
}
