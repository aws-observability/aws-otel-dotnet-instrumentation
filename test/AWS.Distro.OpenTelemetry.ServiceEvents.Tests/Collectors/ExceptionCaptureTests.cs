// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using FluentAssertions;

namespace AWS.Distro.OpenTelemetry.ServiceEvents.Tests.Collectors;

/// <summary>
/// Tests for <see cref="ExceptionCapture" />, the private channel that carries an exception's
/// detail to IncidentSnapshot without putting it on the customer's span.
/// </summary>
/// <remarks>
/// The channel exists because the obvious alternative — enabling
/// <c>AspNetCoreTraceInstrumentationOptions.RecordException</c> — attaches
/// <c>exception.message</c> and <c>exception.stacktrace</c> to the customer's own server spans,
/// which their trace pipeline then exports. ServiceEvents is enabled by default alongside
/// Application Signals, so that would have silently changed what a customer's spans contain on
/// upgrade. The most important test in this file is therefore the one asserting the span is left
/// untouched.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class ExceptionCaptureTests : IDisposable
{
    private readonly ActivitySource source;
    private readonly ActivityListener listener;

    public ExceptionCaptureTests()
    {
        // A listener that samples everything, so StartActivity returns a real Activity rather than null.
        this.listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "ServiceEvents.Tests.ExceptionCapture",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(this.listener);

        this.source = new ActivitySource("ServiceEvents.Tests.ExceptionCapture");
    }

    public void Dispose()
    {
        this.source.Dispose();
        this.listener.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Stash_ThenTryRead_ReturnsTypeMessageAndStack()
    {
        using var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        var thrown = Catch(() => throw new InvalidOperationException("boom"));

        ExceptionCapture.Stash(activity, thrown);

        var (type, message, stack) = ExceptionCapture.TryRead(activity);

        type.Should().Be(
            "System.InvalidOperationException",
            "the fully-qualified name is what the exception metric dimension and the snapshot both " +
            "report, and it must match what error.type would have carried");
        message.Should().Be("boom");
        stack.Should().NotBeNullOrEmpty("the stack is what the exception call_path is derived from");
    }

    [Fact]
    public void TryRead_WithNothingStashed_ReturnsAllNulls()
    {
        using var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;

        var (type, message, stack) = ExceptionCapture.TryRead(activity);

        type.Should().BeNull();
        message.Should().BeNull();
        stack.Should().BeNull("a request that did not fail must not look like one that did");
    }

    /// <summary>
    /// The stack is captured with <c>ToString()</c> rather than the <c>StackTrace</c> property
    /// specifically so the inner-exception chain travels with it — that is the shape
    /// <c>IncidentSnapshotCollector.ParseStackTrace</c> parses, and the shape
    /// <c>activity.RecordException</c> would have produced.
    /// </summary>
    [Fact]
    public void Stash_StackTraceCarriesTheInnerExceptionChain()
    {
        using var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        var thrown = Catch(() => throw new InvalidOperationException(
            "outer",
            new ArithmeticException("the real cause")));

        ExceptionCapture.Stash(activity, thrown);

        var (_, message, stack) = ExceptionCapture.TryRead(activity);

        message.Should().Be("outer", "Message is the outer exception's own message");
        stack.Should().Contain(
            "ArithmeticException",
            "losing the inner exception would hide the actual cause from the incident's call_path");
        stack.Should().Contain("the real cause");
    }

    /// <summary>
    /// The whole point of the channel: the exception reaches ServiceEvents without appearing on the
    /// span the customer exports. If this ever fails, we have reintroduced the leak that removing
    /// <c>RecordException</c> was meant to close.
    /// </summary>
    [Fact]
    public void Stash_LeavesTheCustomerSpanUntouched()
    {
        using var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;
        var thrown = Catch(() => throw new InvalidOperationException("connection string: secret"));

        var tagsBefore = activity.TagObjects.Count();

        ExceptionCapture.Stash(activity, thrown);

        activity.Events.Should().BeEmpty(
            "no exception event may be added — that event is what carries exception.message and " +
            "exception.stacktrace onto the customer's exported span");
        activity.TagObjects.Count().Should().Be(
            tagsBefore,
            "no tags may be added either; the capture lives in a custom property that only " +
            "ServiceEvents reads");
        activity.Status.Should().NotBe(
            ActivityStatusCode.Error,
            "capturing must not restate the span's status — that is the instrumentation's call");
    }

    [Fact]
    public void Stash_IsSafeWithNullArguments()
    {
        using var activity = this.source.StartActivity("ingress", ActivityKind.Server)!;

        // Telemetry must never throw into the customer's request pipeline.
        var stashNullException = () => ExceptionCapture.Stash(activity, null!);
        stashNullException.Should().NotThrow();

        var stashNullActivity = () => ExceptionCapture.Stash(null!, new InvalidOperationException());
        stashNullActivity.Should().NotThrow();
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
}
