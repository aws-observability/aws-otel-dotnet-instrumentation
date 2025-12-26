# AWS CloudWatch EMF Exporters for .NET

This directory contains OpenTelemetry metrics exporters that convert metrics to CloudWatch Embedded Metric Format (EMF).

## Exporters

### AwsCloudWatchEmfExporter
Exports metrics to CloudWatch Logs in EMF format, which are automatically extracted as CloudWatch metrics.

### ConsoleEmfExporter
Exports metrics to console output in EMF format for debugging and testing purposes.

## Usage

```csharp
using OpenTelemetry.Metrics;
using AWS.Distro.OpenTelemetry.AutoInstrumentation.Exporters.Aws.Metrics;

// Add CloudWatch EMF exporter
var meterProvider = Meter.CreateMeterProvider(builder =>
    builder.AddAwsCloudWatchEmfExporter(
        namespaceName: "MyApplication",
        logGroupName: "my-app-metrics",
        logStreamName: "my-stream"));

// Add Console EMF exporter for debugging
var meterProvider = Meter.CreateMeterProvider(builder =>
    builder.AddConsoleEmfExporter("MyApplication"));
```

## Features

- Converts OpenTelemetry metrics to CloudWatch EMF format
- Batches log events for efficient CloudWatch Logs delivery
- Handles CloudWatch Logs constraints (message size, batch size, timestamp limits)
- Supports all OpenTelemetry metric types (Counter, Gauge, Histogram)
- Automatic log group and stream creation
- Resource attributes included as metadata

## CloudWatch EMF Format

The exporters generate JSON logs following the [CloudWatch EMF specification](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch_Embedded_Metric_Format_Specification.html).