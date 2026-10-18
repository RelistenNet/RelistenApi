"""Check leakage boundaries, actual fitting, and failed-run recovery for venue experiments."""

import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

import joblib

PATH = Path(__file__).with_name("venue_matcher_experiment.py")
SPEC = importlib.util.spec_from_file_location("venue_experiment", PATH)
EXP = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = EXP
SPEC.loader.exec_module(EXP)


def evidence(key, name="Venue", location="City, ST"):
    return {
        "evidence_id": key, "artist_partition": "development", "family_partition": "development",
        "resource": {"resource_uuid": key}, "source_line_sha256": "source-hash",
        "fields": [{"path": f"/raw_record/row/{field}", "text": text,
                    "text_sha256": hashlib.sha256(text.encode()).hexdigest()}
                   for field, text in [("name", name), ("location", location)]],
    }


def label(key, left, right, decision="same_entity", groups=()):
    return {
        "task_id": key, "evidence_ids": [left, right],
        "usage": "development_experiments_only", "evaluation_eligible": False,
        "family_partition": "development", "answer": {"decision": decision},
        "split_group_ids": [f"evidence:{left}", f"evidence:{right}", *groups],
    }


def fitting_examples():
    result = []
    for index, target in enumerate([0, 0, 0, 1, 1, 1]):
        values = (float(target), float(target), float(target), 1.0, 1.0, 1.0)
        result.append(EXP.Example(f"task-{index}", f"group-{index}", target, values, (), {}))
    result.append(EXP.Example("unknown", "group-0", None, (1.0,) * 6, (), {}))
    return result


class TestVenueMatcherExperiment(unittest.TestCase):
    def test_raw_location_equality_is_separate_from_missing_location(self):
        known = EXP.lexical_features(EXP.VenueWording("Hall", "New York, NY"),
                                     EXP.VenueWording("Hall", "New York NY"))
        missing = EXP.lexical_features(EXP.VenueWording("Hall", "Unknown Location"),
                                       EXP.VenueWording("Hall", "Unknown Location"))
        self.assertEqual(known[3:], (1.0, 1.0, 1.0))
        self.assertEqual(missing[3:], (0.0, 0.0, 0.0))
        self.assertEqual(missing[:3], known[:3])

    def test_unicode_and_pair_symmetry_preserve_lexical_distinctions(self):
        a = EXP.VenueWording("Élan\u2028Theatre", "Montréal, QC")
        b = EXP.VenueWording("E\u0301lan Theatre", "Montréal QC")
        self.assertEqual(EXP.lexical_features(a, b), (1.0,) * 6)
        self.assertEqual(EXP.lexical_features(a, b), EXP.lexical_features(b, a))
        self.assertEqual(EXP.lexical_features(a, EXP.VenueWording("Elan Theatre", "Montreal QC"))[0], 0)
        self.assertEqual(EXP.lexical_features(EXP.VenueWording("", ""), EXP.VenueWording("", "")), (0.0,) * 6)

    def test_rationale_official_sources_and_identifiers_do_not_enter_features(self):
        fields = {key: evidence(key) for key in ["a", "b"]}
        original = label("pair", "a", "b")
        before = EXP.build_examples([original], fields, [], EXP.bundle_validator())[0]
        original["answer"].update({"reason": "different buildings", "primary_source_ids": ["official-address"]})
        original["candidate_reason"] = "misleading selection rationale"
        fields["a"]["resource"]["resource_uuid"] = "another-identity"
        after = EXP.build_examples([original], fields, [], EXP.bundle_validator())[0]
        self.assertEqual(before.values, after.values)
        self.assertEqual(len(after.values), 6)

    def test_unknown_bridge_and_additive_hypotheses_group_before_fitting(self):
        labels = [label("left", "a", "b"), label("right", "c", "d", "different_entities"),
                  label("bridge", "b", "c", "insufficient_evidence"), label("other", "e", "f")]
        fields = {key: evidence(key) for key in "abcdef"}
        examples = EXP.build_examples(labels, fields, [], EXP.bundle_validator())
        self.assertEqual(examples[0].component, examples[1].component)
        self.assertIsNone(examples[2].target)
        linked = EXP.build_examples(labels, fields, [{"split_group_id": "hypothesis",
                                   "task_ids": ["left", "other"]}], EXP.bundle_validator())
        self.assertEqual(len({row.component for row in linked}), 1)

    def test_held_out_input_is_rejected_despite_development_task(self):
        fields = {key: evidence(key) for key in ["a", "b"]}
        fields["b"]["artist_partition"] = "test"
        with self.assertRaisesRegex(ValueError, "Held-out"):
            EXP.build_examples([label("pair", "a", "b")], fields, [], EXP.bundle_validator())

    def test_bundle_change_during_validation_refuses_fitting(self):
        with tempfile.TemporaryDirectory() as temp:
            bundle = Path(temp)
            for name in ["labels.jsonl", "evidence.jsonl", "split-links.jsonl"]:
                (bundle / name).write_bytes(b"")
            manifest = {"files": {name: {"sha256": EXP.digest(bundle / name)} for name in
                        ["labels.jsonl", "evidence.jsonl", "split-links.jsonl"]}, "local_supplement": "unused"}
            (bundle / "manifest.json").write_text(json.dumps(manifest))
            (bundle / "validate_labels.py").write_text("# fixture validator")
            pins = [EXP.digest(bundle / name) for name in ["manifest.json", "labels.jsonl", "split-links.jsonl"]]
            def mutate(*args):
                (bundle / "labels.jsonl").write_bytes(b"changed")
            validator = SimpleNamespace(RAW=bundle, validate_bundle=mutate)
            with patch.object(EXP, "bundle_validator", return_value=validator):
                with self.assertRaisesRegex(ValueError, "changed during validation"):
                    EXP.load_examples(bundle, *pins)

    def test_blank_names_are_outside_fitting_instead_of_negative_agreement(self):
        fields = {"a": evidence("a", name=" "), "b": evidence("b")}
        with self.assertRaisesRegex(ValueError, "Blank venue names"):
            EXP.build_examples([label("pair", "a", "b")], fields, [], EXP.bundle_validator())

    def test_baseline_requires_same_retained_inputs_and_preserves_actual_comparison(self):
        example = EXP.Example("pair", "component", None, (1.0,) * 6,
                              ({"evidence_id": "a"}, {"evidence_id": "b"}), {})
        row = {"task_id": "pair", "evidence_ids": ["a", "b"], "comparison": {
            "domain": "venues", "candidateUuid": None, "policyVersion": None,
            "evaluationReceiptUuid": None, "disposition": "needs_evidence", "nameTokenSimilarity": 0.7}}
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            EXP.write_rows(directory / "baseline.jsonl", [row])
            provenance = {"manifest_sha256": "bundle", "files": {
                name: name for name in ["labels.jsonl", "evidence.jsonl", "split-links.jsonl"]}}
            manifest = {"state": "completed", "version": "venue-pair-baseline-v1",
                "projectionVersion": "venue-retained-names-only-v1", "bundleManifestSha256": "bundle",
                "inputFiles": {name: {"sha256": value} for name, value in provenance["files"].items()},
                "outputFile": "baseline.jsonl", "outputSha256": EXP.digest(directory / "baseline.jsonl"),
                "pairs": 1, "matcherAssemblySha256": "assembly", "runnerAssemblySha256": "runner"}
            EXP.write_json(directory / "manifest.json", manifest)
            actual = EXP.load_baseline(directory, [example], provenance)
            self.assertEqual(actual["rows"][0]["comparison"], row["comparison"])
            provenance["files"]["evidence.jsonl"] = "another snapshot"
            with self.assertRaisesRegex(ValueError, "Baseline input differs"):
                EXP.load_baseline(directory, [example], provenance)

    def test_unknowns_never_train_and_complete_components_are_excluded(self):
        result = EXP.fit_comparison(fitting_examples())
        for fold in result["splits"]:
            self.assertNotIn("unknown", fold["training_tasks"])
            self.assertFalse(set(fold["held_tasks"]) & set(fold["training_tasks"]))
        unknown = next(row for row in result["predictions"] if row["task_id"] == "unknown")
        self.assertIsNone(unknown["target"])
        self.assertIsNone(unknown["diagnostic_at_half"])
        self.assertNotIn(unknown, result["errors"])
        self.assertEqual(set(unknown["scores"]), {"logistic", "boosted_tree"})
        self.assertEqual(len(result["models"]["boosted_tree"]["final"]["trees"]), 20)

    def test_unusable_component_fails_without_completion_report(self):
        examples = fitting_examples()
        examples = [EXP.Example(row.task_id, "all", row.target, row.values, (), {}) for row in examples]
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "failed"
            with self.assertRaisesRegex(ValueError, "training classes"):
                EXP.run(examples, {}, output)
            self.assertFalse((output / "report.json").exists())

    def test_saved_estimators_replay_final_fit_and_artifacts_are_hashed(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "complete"
            examples = fitting_examples()
            report = EXP.run(examples, {}, output)
            models = joblib.load(output / "final-models.joblib")
            direct = EXP.fit_comparison(examples)["final_estimators"]
            for name, model in models.items():
                self.assertEqual(model.predict_proba([examples[0].values]).tolist(),
                                 direct[name].predict_proba([examples[0].values]).tolist())
            self.assertFalse(report["policy_activation_permitted"])
            self.assertFalse(report["accuracy_estimated"])
            self.assertEqual(report["counts"]["unknown_pairs"], 1)
            for row in EXP.read_rows(output / "features.jsonl"):
                self.assertFalse(row["resolved_geographic_identity_used"])
            hashes = json.loads((output / "artifacts.json").read_text())
            self.assertTrue(all(EXP.digest(output / name) == value for name, value in hashes.items()))
            report_bytes = (output / "report.json").read_bytes()
            with self.assertRaises(FileExistsError):
                EXP.run(examples, {}, output)
            self.assertEqual((output / "report.json").read_bytes(), report_bytes)

    def test_partial_write_has_no_completion_and_retry_uses_new_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            failed, retry = Path(temp) / "failed", Path(temp) / "retry"
            real_write = EXP.write_json
            def fail_models(path, value):
                if path.name == "models.json":
                    raise OSError("simulated storage failure")
                real_write(path, value)
            with patch.object(EXP, "write_json", side_effect=fail_models):
                with self.assertRaisesRegex(OSError, "storage failure"):
                    EXP.run(fitting_examples(), {}, failed)
            self.assertTrue((failed / "features.jsonl").exists())
            self.assertFalse((failed / "report.json").exists())
            self.assertEqual(EXP.run(fitting_examples(), {}, retry)["status"], "completed_development_diagnostic")


if __name__ == "__main__":
    unittest.main()
