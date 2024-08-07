@echo off
setlocal

:: This script is expected to be used in a build that specified a RuntimeIdentifier (RID)
set BASE_PATH=%~dp0

set OTEL_DOTNET_AUTO_PLUGINS=AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation

set OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
set OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4316
set OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT=http://127.0.0.1:4316/v1/metrics
set OTEL_METRICS_EXPORTER=none
set OTEL_AWS_APPLICATION_SIGNALS_ENABLED=true
set OTEL_TRACES_SAMPLER=xray
set OTEL_TRACES_SAMPLER_ARG=endpoint=http://127.0.0.1:2000

call %BASE_PATH%instrument.cmd %*
