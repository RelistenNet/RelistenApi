"""Exercise corpus contracts through the same CLI used by corpus authors."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("validate.py")
DOMAINS = ("venues", "songs", "compositions", "people", "lineage")


class TestCorpusValidation(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.evidence = [
            {
                "id": f"evidence-{index}", "kind": "source", "artist_id": index,
                "artist_name": f"Artist {index}", "provider": "archive.org",
                "entity_id": index, "fields": {"notes": "Transferred by Jane. Unknown venue."},
            }
            for index in range(5)
        ]
        self.cases = [
            {
                "id": domain, "domain": domain, "task": "extract",
                "split": "development", "group": f"family-{index}",
                "inputs": [{"evidence_id": f"evidence-{index}", "field": "notes"}],
                "expected": {"status": "supported", "assertions": {"person": "Jane"}},
                "basis": [{
                    "evidence_id": f"evidence-{index}", "field": "notes",
                    "quote": "Transferred by Jane",
                }],
                "rationale": "The credit names Jane; this does not resolve a global person identity.",
                "annotation_status": "development_labeled",
            }
            for index, domain in enumerate(DOMAINS)
        ]

    def write_rows(self, name, rows):
        path = self.root / name
        path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        return path

    def run_cli(self, manifest=False):
        self.write_rows("evidence.jsonl", self.evidence)
        self.write_rows("cases.jsonl", self.cases)
        arguments = [
            sys.executable, str(SCRIPT),
            "--evidence", str(self.root / "evidence.jsonl"),
            "--cases", str(self.root / "cases.jsonl"),
        ]
        if manifest:
            arguments += ["--manifest", str(self.root / "manifest.json")]
        return subprocess.run(arguments, capture_output=True, text=True)

    def receipt(self):
        # Freeze the receipt before a test mutates either file.
        self.write_rows("evidence.jsonl", self.evidence)
        self.write_rows("cases.jsonl", self.cases)
        receipt = {
            "files": [
                {"path": name, "sha256": hashlib.sha256((self.root / name).read_bytes()).hexdigest()}
                for name in ("evidence.jsonl", "cases.jsonl")
            ],
            "record_counts": {"evidence": len(self.evidence), "cases": len(self.cases)},
        }
        (self.root / "manifest.json").write_text(json.dumps(receipt), encoding="utf-8")

    def assert_rejected(self, fragment, manifest=False):
        result = self.run_cli(manifest)
        self.assertEqual(result.returncode, 1, result.stdout)
        self.assertIn(fragment, result.stderr)
        self.assertEqual(result.stdout, "")
        self.assertNotIn("Traceback", result.stderr)

    def test_valid_unresolved_case_does_not_claim_accuracy(self):
        self.cases[0]["expected"] = {
            "status": "unresolved", "reason": "The text does not identify the venue.",
        }
        self.cases[0]["basis"][0]["quote"] = "Unknown venue"
        result = self.run_cli()
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report["cases"], 5)
        self.assertEqual(report["cases_by_expected_status"]["unresolved"], 1)
        self.assertEqual(len(report["artists"]), 5)
        self.assertEqual(report["validation"], "corpus integrity only")
        self.assertTrue(any(
            "no extraction or matching accuracy" in item for item in report["limitations"]
        ))
        self.assertFalse(report["case_digest_verified"])

    def test_manifest_covers_both_files(self):
        self.receipt()
        result = self.run_cli(manifest=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)["case_digest_verified"])

    def test_changed_evidence_rejected_by_receipt(self):
        self.receipt()
        self.evidence[0]["fields"]["notes"] += " Extra information."
        self.assert_rejected("SHA256 mismatch for evidence.jsonl", manifest=True)

    def test_missing_cases_rejected_even_when_all_domains_remain(self):
        extra = dict(self.cases[0], id="extra-case")
        self.cases.append(extra)
        self.receipt()
        self.cases.pop()
        self.assert_rejected("SHA256 mismatch for cases.jsonl", manifest=True)

    def test_duplicate_ids_rejected(self):
        for rows in (self.evidence, self.cases):
            with self.subTest(rows="evidence" if rows is self.evidence else "cases"):
                rows.append(rows[0])
                self.assert_rejected("duplicate id")
                rows.pop()

    def test_dangling_input_and_absent_field_rejected(self):
        self.cases[0]["inputs"][0]["evidence_id"] = "missing"
        self.assert_rejected("unknown evidence missing")
        self.cases[0]["inputs"][0] = {"evidence_id": "evidence-0", "field": "missing"}
        self.assert_rejected("absent field")

    def test_wrong_and_empty_quote_rejected(self):
        self.cases[0]["basis"][0]["quote"] = "Recorded by Jane"
        self.assert_rejected("not an exact substring")
        self.cases[0]["basis"][0]["quote"] = ""
        self.assert_rejected("quote must be nonempty")

    def test_structured_basis_handles_absence_and_unordered_membership(self):
        self.evidence[0]["fields"].update({"credits": None, "order_available": False})
        self.cases[0]["inputs"] = [
            {"evidence_id": "evidence-0", "field": "credits"},
            {"evidence_id": "evidence-0", "field": "order_available"},
        ]
        self.cases[0]["basis"] = [
            {"evidence_id": "evidence-0", "field": "credits", "value": None},
            {"evidence_id": "evidence-0", "field": "order_available", "value": False},
        ]
        self.cases[0]["expected"] = {"status": "unresolved"}
        result = self.run_cli()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.cases[0]["basis"][1]["value"] = 0
        self.assert_rejected("value does not exactly match")
        self.cases[0]["basis"][1]["value"] = False
        self.cases[0]["basis"][0]["value"] = ""
        self.assert_rejected("value does not exactly match")

    def test_basis_requires_exactly_one_citation_form(self):
        self.cases[0]["basis"][0]["value"] = "Transferred by Jane"
        self.assert_rejected("exactly one of quote or value")
        del self.cases[0]["basis"][0]["value"]
        del self.cases[0]["basis"][0]["quote"]
        self.assert_rejected("exactly one of quote or value")

    def test_basis_cannot_cite_hidden_input(self):
        self.cases[0]["basis"][0]["evidence_id"] = "evidence-1"
        self.assert_rejected("basis is not among inputs")

    def test_group_cannot_cross_splits(self):
        self.cases[1]["group"] = self.cases[0]["group"]
        self.cases[1]["split"] = "evaluation"
        self.assert_rejected("group crosses splits")

    def test_evidence_cannot_cross_splits_even_for_different_groups(self):
        self.cases[1]["inputs"][0]["evidence_id"] = "evidence-0"
        self.cases[1]["basis"][0]["evidence_id"] = "evidence-0"
        self.cases[1]["split"] = "evaluation"
        self.assert_rejected("evidence crosses splits")

    def test_partial_domain_coverage_rejected(self):
        self.cases.pop()
        self.assert_rejected("missing domains: lineage")

    def test_supported_case_requires_assertions(self):
        self.cases[0]["expected"]["assertions"] = {}
        self.assert_rejected("supported labels require nonempty assertions")

    def test_unknown_review_state_and_malformed_status_rejected(self):
        self.cases[0]["annotation_status"] = "model_confident"
        self.assert_rejected("invalid annotation_status")
        self.cases[0]["annotation_status"] = "development_labeled"
        self.cases[0]["expected"]["status"] = []
        self.assert_rejected("invalid expected status")

    def test_truncated_json_rejected_with_file_and_line(self):
        evidence = self.write_rows("evidence.jsonl", self.evidence)
        cases = self.write_rows("cases.jsonl", self.cases)
        with cases.open("a", encoding="utf-8") as stream:
            stream.write('{"id":')
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--evidence", str(evidence), "--cases", str(cases)],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("cases.jsonl:6: invalid JSON", result.stderr)


if __name__ == "__main__":
    unittest.main()
