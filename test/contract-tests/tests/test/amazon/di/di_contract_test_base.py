# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""Base class for Dynamic Instrumentation contract tests.

Wires three containers on one network:

  mock-di-api  -- stands in for the local CloudWatch Agent. Serves probe configurations the test seeds and
                  records the status reports the agent sends back.
  application  -- DynamicInstrumentation.NetCore, whose ProbeTargets methods the probes target.
  mock-collector -- receives the snapshot. Started by ContractTestBase.

HOW .NET DIFFERS FROM THE OTHER DI SDKs HERE. Java, Python and JS write snapshots to NDJSON files and their
contract tests read them back through a `/snapshots` endpoint the test app exposes. .NET exports snapshots as
OTLP LogRecords, so there is nothing to read off disk and the application needs no cooperation: assertions go
through the mock collector's Logs RPC instead. That is what mock_collector_client.get_logs() is for.

The collector already listens for OTLP/HTTP on 4316 (added for ServiceEvents), which is where DI snapshots go
-- DI pins http/protobuf and does not honour OTEL_EXPORTER_OTLP_PROTOCOL, so the gRPC port 4315 the rest of
the suite uses is not an option for snapshots.
"""

import json
import os
import time
from logging import INFO, Logger, getLogger
from typing import Any, Dict, List, Optional

import requests
from docker.types import EndpointConfig
from mock_collector_client import ResourceScopeLogRecord
from testcontainers.core.container import DockerContainer
from testcontainers.core.waiting_utils import wait_for_logs

from amazon.base.contract_test_base import NETWORK_NAME, ContractTestBase

_logger: Logger = getLogger(__name__)
_logger.setLevel(INFO)

_MOCK_DI_API_NAME: str = "aws-application-signals-mock-di-api"
_MOCK_DI_API_ALIAS: str = "di-api"
_MOCK_DI_API_PORT: int = 2000

# The collector's OTLP/HTTP listener, reachable from the application container by network alias.
_SNAPSHOT_ENDPOINT: str = "http://collector:4316/v1/logs"

# Minimum the agent accepts; anything lower is clamped to 10. Configurations are seeded BEFORE the
# application starts so the agent's first poll already sees them and no test waits out an interval.
# The agent CLAMPS this to a floor of 10s, so a smaller value here would not speed anything up -- it would
# just make the code lie about what the agent does. Kept as an int and stringified at the point of use so
# anything deriving a wait from it (see wait_for_poll and the disabled test) cannot drift from the real value.
# PUBLIC (no leading underscore) because the disabled-agent test derives its settle time from it. A private
# name imported across modules is exactly the kind of thing pylint flags, and the alternative -- duplicating
# the number in the test -- is what let the old fixed 15s drift out of step with the interval in the first
# place.
POLL_INTERVAL_SECONDS_VALUE: int = 10
_POLL_INTERVAL_SECONDS: str = str(POLL_INTERVAL_SECONDS_VALUE)

_SNAPSHOT_EVENT_NAME: str = "aws.dynamic_instrumentation.snapshot"
_SNAPSHOT_SCOPE_NAME: str = "aws.dynamic_instrumentation"

_SNAPSHOT_WAIT_TIMEOUT: float = 60.0
_SNAPSHOT_POLL_SLEEP: float = 0.5

# Type name the agent binds against: CodeUnit + "." + ClassName.
PROBE_TARGET_CODE_UNIT: str = "DynamicInstrumentation.NetCore"
PROBE_TARGET_CLASS: str = "ProbeTargets"

# Golden snapshot templates, resolved relative to the directory pytest runs from (test/). Deliberately
# OUTSIDE contract-tests/tests so they are not swallowed into the contract_tests wheel -- the same placement
# Java (appsignals-tests/di-contract-tests/templates/di) and JS (contract-tests/templates/di) use, so the
# three SDKs' expected shapes sit in comparable files.
TEMPLATES_DIR: str = os.path.join(os.getcwd(), "contract-tests", "templates", "di")

# Wildcard: the key must be PRESENT, its value is not asserted. Used for ids, timings and thread names.
_ANY: str = "*"


def load_di_template(name: str) -> Dict[str, Any]:
    """Read a golden template by base name, e.g. "probe_snapshot"."""
    path: str = os.path.join(TEMPLATES_DIR, f"{name}.json")
    if not os.path.isfile(path):
        raise AssertionError(f"golden template not found: {path}")
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def compare_against_template(actual: Any, expected: Any, path: str = "") -> None:
    """Compare a snapshot fragment against a template fragment. Raises AssertionError on the first mismatch.

    BIDIRECTIONAL, AND THAT IS THE POINT. Java's and JS's comparators iterate only the template's keys, so a
    snapshot that grows an EXTRA or RENAMED field passes -- which is precisely how a shape drifts without any
    test noticing. Here the key sets must match exactly in both directions, so adding a field to the emitter
    is a deliberate act that requires updating the template.

    A module-level function rather than a TestCase method so it can be exercised directly, with no containers.
    """
    if expected == _ANY:
        return

    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            raise AssertionError(f"{path or 'root'}: expected an object, got {type(actual).__name__}")

        expected_keys = {key for key in expected if not key.startswith("$")}
        actual_keys = set(actual)

        missing = sorted(expected_keys - actual_keys)
        if missing:
            raise AssertionError(f"{path or 'root'}: missing key(s) {missing}")

        unexpected = sorted(actual_keys - expected_keys)
        if unexpected:
            raise AssertionError(
                f"{path or 'root'}: unexpected key(s) {unexpected} -- if the emitter gained a field on "
                f"purpose, add it to the golden template"
            )

        for key in sorted(expected_keys):
            compare_against_template(actual[key], expected[key], f"{path}.{key}" if path else key)
        return

    if isinstance(expected, list):
        if not isinstance(actual, list):
            raise AssertionError(f"{path or 'root'}: expected a list, got {type(actual).__name__}")
        if len(actual) != len(expected):
            raise AssertionError(f"{path}: list length {len(actual)} != expected {len(expected)}")
        for index, (actual_item, expected_item) in enumerate(zip(actual, expected)):
            compare_against_template(actual_item, expected_item, f"{path}[{index}]")
        return

    if actual != expected:
        raise AssertionError(f"{path or 'root'}: expected {expected!r}, got {actual!r}")


def is_di_owned_attribute(key: str) -> bool:
    """Whether an attribute key is part of DI's own contract, as opposed to logging-pipeline furniture."""
    return key == "event.name" or key.startswith("aws.di.")


def _coerce_attribute_value(any_value: Any) -> Any:
    """Flatten an OTLP AnyValue to a plain Python value so templates can hold literals.

    Templates carry `0` and `"PROBE"`, not protobuf wrappers, so the comparison has to see plain values --
    otherwise every attribute would have to be wildcarded and the template would assert nothing but presence.
    """
    which: Optional[str] = any_value.WhichOneof("value") if hasattr(any_value, "WhichOneof") else None
    if which is None:
        return any_value
    return getattr(any_value, which)


# pylint: disable=broad-exception-caught
class DIContractTestBase(ContractTestBase):
    """Contract-test base that additionally runs a mock DI API and seeds probe configurations."""

    mock_di_api: DockerContainer

    def get_application_image_name(self) -> str:
        return "aws-application-signals-tests-dynamicinstrumentation.netcore-app"

    def get_application_wait_pattern(self) -> str:
        # The last line Microsoft.Hosting.Lifetime writes at startup, so matching it means the app is fully
        # up rather than merely binding. Same pattern netcore_test.py uses for the other stock ASP.NET image
        # in this repo, which is what makes it a proven choice rather than a guess.
        return "Content root path: /app"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        """Configurations to seed before the application starts. Overridden per test."""
        return []

    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        return {
            "OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED": "true",
            "OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL": f"http://{_MOCK_DI_API_ALIAS}:{_MOCK_DI_API_PORT}",
            "OTEL_AWS_OTLP_LOGS_ENDPOINT": _SNAPSHOT_ENDPOINT,
            "OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL": _POLL_INTERVAL_SECONDS,
            "OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL": _POLL_INTERVAL_SECONDS,
        }

    def setUp(self) -> None:
        # ORDER IS LOAD-BEARING: the mock API must be serving the configurations before the application
        # starts, because the agent applies whatever its FIRST poll returns. Starting the app first would
        # make every test wait out a poll interval, and a probe applied late races the HTTP request.
        di_api_networking_config: Dict[str, EndpointConfig] = {
            NETWORK_NAME: EndpointConfig(version="1.22", aliases=[_MOCK_DI_API_ALIAS]),
        }
        self.mock_di_api = (
            DockerContainer(_MOCK_DI_API_NAME)
            .with_exposed_ports(_MOCK_DI_API_PORT)
            .with_kwargs(network=NETWORK_NAME, networking_config=di_api_networking_config)
            .with_name(_MOCK_DI_API_NAME)
        )
        self.mock_di_api.start()
        wait_for_logs(self.mock_di_api, "Ready", timeout=30)

        configurations: List[Dict[str, Any]] = self.get_di_configurations()
        if configurations:
            self.seed_configurations(configurations)

        super().setUp()

    def tear_down(self) -> None:
        try:
            super().tear_down()
        finally:
            try:
                _logger.info("Mock DI API stdout")
                _logger.info(self.mock_di_api.get_logs()[0].decode())
                self.mock_di_api.stop()
            except Exception:
                _logger.exception("Failed to tear down mock DI API")

    # --- mock DI API helpers -------------------------------------------------------------------------

    def _di_api_url(self, path: str) -> str:
        host: str = self.mock_di_api.get_container_host_ip()
        port: str = self.mock_di_api.get_exposed_port(_MOCK_DI_API_PORT)
        return f"http://{host}:{port}{path}"

    def seed_configurations(self, configurations: List[Dict[str, Any]]) -> None:
        """Install the configurations the agent's next poll will return."""
        response = requests.post(
            self._di_api_url("/_test/configurations"), json={"Configurations": configurations}, timeout=10
        )
        self.assertEqual(response.status_code, 200, f"seeding configurations failed: {response.text}")

    def get_poll_count(self) -> int:
        """How many times the agent has polled the configuration API, across both instrumentation types."""
        response = requests.get(self._di_api_url("/_test/poll-counts"), timeout=10)
        response.raise_for_status()
        return int(response.json().get("TotalPolls", 0))

    def wait_for_poll(self, min_count: int = 1, timeout: float = _SNAPSHOT_WAIT_TIMEOUT) -> int:
        """Blocks until the agent has polled at least `min_count` times.

        A POSITIVE anchor for tests that would otherwise sleep and hope. Waiting for the agent's own first poll
        is strictly better than a fixed sleep: it collapses the wall time on a fast runner and, unlike a sleep,
        it actually PROVES the agent got as far as talking to the API.
        """
        deadline: float = time.time() + timeout
        observed: int = 0
        while time.time() < deadline:
            observed = self.get_poll_count()
            if observed >= min_count:
                return observed
            time.sleep(_SNAPSHOT_POLL_SLEEP)

        self.fail(f"timed out waiting for {min_count} agent poll(s); observed {observed}")

    def get_status_reports(self) -> List[Dict[str, Any]]:
        """Status entries the agent has reported so far (READY / ACTIVE / ERROR / DISABLED)."""
        response = requests.get(self._di_api_url("/_test/status-reports"), timeout=10)
        self.assertEqual(response.status_code, 200, f"reading status reports failed: {response.text}")
        return response.json().get("StatusReports", [])

    def wait_for_status(self, location_hash: str, status: str, timeout: float = _SNAPSHOT_WAIT_TIMEOUT) -> None:
        """Block until a status entry for this configuration reaches `status`."""
        deadline: float = time.time() + timeout
        seen: List[str] = []
        while time.time() < deadline:
            seen = [
                entry.get("Status", "")
                for entry in self.get_status_reports()
                if entry.get("LocationHash") == location_hash
            ]
            if status in seen:
                return
            time.sleep(_SNAPSHOT_POLL_SLEEP)

        self.fail(f"timed out waiting for status {status} on {location_hash}; saw {seen}")

    # --- snapshot helpers ----------------------------------------------------------------------------

    def get_snapshots(self) -> List[Dict[str, Any]]:
        """Every DI snapshot the collector has received so far, with its JSON body already parsed.

        PEEK, NOT GET. mock_collector_client.get_logs() BLOCKS until logs arrive, which is wrong on both
        counts here: it would double the wait inside a polling loop, and an absence assertion (a disabled
        agent must export nothing) would hang instead of returning an empty list.

        Filtered by the DI event name so an unrelated log the application emits cannot be counted as a
        snapshot; the scope name is checked as well because event.name alone is an attribute any logger
        could set.
        """
        snapshots: List[Dict[str, Any]] = []
        records: List[ResourceScopeLogRecord] = self.mock_collector_client.peek_logs_by_event_name(
            _SNAPSHOT_EVENT_NAME
        )
        for record in records:
            if record.scope_logs.scope.name != _SNAPSHOT_SCOPE_NAME:
                continue

            body: str = record.log_record.body.string_value
            try:
                parsed: Dict[str, Any] = json.loads(body)
            except json.JSONDecodeError:
                self.fail(f"snapshot body was not JSON: {body!r}")

            parsed["_attributes"] = {kv.key: kv.value for kv in record.log_record.attributes}
            parsed["_log_record"] = record.log_record
            snapshots.append(parsed)

        return snapshots

    def wait_for_snapshots(self, min_count: int = 1, timeout: float = _SNAPSHOT_WAIT_TIMEOUT) -> List[Dict[str, Any]]:
        """Poll the collector until at least `min_count` snapshots arrive."""
        deadline: float = time.time() + timeout
        snapshots: List[Dict[str, Any]] = []
        while time.time() < deadline:
            snapshots = self.get_snapshots()
            if len(snapshots) >= min_count:
                return snapshots
            time.sleep(_SNAPSHOT_POLL_SLEEP)

        self.fail(f"timed out waiting for {min_count} snapshot(s); received {len(snapshots)}")

    # --- golden template helpers ---------------------------------------------------------------------

    def assert_snapshot_matches_template(self, snapshot: Dict[str, Any], template_name: str) -> None:
        """Assert a snapshot's BODY and ATTRIBUTES both match a checked-in golden template.

        WHY A TEMPLATE RATHER THAN MORE assertEquals. The snapshot shape is a contract with the backend's
        ingest, and until now it existed only as scattered assertions across individual tests -- nothing a
        reviewer could read as "the shape", and nothing comparable against the other SDKs. Java keeps its
        expected shapes in templates/di/*.json; this is the same idea in .NET's terms.
        """
        template: Dict[str, Any] = load_di_template(template_name)

        body: Dict[str, Any] = {key: value for key, value in snapshot.items() if not key.startswith("_")}
        compare_against_template(body, template["body"], path="body")

        expected_attributes: Optional[Dict[str, Any]] = template.get("attributes")
        if expected_attributes is not None:
            # Scoped to the attributes DI owns. The logging provider and OTLP exporter are free to add their
            # own (a logger category, a formatted-message marker), and failing on those would be asserting
            # someone else's contract -- but WITHIN our namespace the match stays exact in both directions, so
            # a renamed or newly added aws.di.* attribute still fails.
            actual_attributes: Dict[str, Any] = {
                key: _coerce_attribute_value(value)
                for key, value in snapshot["_attributes"].items()
                if is_di_owned_attribute(key)
            }
            compare_against_template(actual_attributes, expected_attributes, path="attributes")

    # --- configuration builders ---------------------------------------------------------------------

    @staticmethod
    def method_probe(
        method_name: str,
        location_hash: str,
        capture_arguments: Optional[List[str]] = None,
        capture_return_value: bool = True,
        max_hits: Optional[int] = None,
    ) -> Dict[str, Any]:
        """A function-level (PROBE) configuration in the shape the agent parses.

        PascalCase throughout, and the code location nests under Location.CodeLocation -- both are the
        real API shape, and the agent silently drops a configuration that deviates.
        """
        # "CaptureReturn", NOT "CaptureReturnValue". The agent reads this exact key
        # (InstrumentationConfiguration.ParseCaptureConfiguration); a near-miss name parses as false and the
        # snapshot silently arrives with no `captures.return`, which reads as a capture bug rather than a
        # malformed configuration.
        code_capture: Dict[str, Any] = {"CaptureReturn": capture_return_value}
        if capture_arguments is not None:
            code_capture["CaptureArguments"] = capture_arguments

        # MaxHits is only honoured for BREAKPOINT; the agent ignores it for PROBE, which is unbounded by
        # design. Passing it on a PROBE configuration is silently a no-op.
        if max_hits is not None:
            code_capture["CaptureLimits"] = {"MaxHits": max_hits}

        return {
            "InstrumentationType": "PROBE",
            "LocationHash": location_hash,
            "Location": {
                "CodeLocation": {
                    "Language": "Dotnet",
                    "CodeUnit": PROBE_TARGET_CODE_UNIT,
                    "ClassName": PROBE_TARGET_CLASS,
                    "MethodName": method_name,
                    "FilePath": "ProbeTargets.cs",
                    "LineNumber": 0,
                }
            },
            "CaptureConfiguration": {"CodeCapture": code_capture},
        }
