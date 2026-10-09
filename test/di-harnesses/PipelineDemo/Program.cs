// Pipeline demo — PROFILER STAGE.
// Reads the REAL backend response bytes captured live by the orchestrator (stage 2), parses them
// through the production .NET client/model, applies via the real ProfilerTranslator, lets the REAL
// native CLR profiler weave the target (ReJIT), invokes it, and prints what DIDataStore captured.
// A NEGATIVE CONTROL (a method the config does NOT name) proves the backend config — not canned
// code — is what drives the profiler. Then emits the status body the orchestrator reports live.

using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

static void Line(string s) => Console.WriteLine(s);

if (Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") != "1")
{
    Line("[FATAL] native profiler not loaded — run via run-pipeline-demo.sh");
    return 99;
}

var liveResponsePath = args.Length > 0 ? args[0] : "/tmp/pipeline-demo/list-response.json";
var statusOutPath = args.Length > 1 ? args[1] : "/tmp/pipeline-demo/status-body.json";

Line("┌─ PARSE the live backend response through the real .NET client ─────────────────");
var responseJson = File.ReadAllText(liveResponsePath);
using var doc = JsonDocument.Parse(responseJson);
var configElement = doc.RootElement.GetProperty("LatestConfigurations")[0];

// Show the config EXACTLY as the backend returned it — this is the provenance proof.
Line("   config as returned by the LIVE backend (verbatim):");
foreach (var l in JsonSerializer.Serialize(configElement, new JsonSerializerOptions { WriteIndented = true })
             .Split('\n')) { Line("     " + l); }

// The ONLY field the .NET side changes post-GA: Language Java->Dotnet. Backend rejects "Dotnet"
// today (Stage 2), so it comes back "Java"; GA adds the enum value. Nothing else is touched —
// class/method/hash/capture-config all flow through from the backend as-is.
var raw = configElement.GetRawText().Replace("\"Language\":\"Java\"", "\"Language\":\"Dotnet\"");
if (!configElement.GetRawText().Contains("\"Language\":\"Java\""))
{
    Line("[FAIL] expected Language=Java from backend; provenance check failed"); return 1;
}

using var cfgDoc = JsonDocument.Parse(raw);
var config = InstrumentationConfiguration.Parse(cfgDoc.RootElement);
if (config == null) { Line("[FAIL] parse returned null"); return 1; }
Line("");
Line($"   → parsed target: {config.TypeName}.{config.MethodName}   (hash {config.LocationHash})");
Line($"   → CaptureArguments from backend: [{string.Join(", ", config.Capture.CaptureArguments ?? System.Array.Empty<string>())}]");
Line($"   → CreatedAt from numeric epoch: {config.CreatedAt:o}");

Line("");
Line("┌─ APPLY via real ProfilerTranslator → native ReJIT weave ──────────────────────");
var registry = new InstrumentationRegistry();
DiIntegrationHelper.Configure(registry);
var translator = new ProfilerTranslator();
registry.Register(config);
var applied = translator.ApplyInstrumentation(config);
Line($"   ApplyInstrumentation({config.MethodName}) => {applied}");
if (applied != InstrumentationApplyResult.Applied) { Line("[FAIL] not applied"); return 1; }
Line("   waiting 700ms for native ReJIT…");
Thread.Sleep(700);

Line("");
Line("┌─ INVOKE both the NAMED target and a NEGATIVE CONTROL ──────────────────────────");
DIDataStore.Clear();

// (a) The method the backend config NAMES — must be captured.
var result = new Demo.PaymentService().Charge("order-4242");
// (b) A DIFFERENT method the config does NOT name — must NOT be captured (proves the config drives it).
var ignored = new Demo.PaymentService().Refund("order-9999");
Thread.Sleep(50);

var caps = DIDataStore.Drain();
var charged = caps.FirstOrDefault(c => c.InstrumentationKey == config.InstrumentationKey);
var refundKey = $"{config.TypeName}.Refund";
var refundCap = caps.FirstOrDefault(c => c.InstrumentationKey == refundKey);

int fails = 0;
Line($"   Charge(\"order-4242\")  returned {result}");
if (charged == null) { Line("   [FAIL] NAMED method was NOT captured"); fails++; }
else
{
    Line("   [PASS] NAMED method captured by the native profiler:");
    if (charged.Arguments != null)
    {
        foreach (var kv in charged.Arguments) { Line($"            arg {kv.Key} = {kv.Value.Value} ({kv.Value.Type})"); }
    }

    Line($"            return  = {charged.ReturnValue?.Value} ({charged.ReturnValue?.Type})");
}

Line($"   Refund(\"order-9999\")  returned {ignored}   (NOT named by the backend config)");
if (refundCap != null) { Line("   [FAIL] control method WAS captured — config is not what's driving the profiler"); fails++; }
else { Line("   [PASS] control method NOT captured — the backend config is what selects the target"); }

if (fails > 0) { Line($"\n[FAIL] {fails} check(s) failed"); return 1; }

Line("");
Line("┌─ build the status body the real StatusReporter would send ────────────────────");
var statusBody = new Dictionary<string, object?>
{
    ["Service"] = "pipeline-demo",
    ["Environment"] = "staging",
    ["Configurations"] = new[]
    {
        new Dictionary<string, object?>
        {
            ["InstrumentationType"] = "PROBE",
            ["SignalType"] = "SNAPSHOT",
            ["LocationHash"] = Environment.GetEnvironmentVariable("DEMO_LOCATION_HASH") ?? config.LocationHash,
            ["Status"] = "ACTIVE",
            ["Time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        },
    },
};
File.WriteAllText(statusOutPath, JsonSerializer.Serialize(statusBody));
Line($"   status body written for live report ({config.InstrumentationKey} → ACTIVE)");
return 0;

namespace Demo
{
    public class PaymentService
    {
        public string Charge(string orderId) => $"charged:{orderId}";

        // Negative control: a real, woven-eligible method the backend config never names.
        public string Refund(string orderId) => $"refunded:{orderId}";
    }
}
