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

from amazon.di.di_contract_test_base import DIContractTestBase


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

    def test_snapshot_captures_arguments(self) -> None:
        self._invoke_order(order_id="ORD-42", quantity=5)
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        arguments = snapshot["captures"]["entry"]["arguments"]
        self.assertIn("orderId", arguments)
        self.assertIn("quantity", arguments)
        self.assertEqual(arguments["orderId"]["value"], "ORD-42")
        self.assertEqual(arguments["quantity"]["value"], "5")

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

        # Nothing to wait FOR, so this asserts an absence: give the agent the time it would have needed to
        # poll, weave and export, then require that none of it happened.
        time.sleep(15)
        self.assertEqual(self.get_snapshots(), [], "a disabled agent must not export snapshots")
        self.assertEqual(self.get_status_reports(), [], "a disabled agent must not report status")
