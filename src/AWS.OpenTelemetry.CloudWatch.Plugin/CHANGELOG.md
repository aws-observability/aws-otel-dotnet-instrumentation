# Changelog - AWS.OpenTelemetry.CloudWatchPluginOtel

## Unreleased

* **BREAKING:** Renamed the assembly and `CloudWatchPlugin` namespace from
  `AWS.OpenTelemetry.CloudWatch.Plugin` to
  `AWS.OpenTelemetry.CloudWatchPluginOtel`. Update
  `OTEL_DOTNET_AUTO_PLUGINS` and application imports to use the new name.
* **BREAKING:** Moved the `AddCloudWatchSpanMetrics` extension methods from the
  `OpenTelemetry.Metrics` and `OpenTelemetry.Trace` namespaces to
  `AWS.OpenTelemetry.CloudWatchPluginOtel`.
* **BREAKING:** Moved `service.name` from metric datapoint attributes to the
  metric resource. Manual registration must configure `service.name` on the
  meter provider's resource.
* **BREAKING:** Renamed the diagnostics event source from
  `OpenTelemetry-AWS-CloudWatch-Plugin` to
  `OpenTelemetry-AWS-CloudWatchPluginOtel`.
* Set the `traces.span.metrics.calls` unit to `{call}`.

## 0.1.0 - 2026-08-24

* Added span metrics.
