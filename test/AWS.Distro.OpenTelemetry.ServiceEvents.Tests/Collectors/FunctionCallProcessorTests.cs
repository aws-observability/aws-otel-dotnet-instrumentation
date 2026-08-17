// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using AWS.Distro.OpenTelemetry.ServiceEvents.Telemetry;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Tests for <see cref="FunctionCallProcessor" /> — name/caller/status derivation,
/// server-span skipping, allowlist gating, sampler gating, and operation resolution.
/// </summary>
public class FunctionCallProcessorTests
{
    private const string SourceName = "Test.FnSource";

    private static readonly ActivitySource Source = new(SourceName);

    static FunctionCallProcessorTests()
    {
        // Register a listener so StartActivity returns real, recorded activities.
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    [Fact]
    public void OnEnd_SkipsServerSpans()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var act = Source.StartActivity("HttpRequestIn", ActivityKind.Server)!;
        act.SetTag("http.request.method", "GET");
        processor.OnEnd(act);

        recorder.Calls.Should().BeEmpty("server spans are the endpoint itself (covered by M3)");
    }

    [Fact]
    public void OnEnd_RecordsFunctionName_Status_And_Duration()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var act = Source.StartActivity("HttpRequestOut", ActivityKind.Client)!;
        var start = DateTime.UtcNow;
        act.SetStartTime(start);
        act.SetEndTime(start.AddMilliseconds(2)); // 2 ms = 2000 µs

        processor.OnEnd(act);

        recorder.Calls.Should().ContainSingle();
        var call = recorder.Calls[0];
        call.FunctionName.Should().Be("Test.FnSource.HttpRequestOut");
        call.Status.Should().Be("success");
        call.DurationMicros.Should().BeApproximately(2000, 0.5);
    }

    [Fact]
    public void OnEnd_ErrorStatus_MapsToError()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var act = Source.StartActivity("DoWork", ActivityKind.Internal)!;
        act.SetStatus(ActivityStatusCode.Error);
        processor.OnEnd(act);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].Status.Should().Be("error");
    }

    [Fact]
    public void OnEnd_Caller_IsParentComposite()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var parent = Source.StartActivity("Parent", ActivityKind.Server)!;
        using var child = Source.StartActivity("Child", ActivityKind.Client)!;
        child.Parent.Should().Be(parent);

        processor.OnEnd(child);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].Caller.Should().Be("Test.FnSource.Parent");
    }

    [Fact]
    public void OnEnd_Caller_IsNull_WhenNoParent()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var act = Source.StartActivity("Root", ActivityKind.Client)!;
        act.Parent.Should().BeNull();

        processor.OnEnd(act);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].Caller.Should().BeNull();
    }

    [Fact]
    public void OnEnd_NotInAllowlist_IsNotRecorded()
    {
        var recorder = new FakeRecorder();
        var config = new ServiceEventsConfig { PackagesToInstrument = new[] { "Other.Namespace.*" } };
        var processor = new FunctionCallProcessor(recorder, config, AlwaysSampler());

        using var act = Source.StartActivity("DoWork", ActivityKind.Client)!;
        processor.OnEnd(act);

        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public void OnEnd_NeverSampler_SuppressesRecording()
    {
        var recorder = new FakeRecorder();
        var sampler = new FunctionCallSampler(new ServiceEventsConfig { SamplingMode = "never" });
        var processor = new FunctionCallProcessor(recorder, AllowAll(), sampler);

        using var act = Source.StartActivity("DoWork", ActivityKind.Client)!;
        processor.OnEnd(act);

        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public void OnEnd_Operation_ResolvedFromServerAncestor()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var parent = Source.StartActivity("HttpRequestIn", ActivityKind.Server)!;
        parent.SetTag("http.request.method", "get");
        parent.SetTag("http.route", "/orders");
        using var child = Source.StartActivity("HttpRequestOut", ActivityKind.Client)!;

        processor.OnEnd(child);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].Operation.Should().Be("GET /orders");
    }

    [Fact]
    public void OnEnd_Operation_NullWhenNoServerAncestor()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        using var act = Source.StartActivity("HttpRequestOut", ActivityKind.Client)!;
        processor.OnEnd(act);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].Operation.Should().BeNull();
    }

    [Fact]
    public void OnEnd_FunctionName_DedupsWhenOperationStartsWithSource()
    {
        var recorder = new FakeRecorder();
        var processor = new FunctionCallProcessor(recorder, AllowAll(), AlwaysSampler());

        // OperationName already begins with the source name (mirrors the HttpClient
        // instrumentation's "System.Net.Http" source + "System.Net.Http.HttpRequestOut" op),
        // so the source prefix must NOT be repeated.
        using var act = Source.StartActivity("Test.FnSource.Outbound", ActivityKind.Client)!;
        processor.OnEnd(act);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].FunctionName.Should().Be("Test.FnSource.Outbound");
    }

    private static ServiceEventsConfig AllowAll() =>
        new() { PackagesToInstrument = new[] { "Test.FnSource.*" } };

    private static FunctionCallSampler AlwaysSampler() =>
        new(new ServiceEventsConfig { SamplingMode = "always" });

    private sealed class FakeRecorder : IFunctionCallRecorder
    {
        public List<(double DurationMicros, string FunctionName, string Status, string? Caller, string? Operation)> Calls { get; } = new();

        public void RecordFunctionCall(double durationMicros, string functionName, string status, string? caller, string? operation) =>
            this.Calls.Add((durationMicros, functionName, status, caller, operation));
    }
}
