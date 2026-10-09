// .NET Dynamic Instrumentation — PROBE + BREAKPOINT end-to-end demo.
//
// Full loop, all real components:
//   in-process MOCK BACKEND (serves a PROBE and a BREAKPOINT config, PascalCase wire shape)
//     → real DynamicInstrumentationClient fetches + parses both
//     → real ProfilerTranslator applies → native CLR profiler weaves (ReJIT)
//     → invoke targets → capture
//
// The visible differentiator between the two instrumentation types:
//   PROBE      → permanent, no hit limit → captures EVERY call
//   BREAKPOINT → temporary, MaxHits gate → captures only up to MaxHits, then stops
//
// Proof it's the native profiler (same as the profiler demo): run with CORECLR_ENABLE_PROFILING
// OFF → 0 captures; ON → captures. The native profiler's own log records the ReJIT rewrites.

using System.Net;
using System.Text;
using System.Text.Json;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Capture;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Client;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.FunctionLevel;
using AWS.Distro.OpenTelemetry.DynamicInstrumentation.Model;

static void L(string s) => Console.WriteLine(s);

var profilerOn = Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING") == "1";
int fails = 0;

L("");
L("════════════════════════════════════════════════════════════════════════════");
L("  .NET DYNAMIC INSTRUMENTATION — PROBE + BREAKPOINT (mock backend → profiler)");
L($"  profiler: {(profilerOn ? "[92mON[0m" : "[91mOFF (baseline)[0m")}");
L("════════════════════════════════════════════════════════════════════════════");

// ── MOCK BACKEND ────────────────────────────────────────────────────────────
// Serves the two production endpoints with PascalCase JSON (case-sensitive, as the real backend).
// One PROBE config (Demo.OrderApi.Process) and one BREAKPOINT config (Demo.OrderApi.Checkout,
// MaxHits=3). Both target real methods this process defines below.
const int Port = 5711;
var probe = new
{
    InstrumentationType = "PROBE",
    LocationHash = "probe-process",
    Location = new { CodeLocation = new { Language = "Dotnet", CodeUnit = "Demo", ClassName = "OrderApi", MethodName = "Process", FilePath = "OrderApi.cs" } },
    CaptureConfiguration = new { CodeCapture = new { CaptureArguments = new[] { "orderId" }, CaptureReturn = true } },
};
// NOTE: PROBE and BREAKPOINT targets are on SEPARATE classes. The engine now resolves a woven call by
// (TypeName, arity) — so different-arity methods on one class no longer collide (#3, PR4). But BOTH
// methods here take one arg (Process(orderId), Checkout(cartId)), i.e. same arity, which arity can't
// disambiguate (the documented same-arity residual). So they stay on separate classes for this demo.
var breakpoint = new
{
    InstrumentationType = "BREAKPOINT",
    LocationHash = "bp-checkout",
    Location = new { CodeLocation = new { Language = "Dotnet", CodeUnit = "Demo", ClassName = "CheckoutApi", MethodName = "Checkout", FilePath = "CheckoutApi.cs" } },
    CaptureConfiguration = new { CodeCapture = new { CaptureArguments = new[] { "cartId" }, CaptureReturn = true, CaptureLimits = new { MaxHits = 3 } } },
};

var pascal = new JsonSerializerOptions { PropertyNamingPolicy = null };
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
listener.Start();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { break; }

        var path = ctx.Request.Url!.AbsolutePath;
        using var reader = new StreamReader(ctx.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        object payload;
        if (path.Contains("list-instrumentation-configurations"))
        {
            // Return the config whose type matches the request (client fetches PROBE and BREAKPOINT separately).
            var isProbe = body.Contains("\"PROBE\"");
            payload = new
            {
                Changed = true,
                SyncedAt = 1784050000.0,
                SyncInterval = 300,
                NextToken = (string?)null,
                LatestConfigurations = new[] { isProbe ? (object)probe : breakpoint },
            };
        }
        else
        {
            payload = new { UnprocessedStatusEvents = Array.Empty<object>() };
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, pascal));
        ctx.Response.StatusCode = 200;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
});
L($"\n[mock backend] listening on http://127.0.0.1:{Port} — serving 1 PROBE + 1 BREAKPOINT config");

// ── REAL CLIENT: fetch + parse both configs ────────────────────────────────
var registry = new InstrumentationRegistry();
DiIntegrationHelper.Configure(registry);
var translator = new ProfilerTranslator();

using var http = new HttpClient();
var client = new DynamicInstrumentationClient(http, $"http://127.0.0.1:{Port}", "demo-svc", "staging");

L("\n── FETCH + PARSE (real client ← mock backend) ──────────────────────────────");
var configs = new List<InstrumentationConfiguration>();
foreach (var type in new[] { InstrumentationType.PROBE, InstrumentationType.BREAKPOINT })
{
    var resp = await client.FetchConfigurationsAsync(type);
    foreach (var el in resp.Configurations)
    {
        var cfg = InstrumentationConfiguration.Parse(el);
        if (cfg == null) { continue; }
        configs.Add(cfg);
        var hits = cfg.Capture.MaxHits.HasValue ? $"MaxHits={cfg.Capture.MaxHits}" : "no hit limit";
        L($"   fetched {cfg.Type,-10} {cfg.TypeName}.{cfg.MethodName}   ({hits})");
    }
}

// ── APPLY → native weave ────────────────────────────────────────────────────
L("\n── APPLY via ProfilerTranslator → native ReJIT weave ───────────────────────");
foreach (var cfg in configs)
{
    registry.Register(cfg);
    var applied = translator.ApplyInstrumentation(cfg);
    L($"   {cfg.Type,-10} {cfg.MethodName}  => {applied}");
}

if (profilerOn) { L("   waiting 700ms for native ReJIT…"); Thread.Sleep(700); }

// ── INVOKE: PROBE captures every call; BREAKPOINT gates at MaxHits ──────────
L("\n── INVOKE (methods called 5× each; bodies never touch DI) ──────────────────");
DIDataStore.Clear();
var orderApi = new Demo.OrderApi();
var checkoutApi = new Demo.CheckoutApi();
for (int i = 0; i < 5; i++) { orderApi.Process($"order-{i}"); }       // PROBE target
for (int i = 0; i < 5; i++) { checkoutApi.Checkout($"cart-{i}"); }    // BREAKPOINT target (MaxHits=3)
Thread.Sleep(50);

var caps = DIDataStore.Drain();
var probeHits = caps.Count(c => c.InstrumentationKey == "Demo.OrderApi.Process");
var bpHits = caps.Count(c => c.InstrumentationKey == "Demo.CheckoutApi.Checkout");

L("\n── RESULT ──────────────────────────────────────────────────────────────────");
if (!profilerOn)
{
    L($"   PROBE captures:      {probeHits}   (profiler OFF → expect 0)");
    L($"   BREAKPOINT captures: {bpHits}   (profiler OFF → expect 0)");
    if (probeHits == 0 && bpHits == 0) { L("   [92m✔ baseline: nothing woven without the profiler[0m"); }
    else { L("   [91m✗ unexpected capture with profiler OFF[0m"); fails++; }
}
else
{
    L($"   PROBE      Process  called 5× → captured {probeHits}   (permanent, no limit → expect 5)");
    L($"   BREAKPOINT Checkout called 5× → captured {bpHits}   (MaxHits=3 → expect 3)");
    if (probeHits == 5) { L("   [92m✔ PROBE captured every call[0m"); } else { L("   [91m✗ PROBE expected 5[0m"); fails++; }
    if (bpHits == 3) { L("   [92m✔ BREAKPOINT gated at MaxHits=3 (2 calls dropped)[0m"); } else { L("   [91m✗ BREAKPOINT expected 3[0m"); fails++; }

    var sample = caps.FirstOrDefault(c => c.InstrumentationKey == "Demo.CheckoutApi.Checkout");
    if (sample != null)
    {
        var snap = new Dictionary<string, object?>
        {
            ["method"] = sample.InstrumentationKey,
            ["type"] = "BREAKPOINT",
            ["arguments"] = sample.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.Value),
            ["return_value"] = sample.ReturnValue?.Value,
        };
        L("\n   sample BREAKPOINT snapshot:");
        foreach (var line in JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }).Split('\n')) { L("     " + line); }
    }
}

listener.Stop();
L("");
L("════════════════════════════════════════════════════════════════════════════");
L(fails == 0
    ? "  ✅ Both PROBE and BREAKPOINT flow end-to-end: mock backend → client → native profiler."
    : $"  ❌ {fails} check(s) failed");
L("════════════════════════════════════════════════════════════════════════════");
return fails;

namespace Demo
{
    public class OrderApi
    {
        public string Process(string orderId) => $"processed:{orderId}";
    }

    public class CheckoutApi
    {
        public string Checkout(string cartId) => $"checkout:{cartId}";
    }
}
