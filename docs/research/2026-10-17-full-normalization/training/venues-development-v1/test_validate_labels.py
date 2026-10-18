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
from types import MappingProxyType
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

    def selected_annotation(self, change_selected=None):
        """Keep selected bytes; replace every other physical row without decoding it."""
        annotation = self.root / "selected-annotation"
        annotation.mkdir()
        original_manifest = json.loads((VALIDATOR.RAW / "manifest.json").read_bytes())
        manifest = json.loads((self.bundle / "manifest.json").read_bytes())
        for name, bundle_name, selector in [
            ("evidence.jsonl", "evidence.jsonl", "source_line_number"),
            ("tasks.jsonl", "labels.jsonl", "task_source_line_number"),
        ]:
            selected = {item[selector] for _, _, item in VALIDATOR.rows(self.bundle / bundle_name)}
            count = 0
            with (VALIDATOR.RAW / name).open("rb") as source, (annotation / name).open("wb") as target:
                for count, line in enumerate(source, 1):
                    if count in selected:
                        value = change_selected(name, count, line) if change_selected else line
                    else:
                        value = b"\xff reserved UTF-8\n" if count % 2 else b"reserved non-JSON\n"
                    target.write(value)
            data = (annotation / name).read_bytes()
            original_manifest["files"][name] = {
                "bytes": len(data), "rows": count, "sha256": VALIDATOR.digest(data),
            }
            manifest["source_files"][name].update(
                physical_lines=count, sha256=VALIDATOR.digest(data),
            )
        manifest_path = annotation / "manifest.json"
        manifest_path.write_text(json.dumps(original_manifest))
        manifest["source_annotation_manifest_sha256"] = VALIDATOR.digest(manifest_path.read_bytes())
        (self.bundle / "manifest.json").write_text(json.dumps(manifest))
        return annotation

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

    def test_excluded_invalid_utf8_and_json_never_enter_payload_decoding(self):
        annotation = self.selected_annotation()
        result = self.run_validator(annotation)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["validated_resources"], 67)

    def test_selected_line_hash_is_checked_before_invalid_payload_decoding(self):
        selected_line = min(item["source_line_number"] for _, _, item in VALIDATOR.rows(self.bundle / "evidence.jsonl"))
        def damage(name, number, line):
            return b"\xff changed selected bytes\n" if name == "evidence.jsonl" and number == selected_line else line
        annotation = self.selected_annotation(damage)
        self.require_rejection(self.run_validator(annotation), "Selected physical line hash mismatch")

    def test_selected_metadata_is_development_before_either_original_is_decoded(self):
        path = self.bundle / "labels.jsonl"
        labels = [item for _, _, item in VALIDATOR.rows(path)]
        labels[0]["family_partition"] = "validation"
        path.write_text("".join(json.dumps(item) + "\n" for item in labels))
        manifest = json.loads((self.bundle / "manifest.json").read_bytes())
        manifest["files"]["labels.jsonl"].update(
            bytes=path.stat().st_size, sha256=VALIDATOR.digest(path.read_bytes()),
        )
        (self.bundle / "manifest.json").write_text(json.dumps(manifest))
        annotation = self.selected_annotation(lambda *_: b"\xff invalid selected payload\n")
        self.require_rejection(self.run_validator(annotation), "Held-out selected task")

    def test_duplicate_or_invalid_selectors_fail_before_payload_decoding(self):
        path = self.root / "opaque.jsonl"
        path.write_bytes(b"\xff undecodable\n")
        selected = {"family_partition": "development", "line": 1, "hash": "0" * 64}
        for number in (0, -1, True, "1"):
            with self.subTest(number=number), self.assertRaisesRegex(ValueError, "Invalid selected physical line number"):
                VALIDATOR.read_selected_rows(path, [{**selected, "line": number}], "line", "hash")
        with self.assertRaisesRegex(ValueError, "Duplicate selected physical line number"):
            VALIDATOR.read_selected_rows(path, [selected, selected], "line", "hash")
        with self.assertRaisesRegex(ValueError, "Held-out selected metadata"):
            VALIDATOR.read_selected_rows(path, [{**selected, "family_partition": "test"}], "line", "hash")

    def test_missing_selectors_fail_before_any_selected_payload_is_decoded(self):
        path = self.root / "opaque.jsonl"
        original = b"\xff undecodable"
        path.write_bytes(original + b"\n")
        selected = [
            {"family_partition": "development", "line": 1, "hash": VALIDATOR.digest(original)},
            {"family_partition": "development", "line": 2, "hash": "0" * 64},
        ]
        with self.assertRaisesRegex(ValueError, "Missing selected physical line"):
            VALIDATOR.read_selected_rows(path, selected, "line", "hash")

    def test_selected_rows_keep_unicode_and_immutable_selector_metadata(self):
        path = self.root / "selected-unicode.jsonl"
        item = {"text": "Zoë\u2028River\u2029🎸"}
        original = json.dumps(item, ensure_ascii=False).encode()
        path.write_bytes(b"\xff excluded\n" + original + b"\r\n")
        selected = MappingProxyType({
            "family_partition": "development", "artist_partition": "development",
            "line": 2, "hash": VALIDATOR.digest(original),
        })
        result = VALIDATOR.read_selected_rows(path, [selected], "line", "hash")
        self.assertEqual(result, [(2, original, item)])
        self.assertEqual(selected["line"], 2)


if __name__ == "__main__":
    unittest.main()
