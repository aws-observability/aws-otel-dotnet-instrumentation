# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
from typing import Dict, List

from mock_collector_client import ResourceScopeMetric, ResourceScopeSpan
from typing_extensions import override

from amazon.base.contract_test_base import ContractTestBase
from amazon.utils.application_signals_constants import (
    AWS_LOCAL_SERVICE,
    AWS_REMOTE_OPERATION,
    AWS_REMOTE_RESOURCE_IDENTIFIER,
    AWS_REMOTE_RESOURCE_TYPE,
    AWS_REMOTE_SERVICE,
    AWS_SPAN_KIND,
    LATENCY_METRIC
)
from opentelemetry.proto.common.v1.common_pb2 import AnyValue, KeyValue
from opentelemetry.proto.metrics.v1.metrics_pb2 import ExponentialHistogramDataPoint, Metric
from opentelemetry.proto.trace.v1.trace_pb2 import Span
from opentelemetry.trace import StatusCode

from testcontainers.mysql import MySqlContainer
from typing_extensions import override

from amazon.base.contract_test_base import NETWORK_NAME
from amazon.base.database_contract_test_base import (
    DATABASE_HOST,
    DATABASE_NAME,
    DATABASE_PASSWORD,
    DATABASE_USER,
    AWS_REMOTE_DB_USER,
    SPAN_KIND_CLIENT,
    SPAN_KIND_LOCAL_ROOT,
    DatabaseContractTestBase,
)


class MySqlTest(DatabaseContractTestBase):
    @override
    @classmethod
    def set_up_dependency_container(cls) -> None:
        cls.container = (
            MySqlContainer(MYSQL_USER=DATABASE_USER, MYSQL_PASSWORD=DATABASE_PASSWORD, MYSQL_DATABASE=DATABASE_NAME)
            .with_kwargs(network=NETWORK_NAME)
            .with_name(DATABASE_HOST)
        )
        cls.container.start()

    @override
    @classmethod
    def tear_down_dependency_container(cls) -> None:
        cls.container.stop()

    @override
    @staticmethod
    def get_remote_service() -> str:
        return "mysql"

    @override
    @staticmethod
    def get_database_port() -> int:
        return 3306

    @override
    @staticmethod
    def get_application_image_name() -> str:
        return "aws-application-signals-tests-testsimpleapp.mysql-app"

    @override
    @staticmethod
    def get_application_extra_environment_variables():
        return {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "OTEL_DOTNET_AUTO_TRACES_CONSOLE_EXPORTER_ENABLED": "false",
            "DB_HOST": DATABASE_HOST,
            "DB_USER": DATABASE_USER,
            "DB_PASS": DATABASE_PASSWORD,
            "DB_NAME": DATABASE_NAME
        }
    
    @override
    @staticmethod
    def get_application_wait_pattern() -> str:
        return "Content root path: /app"

    # Disabling due to flakiness
    #def test_select_succeeds(self) -> None:
    #    self.assert_select_succeeds()

    def test_create_item_succeeds(self) -> None:
        self.assert_create_item_succeeds()

    # Disabling due to flakiness
    #def test_drop_database_succeeds(self) -> None:
    #    self.assert_drop_table_succeeds()

    def test_fault(self) -> None:
        self.assert_fault()

    @override
    def _assert_aws_span_attributes(self, resource_scope_spans: List[ResourceScopeSpan], path: str, **kwargs) -> None:
        target_spans: List[Span] = []
        for resource_scope_span in resource_scope_spans:
            # pylint: disable=no-member
            if resource_scope_span.span.kind == Span.SPAN_KIND_CLIENT and resource_scope_span.span.name == "Execute":
                target_spans.append(resource_scope_span.span)

        self.assertEqual(
            len(target_spans), 1, f"target_spans is {str(target_spans)}, although only one value was expected"
        )
        self._assert_aws_attributes(target_spans[0].attributes, **kwargs)

    @override
    def _assert_semantic_conventions_span_attributes(
        self, resource_scope_spans: List[ResourceScopeSpan], method: str, path: str, status_code: int, **kwargs
    ) -> None:
        target_spans: List[Span] = []
        for resource_scope_span in resource_scope_spans:
            # pylint: disable=no-member
            if resource_scope_span.span.kind == Span.SPAN_KIND_CLIENT and resource_scope_span.span.name == "Execute":
                target_spans.append(resource_scope_span.span)
        self._assert_semantic_conventions_attributes(target_spans[0].attributes, kwargs.get("sql_command"))

    def _assert_semantic_conventions_attributes(self, attributes_list: List[KeyValue], command: str) -> None:
        attributes_dict: Dict[str, AnyValue] = self._get_attributes_dict(attributes_list)
        self.assertTrue(attributes_dict.get("db.statement").string_value.startswith(command))
        self._assert_str_attribute(attributes_dict, "db.system", self.get_remote_service())
        self._assert_str_attribute(attributes_dict, "db.name", DATABASE_NAME)
        self._assert_str_attribute(attributes_dict, "net.peer.name", DATABASE_HOST)
        self.assertTrue("server.address" not in attributes_dict)
        self.assertTrue("server.port" not in attributes_dict)
        self.assertTrue("db.operation" not in attributes_dict)

    @override
    def _assert_aws_attributes(
        self, attributes_list: List[KeyValue], expected_span_kind: str = SPAN_KIND_CLIENT, **kwargs
    ) -> None:
        attributes_dict: Dict[str, AnyValue] = self._get_attributes_dict(attributes_list)
        self._assert_str_attribute(attributes_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attributes_dict, AWS_REMOTE_SERVICE, self.get_remote_service())
        # TODO: https://github.com/aws-observability/aws-otel-dotnet-instrumentation/issues/81
        self._assert_str_attribute(attributes_dict, AWS_REMOTE_OPERATION, kwargs.get("sql_command"))
        self._assert_str_attribute(attributes_dict, AWS_REMOTE_RESOURCE_TYPE, "DB::Connection")
        self._assert_str_attribute(attributes_dict, AWS_REMOTE_DB_USER, DATABASE_USER)
        self._assert_str_attribute(
            attributes_dict, AWS_REMOTE_RESOURCE_IDENTIFIER, self.get_remote_resource_identifier()
        )
        self._assert_str_attribute(attributes_dict, AWS_SPAN_KIND, expected_span_kind)

    @override
    def _assert_metric_attributes(
        self, resource_scope_metrics: List[ResourceScopeMetric], metric_name: str, expected_sum: int, **kwargs
    ) -> None:
        target_metrics: List[Metric] = []
        for resource_scope_metric in resource_scope_metrics:
            if resource_scope_metric.metric.name.lower() == metric_name.lower():
                target_metrics.append(resource_scope_metric.metric)
        self.assertLessEqual(
            len(target_metrics),
            4,
            f"target_metrics is {str(target_metrics)}, although we expect less than or equal to 2 metrics",
        )
        dp_list: List[ExponentialHistogramDataPoint] = [
            dp for target_metric in target_metrics for dp in target_metric.exponential_histogram.data_points
        ]

        dependency_dp, service_dp = self._get_dp(dp_list)
       
        self._assert_aws_attributes(dependency_dp.attributes, SPAN_KIND_CLIENT, **kwargs)
        if metric_name == LATENCY_METRIC:
            self.check_sum(metric_name, dependency_dp.sum, expected_sum)

        attribute_dict: Dict[str, AnyValue] = self._get_attributes_dict(service_dp.attributes)
        self._assert_str_attribute(attribute_dict, AWS_SPAN_KIND, SPAN_KIND_LOCAL_ROOT)
        self.check_sum(metric_name, service_dp.sum, expected_sum)
    
    def _get_dp(self, dp_list: List[ExponentialHistogramDataPoint]):
        dependency_dp: ExponentialHistogramDataPoint = None
        service_dp: ExponentialHistogramDataPoint = min(dp_list, key=lambda x: len(x.attributes))
        for dp in dp_list:
            attribute_dict: Dict[str, AnyValue] = self._get_attributes_dict(dp.attributes)
            if AWS_REMOTE_RESOURCE_IDENTIFIER in attribute_dict:
                if attribute_dict[AWS_REMOTE_RESOURCE_IDENTIFIER].string_value == self.get_remote_resource_identifier():
                    dependency_dp = dp
        return dependency_dp, service_dp