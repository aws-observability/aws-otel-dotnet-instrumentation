// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

// Probe target application for the Dynamic Instrumentation contract tests.
//
// A SEPARATE IMAGE rather than probe targets added to AppSignals.NetCore, which is shared by five other
// suites (misc, runtime, netcore). See ProbeTargets.cs for why that separation matters to line-level probes.
//
// This file only exposes HTTP routes that invoke the targets; the targets themselves live in
// ProbeTargets.cs so edits here cannot shift their line numbers.
using DynamicInstrumentation.NetCore;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));

app.MapGet("/probe-target/order", (string orderId, int quantity) =>
    Results.Ok(new { total = ProbeTargets.ComputeOrderTotal(orderId, quantity) }));

app.MapGet("/probe-target/greeting", (string name) => Results.Ok(new { greeting = ProbeTargets.GetGreeting(name) }));

app.MapGet("/probe-target/async", async (int seed) => Results.Ok(new { value = await ProbeTargets.ComputeAsync(seed) }));

// Catches its own exception and returns 500: the probe must capture the throwable, and the app must stay up
// so later tests in the same container still have a live target.
app.MapGet("/probe-target/failing", (string reason) =>
{
    try
    {
        ProbeTargets.FailingOperation(reason);
        return Results.Ok();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
