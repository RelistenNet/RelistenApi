"""Synthetic source/target boundary tests; no catalog, provider, or model calls."""

import hashlib
import json
import os
from pathlib import Path
import py_compile
import tempfile
import unittest
from unittest.mock import patch

import prepare_people_identity_training as adapter


def repository_root():
    candidates = [Path(os.environ["RELISTEN_REPOSITORY"])] if "RELISTEN_REPOSITORY" in os.environ else []
    candidates.extend([Path.cwd(), *Path.cwd().parents, *Path(__file__).resolve().parents])
    for root in candidates:
        if (root / "docs/research/2026-10-17-full-normalization/training/people-development-v1/validate_bundle.py").is_file():
            return root
    raise RuntimeError("Run from the repository or set RELISTEN_REPOSITORY")


def source(key, family="family-a", text="Taper: Åda / Ada"):
    return {"evidence_id": key, "artist": {"uuid": "artist-" + key},
            "artist_partition": "development", "family_partition": "development",
            "input_duplicate_family": family, "known_recording_family": None,
            "raw_record": {"row": {"description": text}}}


def anchor(original, quote="Åda"):
    text = original["raw_record"]["row"]["description"]
    start = text.index(quote)
    return {"kind": "actor_name", "field_path": "/raw_record/row/description",
            "field_sha256": hashlib.sha256(text.encode()).hexdigest(),
            "span": {"start": start, "end": start + len(quote), "quote": quote,
                     "unit": "unicode_code_points"}}


class TestPreparation(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = repository_root()
        path = root / "docs/research/2026-10-17-full-normalization/training/people-development-v1/validate_bundle.py"
        cls.validator = adapter.import_captured(adapter.Inputs(), path, adapter.ORIGINAL_VALIDATOR_SHA,
                                               "synthetic_training_original_validator")

    def setUp(self):
        self.preparation = adapter.Preparation(self.validator)
        self.first = source("first")
        self.second = source("second", "family-b")
        for record in (self.first, self.second):
            self.preparation.add_source(record)
        self.left = self.preparation.add_endpoint("first", [anchor(self.first)])
        self.right = self.preparation.add_endpoint("second", [anchor(self.second)])

    def question(self, key="q", groups=None, candidates=None):
        result = {"task_id": key, "task_kind": "cross_recording_identity",
                  "focus_endpoint_id": self.left,
                  "candidate_endpoint_ids": candidates if candidates is not None else [self.right],
                  "split_group_ids": groups or ["recording:first"],
                  "origin": "synthetic", "candidate_scope": "reviewed_declared_pairs"}
        self.preparation.add_question(result)
        return result

    def target(self, task_id="q", decision="insufficient_evidence"):
        self.preparation.add_target(task_id, self.right, decision,
                                    {"annotation_status": "agent_adjudicated_development"})

    def test_exact_unicode_span_is_required(self):
        selector = anchor(self.first)
        selector["span"]["start"] += 1
        with self.assertRaises(ValueError):
            self.preparation.add_endpoint("first", [selector])

    def test_whole_field_hash_must_match(self):
        selector = anchor(self.first)
        selector["field_sha256"] = "0" * 64
        with self.assertRaises(ValueError):
            self.preparation.add_endpoint("first", [selector])

    def test_reference_selector_keeps_literal_coordinates_without_owner_name(self):
        citation = anchor(self.first)
        side = {"actor_kind": None, "actor_identity": None,
                "focal_reference_occurrences": [{"field": "description", "field_sha256": citation["field_sha256"],
                    "match_span": citation["span"], "kind": "url", "context_span": citation["span"]}],
                "answer": {"name": "must never enter endpoint"}}
        selected = adapter.endpoint_selectors(side, False)
        self.assertEqual(selected, [{"field_path": citation["field_path"],
            "field_sha256": citation["field_sha256"], "span": citation["span"],
            "kind": "source_url_reference", "raw_reference_kind": "url"}])
        self.assertNotIn("name", selected[0])

    def test_reference_selector_does_not_fall_back_to_an_answer_supplied_name(self):
        side = {"actor_kind": None, "actor_identity": None, "focal_reference_occurrences": [],
                "answer": {"name": "unused"}}
        with self.assertRaisesRegex(ValueError, "exact source selector"):
            self.preparation.add_endpoint("first", adapter.endpoint_selectors(side, False))

    def test_same_selector_coalesces_without_using_target(self):
        self.assertEqual(self.left, self.preparation.add_endpoint("first", [anchor(self.first)]))
        self.question()
        self.target(decision="same_entity")
        self.assertEqual(self.left, self.preparation.add_endpoint("first", [anchor(self.first)]))

    def test_shared_evidence_id_requires_exact_original_content(self):
        with self.assertRaisesRegex(ValueError, "conflicting source content"):
            self.preparation.add_source(source("first", text="different original text"))

    def test_unknown_target_is_retained_without_becoming_negative(self):
        self.question()
        self.target()
        self.preparation.validate_coverage()
        self.assertIsNone(self.preparation.targets[0]["label"])

    def test_every_declared_candidate_needs_a_target_even_unknown(self):
        self.question(candidates=[self.right, self.left])
        self.target()
        with self.assertRaisesRegex(ValueError, "cover each declared"):
            self.preparation.validate_coverage()

    def test_answer_cannot_create_an_unlisted_candidate(self):
        self.question(candidates=[])
        with self.assertRaisesRegex(ValueError, "outside the declared"):
            self.target()

    def test_duplicate_answer_does_not_weight_training_twice_silently(self):
        self.question()
        self.target()
        self.target()
        with self.assertRaisesRegex(ValueError, "exactly once"):
            self.preparation.validate_coverage()

    def test_actor_kind_and_unknown_tasks_bridge_groups_before_fit_filter(self):
        self.question("known", ["recording:first"])
        self.target("known", "same_entity")
        bridge = self.question("actor-kind", ["recording:first", "family:second"], [])
        bridge["task_kind"] = "actor_kind"
        self.question("unknown", ["family:second"])
        self.target("unknown")
        self.question("unrelated", ["family:unrelated"])
        self.target("unrelated", "different_entities")
        self.assertEqual(self.preparation.validate_coverage(),
                         [["actor-kind", "known", "unknown"], ["unrelated"]])

    def test_source_package_has_no_decisions_or_adjudication_reason(self):
        self.question()
        self.target(decision="same_entity")
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "prepared"
            receipt = adapter.publish(self.preparation, adapter.Inputs(), output)
            source_rows = [json.loads(line) for line in (output / "source/questions.jsonl").read_bytes().split(b"\n") if line]
            self.assertEqual(source_rows[0]["candidate_endpoint_ids"], [self.right])
            self.assertNotIn("decision", source_rows[0])
            self.assertNotIn("label", source_rows[0])
            targets = [json.loads(line) for line in (output / "targets/decisions.jsonl").read_bytes().split(b"\n") if line]
            self.assertEqual(targets[0]["label"], 1)
            self.assertEqual(receipt["projection_status"], "pending_actual_CSharp_reference_selector_projection")

    def test_input_drift_leaves_no_completion_manifest(self):
        self.question()
        self.target()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            pinned = root / "input.json"
            pinned.write_bytes(b"{}")
            inputs = adapter.Inputs()
            inputs.read(pinned)
            output = root / "prepared"
            original_write = adapter.write_rows

            def write_then_drift(*args):
                result = original_write(*args)
                pinned.write_bytes(b"[]")
                return result

            with patch.object(adapter, "write_rows", side_effect=write_then_drift):
                with self.assertRaisesRegex(ValueError, "changed"):
                    adapter.publish(self.preparation, inputs, output)
            self.assertFalse((output / "manifest.json").exists())

    def test_import_executes_captured_source_despite_same_size_timestamp_pyc(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "helper.py"
            path.write_bytes(b"MARKER = 'old'\n")
            py_compile.compile(str(path), doraise=True)
            before = path.stat()
            data = b"MARKER = 'new'\n"
            path.write_bytes(data)
            os.utime(path, ns=(before.st_atime_ns, before.st_mtime_ns))
            loaded = adapter.import_captured(adapter.Inputs(), path, adapter.digest(data), "training_cache_test")
            self.assertEqual(loaded.MARKER, "new")

    def test_import_refuses_bytes_that_do_not_match_the_declared_digest(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "helper.py"
            path.write_bytes(b"MARKER = 'changed'\n")
            with self.assertRaisesRegex(ValueError, "digest changed"):
                adapter.import_captured(adapter.Inputs(), path, "0" * 64, "training_bad_hash_test")


if __name__ == "__main__":
    unittest.main()
