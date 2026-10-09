// Standalone mock CloudWatch-Agent DI config API. Serves the production endpoints with PascalCase
// JSON, exactly as the real backend does. STATEFUL: starts empty, accepts create requests (the
// operator/console step), stores them, and returns them on the agent's list polls — the real
// create → store → list lifecycle. Prints full request/response JSON so the demo shows the wire shape.
// Post-PR3/GA, point the agent at the real backend instead — identical shape.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.XRay;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;

int port = args.Length > 0 ? int.Parse(args[0]) : 2000;
var pretty = new JsonSerializerOptions { PropertyNamingPolicy = null, WriteIndented = true };
var compact = new JsonSerializerOptions { PropertyNamingPolicy = null };

// Config-leg beta forwarding (E2E-demo ONLY). When DI_BETA_ENDPOINT is set, the three config-API requests
// (list/create/report) are SigV4-signed and relayed verbatim to the REAL backend, then beta's response is
// returned to the caller unchanged. This lets the DI client stay on its exact GA path — unsigned POST to
// this local proxy — while the request is proven against real beta: the proxy plays the CloudWatch Agent's
// signing role, which is what signs in production. Unset = fully local mock (the offline demo). Snapshots
// (/v1/logs) always stay local so the run can decode + assert the captured body.
var betaEndpoint = Environment.GetEnvironmentVariable("DI_BETA_ENDPOINT");
var betaRegion = Environment.GetEnvironmentVariable("DI_BETA_REGION") ?? "us-west-2";
var betaService = Environment.GetEnvironmentVariable("DI_BETA_SERVICE") ?? "application-signals";
var forwardClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

// In-memory store of created configs. Keyed by (InstrumentationType, LocationHash) so the mock can
// hold MANY configs of the same type — required for the bulk-apply test, where one list poll must
// return N probes so the agent applies N instrumentations in a single OnConfigurationsChanged pass.
var store = new Dictionary<(string Type, string Hash), JsonObject>();

string Pretty(JsonNode? n) => n?.ToJsonString(pretty) ?? "null";
void Banner(string s) => Console.WriteLine($"\n[96m════ {s} ════[0m");

var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
var modeLabel = betaEndpoint != null
    ? $"CONFIG-PROXY → beta (signing + forwarding list/create/report to {betaEndpoint}); snapshots local"
    : "stateful local mock; starts empty";
Console.WriteLine($"[mock-backend] listening on http://127.0.0.1:{port}  ({modeLabel})");

var shownList = new HashSet<string>();
var shownStatus = false;

// Decode a real OTLP ExportLogsServiceRequest and print machine-parseable markers for each DI snapshot.
// run-demo.sh asserts on these: a [SNAPSHOT-RECEIVED] with the expected event name + aws.di.* attributes
// and a body carrying the captured argument/return proves the full output leg end to end.
void HandleLogsExport(byte[] payload)
{
    // Raw OTLP bytes on demand, so a non-.NET consumer (the Python contract tests' AnyValue walker) can be
    // checked against a payload the real exporter produced instead of a hand-built one.
    var dumpDir = Environment.GetEnvironmentVariable("DI_SNAPSHOT_DUMP_DIR");
    if (!string.IsNullOrEmpty(dumpDir))
    {
        Directory.CreateDirectory(dumpDir);
        File.WriteAllBytes(Path.Combine(dumpDir, $"logs-{Guid.NewGuid():N}.pb"), payload);
    }

    ExportLogsServiceRequest export;
    try
    {
        export = ExportLogsServiceRequest.Parser.ParseFrom(payload);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SNAPSHOT-PARSE-ERROR] {ex.Message}");
        return;
    }

    foreach (var rl in export.ResourceLogs)
    {
        foreach (var sl in rl.ScopeLogs)
        {
            foreach (var rec in sl.LogRecords)
            {
                string Attr(string key) =>
                    rec.Attributes.FirstOrDefault(a => a.Key == key)?.Value?.StringValue ?? string.Empty;

                var eventName = Attr("event.name");
                if (eventName != "aws.dynamic_instrumentation.snapshot")
                {
                    continue; // not a DI snapshot
                }

                var locationHash = Attr("aws.di.location_hash");
                var method = Attr("aws.di.method_name");
                var level = Attr("aws.di.instrumentation_level");

                // BOTH BODY SHAPES ARE ACCEPTED, and the shape is reported. DiOtlpLogExporter sends the
                // capture tree as a real OTLP kvlist; the stock AddOtlpExporter build could only send it as
                // a JSON string. Rendering the kvlist back to compact JSON keeps every existing
                // [SNAPSHOT-BODY] assertion working against either build instead of silently going blank.
                var shape = rec.Body?.ValueCase switch
                {
                    AnyValue.ValueOneofCase.KvlistValue => "kvlist",
                    AnyValue.ValueOneofCase.StringValue => "string",
                    null => "absent",
                    _ => rec.Body.ValueCase.ToString(),
                };
                var bodyJson = rec.Body is null ? string.Empty : AnyValueToJson(rec.Body);

                Banner("BACKEND ← AGENT  OTLP /v1/logs  (DI snapshot received on the wire)");
                Console.WriteLine($"[SNAPSHOT-RECEIVED] event={eventName} locationHash={locationHash} method={method} level={level}");
                Console.WriteLine($"[SNAPSHOT-BODY-SHAPE] {shape}");
                Console.WriteLine($"[SNAPSHOT-BODY] {bodyJson}");
            }
        }
    }
}

// Render an OTLP AnyValue as compact JSON. A string body passes through verbatim (that IS the JSON); a
// kvlist/array is walked. Key order follows the wire order, which is the order the emitter wrote them, so
// the rendered text matches what the string-body build produced character for character.
string AnyValueToJson(AnyValue value) =>
    value.ValueCase == AnyValue.ValueOneofCase.StringValue
        ? value.StringValue
        : AnyValueToNode(value)?.ToJsonString(compact) ?? "null";

JsonNode? AnyValueToNode(AnyValue value)
{
    switch (value.ValueCase)
    {
        case AnyValue.ValueOneofCase.KvlistValue:
            var obj = new JsonObject();
            foreach (var kv in value.KvlistValue.Values)
            {
                obj[kv.Key] = AnyValueToNode(kv.Value);
            }

            return obj;
        case AnyValue.ValueOneofCase.ArrayValue:
            var arr = new JsonArray();
            foreach (var item in value.ArrayValue.Values)
            {
                arr.Add(AnyValueToNode(item));
            }

            return arr;
        case AnyValue.ValueOneofCase.StringValue:
            return JsonValue.Create(value.StringValue);
        case AnyValue.ValueOneofCase.BoolValue:
            return JsonValue.Create(value.BoolValue);
        case AnyValue.ValueOneofCase.IntValue:
            return JsonValue.Create(value.IntValue);
        case AnyValue.ValueOneofCase.DoubleValue:
            return JsonValue.Create(value.DoubleValue);
        default:
            return null;
    }
}

// SigV4-sign a config-API request body and relay it to the real beta backend, returning (status, body).
// Mirrors the codebase's proven signing idiom (SigV4OtlpLogExporter): DefaultRequest + AWS4Signer +
// FallbackCredentialsFactory (default AWS credential chain / env creds). The request body is forwarded
// byte-for-byte — the DI client already produced the exact PascalCase wire shape beta expects.
// Resolve credentials the SAME way awscurl/botocore does: prefer explicit AWS_* env vars (with session
// token), so the mock signs with exactly the creds the shell holds — not whatever stale cached profile the
// .NET SDK's FallbackCredentialsFactory might pick up first. Falls back to the default chain if env is unset.
ImmutableCredentials ResolveBetaCredentials()
{
    var ak = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
    var sk = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
    var st = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");
    if (!string.IsNullOrEmpty(ak) && !string.IsNullOrEmpty(sk))
    {
        Console.WriteLine($"[BETA-CREDS] using AWS_* env vars; access-key={ak[..Math.Min(8, ak.Length)]}… session-token={(string.IsNullOrEmpty(st) ? "no" : "yes")}");
        return new ImmutableCredentials(ak, sk, string.IsNullOrEmpty(st) ? null : st);
    }

    var c = FallbackCredentialsFactory.GetCredentials().GetCredentialsAsync().GetAwaiter().GetResult();
    Console.WriteLine($"[BETA-CREDS] using default chain; access-key={c.AccessKey[..Math.Min(8, c.AccessKey.Length)]}… session-token={(c.UseToken ? "yes" : "no")}");
    return c;
}

(int Status, string Body) ForwardToBeta(string apiPath, string requestBody)
{
    var url = $"{betaEndpoint!.TrimEnd('/')}/{apiPath}";
    var uri = new Uri(url);
    var bodyBytes = Encoding.UTF8.GetBytes(requestBody);

    // Mirror SigV4OtlpLogExporter EXACTLY: Endpoint carries the full path, ResourcePath is left UNSET.
    // Setting both makes AWS's canonicalizer combine Endpoint.AbsolutePath + ResourcePath → a DOUBLED path
    // in the signature ("/create.../create...") while the wire sends it once → SignatureDoesNotMatch.
    IRequest sigV4Request = new DefaultRequest(new EmptyAmazonWebServiceRequest(), betaService)
    {
        HttpMethod = "POST",
        ContentStream = new MemoryStream(bodyBytes),
        Endpoint = uri,
        SignatureVersion = SignatureVersion.SigV4,
    };

    var config = new AmazonXRayConfig
    {
        AuthenticationRegion = betaRegion,
        AuthenticationServiceName = betaService,
        UseHttp = false,
        ServiceURL = uri.GetLeftPart(UriPartial.Authority),
        RegionEndpoint = RegionEndpoint.GetBySystemName(betaRegion),
    };

    var credentials = ResolveBetaCredentials();
    if (credentials.UseToken && credentials.Token != null)
    {
        sigV4Request.Headers["x-amz-security-token"] = credentials.Token;
    }

    sigV4Request.Headers["Host"] = uri.Host;
    sigV4Request.Headers["content-type"] = "application/json";
    new AWS4Signer().Sign(sigV4Request, config, null, credentials);

    var httpReq = new HttpRequestMessage(HttpMethod.Post, uri);
    foreach (var h in sigV4Request.Headers)
    {
        httpReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    httpReq.Content = new ByteArrayContent(bodyBytes);
    httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    var resp = forwardClient.SendAsync(httpReq).GetAwaiter().GetResult();
    var respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    return ((int)resp.StatusCode, respBody);
}

while (true)
{
    var ctx = listener.GetContext();
    var path = ctx.Request.Url!.AbsolutePath;

    // OTLP/HTTP logs come in as binary protobuf, not JSON — read + decode raw bytes, assert the snapshot,
    // and reply with an ExportLogsServiceResponse. This is the PR3 output leg: the DI OTLP exporter's real
    // wire output, received and verified. The [SNAPSHOT-*] markers are what run-demo.sh asserts on.
    if (path.Contains("v1/logs"))
    {
        using var bin = new MemoryStream();
        ctx.Request.InputStream.CopyTo(bin);
        HandleLogsExport(bin.ToArray());

        var okBytes = new ExportLogsServiceResponse().ToByteArray();
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-protobuf";
        ctx.Response.OutputStream.Write(okBytes);
        ctx.Response.Close();
        continue;
    }

    using var reader = new StreamReader(ctx.Request.InputStream);
    var body = reader.ReadToEnd();
    var req = string.IsNullOrEmpty(body) ? new JsonObject() : (JsonObject)JsonNode.Parse(body)!;
    JsonObject resp;

    // Beta-forwarding mode: sign the DI client's exact request and relay it to real beta, returning beta's
    // response verbatim. The config API leg (list/create/report) is thus proven end to end against the real
    // backend while the DI client stays on its unsigned GA path. Snapshots (handled above) remain local.
    // delete-instrumentation-configuration is forwarded too, but ONLY as a harness housekeeping path: the
    // DI agent itself never calls it (its GA surface is list + report). It exists so a run can remove the
    // configs it created, because beta persists them until ExpiresAt (~24h) and a stale config pinned to a
    // source line that has since MOVED reports a perpetual LINE_NOT_EXECUTABLE on every later run.
    if (betaEndpoint != null && (path.Contains("list-instrumentation-configurations")
        || path.Contains("create-instrumentation-configuration")
        || path.Contains("report-instrumentation-configuration-status")
        || path.Contains("delete-instrumentation-configuration")))
    {
        var apiPath = path.TrimStart('/');
        try
        {
            var (status, betaBody) = ForwardToBeta(apiPath, body);
            Banner($"PROXY → BETA  {apiPath}  (SigV4-signed, forwarded to {betaEndpoint})");
            Console.WriteLine($"[BETA-FORWARD] {apiPath} → HTTP {status}");
            Console.WriteLine("REQUEST (from DI client, verbatim):\n" + (string.IsNullOrEmpty(body) ? "(empty)" : Pretty(JsonNode.Parse(body))));
            Console.WriteLine($"RESPONSE (from beta):\n{betaBody}");
            if (status >= 400)
            {
                Console.WriteLine($"[BETA-FORWARD-ERROR] {apiPath} beta returned HTTP {status}");
            }

            var outBytes = Encoding.UTF8.GetBytes(betaBody);
            ctx.Response.StatusCode = status;
            ctx.Response.OutputStream.Write(outBytes);
            ctx.Response.Close();
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BETA-FORWARD-ERROR] {apiPath} failed: {ex.Message}");
            ctx.Response.StatusCode = 502;
            ctx.Response.Close();
            continue;
        }
    }

    if (path.Contains("create-instrumentation-configuration"))
    {
        // Operator/console creates a probe. Backend assigns a LocationHash/ARN/CreatedAt and stores it,
        // then echoes the full stored config back (matches the real create response).
        var type = req["InstrumentationType"]?.GetValue<string>() ?? "PROBE";
        var loc = (JsonObject)req["Location"]!.DeepClone();
        var cls = loc["CodeLocation"]?["ClassName"]?.GetValue<string>() ?? "Unknown";
        var mth = loc["CodeLocation"]?["MethodName"]?.GetValue<string>() ?? "Unknown";

        // Include the METHOD in the hash. Keying on class alone collides when several probes target
        // different methods of the same class, which is exactly the bulk-apply case.
        var hash = $"{type.ToLowerInvariant()}-{cls.ToLowerInvariant()}-{mth.ToLowerInvariant()}";

        var stored = new JsonObject
        {
            ["ARN"] = $"arn:aws:application-signals:us-east-1:000000000000:instrumentationConfig/mock/staging/SNAPSHOT/{hash}",
            ["InstrumentationType"] = type,
            ["SignalType"] = "SNAPSHOT",
            ["LocationHash"] = hash,
            ["CreatedAt"] = 1784050000.0,
            ["ExpiresAt"] = null,
            ["AttributeFilters"] = null,
            ["Location"] = loc,
            ["CaptureConfiguration"] = (JsonNode)req["CaptureConfiguration"]!.DeepClone(),
        };
        store[(type, hash)] = stored;

        Banner($"OPERATOR → BACKEND  create-instrumentation-configuration ({type})");
        Console.WriteLine("REQUEST:\n" + Pretty(req));
        Console.WriteLine("RESPONSE:\n" + Pretty(stored));
        resp = stored;
    }
    else if (path.Contains("delete-instrumentation-configuration"))
    {
        // Operator/console deletes a probe. Previously only the BETA-forwarding path handled this, so a purely
        // local run had no way to remove a configuration — which made "delete stops capture" undemonstrable
        // offline. Removing it from `store` is enough: the agent's next list poll no longer sees it, which is
        // exactly how the real backend causes a probe to be retired.
        var type = req["InstrumentationType"]?.GetValue<string>() ?? "PROBE";
        var hash = req["LocationIdentifier"]?["LocationHash"]?.GetValue<string>() ?? "";

        var removed = store.Remove((type, hash));

        // Re-arm the full-print flag for this type so the NEXT poll is shown in full. Otherwise the poll that
        // proves the config is gone would be collapsed to "(repeat)" and the audience would never see the
        // now-empty LatestConfigurations — the single most convincing frame of the deletion story.
        shownList.Remove(type);

        Banner($"OPERATOR → BACKEND  delete-instrumentation-configuration ({type})");
        Console.WriteLine("REQUEST:\n" + Pretty(req));
        Console.WriteLine(removed
            ? $"RESPONSE: removed {hash}; {store.Count} configuration(s) remain"
            : $"RESPONSE: no configuration matched {type}/{hash}; {store.Count} remain");
        resp = new JsonObject { ["Deleted"] = removed, ["LocationHash"] = hash };
    }
    else if (path.Contains("list-instrumentation-configurations"))
    {
        var type = req["InstrumentationType"]?.GetValue<string>() ?? "PROBE";
        var configs = new JsonArray();
        foreach (var kv in store)
        {
            if (kv.Key.Type == type) { configs.Add(kv.Value.DeepClone()); }
        }

        resp = new JsonObject
        {
            ["Changed"] = true,
            ["SyncedAt"] = 1784050000.0,   // numeric epoch — the real backend's actual shape
            ["SyncInterval"] = 60,
            ["NextToken"] = null,
            ["LatestConfigurations"] = configs,
        };

        // Show the first list poll per type in full; later identical polls just tick.
        if (shownList.Add(type))
        {
            Banner($"AGENT → BACKEND  list-instrumentation-configurations ({type})");
            Console.WriteLine("REQUEST:\n" + Pretty(req));
            Console.WriteLine("RESPONSE:\n" + Pretty(resp));
        }
        else
        {
            Console.WriteLine($"[mock-backend] ← agent polled {type} (repeat)");
        }
    }
    else
    {
        resp = new JsonObject { ["UnprocessedStatusEvents"] = new JsonArray() };
        if (!shownStatus)
        {
            Banner("AGENT → BACKEND  report-instrumentation-configuration-status");
            Console.WriteLine("REQUEST:\n" + Pretty(req));
            Console.WriteLine("RESPONSE:\n" + Pretty(resp));
            shownStatus = true;
        }
        else
        {
            Console.WriteLine("[mock-backend] ← agent reported status (repeat)");
        }
    }

    var bytes = Encoding.UTF8.GetBytes(resp.ToJsonString(compact));
    ctx.Response.StatusCode = 200;
    ctx.Response.OutputStream.Write(bytes);
    ctx.Response.Close();
}

// Empty AWS request shell so AWS4Signer can sign an arbitrary body (same pattern as SigV4OtlpLogExporter).
internal sealed class EmptyAmazonWebServiceRequest : AmazonWebServiceRequest
{
}
