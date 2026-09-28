// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using AWS.Distro.OpenTelemetry.ServiceEvents;
using AWS.Distro.OpenTelemetry.ServiceEvents.Collectors;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using FluentAssertions;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// ServiceEvents ships inside this distro and loads with no customer configuration, so a
/// customer who has it disabled must observe no change to their telemetry.
/// <para>
/// These tests pin the gate in <see cref="Plugin.ConfigureTracesOptions(AspNetCoreTraceInstrumentationOptions)" />.
/// The subtlety worth protecting: <c>ServiceEventsInstrumentation.Current</c> becomes non-null as
/// soon as <c>GetOrCreate</c> runs, which the plugin does unconditionally — so a null check is NOT
/// a valid proxy for "ServiceEvents is running". <c>Initialize()</c> returns early without setting
/// <c>IsInitialized</c> whenever the feature is disabled (Lambda, an explicit
/// <c>OTEL_AWS_SERVICE_EVENTS_ENABLED=false</c>, Application Signals off with no explicit enable, or
/// missing OTLP endpoints when force-enabled). Gating on <c>Current != null</c> would therefore turn
/// on <c>RecordException</c> for every customer, adding exception events to their server spans.
/// </para>
/// <para>
/// <c>OTEL_AWS_SERVICE_EVENTS_ENABLED</c> is deliberately never set here. An explicit value wins
/// over the Application Signals fallback in <c>DetermineEnabled</c>; leaving it unset is what
/// exercises the default path a customer actually hits.
/// </para>
/// </summary>
[Collection("ProcessGlobalState")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class ServiceEventsGateTest
{
    private const string TestSourceName = "ServiceEvents.Tests.GateExceptionCapture";

    [Fact]
    public void DisabledServiceEvents_LeavesSingletonUninitialized()
    {
        var instrumentation = CreateDisabledInstrumentation();

        // The precondition the plugin's gate depends on: the singleton exists, but is not running.
        ServiceEventsInstrumentation.Current.Should().NotBeNull(
            "GetOrCreate constructs the singleton unconditionally");
        instrumentation.IsInitialized.Should().BeFalse(
            "Initialize() must bail out when ServiceEvents is disabled");
    }

    [Fact]
    public void DisabledServiceEvents_DoesNotEnableRecordException()
    {
        CreateDisabledInstrumentation();

        var options = new AspNetCoreTraceInstrumentationOptions();
        options.RecordException.Should().BeFalse("sanity check: the option defaults to off");

        new Plugin().ConfigureTracesOptions(options);

        options.RecordException.Should().BeFalse(
            "ServiceEvents never enables RecordException on any path — it would attach exception " +
            "messages and stack traces to spans the customer exports. This guards that decision " +
            "against being reverted, rather than distinguishing the enabled path from the disabled " +
            "one: the enabled path does not set it either, and takes the exception through " +
            "EnrichWithException into ServiceEvents' own private channel instead");
    }

    /// <summary>
    /// The distro and ServiceEvents must agree on what <c>OTEL_AWS_APPLICATION_SIGNALS_ENABLED</c>
    /// means, for every casing.
    /// <para>
    /// They did not. This side compared ordinally against <c>"true"</c> while ServiceEvents compared
    /// case-insensitively, so <c>=True</c> left ServiceEvents suppressing its EndpointSummary because
    /// it believed App Signals was carrying that data, while the distro never configured App Signals
    /// at all. The per-endpoint summary — the headline signal of this feature — was then emitted by
    /// neither pipeline, with no error anywhere, and the customer's sampler was silently replaced.
    /// </para>
    /// <para>
    /// Asserting equality of the two readings rather than a specific value is deliberate: either
    /// side flipping to a stricter or looser parse in future reintroduces the same class of bug, and
    /// this fails when they diverge regardless of which one moved.
    /// </para>
    /// </summary>
    /// <param name="rawValue">Casing variant of the flag value to test.</param>
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("tRuE")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("")]
    public void ApplicationSignalsFlag_IsReadIdenticallyByTheDistroAndServiceEvents(string rawValue)
    {
        var original = System.Environment.GetEnvironmentVariable(Plugin.ApplicationSignalsEnabledConfig);
        System.Environment.SetEnvironmentVariable(Plugin.ApplicationSignalsEnabledConfig, rawValue);

        try
        {
            var distroReading = Plugin.IsEnvFlagTrue(Plugin.ApplicationSignalsEnabledConfig);
            var serviceEventsReading = ServiceEventsConfig.FromEnvironment().ApplicationSignalsEnabled;

            const string because =
                "the distro and ServiceEvents both branch on this flag, and when they disagree the " +
                "per-endpoint summary is suppressed by one side and never emitted by the other";

            serviceEventsReading.Should().Be(distroReading, because);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(Plugin.ApplicationSignalsEnabledConfig, original);
        }
    }

    [Fact]
    public void DisabledServiceEvents_DoesNotInstallExceptionCapture()
    {
        CreateDisabledInstrumentation();

        var options = new AspNetCoreTraceInstrumentationOptions();
        new Plugin().ConfigureTracesOptions(options);

        options.EnrichWithException.Should().BeNull(
            "the capture allocates per failed request for a consumer that is not running, which is " +
            "the same reason CallPathCapture is gated on an incident trigger existing");
    }

    /// <summary>
    /// The enabled path installs the private exception channel. Both halves matter: the delegate has
    /// to be there at all, and invoking it must leave the customer's span untouched — that second
    /// property is the entire reason this exists instead of <c>RecordException</c>.
    /// </summary>
    [Fact]
    public void EnabledServiceEvents_InstallsExceptionCaptureWithoutTouchingTheSpan()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-gate-capture-{Guid.NewGuid():N}.ndjson");
        var restore = SetEnvironment(new()
        {
            [Plugin.ApplicationSignalsEnabledConfig] = "false",
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
            ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
            ["RESOURCE_DETECTORS_ENABLED"] = "false",
        });

        try
        {
            ServiceEventsInstrumentation.ResetForTests();
            var instrumentation = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());
            instrumentation.Initialize();
            instrumentation.IsInitialized.Should().BeTrue(
                "this test is about the enabled path, so a bailed-out Initialize would make it vacuous");

            var options = new AspNetCoreTraceInstrumentationOptions();
            options.EnrichWithException.Should().BeNull("sanity check: nothing is installed yet");

            new Plugin().ConfigureTracesOptions(options);

            options.EnrichWithException.Should().NotBeNull(
                "IncidentSnapshot needs the exception message and stack, and no span tag carries " +
                "them; EnrichWithException is the only hook that hands over the live Exception");

            using var source = new ActivitySource(TestSourceName);
            using var listener = ListenTo(TestSourceName);
            using var activity = source.StartActivity("ingress", ActivityKind.Server)!;

            options.EnrichWithException!(activity, Catch(() => throw new InvalidOperationException("boom")));

            var (type, message, stack) = ExceptionCapture.TryRead(activity);
            type.Should().Be("System.InvalidOperationException");
            message.Should().Be("boom");
            stack.Should().NotBeNullOrEmpty("the incident's call_path is derived from the stack");

            activity.Events.Should().BeEmpty(
                "no exception event may be added — that is exactly what RecordException would have " +
                "done, and avoiding it is why this channel exists");
        }
        finally
        {
            ServiceEventsInstrumentation.ResetForTests();
            restore();
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    /// <summary>
    /// <c>options</c> is the customer's object and they may already have set their own enrichment.
    /// The plugin chains rather than assigns, so this asserts their callback still runs alongside
    /// ours. Assigning would silently delete it, with nothing to notice at runtime.
    /// </summary>
    [Fact]
    public void EnabledServiceEvents_ChainsAnExistingCustomerEnrichment()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-gate-chain-{Guid.NewGuid():N}.ndjson");
        var restore = SetEnvironment(new()
        {
            [Plugin.ApplicationSignalsEnabledConfig] = "false",
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
            ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
            ["RESOURCE_DETECTORS_ENABLED"] = "false",
        });

        try
        {
            ServiceEventsInstrumentation.ResetForTests();
            var instrumentation = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());
            instrumentation.Initialize();
            instrumentation.IsInitialized.Should().BeTrue();

            var customerCalls = 0;
            Exception? customerSaw = null;
            var options = new AspNetCoreTraceInstrumentationOptions
            {
                EnrichWithException = (_, ex) =>
                {
                    customerCalls++;
                    customerSaw = ex;
                },
            };

            new Plugin().ConfigureTracesOptions(options);

            using var source = new ActivitySource(TestSourceName);
            using var listener = ListenTo(TestSourceName);
            using var activity = source.StartActivity("ingress", ActivityKind.Server)!;
            var thrown = Catch(() => throw new InvalidOperationException("boom"));

            options.EnrichWithException!(activity, thrown);

            customerCalls.Should().Be(1, "the customer's own enrichment must still be invoked exactly once");
            customerSaw.Should().BeSameAs(thrown, "and it must receive the real exception, unaltered");

            ExceptionCapture.TryRead(activity).Type.Should().Be(
                "System.InvalidOperationException",
                "chaining must not come at the cost of our own capture");
        }
        finally
        {
            ServiceEventsInstrumentation.ResetForTests();
            restore();
            if (File.Exists(outputFile))
            {
                File.Delete(outputFile);
            }
        }
    }

    /// <summary>A listener that samples everything, so StartActivity returns a real Activity.</summary>
    private static ActivityListener ListenTo(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
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

    private static Action SetEnvironment(Dictionary<string, string?> values)
    {
        var previous = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            previous[key] = System.Environment.GetEnvironmentVariable(key);
            System.Environment.SetEnvironmentVariable(key, value);
        }

        return () =>
        {
            foreach (var (key, value) in previous)
            {
                System.Environment.SetEnvironmentVariable(key, value);
            }
        };
    }

    /// <summary>
    /// Puts ServiceEvents into the exact state that made the original gate wrong: singleton
    /// created, but Initialize() bailed out because the feature is disabled.
    /// </summary>
    private static ServiceEventsInstrumentation CreateDisabledInstrumentation()
    {
        // Reset first. The singleton is process-global and GetOrCreate returns whatever already
        // exists, so without this the assertions below would read another test's initialized
        // instance and this test would depend on execution order. ServiceEventsSamplerGateTest
        // resets for the same reason.
        ServiceEventsInstrumentation.ResetForTests();

        // The config is built in memory rather than via FromEnvironment() on purpose. Environment
        // variables are process-global and xUnit runs test classes in parallel, so reading
        // OTEL_AWS_APPLICATION_SIGNALS_ENABLED here would race with other classes that set it
        // (TracerConfigurerTest turns it on in its constructor). Setting ApplicationSignalsEnabled
        // directly reproduces the default customer posture deterministically.
        var config = new ServiceEventsConfig { ApplicationSignalsEnabled = false };

        var instrumentation = ServiceEventsInstrumentation.GetOrCreate(config);
        instrumentation.Initialize();
        return instrumentation;
    }
}
