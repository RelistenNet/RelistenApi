"""Verify exclusive output ownership and partial-query recovery without credentials."""
import base64
import importlib.util
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


if __name__ == "__main__":
    unittest.main()
