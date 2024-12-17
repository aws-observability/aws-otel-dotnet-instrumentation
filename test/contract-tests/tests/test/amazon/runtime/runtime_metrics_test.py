# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
from time import sleep
from typing import Dict, List

from mock_collector_client import ResourceScopeMetric, ResourceScopeSpan
from typing_extensions import override

import amazon.utils.application_signals_constants as constants
from amazon.base.contract_test_base import ContractTestBase
from opentelemetry.proto.common.v1.common_pb2 import AnyValue
from opentelemetry.proto.metrics.v1.metrics_pb2 import Metric, NumberDataPoint


class RuntimeMetricsTest(ContractTestBase):

    def tear_down(self) -> None:
        super().tear_down()
        # sleep for 5s and clear the signals again to avoid race condition between sending the last batch of runtime
        # metrics and the complete termination of the container
        sleep(5)
        self.mock_collector_client.clear_signals()

    @override
    def is_runtime_enabled(self) -> str:
        return "true"

    @override
    @staticmethod
    def get_application_image_name() -> str:
        return "aws-application-signals-tests-appsignals.netcore-app"

    @override
    def get_application_wait_pattern(self) -> str:
        return "Content root path: /app"

    @override
    def get_application_extra_environment_variables(self):
        return {
            "OTEL_DOTNET_AUTO_TRACES_CONSOLE_EXPORTER_ENABLED": "true"
        }

    def test_runtime_succeeds(self) -> None:
        # Trigger GC to generate all runtime metrics
        self.do_send_request("gc", "GET", 200)

        metrics: List[ResourceScopeMetric] = self.mock_collector_client.get_metrics(
            {
                constants.LATENCY_METRIC,
                constants.ERROR_METRIC,
                constants.FAULT_METRIC,
                constants.GC_COUNT_METRIC,
                constants.GC_DURATION_METRIC,
                constants.GC_HEAP_METRIC,
                constants.THREAD_COUNT_METRIC,
                constants.THREAD_QUEUE_METRIC,
            },
            False
        )
        self._assert_resource_attributes(metrics)
        self._assert_counter_attribute_exists(metrics, constants.GC_COUNT_METRIC, "generation")
        self._assert_counter_attribute_exists(metrics, constants.GC_DURATION_METRIC, "")
        self._assert_counter_attribute_exists(metrics, constants.GC_HEAP_METRIC, "generation")
        self._assert_counter_attribute_exists(metrics, constants.THREAD_COUNT_METRIC, "")
        self._assert_counter_attribute_exists(metrics, constants.THREAD_QUEUE_METRIC, "")

    @override
    def _assert_aws_span_attributes(self, resource_scope_spans: List[ResourceScopeSpan], path: str, **kwargs) -> None:
        return

    @override
    def _assert_semantic_conventions_span_attributes(
            self, resource_scope_spans: List[ResourceScopeSpan], method: str, path: str, status_code: int, **kwargs
    ) -> None:
        return

    @override
    def _assert_metric_attributes(
            self,
            resource_scope_metrics: List[ResourceScopeMetric],
            metric_name: str,
            expected_sum: int,
            **kwargs,
    ) -> None:
        return

    def _assert_resource_attributes(
        self,
        resource_scope_metrics: List[ResourceScopeMetric],
    ) -> None:
        for metric in resource_scope_metrics:
            attribute_dict: Dict[str, AnyValue] = self._get_attributes_dict(metric.resource_metrics.resource.attributes)
            self._assert_str_attribute(
                attribute_dict, constants.AWS_LOCAL_SERVICE, self.get_application_otel_service_name()
            )

    def _assert_counter_attribute_exists(
        self,
        resource_scope_metrics: List[ResourceScopeMetric],
        metric_name: str,
        attribute_key: str,
    ) -> None:
        target_metrics: List[Metric] = []
        for resource_scope_metric in resource_scope_metrics:
            if resource_scope_metric.metric.name.lower() == metric_name.lower():
                target_metrics.append(resource_scope_metric.metric)
        self.assertTrue(len(target_metrics) > 0)

        for target_metric in target_metrics:
            dp_list: List[NumberDataPoint] = target_metric.sum.data_points
            self.assertTrue(len(dp_list) > 0)
            if attribute_key != "":
                attribute_dict: Dict[str, AnyValue] = self._get_attributes_dict(dp_list[0].attributes)
                self.assertIsNotNone(attribute_dict.get(attribute_key))