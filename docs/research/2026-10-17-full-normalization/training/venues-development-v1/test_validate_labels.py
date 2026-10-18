"""Exercise provenance rejection through the optimized-Python validator CLI.

Only temporary copies of this development bundle are changed. Annotation and
blind-review originals are read-only inputs; no held-out venue content is used
as a test example.
"""

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


BUNDLE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("venue_labels_validator", BUNDLE / "validate_labels.py")
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)
MANIFEST = json.loads((BUNDLE / "manifest.json").read_text())
SUPPLEMENT = VALIDATOR.REPO / MANIFEST["local_supplement"]


class TestVenueLabelValidation(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="relisten-venue-labels-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.bundle = self.root / "bundle"
        shutil.copytree(BUNDLE, self.bundle, ignore=shutil.ignore_patterns("__pycache__"))

    def run_validator(self, annotation=VALIDATOR.RAW):
        environment = dict(os.environ, PYTHONOPTIMIZE="1")
        return subprocess.run(
            [sys.executable, "-O", str(BUNDLE / "validate_labels.py"),
             "--bundle", str(self.bundle), "--annotation", str(annotation),
             "--supplement", str(SUPPLEMENT)],
            capture_output=True, text=True, env=environment, check=False,
        )

    def replace_evidence(self, change):
        path = self.bundle / "evidence.jsonl"
        records = [item for _, _, item in VALIDATOR.rows(path)]
        change(records[0])
        with path.open("w") as stream:
            for record in records:
                stream.write(json.dumps(record, ensure_ascii=False, sort_keys=True) + "\n")

        # Refresh the outer receipt so the test reaches the semantic check.
        manifest = json.loads((self.bundle / "manifest.json").read_text())
        manifest["files"]["evidence.jsonl"].update(
            sha256=VALIDATOR.digest(path.read_bytes()), bytes=path.stat().st_size,
        )
        (self.bundle / "manifest.json").write_text(json.dumps(manifest))

    def require_rejection(self, result, reason):
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ValueError", result.stderr)
        self.assertIn(reason, result.stderr)
        self.assertNotIn('"validated_pairs"', result.stdout)

    def test_valid_bundle_passes_with_optimization(self):
        result = self.run_validator()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["validated_pairs"], 34)

    def test_out_of_range_span_is_rejected_with_optimization(self):
        # Python slicing would silently clamp this offset and return the same
        # text. The validator must reject an invalid range before slicing.
        def damage(record):
            record["fields"][0]["span"]["end"] += 1

        self.replace_evidence(damage)
        self.require_rejection(self.run_validator(), "Field span outside original text")

    def test_held_out_partition_is_rejected_with_optimization(self):
        self.replace_evidence(lambda record: record.update(artist_partition="validation"))
        self.require_rejection(self.run_validator(), "Held-out evidence")

    def test_changed_source_is_rejected_with_optimization(self):
        annotation = self.root / "annotation"
        annotation.mkdir()
        for name in ("manifest.json", "tasks.jsonl"):
            (annotation / name).symlink_to(VALIDATOR.RAW / name)
        original = (VALIDATOR.RAW / "evidence.jsonl").read_bytes()
        (annotation / "evidence.jsonl").write_bytes(original + b"\n")
        self.require_rejection(
            self.run_validator(annotation), "Changed annotation file: evidence.jsonl",
        )

    def test_shared_uncertain_context_cannot_cross_split_components(self):
        labels = [item for _, _, item in VALIDATOR.rows(BUNDLE / "labels.jsonl")]
        links = [item for _, _, item in VALIDATOR.rows(BUNDLE / "split-links.jsonl")]
        components = VALIDATOR.build_split_components(labels, links)
        for link in links:
            self.assertEqual(len({components[task_id] for task_id in link["task_ids"]}), 1)
        self.assertEqual(len(set(components.values())), 28)

        # Unknown pairs must participate before resolved training rows are
        # selected; they can carry links between otherwise separate examples.
        resolved = [label for label in labels if label["answer"]["decision"] != "insufficient_evidence"]
        self.assertEqual(len(resolved), 11)
        self.assertEqual(len({components[label["task_id"]] for label in resolved}), 10)

    def test_omitted_split_links_are_rejected_after_refreshing_file_hash(self):
        path = self.bundle / "split-links.jsonl"
        path.write_bytes(b"")
        manifest = json.loads((self.bundle / "manifest.json").read_text())
        manifest["files"]["split-links.jsonl"].update(
            sha256=VALIDATOR.digest(b""), bytes=0, rows=0,
        )
        (self.bundle / "manifest.json").write_text(json.dumps(manifest))
        self.require_rejection(self.run_validator(), "Venue split grouping differs")

    def test_unicode_line_separators_remain_inside_physical_records(self):
        path = self.root / "unicode.jsonl"
        value = {"text": "A\u2028B\u2029C\U0001f3b8"}
        path.write_bytes((json.dumps(value, ensure_ascii=False) + "\n" + '{}\n').encode())
        records = list(VALIDATOR.rows(path))
        self.assertEqual([number for number, _, _ in records], [1, 2])
        self.assertEqual(records[0][2], value)
        self.assertEqual(len(records[0][2]["text"]), 6)
        self.assertEqual(records[0][2]["text"][1:4], "\u2028B\u2029")


if __name__ == "__main__":
    unittest.main()
