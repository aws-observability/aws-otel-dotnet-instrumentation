// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0
//
// Minimal ASP.NET Core app that exercises the ServiceEvents SDK for the
// serviceevents contract tests. Each route maps to a signal scenario the
// contract-test base asserts against (see
// test/contract-tests/tests/test/amazon/serviceevents/).

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// A single shared HttpClient used to make an in-process downstream call on the
// success path. The outbound call produces an auto-instrumented HttpClient
// (System.Net.Http) child span whose parent is the endpoint's server span, so
// the SDK records a FunctionCall (service.function.duration) data point WITH a
// caller attribute. .NET v1 FunctionCall covers framework-derived spans
// (HttpClient/AWS SDK/internal), not arbitrary user methods, so a real
// downstream call is the reliable way to generate that signal.
var httpClient = new HttpClient();
const string SelfBaseUrl = "http://localhost:8080";

async Task CallDownstreamAsync()
{
    try
    {
        await httpClient.GetAsync($"{SelfBaseUrl}/health");
    }
    catch
    {
        // Best-effort: the downstream call only exists to produce a child span.
    }
}

// Readiness endpoint. Also the target of the downstream self-call above.
app.MapGet("/health", () => Results.Ok("ok"));

// 200 success. Makes a downstream call so FunctionCall data points are produced.
app.MapGet("/success", async () =>
{
    await CallDownstreamAsync();
    return Results.Ok("success");
});

// 500 via a thrown exception (fault WITH a captured exception type).
app.MapGet("/fault", () =>
{
    throw new ArithmeticException("intentional /fault exception for contract test");
});

// 500 via a thrown exception (distinct type from /fault) — drives the
// exception IncidentSnapshot + EndpointErrorMetrics count breakdown.
app.MapGet("/exception", () =>
{
    throw new InvalidOperationException("intentional /exception for contract test");
});

// 400 client error — recorded as an error, never a fault.
app.MapGet("/error", () => Results.StatusCode(400));

// 500 WITHOUT throwing — a server fault that raised no exception. Exercises the
// gate that a bare 5xx increments faults but produces no exception_breakdown /
// count data point.
app.MapGet("/error-status", () => Results.StatusCode(500));

// Sleeps past the default global latency threshold (5000ms) then returns 200,
// producing a pure latency-triggered IncidentSnapshot.
app.MapGet("/slow", async () =>
{
    await Task.Delay(6000);
    return Results.Ok("slow");
});

// Sleeps ~1s: below the global threshold but above the per-endpoint override
// (OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS="GET /slow-success:500"). An
// incident here can only come from the per-endpoint override, proving it applied.
app.MapGet("/slow-success", async () =>
{
    await Task.Delay(1000);
    return Results.Ok("slow-success");
});

// POST that throws only when the body requests it ({"forceError": true}).
// Verifies non-GET methods are captured on the incident snapshot.
app.MapPost("/data", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (body.Contains("\"forceError\"") && body.Contains("true"))
    {
        throw new InvalidOperationException("forced error from POST /data");
    }

    return Results.Ok("data");
});

// Signal readiness once Kestrel is listening so the contract-test harness's
// wait_for_logs("Ready") returns.
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine("Ready"));

app.Run();
