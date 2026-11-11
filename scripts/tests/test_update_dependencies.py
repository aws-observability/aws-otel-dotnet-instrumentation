import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock


spec = importlib.util.spec_from_file_location(
    "update_dependencies", Path(__file__).resolve().parents[1] / "update_dependencies.py"
)
update_dependencies = importlib.util.module_from_spec(spec)
spec.loader.exec_module(update_dependencies)


class UpdateInstrumentationVersionTest(unittest.TestCase):
    def test_release_tag_updates_shared_version_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "versions.props"
            props.write_text(
                "<Project><PropertyGroup>"
                "<OpenTelemetryAutoInstrumentationVersion>1.19.1</OpenTelemetryAutoInstrumentationVersion>"
                "</PropertyGroup></Project>",
                encoding="utf-8",
            )
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertTrue(
                    update_dependencies.update_instrumentation_version(props, "v1.20.0")
                )
                self.assertFalse(
                    update_dependencies.update_instrumentation_version(props, "1.20.0")
                )
            self.assertIn(
                "<OpenTelemetryAutoInstrumentationVersion>1.20.0</OpenTelemetryAutoInstrumentationVersion>",
                props.read_text(encoding="utf-8"),
            )

    def test_missing_property_fails_instead_of_silently_leaving_versions_stale(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "versions.props"
            props.write_text("<Project />", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Missing OpenTelemetryAutoInstrumentationVersion"):
                update_dependencies.update_instrumentation_version(props, "v1.20.0")

    def test_invalid_version_does_not_change_file(self):
        with tempfile.TemporaryDirectory() as directory:
            props = Path(directory) / "versions.props"
            original = "<OpenTelemetryAutoInstrumentationVersion>1.19.1</OpenTelemetryAutoInstrumentationVersion>"
            props.write_text(original, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Invalid automatic instrumentation version"):
                update_dependencies.update_instrumentation_version(props, "latest")
            self.assertEqual(original, props.read_text(encoding="utf-8"))

    def test_contrib_updates_do_not_replace_shared_package_versions(self):
        outdated = """
Top-level Package                                      Requested  Resolved  Latest
> OpenTelemetry.AutoInstrumentation.PluginApi           1.19.1     1.19.1    1.20.0
> OpenTelemetry.AutoInstrumentation                     1.19.1     1.19.1    1.20.0
> OpenTelemetry.Instrumentation.Http                    1.20.2     1.20.2    1.21.0
"""
        results = [
            subprocess.CompletedProcess([], 0, stdout="", stderr=""),
            subprocess.CompletedProcess([], 0, stdout=outdated, stderr=""),
            subprocess.CompletedProcess([], 0, stdout="", stderr=""),
        ]
        with mock.patch.object(update_dependencies.subprocess, "run", side_effect=results) as run:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertTrue(update_dependencies.update_contrib_packages("project"))
        self.assertEqual(
            run.call_args_list[2].args[0],
            ["dotnet", "add", "package", "OpenTelemetry.Instrumentation.Http", "--version", "1.21.0"],
        )
        self.assertEqual(run.call_count, 3)


if __name__ == "__main__":
    unittest.main()
