"""Exercise draft provenance failures, including optimized Python and Unicode JSONL."""

import hashlib
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("validate_labels.py")
SPEC = importlib.util.spec_from_file_location("credit_labels", MODULE_PATH)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


def write_rows(path, rows):
    path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows))


class TestCreditLabelValidation(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.output = Path(self.directory.name)
        self.pack = self.output / "pack"
        self.pack.mkdir()
        self.text = "Zoë\u2028River"
        self.source = {
            "evidence_id": "source_sample:fixture",
            "resource": {"resource_uuid": "fixture"},
            "artist": {"uuid": "artist"},
            "raw_record": {"row": {"taper": self.text}},
            "family_partition": "development",
            "artist_partition": "development",
            "known_recording_family": "family",
            "input_duplicate_family": "duplicate",
        }
        self.task = {
            "task_id": "extract:source_sample:fixture",
            "domain": "people",
            "task_kind": "evidence_extraction",
            "answer": None,
            "evidence_ids": [self.source["evidence_id"]],
            "family_partition": "development",
            "artist_partition": "development",
        }
        self.label = {
            "label_id": "field:fixture:taper",
            "task_id": self.task["task_id"],
            "evidence_id": self.source["evidence_id"],
            "field_path": "/raw_record/row/taper",
            "field_sha256": VALIDATOR.digest(self.text.encode()),
            "field_codepoints": len(self.text),
            "status": "draft_development",
            "scope": "complete_single_field",
            "answer": {
                "exhaustive": True,
                "unresolved": False,
                "unknowns": [],
                "adjudicator": None,
                "assertions": [{
                    "name": {"start": 0, "end": len(self.text), "quote": self.text},
                    "role": "Taper",
                    "role_basis": {"kind": "structured_field", "field": "taper"},
                    "identity_resolution": "not_labeled",
                }],
            },
        }
        for key in ["resource", "artist", "family_partition", "artist_partition",
                    "known_recording_family", "input_duplicate_family"]:
            self.label[key] = self.source[key]
        self.raw = {
            "label_id": self.label["label_id"],
            "evidence_id": self.label["evidence_id"],
            "field_path": self.label["field_path"],
            "field_sha256": self.label["field_sha256"],
            "field_text": self.text,
        }
        self.save()

    def save(self):
        self.label["evidence_sha256"] = VALIDATOR.digest(VALIDATOR.canonical(self.source))
        write_rows(self.pack / "evidence.jsonl", [self.source])
        write_rows(self.pack / "tasks.jsonl", [self.task])
        write_rows(self.output / "labels.jsonl", [self.label])
        write_rows(self.output / "fields.jsonl", [self.raw])
        write_rows(self.output / "blind-review.jsonl", [])
        write_rows(self.output / "review-comparison.jsonl", [])
        digest_file = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
        self.receipt = {
            "annotation_evidence_sha256": digest_file(self.pack / "evidence.jsonl"),
            "annotation_tasks_sha256": digest_file(self.pack / "tasks.jsonl"),
            "labels_sha256": digest_file(self.output / "labels.jsonl"),
            "fields": 1,
            "assertions": 1,
            "unresolved_fields": 0,
            "exhaustive_empty_assertion_fields": 0,
            "raw_fields_path": str(self.output / "fields.jsonl"),
            "raw_fields_sha256": digest_file(self.output / "fields.jsonl"),
            "blinded_subset_review": {
                "answers_file": "blind-review.jsonl",
                "answers_sha256": digest_file(self.output / "blind-review.jsonl"),
                "comparison_file": "review-comparison.jsonl",
                "comparison_sha256": digest_file(self.output / "review-comparison.jsonl"),
                "fields": 0,
            },
            "independent_review_complete": False,
            "policy_activation_permitted": False,
        }
        self.save_receipt()

    def save_receipt(self):
        (self.output / "receipt.json").write_text(json.dumps(self.receipt))

    def test_literal_unicode_line_separator_is_one_jsonl_record(self):
        self.assertEqual(VALIDATOR.validate(self.output, self.pack)["fields"], 1)

    def test_bad_span_is_rejected_under_optimized_python(self):
        self.label["answer"]["assertions"][0]["name"]["end"] -= 1
        self.save()
        program = (
            "import runpy,sys; from pathlib import Path; "
            "runpy.run_path(sys.argv[1])['validate'](Path(sys.argv[2]),Path(sys.argv[3]))"
        )
        result = subprocess.run(
            [sys.executable, "-O", "-c", program, str(MODULE_PATH),
             str(self.output), str(self.pack)], capture_output=True, text=True, check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Invalid Unicode codepoint span", result.stderr)

    def test_held_out_partition_is_rejected_even_when_hashes_match(self):
        for record in [self.source, self.task, self.label]:
            record["artist_partition"] = "test"
        self.save()
        with self.assertRaisesRegex(ValueError, "partition"):
            VALIDATOR.validate(self.output, self.pack)

    def test_changed_pack_bytes_are_rejected(self):
        self.source["raw_record"]["row"]["taper"] = "Changed retained text"
        write_rows(self.pack / "evidence.jsonl", [self.source])
        with self.assertRaisesRegex(ValueError, "hash"):
            VALIDATOR.validate(self.output, self.pack)

    def test_stale_training_text_is_rejected_even_with_updated_file_hash(self):
        self.raw["field_text"] = "Different actor"
        self.save()
        with self.assertRaisesRegex(ValueError, "Training fields"):
            VALIDATOR.validate(self.output, self.pack)


if __name__ == "__main__":
    unittest.main()
