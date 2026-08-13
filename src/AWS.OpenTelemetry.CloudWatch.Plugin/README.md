# AWS OpenTelemetry CloudWatch Plugin

Amazon CloudWatch plugin for OpenTelemetry .NET.

## Installation

```console
dotnet add package AWS.OpenTelemetry.CloudWatch.Plugin
```

## Span metrics

The span metrics connector emits `traces.span.metrics.calls` and
`traces.span.metrics.duration` from recorded spans. Register it through the
standard OpenTelemetry builder APIs:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddCloudWatchSpanMetrics()
    .AddOtlpExporter()
    .Build();

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddCloudWatchSpanMetrics()
    .AddOtlpExporter()
    .Build();
```

The tracer extension wraps the sampler selected by `OTEL_TRACES_SAMPLER` and
`OTEL_TRACES_SAMPLER_ARG`. Drop decisions become record-only decisions so the
connector observes every span without changing which spans are exported.

Pass a sampler to preserve an application-defined sampling policy:

```csharp
var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1));

tracerBuilder.AddCloudWatchSpanMetrics(sampler);
meterBuilder.AddCloudWatchSpanMetrics();
```

A later `SetSampler` call replaces the wrapper. Do not combine builder
registration with the auto-instrumentation plugin.

## Auto-instrumentation plugin

This package is distributed independently and is not bundled with the AWS
Distro for OpenTelemetry .NET distribution. Install it separately and place
`AWS.OpenTelemetry.CloudWatch.Plugin.dll` in the managed assemblies directory
under `OTEL_DOTNET_AUTO_HOME` (`net` for .NET or `netfx` for .NET Framework).

Add the assembly-qualified plugin name to `OTEL_DOTNET_AUTO_PLUGINS`:

```sh
export OTEL_DOTNET_AUTO_PLUGINS="AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, AWS.OpenTelemetry.CloudWatch.Plugin"
```

When combining it with another plugin, separate assembly-qualified names with
`:`, and list this plugin after any plugin that sets a sampler.

## License

This project is licensed under the Apache-2.0 License.
