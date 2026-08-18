# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

import time
from logging import INFO, Logger, getLogger
from typing import Any, Callable, Dict, List, TypeVar

from docker.types import EndpointConfig
from mock_collector_client import ResourceScopeMetric, ResourceScopeSpan
from mock_collector_service_pb2 import GetTracesRequest
from requests import Response, request
from testcontainers.core.container import DockerContainer
from testcontainers.core.waiting_utils import wait_for_logs
from testcontainers.localstack import LocalStackContainer
from typing_extensions import override

from amazon.base.contract_test_base import NETWORK_NAME, ContractTestBase
from amazon.cloudwatch_plugin_otel.span_metrics import InstrumentationMode
from opentelemetry.proto.trace.v1.trace_pb2 import Span, Status

_logger: Logger = getLogger(__name__)
_logger.setLevel(INFO)

_TestMethod = TypeVar("_TestMethod", bound=Callable[..., None])

_APPLICATION_IMAGE = "aws-application-signals-tests-cloudwatch-plugin-otel-app"
_APPLICATION_SOURCE = "CloudWatchPluginOtel.Contract"
_LIBRARY_VERSION = "1.0.0"
_READY_MESSAGE = "CloudWatchPluginOtel dependencies ready."
_SERVICE_NAME = "cloudwatch-plugin-otel-contract-test"
_SCOPE_NAME = "cloudwatch.plugin.otel.span_metrics"


def _with_sampler(sampler: str) -> Callable[[_TestMethod], _TestMethod]:
    def decorator(test_method: _TestMethod) -> _TestMethod:
        setattr(test_method, "sampler", sampler)
        return test_method

    return decorator


class SpanMetricsContractTestBase(ContractTestBase):
    __test__ = False

    _local_stack: LocalStackContainer
    _redis: DockerContainer

    @classmethod
    @override
    def set_up_dependency_container(cls) -> None:
        local_stack_networking_config = {
            NETWORK_NAME: EndpointConfig(
                version="1.22",
                aliases=["localstack", "s3.localstack"],
            )
        }
        cls._local_stack = (
            LocalStackContainer(image="localstack/localstack:4.0.0")
            .with_name(f"localstack-{cls.__name__.lower()}")
            .with_services("s3", "sqs", "sns", "dynamodb")
            .with_env("DEFAULT_REGION", "us-east-1")
            .with_kwargs(
                network=NETWORK_NAME, networking_config=local_stack_networking_config
            )
        )
        cls._local_stack.start()

        redis_networking_config = {
            NETWORK_NAME: EndpointConfig(version="1.22", aliases=["redis"])
        }
        cls._redis = (
            DockerContainer("redis:7")
            .with_name(f"redis-{cls.__name__.lower()}")
            .with_kwargs(
                network=NETWORK_NAME, networking_config=redis_networking_config
            )
        )
        cls._redis.start()
        wait_for_logs(cls._redis, "Ready to accept connections", timeout=30)

    @classmethod
    @override
    def tear_down_dependency_container(cls) -> None:
        if hasattr(cls, "_redis"):
            _logger.info("Redis stdout\n%s", cls._redis.get_logs()[0].decode())
            _logger.info("Redis stderr\n%s", cls._redis.get_logs()[1].decode())
            cls._redis.stop()
        if hasattr(cls, "_local_stack"):
            _logger.info(
                "LocalStack stdout\n%s", cls._local_stack.get_logs()[0].decode()
            )
            _logger.info(
                "LocalStack stderr\n%s", cls._local_stack.get_logs()[1].decode()
            )
            cls._local_stack.stop()

    @override
    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        common = {
            "AWS_ACCESS_KEY_ID": "testing",
            "AWS_SECRET_ACCESS_KEY": "testing",
            "AWS_REGION": "us-east-1",
            "OTEL_AWS_APPLICATION_SIGNALS_ENABLED": "false",
            "OTEL_BSP_SCHEDULE_DELAY": "50",
            "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
            "OTEL_METRIC_EXPORT_INTERVAL": "100",
            "OTEL_METRICS_EXPORTER": "otlp",
            "OTEL_SERVICE_NAME": _SERVICE_NAME,
            "OTEL_TRACES_EXPORTER": "otlp",
            "OTEL_TRACES_SAMPLER": self.get_sampler(),
            "SPAN_METRICS_MODE": str(self.get_mode()),
        }
        if self.get_mode() is InstrumentationMode.AUTO:
            common.update(
                {
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
                        "AWS.OpenTelemetry.CloudWatch.Plugin.CloudWatchPlugin, "
                        "AWS.OpenTelemetry.CloudWatch.Plugin:"
                        "CloudWatchPluginOtel.AwsInstrumentationPlugin, CloudWatchPluginOtel"
                    ),
                    "OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES": _APPLICATION_SOURCE,
                    "OTEL_DOTNET_AUTO_TRACES_ASPNETCORE_INSTRUMENTATION_ENABLED": "true",
                    "OTEL_DOTNET_AUTO_TRACES_ENTITYFRAMEWORKCORE_INSTRUMENTATION_ENABLED": "true",
                    "OTEL_DOTNET_AUTO_TRACES_GRPCNETCLIENT_INSTRUMENTATION_ENABLED": "true",
                    "OTEL_DOTNET_AUTO_TRACES_HTTPCLIENT_INSTRUMENTATION_ENABLED": "true",
                    "OTEL_DOTNET_AUTO_TRACES_INSTRUMENTATION_ENABLED": "false",
                    "OTEL_DOTNET_AUTO_TRACES_STACKEXCHANGEREDIS_INSTRUMENTATION_ENABLED": "true",
                    "OTEL_LOG_LEVEL": "info",
                }
            )
        else:
            common.update(
                {
                    "CORECLR_ENABLE_PROFILING": "0",
                    "CORECLR_PROFILER_PATH": "",
                    "DOTNET_ADDITIONAL_DEPS": "",
                    "DOTNET_SHARED_STORE": "",
                    "DOTNET_STARTUP_HOOKS": "",
                    "OTEL_DOTNET_AUTO_HOME": "",
                    "OTEL_DOTNET_AUTO_PLUGINS": "",
                }
            )
        return common

    @override
    def get_application_image_name(self) -> str:
        return _APPLICATION_IMAGE

    @override
    def get_application_network_aliases(self) -> List[str]:
        return ["cloudwatch-plugin-otel"]

    @override
    def get_application_otel_service_name(self) -> str:
        return _SERVICE_NAME

    @override
    def get_application_wait_pattern(self) -> str:
        return _READY_MESSAGE

    def get_mode(self) -> InstrumentationMode:
        raise NotImplementedError

    def get_sampler(self) -> str:
        test_method = getattr(self, self._testMethodName)
        return getattr(test_method, "sampler", "always_on")

    def test_derives_metrics_for_auto_instrumented_and_explicit_spans(self) -> None:
        self._assert_mode_configuration()
        self.assertEqual(200, self.send_request("GET", "exercise").status_code)
        self.assertEqual(500, self.send_request("GET", "error").status_code)

        metrics = self._get_plugin_metrics(
            [
                {"span.name": "GET", "db.system": "redis"},
                {"http.route": "/error"},
            ]
        )
        traces = self.mock_collector_client.get_traces()
        self._assert_exported_span_contract(traces)

        self._assert_exercise_metrics(metrics)
        error_attributes = self._assert_span_metrics_recorded(
            metrics,
            {
                "span.kind": "SERVER",
                "status.code": "ERROR",
                "http.route": "/error",
                "http.request.method": "GET",
                "http.response.status_code": 500,
            },
        )
        self.assertIn("error.type", error_attributes)

    @_with_sampler("always_off")
    def test_always_off_records_metrics_without_exporting_spans(self) -> None:
        self._assert_mode_configuration()
        self.assertEqual(200, self.send_request("GET", "exercise").status_code)

        metrics = self._get_plugin_metrics(
            [
                {"span.name": "GET", "db.system": "redis"},
                {"http.route": "/exercise"},
            ]
        )
        self._assert_exercise_metrics(metrics)

        response = self.mock_collector_client.client.get_traces(GetTracesRequest())
        self.assertEqual([], list(response.traces))

    def send_request(self, method: str, path: str) -> Response:
        address = self.application.get_container_host_ip()
        port = self.application.get_exposed_port(self.get_application_port())
        return request(method, f"http://{address}:{port}/{path}", timeout=200)

    def _assert_mode_configuration(self) -> None:
        stdout, stderr = self.application.get_logs()
        logs = stdout.decode() + stderr.decode()

        if self.get_mode() is InstrumentationMode.AUTO:
            self.assertIn("SPAN_METRICS_MODE=auto -> ConfigureAuto", logs)
            self.assertIn("OpenTelemetry tracer initialized.", logs)
            self.assertIn("OpenTelemetry meter initialized.", logs)
            self.assertIn(
                "AwsInstrumentationPlugin.BeforeConfigureTracerProvider invoked.", logs
            )
            self.assertIn("CORECLR_ENABLE_PROFILING=1", logs)
            self.assertIn(
                "DOTNET_STARTUP_HOOKS=/opt/aws/otel/dotnet/net/"
                "OpenTelemetry.AutoInstrumentation.StartupHook.dll",
                logs,
            )
            self.assertIn(
                "CORECLR_PROFILER_PATH=/opt/aws/otel/dotnet/linux-x64/"
                "OpenTelemetry.AutoInstrumentation.Native.so",
                logs,
            )
            for assembly_name in (
                "OpenTelemetry.Instrumentation.AspNetCore",
                "OpenTelemetry.Instrumentation.Http",
                "OpenTelemetry.Instrumentation.EntityFrameworkCore",
                "OpenTelemetry.Instrumentation.GrpcNetClient",
                "OpenTelemetry.Instrumentation.StackExchangeRedis",
            ):
                escaped_name = assembly_name.replace(".", r"\.")
                self.assertRegex(
                    logs,
                    rf"Instrumentation assembly {escaped_name}, .+ "
                    rf"location=/app/{escaped_name}\.dll",
                )
                self.assertNotIn(
                    f"Instrumentation assembly {assembly_name}=not-loaded",
                    logs,
                )
            self.assertRegex(
                logs,
                r"Instrumentation assembly OpenTelemetry\.Instrumentation\.AWS, "
                r"Version=1\.0\.0\.0, Culture=neutral, PublicKeyToken=6ba7de5ce46d6af3 "
                r"location=/app/OpenTelemetry\.Instrumentation\.AWS\.dll",
            )
            self.assertRegex(
                logs,
                r"Instrumentation assembly OpenTelemetry\.Extensions\.AWS, "
                r"Version=1\.16\.0\.1120, .+ "
                r"location=/app/OpenTelemetry\.Extensions\.AWS\.dll",
            )
            self.assertNotIn(
                "Instrumentation assembly OpenTelemetry.Instrumentation.AWS=not-loaded",
                logs,
            )
            return

        expected_method = (
            "ConfigureManualRawSdk"
            if self.get_mode() is InstrumentationMode.MANUAL
            else "ConfigureManualGlobalProviders"
        )
        self.assertIn(
            f"SPAN_METRICS_MODE={self.get_mode()} -> {expected_method}",
            logs,
        )
        self.assertNotIn("SPAN_METRICS_MODE=auto -> ConfigureAuto", logs)
        self.assertNotIn("OpenTelemetry tracer initialized.", logs)
        self.assertNotIn("OpenTelemetry meter initialized.", logs)
        self.assertIn("CORECLR_ENABLE_PROFILING=0", logs)
        self.assertIn("CORECLR_PROFILER_PATH=\n", logs)
        self.assertIn("DOTNET_STARTUP_HOOKS=\n", logs)
        self.assertIn("OTEL_DOTNET_AUTO_HOME=\n", logs)

    def _assert_exported_span_contract(self, spans: List[ResourceScopeSpan]) -> None:
        exercise_span = self._find_span(
            spans,
            Span.SPAN_KIND_SERVER,
            {"http.route": "/exercise"},
        )
        error_span = self._find_span(
            spans,
            Span.SPAN_KIND_SERVER,
            {"http.route": "/error", "error.type": "System.InvalidOperationException"},
        )
        exercise_spans = [
            exercise_span,
            self._find_span(spans, Span.SPAN_KIND_INTERNAL, {}, name="internal-work"),
            self._find_span(
                spans,
                Span.SPAN_KIND_CONSUMER,
                {
                    "messaging.system": "contract-broker",
                    "messaging.operation.name": "receive",
                },
                name="orders receive",
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_CLIENT,
                {
                    "db.system": "sqlite",
                    "db.operation": "SELECT",
                    "db.sql.table": "users",
                },
                name="SELECT users",
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_CLIENT,
                {
                    "rpc.system": "aws-api",
                    "rpc.service": "S3",
                    "rpc.method": "ListBuckets",
                },
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_CLIENT,
                {
                    "rpc.system": "aws-api",
                    "rpc.service": "SQS",
                    "rpc.method": "SendMessage",
                },
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_CLIENT,
                {
                    "rpc.system": "aws-api",
                    "rpc.service": "DynamoDB",
                    "rpc.method": "GetItem",
                },
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_CLIENT,
                {
                    "rpc.system": "aws-api",
                    "rpc.service": "SNS",
                    "rpc.method": "Publish",
                },
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_CLIENT,
                {
                    "rpc.system.name": "grpc",
                    "rpc.method": "contract.Health/Check",
                },
                name="contract.Health/Check",
            ),
            self._find_span(
                spans,
                Span.SPAN_KIND_SERVER,
                {
                    "grpc.method": "/contract.Health/Check",
                    "http.route": "/contract.Health/Check",
                },
            ),
        ]

        exercise_spans.append(
            self._find_span_with_attribute_variants(
                spans,
                Span.SPAN_KIND_CLIENT,
                [
                    {"db.system.name": "sqlite"},
                    {"db.system": "sqlite"},
                ],
                name="main",
            )
        )
        exercise_spans.append(
            self._find_span_with_attribute_variants(
                spans,
                Span.SPAN_KIND_CLIENT,
                [
                    {"db.system.name": "redis"},
                    {"db.system": "redis"},
                ],
                name="GET",
            )
        )
        exercise_spans.append(
            self._find_span_with_attribute_variants(
                spans,
                Span.SPAN_KIND_CLIENT,
                [
                    {"http.request.method": "GET"},
                    {"http.method": "GET"},
                ],
                name="GET",
            )
        )

        self.assertEqual(Status.STATUS_CODE_ERROR, error_span.status.code)
        exercise_trace_ids = {
            resource_scope_span.span.trace_id
            for resource_scope_span in spans
            if self._attributes(resource_scope_span.span.attributes).get("http.route")
            == "/exercise"
        }
        self.assertEqual(1, len(exercise_trace_ids))
        exercise_trace_id = next(iter(exercise_trace_ids))
        self.assertGreaterEqual(
            sum(
                1
                for resource_scope_span in spans
                if resource_scope_span.span.trace_id == exercise_trace_id
            ),
            12,
        )
        for span in exercise_spans:
            self.assertEqual(exercise_trace_id, span.trace_id)

        for span in [*exercise_spans, error_span]:
            attributes = self._attributes(span.attributes)
            self.assertEqual("v1", attributes["aws.otel.span.metrics.schema"])
            self.assertEqual(
                _LIBRARY_VERSION, attributes["aws.otel.extension.lib.version"]
            )

    def _assert_exercise_metrics(self, metrics: List[ResourceScopeMetric]) -> None:
        self._assert_span_metrics_recorded(
            metrics,
            {
                "span.kind": "SERVER",
                "status.code": "UNSET",
                "http.route": "/exercise",
                "http.request.method": "GET",
                "http.response.status_code": 200,
            },
        )
        self._assert_span_metrics_recorded(
            metrics,
            {"span.name": "internal-work", "span.kind": "INTERNAL"},
        )
        self._assert_span_metrics_recorded(
            metrics,
            {
                "span.name": "orders receive",
                "span.kind": "CONSUMER",
                "messaging.system": "contract-broker",
                "messaging.operation.name": "receive",
                "messaging.destination.name": "orders",
            },
        )
        self._assert_span_metrics_recorded(
            metrics,
            {
                "span.name": "SELECT users",
                "span.kind": "CLIENT",
                "db.system": "sqlite",
                "db.operation": "SELECT",
                "db.sql.table": "users",
            },
        )
        self._assert_span_metrics_recorded(
            metrics,
            {
                "span.kind": "CLIENT",
                "http.request.method": "GET",
            },
        )
        self._assert_span_metrics_recorded_variants(
            metrics,
            [
                {
                    "span.name": "main",
                    "span.kind": "CLIENT",
                    "db.system.name": "sqlite",
                },
                {
                    "span.name": "main",
                    "span.kind": "CLIENT",
                    "db.system": "sqlite",
                },
            ],
        )
        self._assert_span_metrics_recorded_variants(
            metrics,
            [
                {"span.name": "GET", "span.kind": "CLIENT", "db.system.name": "redis"},
                {"span.name": "GET", "span.kind": "CLIENT", "db.system": "redis"},
            ],
        )
        self._assert_span_metrics_recorded(
            metrics,
            {
                "span.name": "contract.Health/Check",
                "span.kind": "CLIENT",
                "rpc.system.name": "grpc",
                "rpc.method": "contract.Health/Check",
            },
        )
        self._assert_span_metrics_recorded(
            metrics,
            {
                "span.name": "POST /contract.Health/Check",
                "span.kind": "SERVER",
                "http.route": "/contract.Health/Check",
                "http.request.method": "POST",
                "http.response.status_code": 200,
            },
        )
        for service, method in (
            ("S3", "ListBuckets"),
            ("SQS", "SendMessage"),
            ("DynamoDB", "GetItem"),
            ("SNS", "Publish"),
        ):
            self._assert_span_metrics_recorded(
                metrics,
                {
                    "span.kind": "CLIENT",
                    "rpc.system": "aws-api",
                    "rpc.service": service,
                    "rpc.method": method,
                },
            )

    def _get_plugin_metrics(
        self,
        required_attribute_sets: List[Dict[str, Any]],
    ) -> List[ResourceScopeMetric]:
        deadline = time.time() + 30
        plugin_metrics: List[ResourceScopeMetric] = []
        while time.time() < deadline:
            metrics = self.mock_collector_client.get_metrics(
                {"traces.span.metrics.calls", "traces.span.metrics.duration"},
                exact_match=False,
            )
            plugin_metrics = [
                metric
                for metric in metrics
                if metric.scope_metrics.scope.name == _SCOPE_NAME
            ]
            if all(
                self._get_matching_data_points(
                    plugin_metrics,
                    "traces.span.metrics.calls",
                    required_attributes,
                )
                for required_attributes in required_attribute_sets
            ):
                for metric in plugin_metrics:
                    self.assertEqual(
                        _LIBRARY_VERSION, metric.scope_metrics.scope.version
                    )
                return plugin_metrics
            time.sleep(0.1)
        raise AssertionError(
            f"No plugin calls datapoints matched all required attribute sets {required_attribute_sets}"
        )

    def _assert_span_metrics_recorded(
        self,
        metrics: List[ResourceScopeMetric],
        expected: Dict[str, Any],
    ) -> Dict[str, Any]:
        calls = self._get_latest_data_point(
            metrics, "traces.span.metrics.calls", expected
        )
        calls_attributes = self._attributes(calls.attributes)
        value_field = calls.WhichOneof("value")
        self.assertIsNotNone(value_field)
        self.assertEqual(1, getattr(calls, value_field))
        self.assertEqual(_SERVICE_NAME, calls_attributes["service.name"])
        self.assertEqual("v1", calls_attributes["aws.otel.span.metrics.schema"])
        self.assertEqual(
            _LIBRARY_VERSION, calls_attributes["aws.otel.extension.lib.version"]
        )

        duration = self._get_latest_data_point(
            metrics,
            "traces.span.metrics.duration",
            calls_attributes,
        )
        self.assertEqual(1, duration.count)
        self.assertGreaterEqual(duration.sum, 0)
        return calls_attributes

    def _assert_span_metrics_recorded_variants(
        self,
        metrics: List[ResourceScopeMetric],
        variants: List[Dict[str, Any]],
    ) -> Dict[str, Any]:
        for expected in variants:
            if self._get_matching_data_points(
                metrics, "traces.span.metrics.calls", expected
            ):
                return self._assert_span_metrics_recorded(metrics, expected)
        raise AssertionError(f"No plugin calls datapoint matched any of {variants}")

    def _get_latest_data_point(
        self,
        metrics: List[ResourceScopeMetric],
        metric_name: str,
        expected: Dict[str, Any],
    ) -> Any:
        candidates = self._get_matching_data_points(metrics, metric_name, expected)
        self.assertTrue(candidates, f"No {metric_name} datapoint matched {expected}")
        return max(candidates, key=lambda data_point: data_point.time_unix_nano)

    def _get_matching_data_points(
        self,
        metrics: List[ResourceScopeMetric],
        metric_name: str,
        expected: Dict[str, Any],
    ) -> List[Any]:
        candidates: List[Any] = []
        for resource_scope_metric in metrics:
            metric = resource_scope_metric.metric
            if metric.name != metric_name:
                continue
            if metric.HasField("sum"):
                data_points = metric.sum.data_points
            elif metric.HasField("histogram"):
                data_points = metric.histogram.data_points
            else:
                data_points = []
            for data_point in data_points:
                attributes = self._attributes(data_point.attributes)
                if all(attributes.get(key) == value for key, value in expected.items()):
                    candidates.append(data_point)
        return candidates

    def _find_span(
        self,
        spans: List[ResourceScopeSpan],
        kind: int,
        expected: Dict[str, Any],
        *,
        name: str = "",
    ) -> Span:
        candidates: List[Dict[str, Any]] = []
        for resource_scope_span in spans:
            span = resource_scope_span.span
            attributes = self._attributes(span.attributes)
            if span.kind != kind:
                continue
            candidates.append({"name": span.name, "attributes": attributes})
            if name and span.name != name:
                continue
            if all(attributes.get(key) == value for key, value in expected.items()):
                return span
        raise AssertionError(
            f"No span matched kind={kind}, name={name}, attributes={expected}; candidates={candidates}"
        )

    def _find_span_with_attribute_variants(
        self,
        spans: List[ResourceScopeSpan],
        kind: int,
        variants: List[Dict[str, Any]],
        *,
        name: str = "",
    ) -> Span:
        for expected in variants:
            try:
                return self._find_span(spans, kind, expected, name=name)
            except AssertionError:
                continue
        raise AssertionError(f"No span matched any attribute variant {variants}")

    @staticmethod
    def _attributes(attributes: Any) -> Dict[str, Any]:
        result: Dict[str, Any] = {}
        for attribute in attributes:
            value_field = attribute.value.WhichOneof("value")
            result[attribute.key] = (
                getattr(attribute.value, value_field)
                if value_field is not None
                else None
            )
        return result
