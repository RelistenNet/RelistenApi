"""Public CLI/output checks over synthetic fields; no real-data fitting or provider calls."""
from collections import Counter
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid

import matched_credit_experiment as study
import credit_tagger_experiment as tagger
from test_luna_credit_field_report import SyntheticCohort


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False) + "\n", encoding="utf-8")


def write_rows(path, values):
    path.write_text("".join(json.dumps(v, ensure_ascii=False) + "\n" for v in values), encoding="utf-8")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def evidence(field, text, start=0, end=None):
    end = len(text) if end is None else end
    return {"field": field, "jsonPointer": "/" + field, "startCodePoint": start,
            "endCodePoint": end, "quote": text[start:end]}


class SyntheticStudy:
    """Exactly 100 synthetic fields authenticate the same boundary as the real frozen pack."""

    def __init__(self, root):
        self.root = root
        self.comparison = root / "saved-comparison"
        self.comparison.mkdir()
        self.labels, self.fields, self.items, self.outputs, self.requests, self.eligible = [], [], [], [], [], []
        values = [("Alice", "taper", "Alice"), ("Bob", "taper", "Bob"),
                  ("Cara", "transferrer", "Cara"), ("Drew", "transferrer", "Drew"),
                  ("Excludedone", "taper", "Excludedone"), ("Excludedtwo", "taper", "Excludedtwo"),
                  ("🎵Recorded by <b>Zoë</b>", "description", "Zoë"),
                  ("Recorded by Léon&nbsp;March", "description", "Léon&nbsp;March"),
                  ("Maybe Friend\u2028someone", "description", "Friend"),
                  ("", "taper", None), ("Evan", "taper", "Evan")]
        values += [(f"Equipment note{index}", "lineage", None) for index in range(11, 100)]
        for number, (text, field, name) in enumerate(values):
            source = str(uuid.uuid5(uuid.NAMESPACE_URL, "synthetic-source-" + str(number)))
            artist = str(uuid.uuid5(uuid.NAMESPACE_URL, "artist-private-" + str(number)))
            key = "credit-field:" + source + ":" + field
            role = "Transferrer" if field == "transferrer" else "Taper"
            name_start = text.index(name) if name else None
            claims = [] if name is None else [{
                "name": {"start": name_start, "end": name_start + len(name), "quote": name},
                "role": role,
                "role_basis": {"kind": "structured_field", "field": field} if field in {"taper", "transferrer"}
                              else {"kind": "prose", "span": {"start": 0, "end": len(text), "quote": text}},
            }]
            field_hash = hashlib.sha256(text.encode()).hexdigest()
            label = {"label_id": key, "task_id": "extract:source_sample:" + source,
                     "status": "draft_development", "scope": "complete_single_field",
                     "evidence_id": "source_sample:" + source, "field_path": "/raw_record/row/" + field,
                     "field_sha256": field_hash, "field_codepoints": len(text),
                     "family_partition": "development", "artist_partition": "development",
                     "artist": {"uuid": artist}, "input_duplicate_family": "duplicate-" + source,
                     "known_recording_family": "family-" + source,
                     "resource": {"provider": "relisten_catalog_snapshot", "resource_type": "source", "resource_uuid": source},
                     "answer": {"exhaustive": True, "unresolved": number == 8, "unknowns": [], "assertions": claims}}
            if number == 8:
                label["answer"]["unknowns"] = [{"span": {"start": 0, "end": len(text), "quote": text}}]
            self.labels.append(label)
            self.fields.append({"label_id": key, "evidence_id": label["evidence_id"], "field_path": label["field_path"],
                                "field_sha256": field_hash, "field_text": text})
            item = {"labelId": key, "taskId": label["task_id"], "evidenceId": label["evidence_id"],
                    "sourceUuid": source, "artistUuid": artist, "field": field,
                    "text": text, "fieldSha256": field_hash, "noText": not text.strip()}
            self.items.append(item)
            local_credits, luna_credits, response = [], [], []
            if name:
                display = "Léon March" if number == 7 else name
                local_credits = [{"name": display, "role": role.lower(), "evidence": evidence(field, text),
                                  "actorFacts": {"anchor": evidence(field, text, name_start, name_start + len(name))}}]
                luna_credits = [{"name": name, "role": role.lower(), "evidence": evidence(field, text)}]
                response = [{"name": name, "role": role.lower(), "field": field, "quote": text, "start": 0, "end": len(text)}]
            extraction = {"credits": luna_credits, "unresolved": []}
            request = json.dumps({"model": "gpt-6-luna", "input": [
                {"role": "system", "content": "Extract credits, version " + study.LUNA_VERSION},
                {"role": "user", "content": json.dumps({"fields": {field: text}, "context": None})}]})
            cache_key = hashlib.sha256(request.encode()).hexdigest()
            luna = {"state": "extracted", "extraction": extraction, "cacheKey": cache_key, "fromCache": True, "reason": None}
            state, replay = "extracted", dict(luna)
            if number == 9:
                state, luna, replay = "no_text", None, None
            elif number == 10:
                state = "unresolved"
                luna = {"state": "unresolved", "extraction": {"credits": local_credits, "unresolved": []},
                        "cacheKey": None, "fromCache": False, "reason": "configuration_unavailable"}
                replay = dict(luna)
            else:
                self.requests.append({"cacheKey": cache_key, "renderedRequest": request,
                                      "responseJson": json.dumps({"credits": response})})
            self.outputs.append({"labelId": key, "state": state, "local": {"credits": local_credits, "unresolved": []},
                                 "luna": luna, "cacheReplay": replay, "currentRouting": {"state": "local"}})
        # The excluded field connects two otherwise separate development families. Masking it
        # before grouping would let their source context cross folds.
        self.labels[4]["artist"]["uuid"] = self.labels[0]["artist"]["uuid"]
        self.labels[4]["known_recording_family"] = self.labels[2]["known_recording_family"]
        self.items[4]["artistUuid"] = self.labels[4]["artist"]["uuid"]
        for label in self.labels:
            self.eligible.append({"evidence_id": label["evidence_id"], "artist_uuid": label["artist"]["uuid"],
                                  "artist_partition": "development", "family_partition": "development",
                                  "family_group": label["input_duplicate_family"], "family": label["known_recording_family"]})
        self.code_root = root / "archived-code"
        code_files = []
        for name in sorted(study.CODE_PATHS):
            path = self.code_root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(("synthetic-code-receipt:" + name).encode())
            code_files.append({"path": name, "sha256": digest(path)})
        self.code_receipt = {"local_extractor_version": study.LOCAL_VERSION, "luna_extractor_version": study.LUNA_VERSION,
                             "files": code_files}
        self.save()

    def save(self):
        write_rows(self.root / "labels.jsonl", self.labels)
        write_rows(self.root / "fields.jsonl", self.fields)
        self.receipt = {"status": "draft_development", "schema_version": "credit-field-draft-v1", "fields": 100,
                        "labels_sha256": digest(self.root / "labels.jsonl"), "raw_fields_sha256": digest(self.root / "fields.jsonl"),
                        "snapshot_sha256": "synthetic-snapshot", "training_excluded_label_ids": [self.labels[4]["label_id"]]}
        write(self.root / "receipt.json", self.receipt)
        write(self.root / "reserved-index.json", {"raw_snapshot_sha256": "synthetic-snapshot", "eligible_source_metadata": self.eligible})
        index_path = self.root / "reserved-index.json"
        write(self.root / "reservation-receipt.json", {"index": {"path": index_path.name, "bytes": index_path.stat().st_size, "sha256": digest(index_path)}})
        write(self.root / "exclusions.json", {"provenance": {"labels_sha256": self.receipt["labels_sha256"]},
            "explicit_receipt_exclusions": [self.labels[4]["label_id"], self.labels[5]["label_id"]],
            "sensitivity": {"kind": "conservative_whole_field_exclusion", "additional_excluded_label_ids": [self.labels[5]["label_id"]]}})
        write(self.root / "code-receipt.json", self.code_receipt)
        selection = {"labelsSha256": self.receipt["labels_sha256"], "fieldsSha256": self.receipt["raw_fields_sha256"],
                     "receiptSha256": digest(self.root / "receipt.json"), "items": self.items}
        write(self.comparison / "selection.json", selection)
        write_rows(self.comparison / "results.jsonl", self.outputs)
        write(self.comparison / "receipts.json", {"requests": self.requests})
        self.manifest = {"state": "completed_partial", "version": "luna-credit-fields-v1", "fields": 100,
                         "selection": selection, "states": dict(Counter(row["state"] for row in self.outputs)),
                         "options": {"model": "gpt-6-luna"},
                         "resultsSha256": digest(self.comparison / "results.jsonl"),
                         "receiptsSha256": digest(self.comparison / "receipts.json"),
                         "extractorAssemblySha256": digest(self.code_root / "tools/RelistenNormalization/bin/Debug/net10.0/RelistenApi.dll"),
                         "runnerAssemblySha256": digest(self.code_root / "tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll")}
        write(self.comparison / "manifest.json", self.manifest)

    def argv(self, output="output"):
        values = {"labels": self.root / "labels.jsonl", "receipt": self.root / "receipt.json", "fields": self.root / "fields.jsonl",
                  "comparison": self.comparison, "code-receipt": self.root / "code-receipt.json", "code-root": self.code_root,
                  "exclusions": self.root / "exclusions.json", "reservation-receipt": self.root / "reservation-receipt.json",
                  "source-root": self.root, "output": self.root / output,
                  "labels-sha256": digest(self.root / "labels.jsonl"), "comparison-sha256": digest(self.comparison / "manifest.json"),
                  "code-sha256": digest(self.root / "code-receipt.json"), "exclusions-sha256": digest(self.root / "exclusions.json"),
                  "folds": 2, "epochs": 1}
        return [str(part) for key, value in values.items() for part in ("--" + key, value)]

    def call(self, output="output"):
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            return study.main(self.argv(output))


class MatchedCreditCliTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.fixture = SyntheticStudy(Path(self.directory.name))

    def test_cli_outputs_oof_models_masks_group_bridge_and_unavailable_denominators(self):
        fixture = self.fixture
        result = subprocess.run([sys.executable, "-B", "-O", str(Path(study.__file__)), *fixture.argv()],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads((fixture.root / "output/report.json").read_text())
        self.assertEqual(report["fields"], 100)
        self.assertEqual(report["resolved_fields"], 97)
        self.assertEqual(report["full_resolved_priority"]["methods"]["luna"]["unavailable_fields"], 1)
        self.assertEqual(report["shared_luna_available_priority"]["resolved_fields"], 96)
        self.assertEqual(report["all_role_diagnostics"]["methods"]["luna"]["status"], "not_evaluated")
        excluded = {fixture.labels[i]["label_id"] for i in (4, 5, 8)}
        assignment = {}
        for fold in report["folds"]:
            self.assertFalse(excluded & set(fold["training_label_ids"]))
            self.assertFalse(set(fold["training_label_ids"]) & set(fold["development_label_ids"]))
            assignment.update({key: fold["fold"] for key in fold["development_label_ids"]})
            model = json.loads((fixture.root / f"output/models-fold-{fold['fold']}.json").read_text())
            for head in model["perceptron"].values():
                features = {feature for weights in head.values() for feature in weights}
                self.assertFalse(any("excludedone" in feature or "excludedtwo" in feature for feature in features))
                self.assertFalse(any(label["artist"]["uuid"] in feature or label["label_id"] in feature
                                     for label in fixture.labels for feature in features))
            for head in model["logistic"].values():
                vocab = head["vocabulary"]
                self.assertFalse(any("excludedone" in feature or "excludedtwo" in feature for feature in vocab))
                self.assertFalse(any(label["artist"]["uuid"] in feature or label["label_id"] in feature
                                     for label in fixture.labels for feature in vocab))
                held_names = [fixture.labels[i]["answer"]["assertions"][0]["name"]["quote"].lower()
                              for i in (0, 1, 2, 3, 10) if fixture.labels[i]["label_id"] in fold["development_label_ids"]]
                self.assertFalse(any("word=" + name in vocab for name in held_names))
        self.assertEqual(assignment[fixture.labels[0]["label_id"]], assignment[fixture.labels[2]["label_id"]])
        counts = report["full_resolved_priority"]["boundary_diagnostics"]
        self.assertEqual(counts["local"]["exact_gold_span_with_different_display"], 1)
        self.assertGreater(counts["luna"]["unanchored_prediction"], 0)
        predictions = {r["label_id"]: r for r in study.jsonl((fixture.root / "output/predictions.jsonl").read_bytes())}
        unavailable = predictions[fixture.labels[10]["label_id"]]
        self.assertIsNone(unavailable["methods"]["luna"])
        self.assertTrue(unavailable["methods"]["local"])
        for row in predictions.values():
            original = next(r["field_text"] for r in fixture.fields if r["label_id"] == row["label_id"])
            for method in ("perceptron", "logistic"):
                for prediction in row["methods"][method]:
                    start, end = prediction["span"]
                    self.assertEqual(original[start:end], prediction["literal"])

    def test_reserved_metadata_is_rejected_before_raw_fields_are_decoded(self):
        fixture = self.fixture
        fixture.eligible = fixture.eligible[1:]
        fixture.save()
        (fixture.root / "fields.jsonl").write_bytes(b'\xffnot-json\n')
        with patch.object(study, "jsonl", wraps=study.jsonl) as parser:
            self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())
        self.assertEqual(parser.call_count, 1, "only the label metadata may be decoded before eligibility")

    def test_unapproved_label_bytes_are_rejected_before_answer_decoding(self):
        fixture = self.fixture
        (fixture.root / "labels.jsonl").write_bytes(b"\xffunapproved")
        with patch.object(study, "jsonl", wraps=study.jsonl) as parser:
            self.assertEqual(fixture.call(), 1)
        self.assertEqual(parser.call_count, 0)
        self.assertFalse((fixture.root / "output").exists())

    def test_consumed_examples_must_equal_snapshot_despite_restored_input_files(self):
        fixture = self.fixture
        original = study.load_trainer
        def changed_consumption(snapshot):
            trainer = original(snapshot)
            loader = trainer.load_examples
            def altered(*args, **kwargs):
                examples = loader(*args, **kwargs)
                examples[0].text = "Transient unapproved text"
                return examples
            trainer.load_examples = altered
            return trainer
        with patch.object(study, "load_trainer", side_effect=changed_consumption):
            self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())

    def test_display_differences_are_measured_at_the_exact_anchor(self):
        first = {"display_name": "Bob", "literal": "Alice", "span": [0, 5], "role": "Taper"}
        second = {"display_name": "Alice", "literal": "Bob", "span": [6, 9], "role": "Taper"}
        row = {"complete_scoring": True,
               "gold": [{"name": "Alice", "span": [0, 5], "role": "Taper"},
                        {"name": "Bob", "span": [6, 9], "role": "Taper"}],
               "methods": {"local": [first, second], "perceptron": [], "logistic": [], "luna": None}}
        report = study.score([row], tagger)
        self.assertEqual(report["methods"]["local"]["true_positive"], 2)
        self.assertEqual(report["boundary_diagnostics"]["local"]["exact_gold_span_with_different_display"], 2)

    def test_changed_binary_and_source_reference_fail_even_with_updated_envelope_hashes(self):
        fixture = self.fixture
        fixture.outputs[0]["labelId"] = "foreign-source"
        fixture.save()
        self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())
        fixture.outputs[0]["labelId"] = fixture.labels[0]["label_id"]
        fixture.save()
        next(iter(fixture.code_root.rglob("*.dll"))).write_bytes(b"changed loaded binary")
        self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())

    def test_luna_response_and_request_must_bind_to_original_single_field(self):
        fixture = self.fixture
        request = fixture.requests[0]
        request["responseJson"] = json.dumps({"credits": []})
        fixture.save()
        self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())

    def test_equal_cached_credits_do_not_repair_a_replay_from_another_request(self):
        fixture = self.fixture
        fixture.outputs[0]["cacheReplay"]["cacheKey"] = "f" * 64
        fixture.save()
        self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())

    def test_invalid_raw_unicode_anchor_is_rejected_under_optimized_python(self):
        fixture = self.fixture
        fixture.outputs[6]["local"]["credits"][0]["actorFacts"]["anchor"]["startCodePoint"] += 1
        fixture.save()
        result = subprocess.run([sys.executable, "-B", "-O", str(Path(study.__file__)), *fixture.argv()],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertFalse((fixture.root / "output").exists())

    def test_original_and_additional_masks_are_both_required(self):
        fixture = self.fixture
        path = fixture.root / "exclusions.json"
        excluded = json.loads(path.read_text())
        excluded["explicit_receipt_exclusions"] = excluded["explicit_receipt_exclusions"][1:]
        write(path, excluded)
        self.assertEqual(fixture.call(), 1)
        self.assertFalse((fixture.root / "output").exists())

    def test_late_input_edit_retains_diagnostics_without_completed_report(self):
        fixture = self.fixture
        original = study.write_json
        def mutate_after_report(path, value):
            original(path, value)
            if path.name == "report.pending.json":
                (fixture.root / "fields.jsonl").write_bytes(b"changed after fitting")
        with patch.object(study, "write_json", side_effect=mutate_after_report):
            self.assertEqual(fixture.call(), 1)
        self.assertTrue((fixture.root / "output/predictions.jsonl").exists())
        self.assertFalse((fixture.root / "output/report.json").exists())

    def test_existing_output_is_preserved_and_failed_fit_never_completes(self):
        fixture = self.fixture
        output = fixture.root / "output"
        output.mkdir()
        (output / "report.json").write_bytes(b"original")
        self.assertEqual(fixture.call(), 1)
        self.assertEqual((output / "report.json").read_bytes(), b"original")
        original = study.load_trainer
        def fail_fit(snapshot):
            trainer = original(snapshot)
            trainer.fit_models = lambda *args, **kwargs: (_ for _ in ()).throw(RuntimeError("synthetic interruption"))
            return trainer
        with patch.object(study, "load_trainer", side_effect=fail_fit):
            self.assertEqual(fixture.call("failed"), 1)
        self.assertTrue((fixture.root / "failed/splits.json").exists())
        self.assertFalse((fixture.root / "failed/report.json").exists())


class MatchedCohortTests(unittest.TestCase):
    def test_cohort_cli_accepts_fresh_primary_and_fits_only_old84_before_scoring_every_new_field(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            reference = SyntheticStudy(root)
            path = root / "exclusions.json"
            exclusions = json.loads(path.read_text())
            exclusions["explicit_receipt_exclusions"] += [reference.labels[i]["label_id"] for i in range(11, 24)]
            write(path, exclusions)
            cohort = SyntheticCohort(root / "cohort", 6)
            cohort.replace_field_with_skipped(0, "Alice <alice@example.org>")
            cohort.outputs[1]["luna"]["fromCache"] = False
            cohort.attempts.append({"uuid": str(uuid.uuid4()), "jobUuid": cohort.job_uuid,
                                   "cacheKey": cohort.outputs[1]["luna"]["cacheKey"], "state": "succeeded"})
            cohort.save()
            argv = reference.argv("cohort-output") + [
                "--cohort-plan", str(cohort.root / "plan.json"), "--cohort-plan-sha256", cohort.plan_sha,
                "--cohort-comparison", str(cohort.comparison), "--cohort-comparison-sha256",
                digest(cohort.comparison / "manifest.json")]
            fitted = []
            original = study.load_trainer

            class SyntheticModel:
                weights = {}
                def decode(self, features):
                    return ["O"] * len(features)
                def to_json(self):
                    return {"synthetic": True, "weights": self.weights}

            def trainer_with_observed_fit(snapshot):
                trainer = original(snapshot)
                def fit(examples, roles, *args, **kwargs):
                    fitted.append((tuple(example.key for example in examples), tuple(roles)))
                    self.assertEqual(len(examples), 84)
                    self.assertFalse(set(cohort.authenticated().labels) & {example.key for example in examples})
                    self.assertFalse(any("New cohort actor" in example.text for example in examples))
                    return {role: SyntheticModel() for role in roles}
                trainer.fit_models = fit
                trainer.fit_logistic_models = fit
                return trainer

            with patch.object(study, "load_trainer", side_effect=trainer_with_observed_fit), \
                 patch.object(study, "training_environment", return_value={"synthetic": True}), \
                 redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                self.assertEqual(study.main(argv), 0)
            self.assertEqual(len(fitted), 2, "one fit for each algorithm, not one per new partition/fold")
            self.assertEqual(fitted[0], fitted[1])
            output = root / "cohort-output"
            value = json.loads((output / "report.json").read_text())
            predictions = study.jsonl((output / "predictions.jsonl").read_bytes())
            self.assertEqual(value["fields"], 6)
            self.assertEqual(value["full_resolved_priority"]["methods"]["luna"]["true_positive"], 5)
            self.assertEqual(value["full_resolved_priority"]["methods"]["luna"]["unavailable_fields"], 1)
            self.assertEqual({row["label_id"] for row in predictions}, set(cohort.authenticated().labels))
            self.assertEqual(value["training_reference"]["fit_fields"], 84)
            self.assertFalse(value["training_reference"]["new_cohort_labels_used_for_fitting"])
            self.assertEqual(value["cohort"]["artist_partition"], "test")
            self.assertEqual(value["coverage"]["fields"]["contact_excluded"], 1)
            self.assertEqual(value["coverage"]["fields"]["attempted_in_job"], 1)
            self.assertEqual(value["coverage"]["attempts_in_job"], 1)
            contact = next(row for row in predictions if row["label_id"] == cohort.labels[0]["label_id"])
            self.assertIsNone(contact["methods"]["luna"])
            self.assertEqual(contact["luna_unavailable_reason"], "contact_scope")
            self.assertFalse(any(output.glob("models-fold-*")))
            self.assertTrue((output / "models-frozen-development.json").exists())

    def test_fresh_cohort_primary_cannot_bypass_replay_request_or_response_authentication(self):
        alterations = ("uncached_replay", "different_replay_key", "different_replay_answer",
                       "missing_request", "missing_response")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            reference = SyntheticStudy(root)
            for alteration in alterations:
                with self.subTest(alteration=alteration):
                    cohort = SyntheticCohort(root / alteration)
                    output = cohort.outputs[0]
                    output["luna"]["fromCache"] = False
                    cohort.attempts.append({"uuid": str(uuid.uuid4()), "jobUuid": cohort.job_uuid,
                                           "cacheKey": output["luna"]["cacheKey"], "state": "succeeded"})
                    if alteration == "uncached_replay":
                        output["cacheReplay"]["fromCache"] = False
                    elif alteration == "different_replay_key":
                        output["cacheReplay"]["cacheKey"] = "f" * 64
                    elif alteration == "different_replay_answer":
                        output["cacheReplay"]["extraction"] = {"credits": [], "unresolved": []}
                    elif alteration == "missing_request":
                        cohort.requests = [value for value in cohort.requests
                                           if value["cacheKey"] != output["luna"]["cacheKey"]]
                    else:
                        cohort.requests[0]["responseJson"] = None
                    cohort.save()
                    argv = reference.argv("rejected-" + alteration) + [
                        "--cohort-plan", str(cohort.root / "plan.json"), "--cohort-plan-sha256", cohort.plan_sha,
                        "--cohort-comparison", str(cohort.comparison), "--cohort-comparison-sha256",
                        digest(cohort.comparison / "manifest.json")]
                    error = io.StringIO()
                    with patch.object(study, "training_environment", return_value={"synthetic": True}), \
                         patch.object(study, "fit_cohort_reference") as fit, \
                         redirect_stdout(io.StringIO()), redirect_stderr(error):
                        self.assertEqual(study.main(argv), 1)
                    fit.assert_not_called()
                    self.assertIn("Successful", error.getvalue())
                    self.assertFalse((root / ("rejected-" + alteration)).exists())

    def test_legacy100_still_requires_primary_cached_success(self):
        with tempfile.TemporaryDirectory() as directory:
            reference = SyntheticStudy(Path(directory))
            reference.outputs[0]["luna"]["fromCache"] = False
            reference.save()
            error = io.StringIO()
            with patch.object(study, "training_environment", return_value={"synthetic": True}), \
                 redirect_stdout(io.StringIO()), redirect_stderr(error):
                self.assertEqual(study.main(reference.argv()), 1)
            self.assertIn("Successful answer lacks equal key-free cached replay", error.getvalue())
            self.assertFalse((reference.root / "output").exists())

    def test_wrong_reference_count_fails_before_any_fit(self):
        reference = SimpleNamespace(examples=[SimpleNamespace(complete=True) for _ in range(83)])
        reference.trainer = SimpleNamespace(fit_models=lambda *args: self.fail("unexpected fit"))
        with self.assertRaisesRegex(ValueError, "84-field"):
            study.fit_cohort_reference(reference, SimpleNamespace())

    def test_exposed_old_excluded_id_cannot_be_used_as_tune_or_untouched(self):
        with tempfile.TemporaryDirectory() as directory:
            reference = SimpleNamespace(labels={"old-excluded": {}})
            for purpose in ("tune", "untouched_evaluation"):
                inputs = SimpleNamespace(labels={"old-excluded": {}},
                                         cohort=SimpleNamespace(plan={"purpose": purpose}))
                with patch.object(study, "MatchedFields", return_value=reference), \
                     patch.object(study, "CohortFields", return_value=inputs), \
                     patch.object(study, "fit_cohort_reference") as fit:
                    with self.assertRaisesRegex(ValueError, "already exposed"):
                        study.run_cohort(SimpleNamespace(output=Path(directory) / "new"))
                    fit.assert_not_called()


if __name__ == "__main__":
    unittest.main()
