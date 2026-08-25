# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""Function-level Dynamic Instrumentation contract tests for the .NET agent.

Each test seeds one probe configuration, drives the application over HTTP, and asserts on the snapshot the
mock collector receives. This is the only coverage that exercises the whole chain -- configuration poll,
profiler weave, capture, OTLP export -- against a real profiler in a container; the unit suites stop at the
managed boundary and cannot see a weave that never fires.

Line-level probes are deliberately not covered here yet: they need PDBs resolved against source line numbers
and are gated behind the merge-point rules. ProbeTargets.cs already carries `@probe:` markers for them.
"""

import time
from typing import Any, Dict, List

from amazon.di.di_contract_test_base import DIContractTestBase, POLL_INTERVAL_SECONDS_VALUE


class DotnetDynamicInstrumentationTest(DIContractTestBase):
    """One probe on ComputeOrderTotal, asserted from several angles."""

    LOCATION_HASH: str = "contract-order-total"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_probe(
                method_name="ComputeOrderTotal",
                location_hash=self.LOCATION_HASH,
                capture_arguments=["orderId", "quantity"],
                capture_return_value=True,
            )
        ]

    def _invoke_order(self, order_id: str = "ORD-1", quantity: int = 3) -> None:
        self.do_send_request(f"probe-target/order?orderId={order_id}&quantity={quantity}", "GET", 200)

    def test_probe_produces_a_snapshot(self) -> None:
        self._invoke_order()
        snapshots = self.wait_for_snapshots(min_count=1)

        self.assertGreaterEqual(len(snapshots), 1)

    def test_snapshot_matches_the_probe_golden_template(self) -> None:
        """The WHOLE shape in one assertion, against a checked-in artifact.

        The other tests here each pick one field out of a snapshot, which proves those fields are right but
        says nothing about the shape as a whole -- a renamed or newly added key passes every one of them. This
        compares body and attributes against templates/di/probe_snapshot.json in both directions, so the shape
        is reviewable as a file and cannot drift silently. Mirrors what Java asserts with its own templates.
        """
        self._invoke_order(order_id="ORD-42", quantity=5)
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        self.assert_snapshot_matches_template(snapshot, "probe_snapshot")

    def test_snapshot_captures_arguments(self) -> None:
        self._invoke_order(order_id="ORD-42", quantity=5)
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        arguments = snapshot["captures"]["entry"]["arguments"]
        self.assertIn("orderId", arguments)
        self.assertIn("quantity", arguments)
        self.assertEqual(arguments["orderId"]["value"], "ORD-42")
        self.assertEqual(arguments["quantity"]["value"], "5")

    def test_agent_polls_the_configuration_api(self) -> None:
        """THE CONTROL for DotnetDynamicInstrumentationDisabledTest's "never polled" assertion.

        That test proves a disabled agent produces nothing by asserting the poll count is ZERO. A zero is also
        what a broken counter, a mis-wired `/_test/poll-counts` endpoint, or an agent pointed at the wrong URL
        would produce -- so on its own it proves nothing. This asserts the SAME counter moves when the agent is
        enabled, which is what gives the zero meaning.
        """
        self.assertGreaterEqual(self.wait_for_poll(min_count=1), 1)

    def test_snapshot_captures_return_value(self) -> None:
        # unitCost is 7 in ProbeTargets.ComputeOrderTotal, so 7 * 4 == 28. Asserting the arithmetic rather
        # than merely "a return value exists" is what proves the capture read the real frame.
        self._invoke_order(quantity=4)
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        self.assertEqual(snapshot["captures"]["return"]["return_value"]["value"], "28")

    def test_snapshot_carries_trace_context(self) -> None:
        # The snapshot is drained and exported on a background thread, so its trace context has to be
        # stamped from the capture rather than taken from the exporting thread's ambient Activity -- an
        # all-zero id here means the processor regressed.
        self._invoke_order()
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        log_record = snapshot["_log_record"]
        self.assertNotEqual(log_record.trace_id, b"\x00" * 16, "snapshot must carry the captured trace id")
        self.assertNotEqual(log_record.span_id, b"\x00" * 8, "snapshot must carry the captured span id")

    def test_probe_reports_ready_then_active(self) -> None:
        # READY is reported once the profiler has actually woven the target; ACTIVE follows once it is hit.
        self.wait_for_status(self.LOCATION_HASH, "READY")
        self._invoke_order()
        self.wait_for_snapshots(min_count=1)
        self.wait_for_status(self.LOCATION_HASH, "ACTIVE")

    def test_repeated_calls_produce_multiple_snapshots(self) -> None:
        self._invoke_order()
        self._invoke_order()
        self._invoke_order()

        snapshots = self.wait_for_snapshots(min_count=3)
        self.assertGreaterEqual(len(snapshots), 3)


class DotnetDynamicInstrumentationExceptionTest(DIContractTestBase):
    """A probe on a method that throws must capture the throwable, not just a missing return value."""

    LOCATION_HASH: str = "contract-failing-operation"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_probe(
                method_name="FailingOperation",
                location_hash=self.LOCATION_HASH,
                capture_arguments=["reason"],
                capture_return_value=False,
            )
        ]

    def test_snapshot_captures_the_thrown_exception(self) -> None:
        self.do_send_request("probe-target/failing?reason=contract", "GET", 500)
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        throwable = snapshot["captures"]["return"]["throwable"]
        self.assertEqual(throwable["type"], "System.InvalidOperationException")
        self.assertIn("contract", throwable["message"])


class DotnetDynamicInstrumentationAsyncTest(DIContractTestBase):
    """Async targets are captured at task completion, with the awaited result as the return value."""

    LOCATION_HASH: str = "contract-compute-async"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_probe(
                method_name="ComputeAsync",
                location_hash=self.LOCATION_HASH,
                capture_arguments=["seed"],
                capture_return_value=True,
            )
        ]

    def test_async_snapshot_captures_the_awaited_result(self) -> None:
        self.do_send_request("probe-target/async?seed=21", "GET", 200)
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        self.assertEqual(snapshot["captures"]["return"]["return_value"]["value"], "42")


class DotnetDynamicInstrumentationDisabledTest(DIContractTestBase):
    """With the master switch off, a configured probe must produce nothing at all."""

    LOCATION_HASH: str = "contract-disabled"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [self.method_probe(method_name="GetGreeting", location_hash=self.LOCATION_HASH)]

    def get_application_extra_environment_variables(self) -> Dict[str, str]:
        env = super().get_application_extra_environment_variables()
        env["OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED"] = "false"
        return env

    def test_no_snapshots_when_disabled(self) -> None:
        self.do_send_request("probe-target/greeting?name=contract", "GET", 200)

        # AN ABSENCE TEST, so it cannot wait for the thing it is asserting -- and a DISABLED agent never polls,
        # so there is no signal of its own to wait for either. Two things make it sound rather than a hopeful
        # sleep:
        #
        #   1. The wait is DERIVED from the agent's real poll interval instead of a magic 15. Two full cycles
        #      plus a buffer, so a working agent would certainly have polled by now. The previous fixed 15s
        #      allowed only one 10s cycle plus 5s of slack, which cold-start and image-pull jitter on a shared
        #      runner can eat -- and then "no snapshots" would have been trivially true without proving
        #      anything. Derived from the same constant the container is configured with, so the two cannot
        #      drift apart.
        #   2. It asserts the agent NEVER POLLED, which is a direct claim about the master switch rather than a
        #      downstream symptom. "No snapshots" is equally true of an agent that polled and found nothing, or
        #      one that was merely slow; "no polls at all" is only true of an agent that stayed off.
        #      DotnetDynamicInstrumentationTest.test_agent_polls_the_configuration_api is the control proving
        #      this counter moves when DI is enabled, so a zero here cannot be a broken counter.
        settle_seconds = POLL_INTERVAL_SECONDS_VALUE * 2 + 5
        time.sleep(settle_seconds)

        self.assertEqual(
            self.get_poll_count(),
            0,
            f"a disabled agent must never poll the configuration API, but it did within {settle_seconds}s",
        )
        self.assertEqual(self.get_snapshots(), [], "a disabled agent must not export snapshots")
        self.assertEqual(self.get_status_reports(), [], "a disabled agent must not report status")
