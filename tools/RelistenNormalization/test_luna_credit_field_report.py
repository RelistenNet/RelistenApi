"""Behavioral checks for the offline, priority-role development comparison."""

import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import luna_credit_field_report as report


def extraction(name="Renée 🌿", role="taper", field="taper"):
    return {"credits": [{"name": name, "role": role, "evidence": {
        "field": field, "quote": name, "startCodePoint": 0, "endCodePoint": len(name)}}],
        "unresolved": []}


def result(label_id, local=None, luna=None, state="extracted"):
    local = extraction() if local is None else local
    luna = extraction() if luna is None else luna
    return {"labelId": label_id, "state": state, "local": local,
            "luna": {"state": state, "extraction": luna},
            "currentRouting": {"state": "local", "extraction": local}}


class CreditComparisonTests(unittest.TestCase):
    def setUp(self):
        self.label = {"label_id": "one", "family_partition": "development",
                      "artist_partition": "development", "field_path": "/raw_record/row/taper",
                      "field_sha256": hashlib.sha256("Renée 🌿".encode()).hexdigest(),
                      "answer": {"exhaustive": True, "unresolved": False, "unknowns": [],
                                 "assertions": [{"name": {"quote": "Renée 🌿", "start": 0, "end": 7},
                                                 "role": "Taper"}]}}
        self.item = {"labelId": "one", "field": "taper", "text": "Renée 🌿", "noText": False,
                     "fieldSha256": self.label["field_sha256"]}
        self.output = result("one")

    def compare(self, outputs=None, labels=None, items=None):
        labels_bytes = (json.dumps(self.label, ensure_ascii=False) + "\n").encode() if labels is None else labels
        selection = {"labelsSha256": hashlib.sha256(labels_bytes).hexdigest(),
                     "items": [self.item] if items is None else items}
        return report.compare(labels_bytes, selection, [self.output] if outputs is None else outputs)

    def test_exact_unicode_names_and_roles_match_original_codepoints(self):
        value = self.compare()
        self.assertEqual(1, value["methods"]["luna"]["true_positive"])
        self.assertEqual(1, value["methods"]["luna"]["gold_assertions"])
        self.assertFalse(value["policy_activation_permitted"])

    def test_failed_model_retaining_local_output_is_not_scored_as_luna_success(self):
        self.output["state"] = self.output["luna"]["state"] = "unresolved"
        value = self.compare()
        self.assertEqual(0, value["methods"]["luna"]["scored_fields"])
        self.assertEqual(1, value["methods"]["luna"]["unavailable_fields"])
        self.assertEqual(1, value["methods"]["luna_with_local_on_failure"]["true_positive"])
        self.assertIsNone(value["methods"]["luna"]["precision"])

    def test_luna_recovery_and_new_error_are_measured_in_opposite_directions(self):
        empty = {"credits": [], "unresolved": []}
        self.output["local"] = empty
        value = self.compare()
        self.assertEqual(1, value["paired_local_luna"]["transitions"]["corrected_fields"])
        self.assertEqual(1, value["paired_local_luna"]["transitions"]["recovered_gold_assertions"])
        self.output["local"] = extraction()
        self.output["luna"]["extraction"] = empty
        value = self.compare()
        self.assertEqual(1, value["paired_local_luna"]["transitions"]["new_error_fields"])
        self.assertEqual(1, value["paired_local_luna"]["transitions"]["lost_gold_assertions"])

    def test_uncertain_fields_are_diagnostics_and_never_false_positive_negatives(self):
        self.label["answer"]["assertions"] = []
        self.label["answer"]["unresolved"] = True
        self.label["answer"]["unknowns"] = [{"span": {"start": 0, "end": 7, "quote": "Renée 🌿"}}]
        value = self.compare()
        self.assertEqual(0, value["scored_resolved_fields"])
        self.assertEqual(0, value["methods"]["luna"]["false_positive"])
        self.assertEqual(1, value["excluded_fields"])
        self.assertEqual("Renée 🌿", value["diagnostics"][0]["predictions"]["luna"][0]["name"])

    def test_nonpriority_roles_do_not_count_as_taper_or_transfer_errors(self):
        self.label["answer"]["assertions"][0]["role"] = "Musician"
        self.output["local"] = extraction(role="musician")
        self.output["luna"]["extraction"] = {"credits": [], "unresolved": []}
        value = self.compare()
        self.assertEqual(0, value["methods"]["local"]["gold_assertions"])
        self.assertEqual(0, value["methods"]["local"]["false_positive"])

    def test_receipt_exclusions_cannot_be_overridden_by_resolved_labels(self):
        labels = (json.dumps(self.label) + "\n").encode()
        selection = {"labelsSha256": report.sha256(labels), "items": [self.item]}
        value = report.compare(labels, selection, [self.output], ["one"])
        self.assertEqual(0, value["scored_resolved_fields"])
        self.assertEqual(["one"], value["explicit_receipt_exclusions"])
        with self.assertRaisesRegex(ValueError, "outside the frozen bundle"):
            report.compare(labels, selection, [self.output], ["another"])

    def test_unrelated_field_or_bad_unicode_offsets_are_rejected(self):
        self.output["luna"]["extraction"]["credits"][0]["evidence"]["field"] = "description"
        with self.assertRaisesRegex(ValueError, "original support"):
            self.compare()
        self.output = result("one")
        self.output["luna"]["extraction"]["credits"][0]["evidence"]["endCodePoint"] = 8
        with self.assertRaisesRegex(ValueError, "coordinates"):
            self.compare()

    def test_missing_duplicate_and_held_out_fields_cannot_change_denominators(self):
        with self.assertRaisesRegex(ValueError, "every frozen field"):
            self.compare(outputs=[])
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.compare(outputs=[self.output, copy.deepcopy(self.output)])
        self.label["artist_partition"] = "test"
        with self.assertRaisesRegex(ValueError, "Held-out"):
            self.compare()

    def test_literal_unicode_line_separator_is_not_an_extra_jsonl_record(self):
        value = {"text": "First\u2028Second\u0085Third"}
        self.assertEqual([value], report.rows((json.dumps(value, ensure_ascii=False) + "\n").encode()))

    def test_changed_completed_bytes_fail_before_a_report_directory_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory) / "source", Path(directory) / "report"
            source.mkdir()
            labels = Path(directory) / "labels.jsonl"
            labels_bytes = (json.dumps(self.label) + "\n").encode()
            labels.write_bytes(labels_bytes)
            receipt = json.dumps({"labels_sha256": report.sha256(labels_bytes),
                                  "training_excluded_label_ids": []}).encode()
            (Path(directory) / "receipt.json").write_bytes(receipt)
            selection = {"labelsSha256": report.sha256(labels_bytes), "items": [self.item],
                         "receiptSha256": report.sha256(receipt)}
            results = (json.dumps(self.output) + "\n").encode()
            receipts = b"{}"
            manifest = {"state": "completed", "selection": selection,
                        "resultsSha256": report.sha256(results), "receiptsSha256": report.sha256(receipts),
                        "job": {"capUsd": 5, "accountedUsd": .001}, "paidAttempts": 1,
                        "lunaFromCache": 0, "keylessReplayFromCache": 1}
            (source / "manifest.json").write_text(json.dumps(manifest))
            (source / "selection.json").write_text(json.dumps(selection))
            (source / "results.jsonl").write_bytes(results + b"\n")
            (source / "receipts.json").write_bytes(receipts)
            with self.assertRaisesRegex(ValueError, "output bytes changed"):
                report.run(labels, source, output)
            self.assertFalse(output.exists())
            (source / "results.jsonl").write_bytes(results)
            value = report.run(labels, source, output)
            self.assertEqual(1, value["fields"])
            self.assertTrue((output / "report.json").exists())
            with self.assertRaisesRegex(ValueError, "new offline report"):
                report.run(labels, source, output)


if __name__ == "__main__":
    unittest.main()
