# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""Tests for the golden-template comparator itself.

WHY THIS EXISTS. `compare_against_template` is the thing every snapshot-shape assertion now leans on, so a
bug in it would silently weaken every test that uses it -- a comparator that never fails is worse than no
comparator, because it reads as coverage. These cases pin both directions: a matching snapshot passes, and
each way of breaking the shape (missing key, EXTRA key, wrong literal, wrong type, wrong list length) fails.

Runs in-process with no containers and no Docker, so it is fast and can be run anywhere.
"""

from typing import Any, Dict
from unittest import TestCase

from amazon.di.di_contract_test_base import compare_against_template, is_di_owned_attribute, load_di_template


class TemplateComparatorTest(TestCase):
    """The comparator's own contract."""

    def _template(self) -> Dict[str, Any]:
        return {
            "captures": {
                "entry": {"arguments": "*"},
                "return": {"return_value": "*"},
            }
        }

    def _snapshot(self) -> Dict[str, Any]:
        return {
            "captures": {
                "entry": {"arguments": {"orderId": {"type": "System.String", "value": "ORD-42"}}},
                "return": {"return_value": {"type": "System.Int32", "value": "35"}},
            }
        }

    def test_matching_snapshot_passes(self) -> None:
        compare_against_template(self._snapshot(), self._template())

    def test_wildcard_accepts_any_value_but_still_requires_the_key(self) -> None:
        snapshot = self._snapshot()
        snapshot["captures"]["entry"]["arguments"] = {"anything": "at all"}
        compare_against_template(snapshot, self._template())

    def test_missing_key_fails(self) -> None:
        snapshot = self._snapshot()
        del snapshot["captures"]["return"]
        with self.assertRaises(AssertionError) as caught:
            compare_against_template(snapshot, self._template())
        self.assertIn("missing key", str(caught.exception))

    def test_unexpected_key_fails(self) -> None:
        """The case Java's and JS's subset comparators let through."""
        snapshot = self._snapshot()
        snapshot["captures"]["exit"] = {"return_value": {"value": "35"}}
        with self.assertRaises(AssertionError) as caught:
            compare_against_template(snapshot, self._template())
        self.assertIn("unexpected key", str(caught.exception))

    def test_renamed_key_fails_both_ways(self) -> None:
        """A rename is a missing key AND an unexpected one; either message is a pass, silence is not."""
        snapshot = self._snapshot()
        snapshot["captures"]["return"] = {"returnValue": {"type": "System.Int32", "value": "35"}}
        with self.assertRaises(AssertionError):
            compare_against_template(snapshot, self._template())

    def test_wrong_literal_fails(self) -> None:
        with self.assertRaises(AssertionError) as caught:
            compare_against_template({"level": "line"}, {"level": "method"})
        self.assertIn("expected 'method'", str(caught.exception))

    def test_wrong_type_fails(self) -> None:
        with self.assertRaises(AssertionError) as caught:
            compare_against_template({"captures": "not-an-object"}, {"captures": {"entry": "*"}})
        self.assertIn("expected an object", str(caught.exception))

    def test_list_length_mismatch_fails(self) -> None:
        with self.assertRaises(AssertionError) as caught:
            compare_against_template({"stack": [{"a": 1}]}, {"stack": [{"a": 1}, {"a": 2}]})
        self.assertIn("list length", str(caught.exception))

    def test_path_is_reported_so_a_failure_names_the_field(self) -> None:
        snapshot = self._snapshot()
        snapshot["captures"]["return"]["return_value"] = {"type": "System.Int32", "value": "WRONG"}
        template = self._template()
        template["captures"]["return"]["return_value"] = {"type": "System.Int32", "value": "35"}
        with self.assertRaises(AssertionError) as caught:
            compare_against_template(snapshot, template)
        self.assertIn("captures.return.return_value.value", str(caught.exception))

    def test_dollar_prefixed_template_keys_are_metadata_not_contract(self) -> None:
        """`$comment` documents a template; it must not be demanded of the snapshot."""
        compare_against_template({"a": 1}, {"$comment": "notes", "a": 1})


class AttributeOwnershipTest(TestCase):
    """Which attributes the template is allowed to speak for."""

    def test_di_attributes_are_owned(self) -> None:
        self.assertTrue(is_di_owned_attribute("event.name"))
        self.assertTrue(is_di_owned_attribute("aws.di.location_hash"))

    def test_logging_pipeline_attributes_are_not_owned(self) -> None:
        """Otherwise a logger category the OTLP exporter adds would fail a shape assertion it has no part in."""
        self.assertFalse(is_di_owned_attribute("dotnet.ilogger.category"))
        self.assertFalse(is_di_owned_attribute("logrecord.original_format"))

    def test_a_foreign_aws_attribute_is_not_silently_owned(self) -> None:
        """Scoped to aws.di.*, not all of aws.* -- resource/span attributes are a different contract."""
        self.assertFalse(is_di_owned_attribute("aws.local.service"))


class GoldenTemplateFilesTest(TestCase):
    """The checked-in templates must load and describe the shape the emitter actually produces."""

    def test_probe_template_matches_a_measured_function_level_body(self) -> None:
        # Measured from the real export pipeline (DISnapshotOtlpEmitter -> LogRecord), not hand-written.
        measured_body: Dict[str, Any] = {
            "captures": {
                "entry": {
                    "arguments": {
                        "orderId": {"type": "System.String", "value": "ORD-42"},
                        "quantity": {"type": "System.Int32", "value": "5"},
                    }
                },
                "return": {"return_value": {"type": "System.Int32", "value": "35"}},
            }
        }
        compare_against_template(measured_body, load_di_template("probe_snapshot")["body"])

    def test_line_level_template_matches_a_measured_line_level_body(self) -> None:
        measured_body: Dict[str, Any] = {
            "captures": {"lines": {"33": {"locals": {"total": {"type": "System.Int32", "value": "35"}}}}}
        }
        compare_against_template(measured_body, load_di_template("line_level_snapshot")["body"])

    def test_a_function_level_body_does_not_satisfy_the_line_level_template(self) -> None:
        """The two templates must actually discriminate, or neither is asserting the level."""
        function_level_body: Dict[str, Any] = {"captures": {"entry": {"arguments": {}}}}
        with self.assertRaises(AssertionError):
            compare_against_template(function_level_body, load_di_template("line_level_snapshot")["body"])

    def test_method_level_template_matches_a_measured_method_level_body(self) -> None:
        """Method-level BREAKPOINT bodies are shaped exactly like PROBE bodies -- only the type attribute
        differs -- so this must accept the same measured body."""
        measured_body: Dict[str, Any] = {
            "captures": {
                "entry": {"arguments": {"callNumber": {"type": "System.Int32", "value": "1"}}},
                "return": {"return_value": {"type": "System.Int32", "value": "1"}},
            }
        }
        compare_against_template(measured_body, load_di_template("method_level_snapshot")["body"])

    def test_probe_and_method_level_templates_differ_only_in_instrumentation_type(self) -> None:
        """Pins the ONE difference. If these two templates ever diverge elsewhere, one of them is describing a
        shape the emitter does not produce -- the bodies come from the same code path."""
        probe = load_di_template("probe_snapshot")
        method_level = load_di_template("method_level_snapshot")

        self.assertEqual(probe["body"], method_level["body"], "the two bodies are emitted by one code path")

        differing = {
            key
            for key in set(probe["attributes"]) | set(method_level["attributes"])
            if probe["attributes"].get(key) != method_level["attributes"].get(key)
        }
        self.assertEqual(differing, {"aws.di.instrumentation_type", "aws.di.method_name"})
        self.assertEqual(probe["attributes"]["aws.di.instrumentation_type"], "PROBE")
        self.assertEqual(method_level["attributes"]["aws.di.instrumentation_type"], "BREAKPOINT")
