// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Unit tests for <see cref="EndpointActivityProcessor" /> — verifies the
/// Activity → <c>RecordRequest</c> tag mapping using a fake recorder.
/// </summary>
public class EndpointActivityProcessorTests : IDisposable
{
    private readonly ActivitySource source;
    private readonly ActivityListener listener;

    public EndpointActivityProcessorTests()
    {
        // Register the listener BEFORE creating the source, with every delegate set,
        // so StartActivity returns a non-null, sampled, Server-kind Activity.
        this.listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "ServiceEvents.Tests.EndpointProcessor",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(this.listener);

        this.source = new ActivitySource("ServiceEvents.Tests.EndpointProcessor");
    }

    public void Dispose()
    {
        this.source.Dispose();
        this.listener.Dispose();
    }

    [Fact]
    public void OnEnd_ServerSpanWithHttpRoute_RecordsMappedRequest()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/orders/{id}");
        activity.SetTag("http.response.status_code", 200);
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().ContainSingle();
        var call = recorder.Calls[0];
        call.Method.Should().Be("POST");
        call.Route.Should().Be("/orders/{id}");
        call.StatusCode.Should().Be(200);
        call.DurationNs.Should().BeGreaterThan(0);
        call.ErrorType.Should().BeNull("2xx responses are not errors");
    }

    [Fact]
    public void OnEnd_NonServerSpan_IsIgnored()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("client-call", ActivityKind.Client)!;
        activity.SetTag("http.request.method", "GET");
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().BeEmpty("only Server spans are endpoint requests");
    }

    [Fact]
    public void OnEnd_ServerSpanWithoutHttpMethod_IsIgnored()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("non-http", ActivityKind.Server)!;
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public void OnEnd_UnmatchedRoute_CollapsesToFirstPathSegment()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("GET /fallback", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("url.path", "/raw/path/segments"); // no http.route → unmatched
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].Route.Should().Be("/raw", "an unmatched route collapses to its first path segment to bound cardinality");
    }

    [Fact]
    public void OnEnd_5xxWithExceptionEvent_CapturesExceptionType()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection { { "exception.type", "RuntimeException" } }));
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].StatusCode.Should().Be(500);
        recorder.Calls[0].ErrorType.Should().Be("RuntimeException");
    }

    [Fact]
    public void OnEnd_5xxWithoutExceptionEvent_EmitsNoSyntheticErrorType()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        // A 500 returned as a status code without raising (e.g. Results.StatusCode(500)).
        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/error");
        activity.SetTag("http.response.status_code", 500);
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].StatusCode.Should().Be(500);
        recorder.Calls[0].ErrorType.Should().BeNull("no exception was captured → no synthetic HTTP500 (spec §3/§7)");
    }

    [Fact]
    public void OnEnd_RespectsEndpointExcludeFilter()
    {
        var recorder = new FakeRecorder();
        var config = new ServiceEventsConfig
        {
            EndpointExcludePatterns = new[] { "* /health" },
        };
        var processor = new EndpointActivityProcessor(recorder, config);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/health");
        activity.SetTag("http.response.status_code", 200);
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().BeEmpty("excluded endpoints are filtered before recording");
    }

    [Fact]
    public void OnEnd_WithIncidentTrigger_FeedsExceptionDetailsAndTraceContext()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection
            {
                { "exception.type", "RuntimeException" },
                { "exception.message", "boom" },
                { "exception.stacktrace", "at X()" },
            }));
        activity.Stop();

        processor.OnEnd(activity);

        trigger.Calls.Should().ContainSingle();
        var call = trigger.Calls[0];
        call.Route.Should().Be("/checkout");
        call.Method.Should().Be("POST");
        call.StatusCode.Should().Be(500);
        call.ExceptionType.Should().Be("RuntimeException");
        call.ExceptionMessage.Should().Be("boom");
        call.StackTrace.Should().Be("at X()");
        call.TraceId.Should().NotBeNullOrEmpty().And.HaveLength(32);
        call.SpanId.Should().NotBeNullOrEmpty().And.HaveLength(16);
    }

    [Fact]
    public void OnEnd_WithIncidentTrigger_UnsampledSpan_OmitsTraceContext()
    {
        // A recorded-but-unsampled span (RecordOnly / AllData): the AlwaysRecordSampler
        // records it for metrics but does not set the W3C sampled flag, so it is never
        // exported to the trace backend. The incident is still triggered, but its trace/span
        // ids must be omitted so the console does not surface a dead trace link.
        using var unsampledSource = new ActivitySource("ServiceEvents.Tests.EndpointProcessor.Unsampled");
        using var unsampledListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "ServiceEvents.Tests.EndpointProcessor.Unsampled",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(unsampledListener);

        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = unsampledSource.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection
            {
                { "exception.type", "RuntimeException" },
                { "exception.message", "boom" },
                { "exception.stacktrace", "at X()" },
            }));
        activity.Stop();

        activity.Recorded.Should().BeFalse("the span was sampled RecordOnly, not RecordAndSample");

        processor.OnEnd(activity);

        trigger.Calls.Should().ContainSingle("the incident is still triggered for unsampled spans");
        var call = trigger.Calls[0];
        call.ExceptionType.Should().Be("RuntimeException");
        call.TraceId.Should().BeNull("an unsampled span has no exported trace, so the trace id is omitted");
        call.SpanId.Should().BeNull("an unsampled span has no exported span, so the span id is omitted");
    }

    [Fact]
    public void OnEnd_WhenTriggerReturnsResult_RecordsExemplarOnEndpointWindow()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger
        {
            ResultToReturn = new IncidentTriggerResult("POST /checkout", "snap_abc", "exception", "critical", 1700),
        };
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Exemplars.Should().ContainSingle();
        recorder.Exemplars[0].Should().Be(("POST /checkout", "snap_abc", "exception", "critical", 1700L));
    }

    [Fact]
    public void OnEnd_WhenTriggerReturnsNull_RecordsNoExemplar()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger { ResultToReturn = null };
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/ok");
        activity.SetTag("http.response.status_code", 200);
        activity.Stop();

        processor.OnEnd(activity);

        trigger.Calls.Should().ContainSingle("the trigger is always consulted");
        recorder.Exemplars.Should().BeEmpty("no snapshot was produced");
    }

    /// <summary>Captures <c>RecordRequest</c> and <c>RecordIncidentExemplar</c> calls for assertion.</summary>
    private sealed class FakeRecorder : IEndpointRecorder
    {
        public List<(string Route, string Method, int StatusCode, long DurationNs, string? ErrorType, string? FunctionName)> Calls { get; } = new();

        public List<(string Operation, string SnapshotId, string TriggerType, string Severity, long Timestamp)> Exemplars { get; } = new();

        public void RecordRequest(string route, string method, int statusCode, long durationNs, string? errorType = null, string? functionName = null)
            => this.Calls.Add((route, method, statusCode, durationNs, errorType, functionName));

        public void RecordIncidentExemplar(string operation, string snapshotId, string triggerType, string severity, long timestamp)
            => this.Exemplars.Add((operation, snapshotId, triggerType, severity, timestamp));
    }

    /// <summary>Captures <c>ProcessPotentialIncident</c> calls and returns a configurable result.</summary>
    private sealed class FakeIncidentTrigger : IIncidentTrigger
    {
        public List<(string Route, string Method, int StatusCode, double DurationMs, string? ExceptionType, string? ExceptionMessage, string? StackTrace, string? TraceId, string? SpanId, long Timestamp, System.Collections.Generic.IReadOnlyList<AWS.Distro.OpenTelemetry.ServiceEvents.Models.CallPathEntry>? SpanFrames)> Calls { get; } = new();

        public IncidentTriggerResult? ResultToReturn { get; set; }

        public IncidentTriggerResult? ProcessPotentialIncident(
            string route,
            string method,
            int statusCode,
            double durationMs,
            string? exceptionType,
            string? exceptionMessage,
            string? stackTrace,
            string? traceId,
            string? spanId,
            long requestTimestampMs,
            System.Collections.Generic.IReadOnlyList<AWS.Distro.OpenTelemetry.ServiceEvents.Models.CallPathEntry>? spanFrames = null)
        {
            this.Calls.Add((route, method, statusCode, durationMs, exceptionType, exceptionMessage, stackTrace, traceId, spanId, requestTimestampMs, spanFrames));
            return this.ResultToReturn;
        }
    }
}
