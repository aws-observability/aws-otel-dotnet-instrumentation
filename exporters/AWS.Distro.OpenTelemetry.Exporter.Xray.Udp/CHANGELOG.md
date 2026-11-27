# Release History: AWS.Distro.OpenTelemetry.Exporter.Xray.Udp

### v1.1.0 / 2026-11-24

* Fix `XrayUdpExporter` crashing with `MissingMethodException: WriteTraceData not found` when
  `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18.0 or later is resolved
  ([#483](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/483)).
* Return `ExportResult.Failure` and log a warning for trace batches that exceed the UDP datagram size limit,
  instead of throwing from the serializer
  ([#500](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/500)).
* Update OpenTelemetry dependencies to 1.20.1
  ([#465](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/465)).
* Add `net9.0` and `net10.0` target frameworks
  ([#373](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/373)).
* **(Breaking Change)** Raise the minimum .NET Framework target from `net462` to `net472`
  ([#436](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/436)).

### v1.0.0 / 2025-05-15

* Initial release
