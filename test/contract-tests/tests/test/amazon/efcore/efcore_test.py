# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
from typing import Dict, List

from mock_collector_client import ResourceScopeMetric, ResourceScopeSpan
from typing_extensions import override

from amazon.base.contract_test_base import ContractTestBase
from amazon.utils.application_signals_constants import AWS_LOCAL_OPERATION, AWS_LOCAL_SERVICE, AWS_SPAN_KIND, HTTP_RESPONSE_STATUS, HTTP_REQUEST_METHOD, LATENCY_METRIC
from opentelemetry.proto.common.v1.common_pb2 import AnyValue, KeyValue
from opentelemetry.proto.metrics.v1.metrics_pb2 import ExponentialHistogramDataPoint, Metric
from opentelemetry.proto.trace.v1.trace_pb2 import Span
from opentelemetry.semconv.trace import SpanAttributes

class EfCoreTest(ContractTestBase):
    @override
    @staticmethod
    def get_application_image_name() -> str:
        return "aws-application-signals-tests-testsimpleapp.efcore-app"
    
    @override
    def get_application_wait_pattern(self) -> str:
        return "Content root path: /app"
    
    @override
    def get_application_extra_environment_variables(self):
        return {
            "ASPNETCORE_ENVIRONMENT": "Development"
        }
    
    def test_success(self) -> None:
        self.do_test_requests("/blogs", "GET", 200, 0, 0, request_method="GET", local_operation="GET /blogs")

    def test_post_success(self) -> None:
        self.do_test_requests(
            "/blogs", "POST", 200, 0, 0, request_method="POST", local_operation="POST /blogs")

    def test_route(self) -> None:
        self.do_test_requests(
            "/blogs/1",
            "GET",
            200,
            0,
            0,
            request_method="GET",
            local_operation="GET /blogs/{id}"
        )

    def test_delete_success(self) -> None:
        self.do_test_requests(
            "/blogs/1", "DELETE", 200, 0, 0, request_method="DELETE", local_operation="DELETE /blogs/{id}"
        )
    def test_error(self) -> None:
        self.do_test_requests("/blogs/100", "GET", 404, 1, 0, request_method="GET", local_operation="GET /blogs/{id}")

    @override
    def _assert_aws_span_attributes(self, resource_scope_spans: List[ResourceScopeSpan], path: str, **kwargs) -> None:
        target_spans: List[Span] = []
        for resource_scope_span in resource_scope_spans:
            # pylint: disable=no-member
            if resource_scope_span.span.kind == Span.SPAN_KIND_SERVER:
                target_spans.append(resource_scope_span.span)

        self.assertEqual(len(target_spans), 1)
        self._assert_aws_attributes(target_spans[0].attributes, kwargs.get("request_method"), kwargs.get("local_operation"))

    def _assert_aws_attributes(self, attributes_list: List[KeyValue], method: str, local_operation: str) -> None:
        attributes_dict: Dict[str, AnyValue] = self._get_attributes_dict(attributes_list)
        self._assert_str_attribute(attributes_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attributes_dict, AWS_LOCAL_OPERATION, local_operation)
        self._assert_str_attribute(attributes_dict, AWS_SPAN_KIND, "LOCAL_ROOT")

    @override
    def _assert_semantic_conventions_span_attributes(
        self, resource_scope_spans: List[ResourceScopeSpan], method: str, path: str, status_code: int, **kwargs
    ) -> None:
        target_spans: List[Span] = []
        for resource_scope_span in resource_scope_spans:
            # pylint: disable=no-member
            if resource_scope_span.span.kind == Span.SPAN_KIND_SERVER:
                target_spans.append(resource_scope_span.span)

        self.assertEqual(len(target_spans), 1)
        self._assert_semantic_conventions_attributes(target_spans[0].attributes, method, path, status_code)

    def _assert_semantic_conventions_attributes(
        self, attributes_list: List[KeyValue], method: str, endpoint: str, status_code: int
    ) -> None:
        attributes_dict: Dict[str, AnyValue] = self._get_attributes_dict(attributes_list)
        self._assert_int_attribute(attributes_dict, HTTP_RESPONSE_STATUS, status_code)
        address: str = self.application.get_container_host_ip()
        port: str = self.application.get_exposed_port(self.get_application_port())
        self._assert_str_attribute(attributes_dict, HTTP_REQUEST_METHOD, method)
        self.assertNotIn(SpanAttributes.HTTP_TARGET, attributes_dict)

    @override
    def _assert_metric_attributes(
        self,
        resource_scope_metrics: List[ResourceScopeMetric],
        metric_name: str,
        expected_sum: int,
        **kwargs,
    ) -> None:
        target_metrics: List[Metric] = []
        for resource_scope_metric in resource_scope_metrics:
            if resource_scope_metric.metric.name.lower() == metric_name.lower():
                target_metrics.append(resource_scope_metric.metric)
        if (len(target_metrics) == 2):
            dependency_target_metric: Metric = target_metrics[0]
            service_target_metric: Metric = target_metrics[1]
            # Test dependency metric
            dep_dp_list: List[ExponentialHistogramDataPoint] = dependency_target_metric.exponential_histogram.data_points
            dep_dp_list_count: int = kwargs.get("dp_count", 1)
            self.assertEqual(len(dep_dp_list), dep_dp_list_count)
            dependency_dp: ExponentialHistogramDataPoint = dep_dp_list[0]
            service_dp_list = service_target_metric.exponential_histogram.data_points
            service_dp_list_count = kwargs.get("dp_count", 1)
            self.assertEqual(len(service_dp_list), service_dp_list_count)
            service_dp: ExponentialHistogramDataPoint = service_dp_list[0]
            if len(service_dp_list[0].attributes) > len(dep_dp_list[0].attributes):
                dependency_dp = service_dp_list[0]
                service_dp = dep_dp_list[0]
            self._assert_dependency_dp_attributes(dependency_dp, expected_sum, metric_name, **kwargs)
            self._assert_service_dp_attributes(service_dp, expected_sum, metric_name, **kwargs)
        elif (len(target_metrics) == 1):
            target_metric: Metric = target_metrics[0]
            dp_list: List[ExponentialHistogramDataPoint] = target_metric.exponential_histogram.data_points
            dp_list_count: int = kwargs.get("dp_count", 2)
            self.assertEqual(len(dp_list), dp_list_count)
            dependency_dp: ExponentialHistogramDataPoint = dp_list[0]
            service_dp: ExponentialHistogramDataPoint = dp_list[1]
            if len(dp_list[1].attributes) > len(dp_list[0].attributes):
                dependency_dp = dp_list[1]
                service_dp = dp_list[0]
            self._assert_dependency_dp_attributes(dependency_dp, expected_sum, metric_name, **kwargs)
            self._assert_service_dp_attributes(service_dp, expected_sum, metric_name, **kwargs)
        else:
            raise AssertionError("Target metrics count is incorrect")
    
    def _assert_dependency_dp_attributes(self, dependency_dp: ExponentialHistogramDataPoint, expected_sum: int, metric_name: str, **kwargs):
        attribute_dict = self._get_attributes_dict(dependency_dp.attributes)
        self._assert_str_attribute(attribute_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attribute_dict, AWS_SPAN_KIND, "CLIENT")
        if metric_name == LATENCY_METRIC:
            self.check_sum(metric_name, dependency_dp.sum, expected_sum)

    def _assert_service_dp_attributes(self, service_dp: ExponentialHistogramDataPoint, expected_sum: int, metric_name: str, **kwargs):
        attribute_dict = self._get_attributes_dict(service_dp.attributes)
        self._assert_str_attribute(attribute_dict, AWS_LOCAL_SERVICE, self.get_application_otel_service_name())
        self._assert_str_attribute(attribute_dict, AWS_SPAN_KIND, "LOCAL_ROOT")
        if metric_name == LATENCY_METRIC:
            self.check_sum(metric_name, service_dp.sum, expected_sum)