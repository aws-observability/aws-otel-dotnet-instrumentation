// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AWS.Distro.OpenTelemetry.ServiceEvents;

/// <summary>
/// ServiceEvents plugin entry point loaded by the upstream OpenTelemetry .NET
/// auto-instrumentation agent via <c>OTEL_DOTNET_AUTO_PLUGINS</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered alongside the AWS distro's existing
/// <c>AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin</c> as a sibling
/// entry. The upstream CLR profiler reflects this class, instantiates it,
/// and invokes its lifecycle hooks at well-defined phases of SDK setup.
/// </para>
/// <para>
/// Lifecycle order (from AWS distro <c>Plugin.cs</c> reference):
/// <list type="number">
/// <item><description><see cref="Initializing" /> — before any SDK config.</description></item>
/// <item><description><see cref="ConfigureResource" /> — resource detector phase.</description></item>
/// <item><description><see cref="BeforeConfigureTracerProvider" /> — pre-tracer.</description></item>
/// <item><description><see cref="AfterConfigureTracerProvider" /> — post-tracer (collectors register here as <c>BaseProcessor&lt;Activity&gt;</c>).</description></item>
/// <item><description><see cref="TracerProviderInitialized" /> — after build.</description></item>
/// <item><description><see cref="AfterConfigureMeterProvider" /> — post-meter (EndpointErrorMetrics counter source registered here).</description></item>
/// <item><description><see cref="ConfigureTracesOptions(AspNetCoreTraceInstrumentationOptions)" /> — framework hook.</description></item>
/// </list>
/// </para>
/// <para>
/// Implementation lands in subsequent chunks. This class currently provides
/// the hook surface only — bodies are no-ops so the plugin loads cleanly
/// without affecting customer telemetry.
/// </para>
/// </remarks>
public class ServiceEventsPlugin
{
    /// <summary>
    /// Called by the upstream agent before SDK configuration begins. Builds the
    /// ServiceEvents config from the environment, applies the enablement rules,
    /// and initializes the singleton (which starts the collectors when enabled).
    /// </summary>
    public void Initializing()
    {
        try
        {
            var config = Config.ServiceEventsConfig.FromEnvironment();
            var instrumentation = ServiceEventsInstrumentation.GetOrCreate(config);
            instrumentation.Initialize();
        }
        catch
        {
            // Telemetry must never break the host process during agent startup.
        }
    }

    /// <summary>
    /// Customizes the resource builder with ServiceEvents-specific attributes.
    /// </summary>
    /// <param name="builder">The resource builder being configured.</param>
    /// <returns>The configured resource builder.</returns>
    public ResourceBuilder ConfigureResource(ResourceBuilder builder)
    {
        // TODO(M1): add aws.local.service, deployment.environment.name, etc.
        return builder;
    }

    /// <summary>
    /// Hook fired before the SDK configures the tracer provider.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The configured tracer provider builder.</returns>
    public TracerProviderBuilder BeforeConfigureTracerProvider(TracerProviderBuilder builder)
    {
        // No-op for ServiceEvents; included for hook completeness.
        return builder;
    }

    /// <summary>
    /// Hook fired after the SDK configures the tracer provider. ServiceEvents
    /// collectors register as <c>BaseProcessor&lt;Activity&gt;</c> instances here.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The configured tracer provider builder.</returns>
    public TracerProviderBuilder AfterConfigureTracerProvider(TracerProviderBuilder builder)
    {
        // M3: register the EndpointActivityProcessor (and, in M4, the IncidentSnapshot
        // trigger) on the customer's TracerProvider. The singleton was created and
        // initialized in Initializing(), so its collectors already exist.
        try
        {
            ServiceEventsInstrumentation.Current?.RegisterTracerProcessors(builder);
        }
        catch
        {
            // Never break the customer's tracer pipeline.
        }

        return builder;
    }

    /// <summary>
    /// Hook fired after the tracer provider is built.
    /// </summary>
    /// <param name="tracerProvider">The fully built tracer provider.</param>
    public void TracerProviderInitialized(TracerProvider tracerProvider)
    {
        // TODO(M3..M5): no-op for now. Collector wiring lands in later milestones.
    }

    /// <summary>
    /// Hook fired after the SDK configures the meter provider. The
    /// <c>EndpointErrorMetrics</c> counter source registers here.
    /// </summary>
    /// <param name="builder">The meter provider builder.</param>
    /// <returns>The configured meter provider builder.</returns>
    public MeterProviderBuilder AfterConfigureMeterProvider(MeterProviderBuilder builder)
    {
        // Deliberately a no-op. ServiceEvents metrics (the EndpointErrorMetrics `count`
        // counter) are exported via the SDK's OWN dedicated MeterProvider — see
        // ServiceEventsInstrumentation.BuildMeterProvider — whose resource carries the
        // ServiceEvents provenance attributes (service.name, aws.service_events.deployment.id,
        // vcs.ref.head.revision). Subscribing the customer's (App Signals) provider here would
        // export the counter without those resource attributes, diverging from the other SDKs'
        // wire format. The App Signals provider may log a harmless "meter not subscribed" note
        // for the serviceevents meter — that is expected and does not affect export.
        return builder;
    }

    /// <summary>
    /// Hook fired by the upstream ASP.NET Core trace instrumentation so
    /// ServiceEvents can register request-enrichment callbacks for endpoint and
    /// incident capture.
    /// </summary>
    /// <param name="options">The ASP.NET Core trace options being configured.</param>
    public void ConfigureTracesOptions(AspNetCoreTraceInstrumentationOptions options)
    {
        // Record unhandled exceptions as an `exception` event (with exception.type /
        // .message / .stacktrace) on the server span. IncidentSnapshot reads these to
        // populate exception_info; it also upgrades EndpointErrorMetrics' `exception`
        // dimension from the HTTP{status} fallback to the real exception type.
        options.RecordException = true;
    }
}
