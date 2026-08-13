# AWS OpenTelemetry CloudWatch Plugin

Amazon CloudWatch plugin for OpenTelemetry .NET.

## Installation

```console
dotnet add package AWS.OpenTelemetry.CloudWatch.Plugin
```

## Span metrics

The span metrics connector emits `traces.span.metrics.calls` and
`traces.span.metrics.duration` from recorded spans. Wire the sampler and
processor into the OpenTelemetry SDK directly:

```csharp
using AWS.OpenTelemetry.CloudWatch.Plugin;
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

Wrap an application-defined sampler to preserve its export decisions:

```csharp
var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1));

tracerBuilder
    .SetSampler(new AlwaysRecordSampler(sampler))
    .AddProcessor(new SpanMetricsConnector());
meterBuilder.AddMeter(SpanMetricsConnector.ScopeName);
```

`AlwaysRecordSampler` converts drop decisions to record-only decisions so the
connector observes every span without changing which spans are exported. A
later `SetSampler` call replaces the wrapper. Do not combine manual registration
with the auto-instrumentation plugin.

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
