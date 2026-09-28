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

    /// <summary>
    /// The call-path buffer is only allocated when something will drain it. The incident trigger is
    /// the only consumer, and it is absent until the incident collector is wired, so allocating
    /// unconditionally cost a queue and a custom property on every server span for data nobody read.
    /// </summary>
    [Fact]
    public void OnStart_WithoutAnIncidentTrigger_AllocatesNoCallPathBuffer()
    {
        var processor = new EndpointActivityProcessor(new FakeRecorder(), new ServiceEventsConfig());

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        processor.OnStart(activity);

        activity.GetCustomProperty(CallPathCapture.PropertyKey).Should().BeNull(
            "nothing drains the buffer without an incident trigger, so it must not be allocated");
    }

    /// <summary>
    /// The default configuration has no <c>exception</c> event to read, because ServiceEvents
    /// deliberately does not enable <c>RecordException</c> — that would attach exception messages and
    /// stack traces to the customer's own exported spans. The type comes from the <c>error.type</c>
    /// tag instead, which the ASP.NET Core instrumentation sets on the error path regardless.
    /// </summary>
    [Fact]
    public void OnEnd_5xxWithErrorTypeTagOnly_CapturesExceptionTypeWithoutAnExceptionEvent()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/exception");
        activity.SetTag("http.response.status_code", 500);
        activity.SetTag("error.type", "System.InvalidOperationException");
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].ErrorType.Should().Be(
            "System.InvalidOperationException",
            "error.type carries the same GetType().FullName the exception event would have, so the " +
            "dimension is unchanged without putting messages or stacks on the customer's span");
    }

    /// <summary>
    /// The semantic conventions let <c>error.type</c> hold a protocol error code when no exception was
    /// involved, so a hand-returned 500 can set it to the literal <c>"500"</c>. That must not be
    /// mistaken for an exception type.
    /// </summary>
    [Fact]
    public void OnEnd_5xxWithStatusCodeShapedErrorType_DoesNotTreatItAsAnExceptionType()
    {
        var recorder = new FakeRecorder();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig());

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/error-status");
        activity.SetTag("http.response.status_code", 500);
        activity.SetTag("error.type", "500");
        activity.Stop();

        processor.OnEnd(activity);

        recorder.Calls.Should().ContainSingle();
        recorder.Calls[0].ErrorType.Should().NotBe(
            "500",
            "a status code is not an exception type; reporting it as one would put exception=\"500\" " +
            "on the metric, which is neither the real type nor the HTTP{status} fallback shape");
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
        recorder.Calls[0].ErrorType.Should().BeNull("no exception was captured → no synthetic HTTP500");
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

    /// <summary>
    /// The private capture channel outranks a span exception event. Both can be present when a
    /// customer has independently enabled <c>RecordException</c> on their own instrumentation; the
    /// capture is the more trustworthy of the two because it is taken from the exception object at
    /// the point it escaped, whereas the event's tags may have been rewritten by a span processor.
    /// </summary>
    [Fact]
    public void OnEnd_CapturedException_TakesPrecedenceOverSpanExceptionEvent()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);

        // A span exception event carrying deliberately different values.
        activity.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection
            {
                { "exception.type", "FromTheSpanEvent" },
                { "exception.message", "from the span event" },
                { "exception.stacktrace", "at X()" },
            }));

        ExceptionCapture.Stash(activity, Catch(() => throw new InvalidOperationException("from the capture")));
        activity.Stop();

        processor.OnEnd(activity);

        var call = trigger.Calls.Should().ContainSingle().Subject;
        call.ExceptionType.Should().Be("System.InvalidOperationException", "the capture wins over the span event");
        call.ExceptionMessage.Should().Be("from the capture");
        call.StackTrace.Should().NotBe("at X()").And.Contain("InvalidOperationException");
    }

    /// <summary>
    /// <c>error.type</c> is the last resort and yields a type only. When the capture is present it
    /// supersedes the tag and additionally supplies the message and stack that <c>error.type</c>
    /// can never carry — without them an IncidentSnapshot has no <c>call_path</c> to derive.
    /// </summary>
    [Fact]
    public void OnEnd_CapturedException_TakesPrecedenceOverErrorTypeTag()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.SetTag("error.type", "System.TimeoutException");

        ExceptionCapture.Stash(activity, Catch(() => throw new InvalidOperationException("the real one")));
        activity.Stop();

        processor.OnEnd(activity);

        var call = trigger.Calls.Should().ContainSingle().Subject;
        call.ExceptionType.Should().Be("System.InvalidOperationException", "the capture wins over error.type");
        call.ExceptionMessage.Should().Be("the real one");
        call.StackTrace.Should().NotBeNullOrEmpty("error.type alone could not have supplied a stack");
    }

    /// <summary>
    /// <c>ReadError</c> (which feeds the <c>exception</c> metric dimension) and
    /// <c>ReadExceptionDetails</c> (which feeds the snapshot) resolve the exception type through the
    /// same precedence chain, so a request cannot be attributed to one type on the metric and a
    /// different one on the snapshot the metric's exemplar points at.
    /// </summary>
    [Fact]
    public void OnEnd_CapturedException_ReportsTheSameTypeOnTheMetricAndTheSnapshot()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger();
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.SetTag("error.type", "System.TimeoutException");

        ExceptionCapture.Stash(activity, Catch(() => throw new InvalidOperationException("boom")));
        activity.Stop();

        processor.OnEnd(activity);

        var metricErrorType = recorder.Calls.Should().ContainSingle().Subject.ErrorType;
        var snapshotErrorType = trigger.Calls.Should().ContainSingle().Subject.ExceptionType;

        metricErrorType.Should().Be("System.InvalidOperationException");
        snapshotErrorType.Should().Be(
            metricErrorType,
            "both readers consult the capture first; if they diverge, the exception metric and the " +
            "snapshot it links to would disagree about what failed");
    }

    /// <summary>Throw and catch so the exception carries a real stack trace.</summary>
    private static Exception Catch(Action throwing)
    {
        try
        {
            throwing();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("the action was expected to throw");
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

    /// <summary>
    /// <c>OnEnd</c> runs inside <c>Activity.Stop()</c> on the customer's request path, with no
    /// framework boundary in between, so a telemetry failure must not surface in their request.
    /// </summary>
    [Fact]
    public void OnEnd_WhenTheRecorderThrows_DoesNotPropagateToTheCaller()
    {
        var processor = new EndpointActivityProcessor(new ThrowingRecorder(), new ServiceEventsConfig());

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.route", "/orders");
        activity.SetTag("http.response.status_code", 200);
        activity.Stop();

        var onEnd = () => processor.OnEnd(activity);

        onEnd.Should().NotThrow(
            "a throw here would propagate through Activity.Stop() into ASP.NET Core's " +
            "HostingApplicationDiagnostics and out into the customer's request");
    }

    [Fact]
    public void OnEnd_WhenTheIncidentTriggerThrows_DoesNotPropagateToTheCaller()
    {
        var recorder = new FakeRecorder();
        var trigger = new FakeIncidentTrigger { ShouldThrow = true };
        var processor = new EndpointActivityProcessor(recorder, new ServiceEventsConfig(), trigger);

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.Stop();

        var onEnd = () => processor.OnEnd(activity);

        onEnd.Should().NotThrow(
            "the incident path is downstream of the endpoint recording and must not escape either");

        recorder.Calls.Should().ContainSingle(
            "the incident path runs after RecordRequest, so a failure there costs the incident but " +
            "must not also cost the endpoint metric for the request");
    }

    /// <summary>
    /// Pins the incident timestamp to epoch <b>milliseconds</b> read as UTC. Scope worth being clear
    /// about: this catches a unit error (seconds for milliseconds) or a wrong epoch, but it does not
    /// exercise the <c>SpecifyKind</c> guard in <c>FeedIncidentTrigger</c> — <c>SetStartTime</c>
    /// rejects any <c>DateTimeKind</c> other than UTC, so the kind this test can supply makes that
    /// call a no-op either way.
    /// </summary>
    [Fact]
    public void OnEnd_DerivesTheIncidentTimestampAsUtcMilliseconds()
    {
        var trigger = new FakeIncidentTrigger();
        var processor = new EndpointActivityProcessor(new FakeRecorder(), new ServiceEventsConfig(), trigger);

        // 2021-01-01T00:00:00Z. Hard-coded rather than round-tripped through the same conversion the
        // production code uses, so the assertion is independent of that code being right.
        var start = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const long expectedEpochMs = 1609459200000L;

        var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        activity.SetStartTime(start);
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("http.route", "/checkout");
        activity.SetTag("http.response.status_code", 500);
        activity.Stop();

        processor.OnEnd(activity);

        trigger.Calls.Should().ContainSingle().Which
            .Timestamp.Should().Be(
                expectedEpochMs,
                "the timestamp must be epoch milliseconds read as UTC, not seconds and not shifted " +
                "by the host's local offset");
    }

    [Fact]
    public void OnStart_WhenTheActivityIsUnusable_DoesNotPropagateToTheCaller()
    {
        // OnStart is reached from Activity.Start(), equally inside the request path. A null
        // activity stands in for any unexpected state that makes the body throw.
        var processor = new EndpointActivityProcessor(
            new FakeRecorder(), new ServiceEventsConfig(), new FakeIncidentTrigger());

        var onStart = () => processor.OnStart(null!);

        onStart.Should().NotThrow();
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

    /// <summary>Stands in for a recorder that fails, to prove the processor's guard holds.</summary>
    private sealed class ThrowingRecorder : IEndpointRecorder
    {
        public void RecordRequest(string route, string method, int statusCode, long durationNs, string? errorType = null, string? functionName = null)
            => throw new InvalidOperationException("recorder failed");

        public void RecordIncidentExemplar(string operation, string snapshotId, string triggerType, string severity, long timestamp)
            => throw new InvalidOperationException("recorder failed");
    }

    /// <summary>Captures <c>ProcessPotentialIncident</c> calls and returns a configurable result.</summary>
    private sealed class FakeIncidentTrigger : IIncidentTrigger
    {
        public List<(string Route, string Method, int StatusCode, double DurationMs, string? ExceptionType, string? ExceptionMessage, string? StackTrace, string? TraceId, string? SpanId, long Timestamp, System.Collections.Generic.IReadOnlyList<AWS.Distro.OpenTelemetry.ServiceEvents.Models.CallPathEntry>? SpanFrames)> Calls { get; } = new();

        public IncidentTriggerResult? ResultToReturn { get; set; }

        /// <summary>Gets or sets a value indicating whether the trigger should throw when consulted.</summary>
        public bool ShouldThrow { get; set; }

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

            if (this.ShouldThrow)
            {
                throw new InvalidOperationException("incident trigger failed");
            }

            return this.ResultToReturn;
        }
    }
}
