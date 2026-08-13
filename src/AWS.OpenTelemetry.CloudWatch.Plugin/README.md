# AWS OpenTelemetry CloudWatch Plugin

Amazon CloudWatch plugin for OpenTelemetry .NET.

## OpenTelemetry .NET auto-instrumentation

This package extends the upstream OpenTelemetry .NET Automatic Instrumentation
distribution. It does not require the AWS Distro for OpenTelemetry.

The `1.0.x` package line supports OpenTelemetry SDK versions greater than or
equal to `1.15.3` and less than `2.0.0`.

For automatic instrumentation, use a distribution that contains a supported
OpenTelemetry SDK. The convention-based adapter supports these released
upstream OpenTelemetry .NET Automatic Instrumentation versions:

| Automatic Instrumentation | OpenTelemetry SDK |
|---------------------------|-------------------|
| `1.15.0`                  | `1.15.3`          |
| `1.16.0`                  | `1.16.0`          |

Later automatic instrumentation versions that require the experimental public
plugin API are not supported by this adapter.

Install the package in the instrumented application:

```console
dotnet add package AWS.OpenTelemetry.CloudWatch.Plugin
```

Then add the plugin's assembly-qualified type to the upstream OTel
auto-instrumentation configuration:

```sh
export OTEL_DOTNET_AUTO_PLUGINS="AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, AWS.OpenTelemetry.CloudWatch.Plugin"
```

Keep the application's existing upstream OTel auto-instrumentation settings,
including `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER`,
`CORECLR_PROFILER_PATH`, `DOTNET_ADDITIONAL_DEPS`, `DOTNET_SHARED_STORE`,
`DOTNET_STARTUP_HOOKS`, and exporter configuration.

For a deployment that cannot add a package reference, extract the
framework-specific `AWS.OpenTelemetry.CloudWatch.Plugin.dll` from the NuGet
package into the upstream distribution's managed assemblies directory under
`OTEL_DOTNET_AUTO_HOME` (`net` for .NET or `netfx` for .NET Framework), then set
`OTEL_DOTNET_AUTO_PLUGINS` as shown above.

When combining this with another plugin, separate assembly-qualified names with
`:`, and list this plugin after any plugin that sets a sampler.

## Manual OpenTelemetry SDK registration

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

## License

This project is licensed under the Apache-2.0 License.
