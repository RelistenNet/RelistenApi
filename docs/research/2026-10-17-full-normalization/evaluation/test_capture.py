"""Verify capture safety and evidence preservation through its public CLI.

The fake database tools use a temporary PATH. No test connects to a database or
reads real credentials; the password below is a deliberate failure-output marker.
"""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("capture.py")
SECRET_MARKER = "capture-test-secret-never-print"
FAKE_TOOL = r'''
import base64
import os
from pathlib import Path
import sys

state = Path(os.environ["CAPTURE_TEST_STATE"])
tool = Path(sys.argv[0]).name
(state / (tool + ".called")).write_text("called", encoding="utf-8")
mode = os.environ.get("CAPTURE_TEST_MODE", "success")
secret = os.environ["CAPTURE_TEST_SECRET"]
if tool == "kubectl":
    if mode == "lookup_failure":
        print(secret)
        print(secret, file=sys.stderr)
        sys.exit(17)
    print(base64.b64encode(secret.encode()).decode(), end="")
else:
    (state / "received.sql").write_bytes(sys.stdin.buffer.read())
    if mode == "query_failure":
        print(os.environ["PGPASSWORD"])
        print(os.environ["PGPASSWORD"], file=sys.stderr)
        sys.exit(23)
    sys.stdout.buffer.write((state / "response.jsonl").read_bytes())
'''


class TestCaptureCli(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.tools = self.root / "tools"
        self.tools.mkdir()
        for name in ("kubectl", "psql"):
            executable = self.tools / name
            executable.write_text(f"#!{sys.executable}\n{FAKE_TOOL}", encoding="utf-8")
            executable.chmod(0o700)
        self.output = self.root / "capture"
        self.sql = self.root / "input.sql"
        self.sql.write_bytes(b"-- A query frozen before execution.\nSELECT 'fixture';\n")
        self.snapshot = {
            "kind": "snapshot",
            "snapshot_utc": "2026-10-17T18:00:00Z",
            "transaction_read_only": "on",
            "is_replica": True,
        }
        self.evidence = [{
            "id": "source:123", "kind": "source", "artist_id": 7,
            "artist_name": "Fixture Artist", "provider": "archive.org", "entity_id": 123,
            "fields": {"notes": "  Taped by Zoë\nTransferred by Jane\r\n\tOriginal spacing.  "},
        }]

    def run_capture(self, mode="success", origin="production"):
        rows = [self.snapshot, *self.evidence]
        (self.root / "response.jsonl").write_text(
            "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows),
            encoding="utf-8",
        )
        environment = os.environ.copy()
        environment.update(
            PATH=str(self.tools),
            CAPTURE_TEST_STATE=str(self.root),
            CAPTURE_TEST_MODE=mode,
            CAPTURE_TEST_SECRET=SECRET_MARKER,
        )
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(self.output), "--origin", origin,
             "--sql", str(self.sql)],
            env=environment, capture_output=True, text=True,
        )

    def assert_nothing_published(self):
        self.assertEqual(list(self.output.iterdir()), [])

    def assert_secret_absent(self, result):
        self.assertNotIn(SECRET_MARKER, result.stdout + result.stderr)

    def test_lookup_failure_never_queries_or_publishes(self):
        result = self.run_capture(mode="lookup_failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.root / "kubectl.called").exists())
        self.assertFalse((self.root / "psql.called").exists())
        self.assert_nothing_published()
        self.assert_secret_absent(result)
        self.assertIn("kubectl failed with exit code 17", result.stderr)

    def test_wrong_snapshot_provenance_never_publishes(self):
        for origin, readonly, replica in (
            ("production", "off", True),
            ("production", "on", False),
            ("local", "on", True),
        ):
            with self.subTest(origin=origin, readonly=readonly, replica=replica):
                self.snapshot.update(transaction_read_only=readonly, is_replica=replica)
                result = self.run_capture(origin=origin)
                self.assertNotEqual(result.returncode, 0)
                self.assert_nothing_published()
                self.assert_secret_absent(result)

    def test_query_failure_does_not_leak_database_output_or_credentials(self):
        result = self.run_capture(mode="query_failure")
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue((self.root / "psql.called").exists())
        self.assert_nothing_published()
        self.assert_secret_absent(result)
        self.assertEqual(result.stdout, "")
        self.assertIn("psql failed with exit code 23", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_existing_or_partial_capture_is_preserved_without_querying(self):
        self.output.mkdir()
        for name in ("evidence.jsonl", "manifest.json", "executed.sql"):
            with self.subTest(existing=name):
                existing = self.output / name
                original = b"Partial or previous capture.\nDo not overwrite.\n"
                existing.write_bytes(original)
                result = self.run_capture()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(existing.read_bytes(), original)
                self.assertEqual(list(self.output.iterdir()), [existing])
                self.assertFalse((self.root / "kubectl.called").exists())
                self.assertFalse((self.root / "psql.called").exists())
                existing.unlink()

    def test_success_preserves_values_and_freezes_matching_sql_and_hashes(self):
        result = self.run_capture()
        self.assertEqual(result.returncode, 0, result.stderr)
        evidence = [
            json.loads(line)
            for line in (self.output / "evidence.jsonl").read_text().splitlines()
        ]
        self.assertEqual(evidence, self.evidence)
        self.assertEqual((self.output / "executed.sql").read_bytes(), self.sql.read_bytes())
        self.assertEqual((self.root / "received.sql").read_bytes(), self.sql.read_bytes())
        manifest = json.loads((self.output / "manifest.json").read_text())
        self.assertEqual(manifest["record_counts"], {"evidence": 1, "artists": 1})
        self.assertEqual(
            {entry["path"] for entry in manifest["files"]},
            {"evidence.jsonl", "executed.sql"},
        )
        for entry in manifest["files"]:
            self.assertEqual(
                hashlib.sha256((self.output / entry["path"]).read_bytes()).hexdigest(),
                entry["sha256"],
            )
        self.assertEqual(json.loads(result.stdout)["evidence"], 1)
        self.assert_secret_absent(result)
        self.assertTrue(all(SECRET_MARKER not in path.read_text() for path in self.output.iterdir()))

    def test_empty_or_duplicate_evidence_is_rejected_without_partial_files(self):
        for rows in ([], self.evidence * 2):
            with self.subTest(evidence_rows=len(rows)):
                self.evidence = rows
                result = self.run_capture()
                self.assertNotEqual(result.returncode, 0)
                self.assert_nothing_published()
                self.assert_secret_absent(result)


if __name__ == "__main__":
    unittest.main()
