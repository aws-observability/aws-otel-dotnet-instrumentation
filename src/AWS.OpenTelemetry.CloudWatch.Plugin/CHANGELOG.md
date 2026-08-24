# Changelog - AWS.OpenTelemetry.CloudWatch.Plugin

## Unreleased

## 0.1.0 - 2026-08-22

### Added

- Generation of `traces.span.metrics.calls` and
  `traces.span.metrics.duration` from OpenTelemetry .NET spans.
- Support for OpenTelemetry .NET automatic instrumentation and manual SDK
  registration.
- Span metric recording independent of trace export sampling.
