#!/bin/sh

# This script is expected to be used in a build that specified a RuntimeIdentifier (RID)
BASE_PATH="$(cd "$(dirname "$0")" && pwd)"

export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"

export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:4316"
export OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT="http://127.0.0.1:4316/v1/metrics"
export OTEL_METRICS_EXPORTER=none
export OTEL_AWS_APPLICATION_SIGNALS_ENABLED="true"
export OTEL_TRACES_SAMPLER="xray"
export OTEL_TRACES_SAMPLER_ARG="endpoint=http://127.0.0.1:2000"

. $BASE_PATH/instrument.sh

exec "$@"
