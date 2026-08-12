// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using AWS.Distro.OpenTelemetry.ServiceEvents;
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
            "a customer with ServiceEvents disabled must see unchanged server-span content");
    }

    /// <summary>
    /// Puts ServiceEvents into the exact state that made the original gate wrong: singleton
    /// created, but Initialize() bailed out because the feature is disabled.
    /// </summary>
    private static ServiceEventsInstrumentation CreateDisabledInstrumentation()
    {
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
