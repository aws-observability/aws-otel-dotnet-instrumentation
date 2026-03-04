# Changelog

All notable changes to this project will be documented in this file.

> **Note:** This CHANGELOG was created starting after version 1.9.1. Earlier changes are not documented here.

For any change that affects end users of this package, please add an entry under the **Unreleased** section. Briefly summarize the change and provide the link to the PR. Example:
- add GenAI attribute support for Amazon Bedrock models
  ([#137](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/137))

If your change does not need a CHANGELOG entry, add the "skip changelog" label to your PR.
## Unreleased
- KafkaEvent input type support for Lambda and Task<unit> return type serialization issue fix for f#
([#368](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/368))

## Unreleased
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
