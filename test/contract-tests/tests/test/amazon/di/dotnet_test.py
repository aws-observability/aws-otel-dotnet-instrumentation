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

from amazon.di.di_contract_test_base import (
    DIContractTestBase,
    EXPORT_SETTLE_SECONDS,
    POLL_INTERVAL_SECONDS_VALUE,
    PROBE_TARGET_CLASS,
    PROBE_TARGET_CODE_UNIT,
)


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


class DotnetDynamicInstrumentationBreakpointTest(DIContractTestBase):
    """METHOD-LEVEL BREAKPOINT — the instrumentation type the PROBE tests above never exercise.

    Worth its own class because the type is not cosmetic: it is the only one for which the agent honours
    MaxHits, so every bounded-capture behaviour depends on BREAKPOINT working at all. Mirrors Java's
    DIBreakpointTest.
    """

    LOCATION_HASH: str = "contract-breakpoint"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_breakpoint(
                method_name="LimitedFunction",
                location_hash=self.LOCATION_HASH,
                capture_arguments=["callNumber"],
                capture_return_value=True,
            )
        ]

    def _invoke(self) -> None:
        self.do_send_request("probe-target/limited", "GET", 200)

    def test_breakpoint_produces_a_snapshot(self) -> None:
        self._invoke()
        self.assertGreaterEqual(len(self.wait_for_snapshots(min_count=1)), 1)

    def test_breakpoint_snapshot_is_reported_as_a_breakpoint_not_a_probe(self) -> None:
        """The one attribute that distinguishes the two method-level types.

        If the agent parsed the type wrongly, every other assertion in this class would still pass while
        MaxHits was silently ignored -- so this is the assertion that gives the MaxHits tests their meaning.
        """
        self._invoke()
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.instrumentation_type"), "BREAKPOINT")
        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.instrumentation_level"), "method")

    def test_breakpoint_snapshot_matches_the_method_level_golden_template(self) -> None:
        self._invoke()
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        self.assert_snapshot_matches_template(snapshot, "method_level_snapshot")

    def test_breakpoint_captures_arguments_and_return_value(self) -> None:
        """LimitedFunction echoes its argument, so argument and return must agree — a capture that read a
        stale or wrong frame would disagree, which counting snapshots would never reveal."""
        self._invoke()
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        captured_argument = snapshot["captures"]["entry"]["arguments"]["callNumber"]["value"]
        captured_return = snapshot["captures"]["return"]["return_value"]["value"]
        self.assertEqual(captured_return, captured_argument)

    def test_breakpoint_reports_the_method_location_with_no_line(self) -> None:
        """A method-level location must carry line 0. A non-zero line here would mean the agent treated a
        method-level configuration as a line probe."""
        self._invoke()
        snapshot = self.wait_for_snapshots(min_count=1)[0]

        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.code_unit"), PROBE_TARGET_CODE_UNIT)
        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.class_name"), PROBE_TARGET_CLASS)
        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.method_name"), "LimitedFunction")
        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.line_number"), 0)


class DotnetDynamicInstrumentationMaxHitsTest(DIContractTestBase):
    """A BREAKPOINT with MaxHits must stop capturing once the budget is spent. Mirrors Java's DIMaxHitsTest.

    The app increments its own counter per request, so every call carries a distinct argument and the
    snapshots can be told apart rather than merely counted.
    """

    LOCATION_HASH: str = "contract-max-hits"
    MAX_HITS: int = 3

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_breakpoint(
                method_name="LimitedFunction",
                location_hash=self.LOCATION_HASH,
                capture_arguments=["callNumber"],
                capture_return_value=True,
                max_hits=self.MAX_HITS,
            )
        ]

    def _invoke(self, times: int = 1) -> None:
        for _ in range(times):
            self.do_send_request("probe-target/limited", "GET", 200)

    def test_bounded_breakpoint_still_captures(self) -> None:
        """The control. Every other test here asserts an UPPER bound, which a probe that captured nothing at
        all would also satisfy."""
        self._invoke()
        snapshots = self.snapshots_for_method(self.wait_for_snapshots(min_count=1), "LimitedFunction")

        self.assertGreaterEqual(len(snapshots), 1, "a bounded breakpoint must still capture within its budget")

    def test_capture_count_never_exceeds_max_hits(self) -> None:
        self._invoke(times=self.MAX_HITS + 2)
        self.wait_for_snapshots(min_count=1)

        # Settle past the export batch interval so late snapshots are counted rather than missed, which would
        # make an upper-bound assertion pass for the wrong reason.
        time.sleep(EXPORT_SETTLE_SECONDS)
        snapshots = self.snapshots_for_method(self.get_snapshots(), "LimitedFunction")

        self.assertLessEqual(
            len(snapshots),
            self.MAX_HITS,
            f"MaxHits={self.MAX_HITS} must bound captures, got {len(snapshots)}",
        )

    def test_capture_stops_growing_once_the_budget_is_spent(self) -> None:
        """Distinct from the bound above: this proves the limiter STAYS closed rather than being a limiter
        that merely admits fewer than requested on the first burst."""
        self._invoke(times=self.MAX_HITS)
        self.wait_for_snapshots(min_count=1)
        time.sleep(EXPORT_SETTLE_SECONDS)
        before = len(self.snapshots_for_method(self.get_snapshots(), "LimitedFunction"))

        self._invoke(times=3)
        time.sleep(EXPORT_SETTLE_SECONDS)
        after = len(self.snapshots_for_method(self.get_snapshots(), "LimitedFunction"))

        self.assertEqual(after, before, "no further captures may appear after the budget is spent")
        self.assertLessEqual(after, self.MAX_HITS)

    def test_exhausting_the_budget_reports_disabled(self) -> None:
        """The status side of MaxHits: the operator must be told the probe stopped, not left guessing.

        StatusReporter runs on a hardcoded 60s timer (ReportIntervalMs, not configurable), and DISABLED is
        reported once, so this waits past one full report cycle rather than the default snapshot timeout.
        """
        self._invoke(times=self.MAX_HITS + 2)
        self.wait_for_snapshots(min_count=1)

        self.wait_for_status(self.LOCATION_HASH, "DISABLED", timeout=90.0)

    def test_bounded_snapshot_still_carries_the_full_capture(self) -> None:
        """A bounded probe must not degrade what it captures -- only how often."""
        self._invoke()
        snapshot = self.snapshots_for_method(self.wait_for_snapshots(min_count=1), "LimitedFunction")[0]

        self.assertIn("callNumber", snapshot["captures"]["entry"]["arguments"])
        self.assertIn("return_value", snapshot["captures"]["return"])
        self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.method_name"), "LimitedFunction")


class DotnetDynamicInstrumentationCaptureLimitsTest(DIContractTestBase):
    """Capture limits are CLAMPED to enforced maximums. Mirrors Java's DICaptureLimitsTest.

    Both configurations below deliberately request 9999, far above what the agent allows. The agent clamps
    each to its enforced maximum (CaptureConfiguration.ClampMaxStringLength / ClampMaxCollectionWidth), and
    these tests assert the clamped value. If an enforced maximum changes, these break -- that is the point.

    The .NET numbers are the same as Java's, verified in CaptureConfiguration.cs rather than assumed.
    """

    ENFORCED_MAX_STRING_LENGTH: int = 255
    ENFORCED_MAX_COLLECTION_WIDTH: int = 20
    ORIGINAL_COLLECTION_SIZE: int = 50

    STRING_LOCATION_HASH: str = "contract-limits-string"
    COLLECTION_LOCATION_HASH: str = "contract-limits-collection"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_breakpoint(
                method_name="ProcessLongString",
                location_hash=self.STRING_LOCATION_HASH,
                capture_arguments=["text"],
                capture_return_value=True,
                max_string_length=9999,
            ),
            # Arity 2, unlike ProcessLongString above: same-arity targets on one class are indistinguishable
            # at capture time, and this pair used to resolve to a single config.
            self.method_breakpoint(
                method_name="ProcessLargeCollection",
                location_hash=self.COLLECTION_LOCATION_HASH,
                capture_arguments=["items", "label"],
                capture_return_value=True,
                max_collection_width=9999,
            ),
        ]

    def test_string_argument_is_truncated_at_the_enforced_maximum(self) -> None:
        self.do_send_request("probe-target/long-string", "GET", 200)
        snapshots = self.snapshots_for_method(self.wait_for_snapshots(min_count=1), "ProcessLongString")
        self.assertTrue(snapshots, "expected a snapshot for ProcessLongString")

        captured = snapshots[0]["captures"]["entry"]["arguments"]["text"]

        self.assertEqual(
            len(captured["value"]),
            self.ENFORCED_MAX_STRING_LENGTH,
            f"a requested MaxStringLength of 9999 must clamp to {self.ENFORCED_MAX_STRING_LENGTH}; if the "
            f"enforced maximum changed, update ENFORCED_MAX_STRING_LENGTH",
        )
        self.assertTrue(captured.get("truncated"), "a truncated string must be marked truncated")

    def test_collection_argument_is_capped_and_reports_its_original_size(self) -> None:
        self.do_send_request("probe-target/large-collection", "GET", 200)
        snapshots = self.snapshots_for_method(self.wait_for_snapshots(min_count=1), "ProcessLargeCollection")
        self.assertTrue(snapshots, "expected a snapshot for ProcessLargeCollection")

        arguments = snapshots[0]["captures"]["entry"]["arguments"]
        captured = arguments["items"]

        self.assertEqual(
            len(captured["elements"]),
            self.ENFORCED_MAX_COLLECTION_WIDTH,
            f"a requested MaxCollectionWidth of 9999 must clamp to {self.ENFORCED_MAX_COLLECTION_WIDTH}",
        )
        # The ORIGINAL size, not the captured count. Reporting the capped count would tell an operator the
        # collection really had 20 elements, which is a wrong answer rather than a partial one.
        self.assertEqual(captured["size"], self.ORIGINAL_COLLECTION_SIZE)
        # Proves the snapshot is this method's rather than the arity-1 probe's, which is what used to resolve.
        self.assertEqual(arguments["label"]["value"], "contract")


class DotnetDynamicInstrumentationBelowMaxCaptureLimitTest(DIContractTestBase):
    """A per-configuration limit BELOW the enforced maximum must be honoured, not widened to the maximum.

    The class above only proves an over-large request is clamped DOWN, which a hardcoded 255 would also
    satisfy. Unit tests pin ClampMaxStringLength's arithmetic, but only the contract layer runs the whole
    poll -> apply -> capture chain, where a per-config limit dropped on the way to the capture path would show
    up as a capture of 255 rather than the 10 that was asked for.

    Its own container so ProcessLongString is the only configured target and no other config shares its arity.
    """

    REQUESTED_MAX_STRING_LENGTH: int = 10

    LOCATION_HASH: str = "contract-limits-below-max"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_breakpoint(
                method_name="ProcessLongString",
                location_hash=self.LOCATION_HASH,
                capture_arguments=["text"],
                capture_return_value=True,
                max_string_length=self.REQUESTED_MAX_STRING_LENGTH,
            )
        ]

    def test_a_below_maximum_string_limit_is_honoured_exactly(self) -> None:
        self.do_send_request("probe-target/long-string", "GET", 200)
        snapshots = self.snapshots_for_method(self.wait_for_snapshots(min_count=1), "ProcessLongString")
        self.assertTrue(snapshots, "expected a snapshot for ProcessLongString")

        captured = snapshots[0]["captures"]["entry"]["arguments"]["text"]

        self.assertEqual(
            len(captured["value"]),
            self.REQUESTED_MAX_STRING_LENGTH,
            f"a requested MaxStringLength of {self.REQUESTED_MAX_STRING_LENGTH} must be honoured as-is, not "
            f"widened to the enforced maximum",
        )
        self.assertTrue(captured.get("truncated"), "a truncated string must be marked truncated")
        # No "size" assertion: ValueSerializer.SerializeString sets Truncated but never OriginalSize, so unlike
        # a capped collection a truncated string carries no original length. Python's suite does assert one.
        self.assertNotIn("size", captured)


class DotnetDynamicInstrumentationProbeAndBreakpointTest(DIContractTestBase):
    """A PROBE and a BREAKPOINT on the SAME method at once. Mirrors Java's DIProbeBreakpointTest.

    The interesting failure this guards against is the two configurations interfering: one instrumentation
    replacing the other, or both collapsing onto a single LocationHash so an operator sees half of what they
    configured.
    """

    PROBE_HASH: str = "contract-both-probe"
    BREAKPOINT_HASH: str = "contract-both-breakpoint"

    def get_di_configurations(self) -> List[Dict[str, Any]]:
        return [
            self.method_probe(
                method_name="GetGreeting",
                location_hash=self.PROBE_HASH,
                capture_arguments=["name"],
                capture_return_value=True,
            ),
            self.method_breakpoint(
                method_name="GetGreeting",
                location_hash=self.BREAKPOINT_HASH,
                capture_arguments=["name"],
                capture_return_value=True,
            ),
        ]

    def _invoke(self) -> None:
        self.do_send_request("probe-target/greeting?name=both", "GET", 200)

    def test_both_configurations_produce_snapshots(self) -> None:
        self._invoke()
        snapshots = self.wait_for_snapshots(min_count=2)

        self.assertTrue(self.snapshots_for_location(snapshots, self.PROBE_HASH), "the PROBE produced nothing")
        self.assertTrue(
            self.snapshots_for_location(snapshots, self.BREAKPOINT_HASH), "the BREAKPOINT produced nothing"
        )

    def test_each_snapshot_carries_its_own_location_hash(self) -> None:
        """Both configurations target one method, so a single shared hash would be indistinguishable from
        working -- until an operator tried to tell the two apart in the console."""
        self._invoke()
        snapshots = self.wait_for_snapshots(min_count=2)

        hashes = {
            self.snapshot_attribute(snapshot, "aws.di.location_hash") for snapshot in snapshots
        }
        self.assertIn(self.PROBE_HASH, hashes)
        self.assertIn(self.BREAKPOINT_HASH, hashes)

    def test_the_two_snapshots_report_different_instrumentation_types(self) -> None:
        self._invoke()
        snapshots = self.wait_for_snapshots(min_count=2)

        probe = self.one_snapshot_for_location(snapshots, self.PROBE_HASH)
        breakpoint_snapshot = self.one_snapshot_for_location(snapshots, self.BREAKPOINT_HASH)

        self.assertEqual(self.snapshot_attribute(probe, "aws.di.instrumentation_type"), "PROBE")
        self.assertEqual(
            self.snapshot_attribute(breakpoint_snapshot, "aws.di.instrumentation_type"), "BREAKPOINT"
        )

    def test_both_capture_the_same_argument_and_return_value(self) -> None:
        """Two instrumentations on one method must each read the real frame, not share one capture."""
        self._invoke()
        snapshots = self.wait_for_snapshots(min_count=2)

        for location_hash in (self.PROBE_HASH, self.BREAKPOINT_HASH):
            snapshot = self.one_snapshot_for_location(snapshots, location_hash)
            with self.subTest(location_hash=location_hash):
                self.assertEqual(snapshot["captures"]["entry"]["arguments"]["name"]["value"], "both")
                self.assertEqual(
                    snapshot["captures"]["return"]["return_value"]["value"], "Hello, both"
                )

    def test_both_report_status(self) -> None:
        self._invoke()
        self.wait_for_snapshots(min_count=2)

        self.wait_for_status(self.PROBE_HASH, "ACTIVE")
        self.wait_for_status(self.BREAKPOINT_HASH, "ACTIVE")

    def test_removing_neither_affects_the_other_method_name_attribution(self) -> None:
        """Both snapshots must attribute to GetGreeting; a mis-keyed registry could attribute one to the
        other configuration's method and still look healthy in aggregate."""
        self._invoke()
        snapshots = self.wait_for_snapshots(min_count=2)

        for snapshot in snapshots:
            self.assertEqual(self.snapshot_attribute(snapshot, "aws.di.method_name"), "GetGreeting")
