# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""ServiceEvents contract-test base for the .NET SDK (OTLP-native).

Ported from the ServiceEvents contract-test suite shared by the Python/Node/Java
SDKs, adapted for .NET:

  * Container is instrumented via the .NET auto-instrumentation profiler
    (CORECLR_* + OTEL_DOTNET_AUTO_PLUGINS), instead of the Python
    distro/configurator env vars. ServiceEvents itself needs no plugin entry of
    its own — it is hosted by the AWS distro plugin, so the plugin list here is
    the same single value the distro's launch scripts set for every customer.
  * Flush cadence is set through the SDK's public flush env vars
    (OTEL_AWS_SERVICE_EVENTS_*_FLUSH_INTERVAL); the .NET SDK has no internal
    DEBUG_SE_TEST_CONFIG hook.
  * OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE targets framework Activity source
    names (System.Net.Http*) rather than app modules — .NET v1 FunctionCall
    covers framework/downstream spans (HttpClient/AWS SDK), not user methods.
    The ServiceEvents.NetCore app makes an HttpClient downstream call on the
    success path so a FunctionCall (service.function.duration) data point with a
    caller is produced.

Signals are asserted off the mock collector over OTLP (the .NET repo's mock
collector, extended with a GetLogs RPC + an OTLP/HTTP receiver on port 4316).
"""
import time
import uuid
from logging import INFO, Logger, getLogger
from typing import Any, Dict, List, Optional
from unittest import TestCase

from docker import DockerClient
from docker.models.networks import Network, NetworkCollection
from docker.types import EndpointConfig
from mock_collector_client import MockCollectorClient
from requests import Response, request
from testcontainers.core.container import DockerContainer
from testcontainers.core.waiting_utils import wait_for_logs

_logger: Logger = getLogger(__name__)
_logger.setLevel(INFO)

SERVICE_EVENTS_FLUSH_INTERVAL_MS: str = "2000"
OTLP_POLL_TIMEOUT: float = 30.0
OTLP_POLL_INTERVAL: float = 1.0
# The ServiceEvents dedicated MeterProvider flushes on a fixed 60s PeriodicExportingMetricReader
# cadence (it does not honor OTEL_METRIC_EXPORT_INTERVAL), so metric polls must wait past one full
# flush window. Matches the Java serviceevents suite, which polls ~90s for the same reason.
METRIC_POLL_TIMEOUT: float = 90.0

# Global latency threshold (ms). /slow (sleeps ~6s) exceeds it; /slow-success
# (sleeps ~1s) does NOT, so an incident on /slow-success can only come from the
# per-endpoint override below — proving OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS.
GLOBAL_LATENCY_THRESHOLD_MS: str = "5000"
# "GET /slow-success:500" → per-endpoint override; "bad-entry:notanumber" is a
# malformed segment that must be skipped without breaking threshold parsing.
LATENCY_THRESHOLDS: str = "GET /slow-success:500,bad-entry:notanumber"

# Exception types thrown by the ServiceEvents.NetCore app. .NET emits the
# fully-qualified type name on the incident snapshot / error-count metric.
EXCEPTION_TYPE: str = "System.InvalidOperationException"  # /exception, POST /data
FAULT_EXCEPTION_TYPE: str = "System.ArithmeticException"  # /fault

# Mock collector config (the .NET repo's collector image, extended for logs/HTTP).
_MOCK_COLLECTOR_IMAGE: str = "aws-application-signals-mock-collector"
_MOCK_COLLECTOR_GRPC_PORT: int = 4315
_MOCK_COLLECTOR_HTTP_PORT: int = 4316
_MOCK_COLLECTOR_ALIAS: str = "collector"
_NETWORK_NAME: str = "serviceevents-contract-test-network"

# The .NET auto-instrumentation profiler GUID + plugin list. This is deliberately the
# SINGLE standard entry that the distro's own launch scripts set (instrument.sh,
# adot-launch.sh/.cmd, the PowerShell module) — no ServiceEvents-specific entry. ServiceEvents
# is hosted by the AWS distro plugin itself, so it initializes with no customer configuration
# change. If this suite passes with only this entry, the customer-does-nothing path works.
_CORECLR_PROFILER_GUID: str = "{918728DD-259F-4A6A-AC2B-B85E1B658318}"
_OTEL_DOTNET_AUTO_PLUGINS: str = (
    "AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
)


# pylint: disable=broad-exception-caught
class ServiceEventsTestInfrastructure(TestCase):
    """Container lifecycle + OTLP assertion helpers. Telemetry is asserted via the
    mock collector's OTLP logs/metrics (no file exporter)."""

    application: Optional[DockerContainer] = None
    mock_collector: Optional[DockerContainer] = None
    mock_collector_client: Optional[MockCollectorClient] = None
    _network: Optional[Network] = None

    def setUp(self) -> None:
        self.addCleanup(self.tear_down)
        self.application = None
        self.mock_collector = None
        self.mock_collector_client = None
        self._network = None

        # Unique network name per test to avoid 409 conflicts.
        network_name = f"{_NETWORK_NAME}-{uuid.uuid4().hex[:8]}"
        self._network = NetworkCollection(client=DockerClient()).create(network_name)
        collector_networking_config = {network_name: EndpointConfig(version="1.22", aliases=[_MOCK_COLLECTOR_ALIAS])}
        app_networking_config = {network_name: EndpointConfig(version="1.22", aliases=["application"])}

        self.mock_collector = (
            DockerContainer(_MOCK_COLLECTOR_IMAGE)
            .with_exposed_ports(_MOCK_COLLECTOR_GRPC_PORT, _MOCK_COLLECTOR_HTTP_PORT)
            .with_kwargs(network=network_name, networking_config=collector_networking_config)
        )
        self.mock_collector.start()
        wait_for_logs(self.mock_collector, "Ready", timeout=20)

        collector_host = self.mock_collector.get_container_host_ip()
        collector_grpc_port = self.mock_collector.get_exposed_port(_MOCK_COLLECTOR_GRPC_PORT)
        self.mock_collector_client = MockCollectorClient(collector_host, collector_grpc_port)

        otlp_logs_endpoint = f"http://{_MOCK_COLLECTOR_ALIAS}:{_MOCK_COLLECTOR_HTTP_PORT}/v1/logs"
        otlp_metrics_endpoint = f"http://{_MOCK_COLLECTOR_ALIAS}:{_MOCK_COLLECTOR_HTTP_PORT}/v1/metrics"

        self.application = (
            DockerContainer(self.get_application_image_name())
            .with_exposed_ports(self.get_application_port())
            .with_kwargs(network=network_name, networking_config=app_networking_config)
            # --- .NET auto-instrumentation load (profiler paths live in the Dockerfile) ---
            .with_env("CORECLR_ENABLE_PROFILING", "1")
            .with_env("CORECLR_PROFILER", _CORECLR_PROFILER_GUID)
            .with_env("OTEL_DOTNET_AUTO_PLUGINS", _OTEL_DOTNET_AUTO_PLUGINS)
            .with_env("RESOURCE_DETECTORS_ENABLED", "false")
            # --- Standard OTel config: only ServiceEvents exports, nothing else ---
            .with_env("OTEL_TRACES_EXPORTER", "none")
            .with_env("OTEL_METRICS_EXPORTER", "none")
            .with_env("OTEL_LOGS_EXPORTER", "none")
            .with_env("OTEL_AWS_APPLICATION_SIGNALS_ENABLED", "false")
            # Force always-on sampling so every request is sampled — incident-snapshot trace
            # correlation is sampling-conditional (trace_id/span_id attached only when the span
            # was sampled), making test_incident_snapshot_on_exception deterministic.
            .with_env("OTEL_TRACES_SAMPLER", "always_on")
            .with_env("OTEL_SERVICE_NAME", self.get_application_otel_service_name())
            .with_env("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment.name=test")
            # --- ServiceEvents config (OTLP-only export path) ---
            .with_env("OTEL_AWS_SERVICE_EVENTS_ENABLED", "true")
            .with_env("OTEL_AWS_SERVICE_EVENTS_FUNCTION_INSTRUMENT_ENABLED", "true")
            .with_env("OTEL_AWS_SERVICE_EVENTS_SAMPLING_MODE", "always")
            # .NET exposes public flush env vars (no internal DEBUG_SE_TEST_CONFIG hook).
            .with_env("OTEL_AWS_SERVICE_EVENTS_FUNCTION_CALL_FLUSH_INTERVAL", SERVICE_EVENTS_FLUSH_INTERVAL_MS)
            .with_env("OTEL_AWS_SERVICE_EVENTS_ENDPOINT_FLUSH_INTERVAL", SERVICE_EVENTS_FLUSH_INTERVAL_MS)
            .with_env("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_FLUSH_INTERVAL", SERVICE_EVENTS_FLUSH_INTERVAL_MS)
            # FunctionCall scope: framework Activity source names (HttpClient), not app modules.
            .with_env("OTEL_AWS_SERVICE_EVENTS_PACKAGES_INCLUDE", "System.Net.Http*")
            # Latency triggers: high global threshold + per-endpoint override for /slow-success.
            .with_env("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_DURATION_THRESHOLD_MS", GLOBAL_LATENCY_THRESHOLD_MS)
            .with_env("OTEL_AWS_SERVICE_EVENTS_LATENCY_THRESHOLDS", LATENCY_THRESHOLDS)
            .with_env("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_PER_MINUTE", "1000")
            .with_env("OTEL_AWS_SERVICE_EVENTS_INCIDENT_SNAPSHOT_MAX_SAME_ERROR", "100")
            .with_env("OTEL_AWS_OTLP_LOGS_ENDPOINT", otlp_logs_endpoint)
            .with_env("OTEL_AWS_OTLP_METRICS_ENDPOINT", otlp_metrics_endpoint)
            # Flush the OTel metric reader every 2s (service.function.duration + count).
            .with_env("OTEL_METRIC_EXPORT_INTERVAL", SERVICE_EVENTS_FLUSH_INTERVAL_MS)
        )

        for key, val in self.get_application_extra_environment_variables().items():
            self.application.with_env(key, val)

        self.application.start()
        wait_for_logs(
            self.application, self.get_application_wait_pattern(), timeout=self.get_application_start_timeout()
        )
        time.sleep(0.5)

    def tear_down(self) -> None:
        try:
            if self.application is not None:
                _logger.info("Application stdout:\n%s", self.application.get_logs()[0].decode())
                _logger.info("Application stderr:\n%s", self.application.get_logs()[1].decode())
                self.application.stop()
        except Exception:
            _logger.exception("Failed to tear down application")
        try:
            if self.mock_collector is not None:
                self.mock_collector.stop()
        except Exception:
            _logger.exception("Failed to tear down mock collector")
        try:
            if self._network is not None:
                self._network.remove()
        except Exception:
            _logger.exception("Failed to remove Docker network")

    # -------------------------------------------------------------------------
    # OTLP value parsing helpers
    # -------------------------------------------------------------------------

    @staticmethod
    def _any_value_to_python(any_value) -> Any:
        kind = any_value.WhichOneof("value")
        if kind == "string_value":
            return any_value.string_value
        if kind == "bool_value":
            return any_value.bool_value
        if kind == "int_value":
            return any_value.int_value
        if kind == "double_value":
            return any_value.double_value
        if kind == "array_value":
            return [ServiceEventsTestInfrastructure._any_value_to_python(v) for v in any_value.array_value.values]
        if kind == "kvlist_value":
            return {
                kv.key: ServiceEventsTestInfrastructure._any_value_to_python(kv.value)
                for kv in any_value.kvlist_value.values
            }
        if kind == "bytes_value":
            return any_value.bytes_value
        return None

    @classmethod
    def attrs(cls, log) -> Dict[str, Any]:
        return {kv.key: cls._any_value_to_python(kv.value) for kv in log.log_record.attributes}

    @classmethod
    def body(cls, log) -> Any:
        return cls._any_value_to_python(log.log_record.body)

    @classmethod
    def resource_attrs(cls, log) -> Dict[str, Any]:
        return {kv.key: cls._any_value_to_python(kv.value) for kv in log.resource_logs.resource.attributes}

    # -------------------------------------------------------------------------
    # OTLP log helpers
    # -------------------------------------------------------------------------

    def get_otlp_logs_by_event_name(self, event_name: str) -> List:
        if self.mock_collector_client is None:
            return []
        return self.mock_collector_client.peek_logs_by_event_name(event_name)

    def wait_for_otlp_logs(self, event_name: str, min_count: int = 1, timeout: Optional[float] = None) -> List:
        if self.mock_collector_client is None:
            self.fail("Mock collector not initialized — cannot poll OTLP logs")
        if timeout is None:
            timeout = OTLP_POLL_TIMEOUT
        start = time.time()
        records: List = []
        while time.time() - start < timeout:
            records = self.get_otlp_logs_by_event_name(event_name)
            if len(records) >= min_count:
                return records
            time.sleep(OTLP_POLL_INTERVAL)
        records = self.get_otlp_logs_by_event_name(event_name)
        if len(records) < min_count:
            self.fail(
                f"Timed out waiting for {min_count} OTLP log(s) with event.name='{event_name}'. "
                f"Found {len(records)} after {timeout}s."
            )
        return records

    def get_endpoint_summary_logs(self, method: str, route: str) -> List:
        logs = self.get_otlp_logs_by_event_name("aws.service_events.endpoint_summary")
        return [
            log
            for log in logs
            if self.attrs(log).get("http.request.method") == method and self.attrs(log).get("url.route") == route
        ]

    def wait_for_endpoint_summary(self, method: str, route: str, timeout: Optional[float] = None) -> List:
        if timeout is None:
            timeout = OTLP_POLL_TIMEOUT
        start = time.time()
        logs: List = []
        while time.time() - start < timeout:
            logs = self.get_endpoint_summary_logs(method, route)
            if logs:
                return logs
            time.sleep(OTLP_POLL_INTERVAL)
        logs = self.get_endpoint_summary_logs(method, route)
        if not logs:
            self.fail(f"Timed out waiting for EndpointSummary log for {method} {route} after {timeout}s.")
        return logs

    # -------------------------------------------------------------------------
    # OTLP metric helpers
    # -------------------------------------------------------------------------

    _ERROR_COUNT_METRIC_NAME: str = "count"

    def _peek_metric(self, metric_name: str) -> List:
        """Return all ResourceScopeMetric entries for a metric (non-blocking)."""
        if self.mock_collector_client is None:
            return []
        try:
            metrics = self.mock_collector_client.get_metrics({metric_name}, exact_match=False)
        except RuntimeError:
            return []
        return [rsm for rsm in metrics if rsm.metric.name == metric_name]

    def _peek_error_count_data_points(self) -> List:
        data_points: List = []
        for rsm in self._peek_metric(self._ERROR_COUNT_METRIC_NAME):
            if rsm.metric.WhichOneof("data") == "sum":
                data_points.extend(rsm.metric.sum.data_points)
        return data_points

    def wait_for_error_count_metric(self, min_count: int = 1, timeout: Optional[float] = None) -> List:
        if timeout is None:
            timeout = METRIC_POLL_TIMEOUT
        start = time.time()
        data_points: List = []
        while time.time() - start < timeout:
            data_points = self._peek_error_count_data_points()
            if len(data_points) >= min_count:
                return data_points
            time.sleep(OTLP_POLL_INTERVAL)
        self.fail(
            f"Timed out waiting for {min_count} '{self._ERROR_COUNT_METRIC_NAME}' data point(s). "
            f"Found {len(data_points)}."
        )
        return data_points

    @classmethod
    def dp_attrs(cls, data_point) -> Dict[str, Any]:
        return {kv.key: cls._any_value_to_python(kv.value) for kv in data_point.attributes}

    @staticmethod
    def dp_value(data_point) -> float:
        if data_point.WhichOneof("value") == "as_int":
            return data_point.as_int
        return data_point.as_double

    # -------------------------------------------------------------------------
    # Request helper
    # -------------------------------------------------------------------------

    def send_request(self, method: str, path: str, **kwargs) -> Response:
        address: str = self.application.get_container_host_ip()
        port: str = self.application.get_exposed_port(self.get_application_port())
        url: str = f"http://{address}:{port}/{path}"
        return request(method, url, timeout=30, **kwargs)

    # -------------------------------------------------------------------------
    # Assertion helpers
    # -------------------------------------------------------------------------

    def assert_endpoint_summary(self, log, **kwargs) -> None:
        attrs = self.attrs(log)
        body = self.body(log)
        self.assertEqual(attrs.get("event.name"), "aws.service_events.endpoint_summary")
        self.assertEqual(log.scope_logs.scope.name, "serviceevents")
        self.assertEqual(log.scope_logs.scope.version, "1.0")
        for key in (
            "http.request.method",
            "url.route",
            "aws.service_events.operation",
            "aws.service_events.request.count",
        ):
            self.assertIn(key, attrs, f"Missing attr {key}")
        self.assertIsInstance(body, dict)
        self.assertIn("duration", body)
        if "method" in kwargs:
            self.assertEqual(attrs["http.request.method"], kwargs["method"])
        if "route" in kwargs:
            self.assertEqual(attrs["url.route"], kwargs["route"])
        if "operation" in kwargs:
            self.assertEqual(attrs["aws.service_events.operation"], kwargs["operation"])

    def assert_duration_structure(self, duration: Dict) -> None:
        for key in ("Values", "Counts", "Max", "Min", "Count", "Sum"):
            self.assertIn(key, duration)
        self.assertGreater(duration["Count"], 0)
        self.assertGreater(duration["Sum"], 0)

    # -------------------------------------------------------------------------
    # Overridable methods
    # -------------------------------------------------------------------------

    @staticmethod
    def get_application_image_name() -> str:
        raise NotImplementedError("Subclasses must implement get_application_image_name")

    def get_application_port(self) -> int:
        return 8080

    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        return {}

    def get_application_wait_pattern(self) -> str:
        return "Ready"

    def get_application_otel_service_name(self) -> str:
        return self.get_application_image_name()

    def get_application_start_timeout(self) -> int:
        return 60


class ServiceEventsContractTestBase(ServiceEventsTestInfrastructure):
    """Standard OTLP suite inherited by the framework test classes."""

    __test__ = False

    # ----- EndpointSummary -----

    def test_endpoint_summary_success(self) -> None:
        for _ in range(3):
            self.assertEqual(200, self.send_request("GET", "success").status_code)
        logs = self.wait_for_endpoint_summary("GET", "/success")
        total_count = sum(self.attrs(log).get("aws.service_events.request.count", 0) for log in logs)
        total_faults = sum(self.attrs(log).get("aws.service_events.request.faults", 0) for log in logs)
        total_errors = sum(self.attrs(log).get("aws.service_events.request.errors", 0) for log in logs)
        self.assertGreaterEqual(total_count, 3)
        self.assertEqual(total_faults, 0)
        self.assertEqual(total_errors, 0)
        self.assert_endpoint_summary(logs[0], method="GET", route="/success", operation="GET /success")
        resource_attrs = self.resource_attrs(logs[0])
        self.assertEqual(resource_attrs.get("service.name"), self.get_application_otel_service_name())
        self.assertEqual(resource_attrs.get("deployment.environment.name"), "test")

    def test_endpoint_summary_fault(self) -> None:
        for _ in range(2):
            self.assertEqual(500, self.send_request("GET", "fault").status_code)
        logs = self.wait_for_endpoint_summary("GET", "/fault")
        total_faults = sum(self.attrs(log).get("aws.service_events.request.faults", 0) for log in logs)
        self.assertGreater(total_faults, 0, "Expected faults > 0")

    def test_endpoint_summary_duration(self) -> None:
        self.send_request("GET", "success")
        logs = self.wait_for_endpoint_summary("GET", "/success")
        self.assert_duration_structure(self.body(logs[0])["duration"])

    def test_endpoint_summary_errors_vs_faults(self) -> None:
        """/error is a 4xx (error, not fault); /fault is a 5xx (fault, not error).
        Counts are summed across flush windows to avoid straddle races."""

        def _sum(route: str, field: str) -> int:
            logs = self.get_otlp_logs_by_event_name("aws.service_events.endpoint_summary")
            return sum(self.attrs(log).get(field, 0) for log in logs if self.attrs(log).get("url.route") == route)

        def _wait_total(route: str, field: str, minimum: int) -> int:
            start = time.time()
            total = 0
            while time.time() - start < OTLP_POLL_TIMEOUT:
                total = _sum(route, field)
                if total >= minimum:
                    return total
                time.sleep(OTLP_POLL_INTERVAL)
            return total

        for _ in range(3):
            self.assertEqual(400, self.send_request("GET", "error").status_code)
        for _ in range(2):
            self.assertEqual(500, self.send_request("GET", "fault").status_code)

        self.assertGreaterEqual(_wait_total("/error", "aws.service_events.request.errors", 3), 3)
        self.assertGreaterEqual(_wait_total("/fault", "aws.service_events.request.faults", 2), 2)
        self.assertEqual(_sum("/error", "aws.service_events.request.faults"), 0, "/error must record no faults")
        self.assertEqual(_sum("/fault", "aws.service_events.request.errors"), 0, "/fault must record no errors")

    # ----- EndpointErrorMetrics (count) -----

    def test_endpoint_error_metric_emitted(self) -> None:
        """/exception populates the `count` Sum counter with per-exception-type breakdown
        and Telemetry.Source=ServiceEvents, operation, and exception dimensions."""
        for _ in range(2):
            self.send_request("GET", "exception")
        data_points = self.wait_for_error_count_metric()
        matching = [
            dp
            for dp in data_points
            if self.dp_attrs(dp).get("operation") == "GET /exception"
            and self.dp_attrs(dp).get("exception") == EXCEPTION_TYPE
        ]
        self.assertGreater(len(matching), 0, f"Expected `count` dp for GET /exception / {EXCEPTION_TYPE}")
        attrs = self.dp_attrs(matching[0])
        self.assertEqual(attrs.get("Telemetry.Source"), "ServiceEvents")
        for key in ("service_name", "environment", "operation", "exception"):
            self.assertIn(key, attrs, f"Missing metric attr {key}")
        self.assertGreaterEqual(self.dp_value(matching[0]), 1)

    # ----- DeploymentEvent -----

    def test_deployment_event_exported(self) -> None:
        self.send_request("GET", "success")
        logs = self.wait_for_otlp_logs("aws.service_events.deployment_event")
        self.assertGreaterEqual(len(logs), 1)
        self.assertEqual(logs[0].scope_logs.scope.name, "serviceevents")
        self.assertEqual(logs[0].scope_logs.scope.version, "1.0")
        triggers = [self.attrs(log).get("aws.service_events.deployment.trigger") for log in logs]
        self.assertIn("startup", triggers, "Expected a DeploymentEvent with trigger='startup'")

    # ----- incidents_exemplar on EndpointSummary (Java-derived) -----

    def test_incidents_exemplar_empty_on_success(self) -> None:
        self.send_request("GET", "success")
        logs = self.wait_for_endpoint_summary("GET", "/success")
        exemplars = self.body(logs[0]).get("incidents_exemplar")
        self.assertIsInstance(exemplars, list)
        self.assertEqual(len(exemplars), 0, "success endpoint should have no incident exemplars")

