# AWS OpenTelemetry CloudWatch Plugin

Amazon CloudWatch plugin for OpenTelemetry .NET.

> [!IMPORTANT]
> `CloudWatchPlugin` must be the last plugin listed in
> `OTEL_DOTNET_AUTO_PLUGINS`. A plugin listed after it can replace the required
> sampler and cause span metrics to be undercounted.

## OpenTelemetry .NET auto-instrumentation

This package extends the upstream OpenTelemetry .NET Automatic Instrumentation
distribution. It does not require the AWS Distro for OpenTelemetry.

The `0.1.x` package line supports OpenTelemetry SDK versions greater than or
equal to `1.15.3` and less than `2.0.0`.

For automatic instrumentation, use a distribution that contains a supported
OpenTelemetry SDK. The convention-based adapter supports these released
upstream OpenTelemetry .NET Automatic Instrumentation versions:

| Automatic Instrumentation | OpenTelemetry SDK |
|---------------------------|-------------------|
| `1.15.0`                  | `1.15.3`          |
| `1.16.0`                  | `1.16.0`          |
| `1.16.0`                  | `1.17.0`          |

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
`:`, keeping `CloudWatchPlugin` last. Standard upstream sampler configuration
through `OTEL_TRACES_SAMPLER` and `OTEL_TRACES_SAMPLER_ARG` is supported.
Unsupported `OTEL_TRACES_SAMPLER` values are rejected rather than replaced with
a different sampling policy.

## Manual OpenTelemetry SDK registration

The span metrics connector emits `traces.span.metrics.calls` and
`traces.span.metrics.duration` from recorded spans. Wire it into the
application's existing OpenTelemetry builders:

```csharp
using AWS.OpenTelemetry.CloudWatch.Plugin;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1));

tracerBuilder
    .AddCloudWatchSpanMetrics(sampler);

meterBuilder.AddCloudWatchSpanMetrics();
```

The tracer extension wraps the supplied sampler so the connector observes every
span without changing which spans are exported. The meter extension subscribes
the application's existing `MeterProvider`; it does not create another
provider. Use the parameterless tracer extension to use the OpenTelemetry SDK
default sampler. Register span metrics after all other sampler configuration;
a later `SetSampler` call replaces the required always-record wrapper.

Do not combine manual registration with the auto-instrumentation plugin.

## Metrics

The plugin emits:

- `traces.span.metrics.calls`, a counter of completed spans.
- `traces.span.metrics.duration`, a histogram of span duration in seconds.

Both metrics include `service.name`, `span.name`, `span.kind`, `status.code`,
schema version, and plugin version when available. They also include applicable
HTTP, RPC, database, error, and messaging attributes from the source span.
While OTLP span metrics are active, recorded spans also include the schema and
plugin version attributes so the backend can avoid deriving duplicate metrics.

Metric dimensions create a distinct CloudWatch time series for each unique
combination of values. In particular, `span.name`, `http.route`, database
collection or table names, and messaging destinations can have high
cardinality. Keep those values bounded to control CloudWatch metric volume and
cost.

## License

This project is licensed under the Apache-2.0 License.
