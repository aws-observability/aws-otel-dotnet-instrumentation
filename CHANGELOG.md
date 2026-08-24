# Changelog

All notable changes to this project will be documented in this file.

> **Note:** This CHANGELOG was created starting after version 1.9.1. Earlier changes are not documented here.

For any change that affects end users of this package, please add an entry under the **Unreleased** section. Briefly summarize the change and provide the link to the PR. Example:
- add GenAI attribute support for Amazon Bedrock models
  ([#137](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/137))

If your change does not need a CHANGELOG entry, add the "skip changelog" label to your PR.

## Unreleased
- Add ServiceEvents, which emits per-endpoint summaries, error metrics, deployment events,
  incident snapshots, and per-function duration histograms to power Application Signals'
  service investigation experience. Targets modern .NET (net8.0/net9.0/net10.0); does not
  initialize on .NET Framework apps. Enabled automatically wherever AWS Application Signals
  is enabled, and never in AWS Lambda. Set `OTEL_AWS_SERVICE_EVENTS_ENABLED=false` to opt
  out, or `=true` to enable it without Application Signals (which additionally requires
  `OTEL_AWS_OTLP_LOGS_ENDPOINT` and `OTEL_AWS_OTLP_METRICS_ENDPOINT`). Per-function
  instrumentation stays off until `OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE` names at least
  one package
  ([#443](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/443),
  [#447](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/447))
- Attribute presigned S3 URLs as `AWS::S3` dependencies in Application Signals, opt-in via
  `OTEL_AWS_APPLICATION_SIGNALS_PRESIGNED_URL_ATTRIBUTION_ENABLED`
  ([#440](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/440))

- Apply OTEL_AWS_HTTP_OPERATION_PATHS to aws.local.operation for ASP.NET Core spans
  ([#441](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/441))

- Add adaptive sampling support for anomaly detection and trace capture
  ([#410](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/410))

- **(Breaking Change)** Migrate AWS SDK for .NET dependency from v3 to v4. Since AWS SDK v4 targets .NET Framework 4.7.2 and above, this change drops support for .NET Framework 4.6.2.
  ([#436](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/436))

  **Note: this release drops support for AWS SDK for .NET v3.** Users should upgrade their applications to AWS SDK v4 to continue using the latest distribution.

## v1.14.0 - 2026-07-15
- Fix Linux arm64 image being built with the x64 payload, which caused the shared
  store and native profiler to not match the image architecture and made .NET
  applications fail to start on arm64. Images are now built per architecture.
  ([#424](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/424))

## v1.13.0 - 2026-06-06
- Update OpenTelemetry dependencies - Core: 1.15.3, Instrumentation: 1.15.0
  ([#414](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/414))
- Support environment-configured endpoint visibility for HTTP operation names
  ([#392](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/392))
- Enhancement(lambda-layer): Align CompactConsoleLogRecordExporter output with CloudWatch OTLP backend schema.
  Field renames: `timestamp` → `timeUnixNano`, `observedTimestamp` → `observedTimeUnixNano`,
  `instrumentationScope` → `scope`, `traceFlags` → `flags`. Attribute values preserve native
  types. Added `exportPath: "console"` discriminator field.
  ([#394](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/394))

## v1.12.0 - 2026-03-19
- KafkaEvent input type support for Lambda and Task<unit> return type serialization issue fix for f#
  ([#368](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/368))
- OTel dependency update: Upgrade Core to 1.15.0 and Instrumentation to v1.13.0
  ([311](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/311))
- add dotnet9 and dotnet10 support
  ([373](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/373))

## v1.11.1 - 2026-02-11
- Migrate dotnet linux image to scratch base to avoid vulnerability scan tickets
  ([#358](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/358))

## v1.11.0 - 2026-01-20
- Ugraded OTel Instrumentation.AWS dependencies to 1.14.2
  ([#309](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/309))

## v1.10.1 - 2025-12-31
- Ugraded OTel Instrumentation.AWS dependencies to 1.14.1
  ([#302](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/302))

## v1.10.0 - 2025-12-10
- Upgraded OTEL runtime dependencies to 1.14 and OTEL AutoInstrumentation to 1.13
  ([#293]https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/293)

## v1.9.2 - 2025-11-11
- Fix: Disable instrumentation of AWS SDK v4
  ([#277](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/277))
