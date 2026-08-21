# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0

from logging import INFO, Logger, getLogger
from typing import Any, Dict, List

from mock_collector_client import ResourceScopeMetric, ResourceScopeSpan
from mock_collector_service_pb2 import GetTracesRequest
from typing_extensions import override

from amazon.base.contract_test_base import MOCK_COLLECTOR_PORT, ContractTestBase
from amazon.cloudwatch_plugin_otel.span_metrics import InstrumentationMode
from opentelemetry.proto.trace.v1.trace_pb2 import Span, Status

_logger: Logger = getLogger(__name__)
_logger.setLevel(INFO)

_APPLICATION_IMAGE = "aws-application-signals-tests-cloudwatch-plugin-otel-app"
_IMAGE_VERSION_LABEL = "com.amazonaws.cloudwatch-plugin.version"
_READY_MESSAGE = "CloudWatchPluginOtel dependencies ready."
_SERVICE_NAME = "cloudwatch-plugin-otel-contract-test"
_SCOPE_NAME = "cloudwatch.plugin.otel.span_metrics"


class SpanMetricsContractTestBase(ContractTestBase):
    __test__ = False

    @classmethod
    @override
    def manages_test_network(cls) -> bool:
        return False

    @override
    def get_application_environment_variables(self) -> Dict[str, str]:
        collector_endpoint = f"http://collector:{MOCK_COLLECTOR_PORT}"
        return {
            "AWS_ACCESS_KEY_ID": "testing",
            "AWS_SECRET_ACCESS_KEY": "testing",
            "AWS_REGION": "us-east-1",
            "OTEL_AWS_APPLICATION_SIGNALS_ENABLED": "false",
            "OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT": collector_endpoint,
            "OTEL_AWS_APPLICATION_SIGNALS_RUNTIME_ENABLED": "false",
            "OTEL_BSP_SCHEDULE_DELAY": "50",
            "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
            "OTEL_EXPORTER_OTLP_ENDPOINT": collector_endpoint,
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": collector_endpoint,
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": collector_endpoint,
            "OTEL_METRIC_EXPORT_INTERVAL": "100",
            "OTEL_METRICS_EXPORTER": "otlp",
            "OTEL_RESOURCE_ATTRIBUTES": f"service.name={_SERVICE_NAME}",
            "OTEL_SERVICE_NAME": _SERVICE_NAME,
            "OTEL_TRACES_EXPORTER": "otlp",
            "OTEL_TRACES_SAMPLER": self.get_sampler(),
            "RESOURCE_DETECTORS_ENABLED": "false",
        }

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
        if (
            self._testMethodName
            == "test_always_off_records_metrics_without_exporting_spans"
        ):
            return "always_off"
        return "always_on"

    def test_derives_metrics_for_auto_instrumented_and_explicit_spans(self) -> None:
        self._assert_mode_configuration()
        self.do_send_request("exercise", "GET", 200)
        self.do_send_request("error", "GET", 500)

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

    def test_always_off_records_metrics_without_exporting_spans(self) -> None:
        self._assert_mode_configuration()
        self.do_send_request("exercise", "GET", 200)

        metrics = self._get_plugin_metrics(
            [
                {"span.name": "GET", "db.system": "redis"},
                {"http.route": "/exercise"},
            ]
        )
        self._assert_exercise_metrics(metrics)

        response = self.mock_collector_client.client.get_traces(GetTracesRequest())
        self.assertEqual([], list(response.traces))

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
                "OpenTelemetry.Instrumentation.AWS",
                "OpenTelemetry.Extensions.AWS",
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
            if self._get_attribute_values(resource_scope_span.span.attributes).get(
                "http.route"
            )
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
            attributes = self._get_attribute_values(span.attributes)
            self.assertEqual("v1", attributes["aws.otel.span.metrics.schema"])
            self.assertEqual(
                self._get_library_version(),
                attributes["aws.otel.extension.lib.version"],
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
        def has_required_data_points(metrics: List[ResourceScopeMetric]) -> bool:
            plugin_metrics = self._filter_plugin_metrics(metrics)
            return all(
                self._get_matching_data_points(
                    plugin_metrics,
                    "traces.span.metrics.calls",
                    required_attributes,
                )
                for required_attributes in required_attribute_sets
            )

        try:
            metrics = self.mock_collector_client.get_metrics(
                {"traces.span.metrics.calls", "traces.span.metrics.duration"},
                exact_match=False,
                content_condition=has_required_data_points,
            )
        except RuntimeError as error:
            raise AssertionError(
                "No plugin calls datapoints matched all required attribute sets "
                f"{required_attribute_sets}"
            ) from error

        plugin_metrics = self._filter_plugin_metrics(metrics)
        for metric in plugin_metrics:
            self.assertEqual(
                self._get_library_version(),
                metric.scope_metrics.scope.version,
            )
        return plugin_metrics

    @staticmethod
    def _filter_plugin_metrics(
        metrics: List[ResourceScopeMetric],
    ) -> List[ResourceScopeMetric]:
        return [
            metric
            for metric in metrics
            if metric.scope_metrics.scope.name == _SCOPE_NAME
        ]

    def _assert_span_metrics_recorded(
        self,
        metrics: List[ResourceScopeMetric],
        expected: Dict[str, Any],
    ) -> Dict[str, Any]:
        calls = self._get_latest_data_point(
            metrics, "traces.span.metrics.calls", expected
        )
        calls_attributes = self._get_attribute_values(calls.attributes)
        value_field = calls.WhichOneof("value")
        self.assertIsNotNone(value_field)
        self.assertEqual(1, getattr(calls, value_field))
        self.assertEqual(_SERVICE_NAME, calls_attributes["service.name"])
        self.assertEqual("v1", calls_attributes["aws.otel.span.metrics.schema"])
        self.assertEqual(
            self._get_library_version(),
            calls_attributes["aws.otel.extension.lib.version"],
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
                attributes = self._get_attribute_values(data_point.attributes)
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
            attributes = self._get_attribute_values(span.attributes)
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

    def _get_library_version(self) -> str:
        image_labels = self.application.get_wrapped_container().image.labels
        self.assertIn(_IMAGE_VERSION_LABEL, image_labels)
        return image_labels[_IMAGE_VERSION_LABEL]
