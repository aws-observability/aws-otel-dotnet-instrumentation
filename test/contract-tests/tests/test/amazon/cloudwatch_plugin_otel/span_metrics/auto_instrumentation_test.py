# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

from typing import Dict

from typing_extensions import override

from amazon.cloudwatch_plugin_otel.span_metrics import InstrumentationMode
from amazon.cloudwatch_plugin_otel.span_metrics.contract_test_base import (
    SpanMetricsContractTestBase,
)


class SpanMetricsAutoInstrumentationTest(SpanMetricsContractTestBase):
    __test__ = True

    @override
    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        return {
            **super().get_application_extra_environment_variables(),
            "CORECLR_ENABLE_PROFILING": "1",
            "CORECLR_PROFILER": "{918728DD-259F-4A6A-AC2B-B85E1B658318}",
            "CORECLR_PROFILER_PATH": (
                "/opt/aws/otel/dotnet/linux-x64/OpenTelemetry.AutoInstrumentation.Native.so"
            ),
            "DOTNET_ADDITIONAL_DEPS": "/opt/aws/otel/dotnet/AdditionalDeps",
            "DOTNET_SHARED_STORE": "/opt/aws/otel/dotnet/store",
            "DOTNET_STARTUP_HOOKS": (
                "/opt/aws/otel/dotnet/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
            ),
            "OTEL_DOTNET_AUTO_HOME": "/opt/aws/otel/dotnet",
            "OTEL_DOTNET_AUTO_LOGGER": "console",
            "OTEL_DOTNET_AUTO_METRICS_INSTRUMENTATION_ENABLED": "false",
            "OTEL_DOTNET_AUTO_PLUGINS": (
                "CloudWatchPluginOtel.AwsInstrumentationPlugin, CloudWatchPluginOtel:"
                "AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, "
                "AWS.OpenTelemetry.CloudWatch.Plugin"
            ),
            "OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES": (
                "CloudWatchPluginOtel.Contract"
            ),
            "OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_ENABLED": "true",
            "OTEL_DOTNET_AUTO_TRACES_ENTITYFRAMEWORKCORE_INSTRUMENTATION_ENABLED": "true",
            "OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_ENABLED": "true",
            "OTEL_DOTNET_AUTO_TRACES_HTTPCLIENT_INSTRUMENTATION_ENABLED": "true",
            "OTEL_DOTNET_AUTO_TRACES_INSTRUMENTATION_ENABLED": "false",
            "OTEL_DOTNET_AUTO_TRACES_STACKEXCHANGEREDIS_INSTRUMENTATION_ENABLED": "true",
            "OTEL_LOG_LEVEL": "info",
            "SPAN_METRICS_MODE": str(InstrumentationMode.AUTO),
        }

    @override
    def get_mode(self) -> InstrumentationMode:
        return InstrumentationMode.AUTO
