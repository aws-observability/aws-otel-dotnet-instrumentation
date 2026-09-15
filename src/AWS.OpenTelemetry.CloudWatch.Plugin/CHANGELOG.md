# Changelog - AWS.OpenTelemetry.CloudWatchPluginOtel

## Unreleased

* Added derived metric dimensions for dependency-edge (topology) metrics: additional messaging
  keys (`messaging.operation.type`, `messaging.consumer.group.name`), peer (`server.address`,
  `server.port`), GenAI (`gen_ai.request.model`, `gen_ai.provider.name`, `gen_ai.operation.name`),
  AWS resource identity (`aws.s3.bucket`, `aws.dynamodb.table_names`, `aws.lambda.invoked_arn`,
  `aws.sns.topic.arn`, `aws.sqs.queue.url`), and FaaS (`faas.invoked_name`, `faas.invoked_provider`,
  `faas.invoked_region`, `faas.trigger`) semantic-convention attributes, copied from the span when
  present.
  ([#468](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/468))
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

* Added CloudWatch span metrics instrumentation.
  ([#445](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/pull/445))
