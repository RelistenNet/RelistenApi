"""Verify exclusive output ownership and partial-query recovery without credentials."""
import base64
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

_spec = importlib.util.spec_from_file_location("scale_capture", Path(__file__).with_name("capture.py"))
_module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_module)


class TestCapture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / "new"

    def test_existing_evidence_is_preserved_before_any_query(self):
        self.directory.mkdir()
        original = self.directory / "snapshot.jsonl"
        original.write_text("original")
        with patch.object(_module.subprocess, "run") as run:
            with self.assertRaises(FileExistsError):
                _module.capture(self.directory)
            run.assert_not_called()
        self.assertEqual("original", original.read_text())

    def test_secret_lookup_failure_prevents_database_connection(self):
        with patch.object(_module.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "kubectl")) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                _module.capture(self.directory)
            self.assertEqual(1, run.call_count)
        self.assertFalse((self.directory / "snapshot.jsonl").exists())
        self.assertTrue((self.directory / "executed.sql").exists())

    def test_query_failure_preserves_partial_without_publishing_complete_snapshot(self):
        def run(command, **kwargs):
            if command[0] == "kubectl":
                return subprocess.CompletedProcess(command, 0, stdout=base64.b64encode(b"test-only"))
            kwargs["stdout"].write('{"kind":"snapshot"}\n')
            raise subprocess.CalledProcessError(1, command)

        with patch.object(_module.subprocess, "run", side_effect=run):
            with self.assertRaises(subprocess.CalledProcessError):
                _module.capture(self.directory)
        self.assertTrue((self.directory / "snapshot.partial.jsonl").exists())
        self.assertFalse((self.directory / "snapshot.jsonl").exists())
        for name in ("query.json", "executed.sql", "snapshot.partial.jsonl"):
            self.assertNotIn("test-only", (self.directory / name).read_text())

    def test_custom_query_is_copied_and_its_completed_response_is_retained(self):
        query = Path(self.temporary.name) / "venue-context.sql"
        query.write_text("BEGIN READ ONLY; SELECT 'a selected development venue'; COMMIT;\n")

        def run(command, **kwargs):
            if command[0] == "kubectl":
                return subprocess.CompletedProcess(command, 0, stdout=base64.b64encode(b"test-only"))
            executed = Path(command[command.index("-f") + 1])
            self.assertEqual(query.read_bytes(), executed.read_bytes())
            kwargs["stdout"].write('{"kind":"snapshot","read_only":"on"}\n')
            kwargs["stdout"].write('{"kind":"snapshot_complete"}\n')
            return subprocess.CompletedProcess(command, 0)

        with patch.object(_module.subprocess, "run", side_effect=run):
            _module.capture(self.directory, query)
        self.assertTrue((self.directory / "snapshot.jsonl").exists())
        receipt = json.loads((self.directory / "query.json").read_text())
        self.assertEqual(receipt["sql_sha256"], _module.hashlib.sha256(query.read_bytes()).hexdigest())
        for name in ("query.json", "executed.sql", "snapshot.jsonl"):
            self.assertNotIn("test-only", (self.directory / name).read_text())

    def test_custom_query_failure_keeps_only_its_partial_evidence(self):
        query = Path(self.temporary.name) / "venue-context.sql"
        query.write_text("BEGIN READ ONLY; SELECT 'bounded context'; COMMIT;\n")

        def run(command, **kwargs):
            if command[0] == "kubectl":
                return subprocess.CompletedProcess(command, 0, stdout=base64.b64encode(b"test-only"))
            kwargs["stdout"].write('{"kind":"snapshot"}\n')
            raise subprocess.CalledProcessError(1, command)

        with patch.object(_module.subprocess, "run", side_effect=run):
            with self.assertRaises(subprocess.CalledProcessError):
                _module.capture(self.directory, query)
        self.assertEqual(query.read_bytes(), (self.directory / "executed.sql").read_bytes())
        self.assertTrue((self.directory / "snapshot.partial.jsonl").exists())
        self.assertFalse((self.directory / "snapshot.jsonl").exists())


if __name__ == "__main__":
    unittest.main()
