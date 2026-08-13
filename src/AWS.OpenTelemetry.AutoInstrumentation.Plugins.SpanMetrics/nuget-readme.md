# AWS OpenTelemetry Auto-Instrumentation Span Metrics Plugin

The span metrics connector derives call count and duration metrics from OpenTelemetry spans without changing the configured trace sampling rate.

## Installation

```console
dotnet add package AWS.OpenTelemetry.AutoInstrumentation.Plugins.SpanMetrics
```

## Manual registration

Register the connector using the standard OpenTelemetry SDK builder APIs:

```csharp
using AWS.OpenTelemetry.AutoInstrumentation.Plugins.SpanMetrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter(SpanMetricsConnector.ScopeName)
    .AddOtlpExporter()
    .Build();

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetSampler(new AlwaysRecordSampler())
    .AddProcessor(new SpanMetricsConnector())
    .AddOtlpExporter()
    .Build();
```

`SetSampler` ensures the processor receives every span, `AddProcessor` derives the measurements, and `AddMeter` makes those measurements visible to configured metric readers and exporters.

To preserve a custom sampling policy, wrap the sampler where it is created:

```csharp
var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1));
tracerBuilder
    .SetSampler(new AlwaysRecordSampler(sampler))
    .AddProcessor(new SpanMetricsConnector());
meterBuilder.AddMeter(SpanMetricsConnector.ScopeName);
```

A later `SetSampler` call replaces `AlwaysRecordSampler`. Do not combine manual registration with the auto-instrumentation plugin.

## Auto-instrumentation plugin

This package is distributed independently and is not bundled with the AWS Distro for OpenTelemetry .NET distribution. Install the package separately and place `AWS.OpenTelemetry.AutoInstrumentation.Plugins.SpanMetrics.dll` in the managed assemblies directory under `OTEL_DOTNET_AUTO_HOME` (`net` for .NET or `netfx` for .NET Framework).

Add the assembly-qualified plugin name to `OTEL_DOTNET_AUTO_PLUGINS`:

```sh
export OTEL_DOTNET_AUTO_PLUGINS="AWS.OpenTelemetry.AutoInstrumentation.Plugins.SpanMetrics.SpanMetricsConnectorPlugin, AWS.OpenTelemetry.AutoInstrumentation.Plugins.SpanMetrics"
```

When combining it with another plugin, separate the assembly-qualified names with `:`. List the connector after any plugin that sets a sampler because `SetSampler` is last-write-wins. The plugin wraps the sampler selected by the standard `OTEL_TRACES_SAMPLER` and `OTEL_TRACES_SAMPLER_ARG` settings with `AlwaysRecordSampler`, so processors receive every span without changing which spans are exported.

## License

This project is licensed under the Apache-2.0 License.
