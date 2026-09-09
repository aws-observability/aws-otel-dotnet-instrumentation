// .NET Dynamic Instrumentation — NATIVE PROFILER demo.
//
// Proof this is the REAL native CLR profiler weaving code (not our own logic faking a capture):
//   • The SAME binary is run twice by run-profiler-demo.sh — profiler OFF, then ON.
//       OFF  → PROBE_ACTIVE unset → 0 captures  (no profiler = nothing woven)
//       ON   → PROBE_ACTIVE=1     → captures    (only the native ReJIT weave can do this)
//   • NEGATIVE CONTROL: a second method the config does NOT name is called every run and must
//       NEVER be captured — proving the config selects the target, not blanket interception.
//
// The method bodies do NOT touch DI at all — capture happens purely because the native profiler
// rewrites the method at JIT time to call our DiIntegration callbacks.

using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

static void L(string s) => Console.WriteLine(s);

var profilerLoaded = Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") == "1";
var mode = profilerLoaded ? "[92mPROFILER ON[0m" : "[91mPROFILER OFF[0m";

L("");
L($"────────────────────────────────────────────────────────────────────");
L($"  RUN MODE: {mode}   (CORECLR_ENABLE_PROFILING={Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") ?? "unset"})");
L($"────────────────────────────────────────────────────────────────────");

// A config naming ONLY PaymentService.Charge — same shape the backend would deliver.
var config = new InstrumentationConfiguration
{
    Type = InstrumentationType.PROBE,
    CodeUnit = "Demo",
    ClassName = "PaymentService",
    MethodName = "Charge",
    LocationHash = "demo-charge-hash",
    Capture = CaptureConfiguration.Default with { CaptureArguments = ["orderId"] },
};

var registry = new InstrumentationRegistry();
DiIntegrationHelper.Configure(registry);
registry.Register(config);
var translator = new ProfilerTranslator();
var applied = translator.ApplyInstrumentation(config);
L($"  registered + ApplyInstrumentation(Charge) => {applied}");
if (profilerLoaded) { L("  waiting 700ms for native ReJIT…"); Thread.Sleep(700); }

L("");
L("  invoking application methods (their bodies never call DI):");
DIDataStore.Clear();
var charged = new Demo.PaymentService().Charge("order-4242");   // NAMED by the config
var refunded = new Demo.PaymentService().Refund("order-9999");  // NEGATIVE CONTROL (not named)
Thread.Sleep(50);
L($"     PaymentService.Charge(\"order-4242\")  → {charged}");
L($"     PaymentService.Refund(\"order-9999\")  → {refunded}   (control: not in config)");

var caps = DIDataStore.Drain();
var chargeCap = caps.FirstOrDefault(c => c.InstrumentationKey == "Demo.PaymentService.Charge");
var refundCap = caps.FirstOrDefault(c => c.InstrumentationKey == "Demo.PaymentService.Refund");

L("");
L("  ═══ RESULT ═══");
L($"  captures collected: {caps.Count}");
if (chargeCap != null)
{
    L("  [92m✔ Charge WAS captured by the native profiler:[0m");
    var snapshot = new Dictionary<string, object?>
    {
        ["method"] = chargeCap.InstrumentationKey,
        ["arguments"] = chargeCap.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Value),
        ["return_value"] = chargeCap.ReturnValue?.Value,
        ["thread"] = chargeCap.ThreadName,
        ["timestamp_ms"] = chargeCap.TimestampMs,
    };
    foreach (var line in JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }).Split('\n'))
    {
        L("      " + line);
    }
}
else
{
    L("  [91m✗ Charge NOT captured[0m  (expected when PROFILER OFF — nothing is woven)");
}

L(refundCap == null
    ? "  [92m✔ Refund NOT captured[0m  (negative control held: config selects the target)"
    : "  [91m✗ Refund WAS captured — control FAILED[0m");

// Exit code encodes the outcome so the orchestrator can assert OFF vs ON.
var chargeCaptured = chargeCap != null;
var controlHeld = refundCap == null;
if (!controlHeld) { return 3; }                 // control must always hold
return chargeCaptured ? 1 : 0;                  // 1 = captured (ON), 0 = not (OFF)

namespace Demo
{
    public class PaymentService
    {
        public string Charge(string orderId) => $"charged:{orderId}";

        public string Refund(string orderId) => $"refunded:{orderId}";
    }
}
