# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

from typing import Dict

from typing_extensions import override

from amazon.cloudwatch_plugin_otel.span_metrics import InstrumentationMode
from amazon.cloudwatch_plugin_otel.span_metrics.contract_test_base import (
    SpanMetricsContractTestBase,
)


class SpanMetricsManualInstrumentationGlobalTest(SpanMetricsContractTestBase):
    __test__ = True

    @override
    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        return {
            **super().get_application_extra_environment_variables(),
            "CORECLR_ENABLE_PROFILING": "0",
            "CORECLR_PROFILER": "",
            "CORECLR_PROFILER_PATH": "",
            "DOTNET_ADDITIONAL_DEPS": "",
            "DOTNET_SHARED_STORE": "",
            "DOTNET_STARTUP_HOOKS": "",
            "OTEL_DOTNET_AUTO_HOME": "",
            "OTEL_DOTNET_AUTO_PLUGINS": "",
            "SPAN_METRICS_MODE": str(InstrumentationMode.MANUAL_GLOBAL),
        }

    @override
    def get_mode(self) -> InstrumentationMode:
        return InstrumentationMode.MANUAL_GLOBAL
