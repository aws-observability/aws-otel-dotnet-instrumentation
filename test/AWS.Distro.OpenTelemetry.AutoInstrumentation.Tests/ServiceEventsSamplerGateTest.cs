// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using AWS.Distro.OpenTelemetry.ServiceEvents;
using AWS.Distro.OpenTelemetry.ServiceEvents.Config;
using FluentAssertions;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests;

/// <summary>
/// Pins the sampler wiring in <see cref="Plugin.AfterConfigureTracerProvider" />.
/// </summary>
/// <remarks>
/// <para>
/// ServiceEvents collects endpoint metrics from a <c>BaseProcessor&lt;Activity&gt;</c>, and the OTel
/// SDK only calls <c>OnEnd</c> for activities it considers recorded. A <c>Drop</c> decision
/// therefore hides the activity from the collector entirely. <c>AlwaysRecordSampler</c> rewrites
/// <c>Drop</c> to <c>RecordOnly</c>, which is why Application Signals installs it — and ServiceEvents
/// needs it for exactly the same reason.
/// </para>
/// <para>
/// It used to be installed only under Application Signals, so a standalone ServiceEvents deployment
/// with a sampling sampler (<c>OTEL_TRACES_SAMPLER=always_off</c>, <c>traceidratio</c>, X-Ray) quietly
/// thinned out or lost its endpoint metrics. The negative test below is the other half of the
/// contract: a customer running neither feature must keep their sampler untouched.
/// </para>
/// </remarks>
[Collection("ServiceEventsSingleton")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Tests")]
public class ServiceEventsSamplerGateTest
{
    [Fact]
    public void ServiceEventsActive_WithoutApplicationSignals_InstallsAlwaysRecordSampler()
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"se-sampler-{Guid.NewGuid():N}.ndjson");

        var restore = SetEnvironment(new()
        {
            [Plugin.ApplicationSignalsEnabledConfig] = "false",
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "true",
            ["OTEL_AWS_SERVICE_EVENTS_OUTPUT_FILE"] = outputFile,
            ["RESOURCE_DETECTORS_ENABLED"] = "false",

            // A sampler that drops everything makes the failure mode unambiguous: without the
            // wrapper every activity is dropped and the collector sees nothing.
            ["OTEL_TRACES_SAMPLER"] = "always_off",
        });

        try
        {
            ServiceEventsInstrumentation.ResetForTests();
            var instrumentation = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());
            instrumentation.Initialize();
            instrumentation.IsInitialized.Should().BeTrue(
                "the test needs ServiceEvents genuinely running, not merely constructed");

            var sampler = BuildAndExtractSampler();

            // Guard against a vacuous pass: xUnit runs collections in parallel and other classes in
            // this assembly set the Application Signals flag process-wide. If it flipped, the
            // assertion below would hold for the wrong reason.
            System.Environment.GetEnvironmentVariable(Plugin.ApplicationSignalsEnabledConfig)
                .Should().Be("false", "Application Signals must stay off for this test to mean anything");

            sampler.Should().NotBeNull("the plugin must set a sampler on the builder");
            sampler!.Description.Should().StartWith(
                "AlwaysRecordSampler",
                "ServiceEvents needs Drop rewritten to RecordOnly so EndpointActivityProcessor.OnEnd runs");
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

    [Fact]
    public void NeitherFeatureActive_LeavesTheConfiguredSamplerUnwrapped()
    {
        var restore = SetEnvironment(new()
        {
            [Plugin.ApplicationSignalsEnabledConfig] = "false",
            ["OTEL_AWS_SERVICE_EVENTS_ENABLED"] = "false",
            ["RESOURCE_DETECTORS_ENABLED"] = "false",
            ["OTEL_TRACES_SAMPLER"] = "always_off",
        });

        try
        {
            ServiceEventsInstrumentation.ResetForTests();
            var instrumentation = ServiceEventsInstrumentation.GetOrCreate(ServiceEventsConfig.FromEnvironment());
            instrumentation.Initialize();
            instrumentation.IsInitialized.Should().BeFalse(
                "the kill switch is off, so ServiceEvents must not be running");

            var sampler = BuildAndExtractSampler();

            System.Environment.GetEnvironmentVariable(Plugin.ApplicationSignalsEnabledConfig)
                .Should().Be("false", "Application Signals must stay off for this test to mean anything");

            sampler.Should().NotBeNull();
            sampler!.Description.Should().NotStartWith(
                "AlwaysRecordSampler",
                "a customer running neither feature must keep the sampling behaviour they configured");
        }
        finally
        {
            ServiceEventsInstrumentation.ResetForTests();
            restore();
        }
    }

    /// <summary>
    /// Run the plugin hook under test and return the sampler the SDK ended up with.
    /// </summary>
    private static Sampler? BuildAndExtractSampler()
    {
        TracerProviderBuilder builder = new TracerProviderBuilderBase();
        builder = new Plugin().AfterConfigureTracerProvider(builder);

        using var provider = builder.Build();
        return ExtractSampler(provider!);
    }

    /// <summary>
    /// Pull the configured sampler off the built provider. The SDK does not expose it publicly, so
    /// this searches for the first member of type <see cref="Sampler" /> rather than hard-coding a
    /// field name that an SDK upgrade could rename.
    /// </summary>
    private static Sampler? ExtractSampler(TracerProvider provider)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = provider.GetType();

        foreach (var property in type.GetProperties(flags))
        {
            if (typeof(Sampler).IsAssignableFrom(property.PropertyType))
            {
                return property.GetValue(provider) as Sampler;
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (typeof(Sampler).IsAssignableFrom(field.FieldType))
            {
                return field.GetValue(provider) as Sampler;
            }
        }

        return null;
    }

    /// <summary>Set environment variables and return an action restoring the previous values.</summary>
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
}
