# AWS Distro for OpenTelemetry .NET - Contract Tests

This directory contains contract tests for AWS Distro for OpenTelemetry .NET

## Test Infrastructure Setup

Current infrastructure for contract testing including 2 parts:
* Mock Collector Service: This is a web server listened to OTLP exporter endpoint to enable us collect traces and metrics from instrumented application.
* Instrumented Test Application: This is the application instrumented with desired OpenTelemetry configuration
Mock Collector Service and Instrumented Test Application will run as docker containers which share the same Network.

## Running the contract test locally (WIP to automate the process)

If you want to run contract test locally, follow steps below:
1. Build Test Application Image. Currently we are using the sample-application, you could find instructions [here](https://github.com/aws-observability/aws-otel-dotnet-instrumentation/tree/main/sample-applications/integration-test-app)
2. Build MockCollector image. Go to test/images/MockCollector folder, run ```bash build-image.sh```
3. In Contract Test, run ```dotnet test```

