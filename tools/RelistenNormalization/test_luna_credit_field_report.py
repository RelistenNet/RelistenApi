"""Behavioral checks for the offline, priority-role development comparison."""

import copy
from collections import Counter
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import uuid

import luna_credit_field_report as report


def extraction(name="Renée 🌿", role="taper", field="taper"):
    return {"credits": [{"name": name, "role": role, "evidence": {
        "field": field, "jsonPointer": "/" + field.replace("~", "~0").replace("/", "~1"),
        "quote": name, "startCodePoint": 0, "endCodePoint": len(name)}}],
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


class SyntheticCohort:
    """A complete declared population; all inputs and provider receipts are synthetic."""

    def __init__(self, root, count=3, purpose="untouched_evaluation", *, field_texts=None):
        self.root, self.count = root, count
        root.mkdir(parents=True)
        self.labels, self.fields, self.items, self.outputs, self.scopes, self.requests = [], [], [], [], [], []
        self.attempts = []
        self.job_uuid = str(uuid.uuid4())
        partition = {"development": "development", "tune": "validation", "untouched_evaluation": "test"}[purpose]
        for index in range(count):
            source = str(uuid.uuid5(uuid.NAMESPACE_URL, "new-cohort-source-" + str(index)))
            artist = str(uuid.uuid5(uuid.NAMESPACE_URL, "new-cohort-artist-" + str(index)))
            key = "credit-field:" + source + ":taper"
            text = "New cohort actor " + str(index) if field_texts is None else field_texts[index]
            digest = report.sha256(text.encode())
            self.labels.append({"label_id": key, "task_id": "extract:source_sample:" + source,
                "evidence_id": "source_sample:" + source, "scope": "complete_single_field",
                "status": "draft_development" if purpose == "development" else "adjudicated",
                "artist_partition": partition, "family_partition": partition,
                "artist": {"uuid": artist}, "input_duplicate_family": "new-family-" + source,
                "known_recording_family": "new-recording-" + source,
                "resource": {"provider": "relisten_catalog_snapshot", "resource_type": "source", "resource_uuid": source},
                "field_path": "/raw_record/row/taper", "field_sha256": digest, "field_codepoints": len(text),
                "answer": {"exhaustive": True, "unresolved": False, "unknowns": [],
                    "adjudicator": "synthetic-reviewer", "assertions": [{"role": "Taper",
                        "name": {"start": 0, "end": len(text), "quote": text},
                        "role_basis": {"kind": "structured_field", "field": "taper"}}]}})
            self.fields.append({"label_id": key, "evidence_id": "source_sample:" + source,
                "field_path": "/raw_record/row/taper", "field_sha256": digest, "field_text": text})
            self.items.append({"labelId": key, "taskId": "extract:source_sample:" + source,
                "evidenceId": "source_sample:" + source, "sourceUuid": source, "artistUuid": artist,
                "field": "taper", "text": text, "fieldSha256": digest, "noText": False})
            self.scopes.append({"label_id": key, "evidence_id": "source_sample:" + source,
                "source_uuid": source, "artist_uuid": artist, "field": "taper", "field_sha256": digest,
                "artist_partition": partition, "family_partition": partition,
                "provider_origin": {"provider": "unknown", "kind": "unknown",
                                    "evidence_sha256": None, "resource_key_sha256": None}})
            request = json.dumps({"model": "gpt-6-luna", "input": [
                {"role": "system", "content": "Extract luna-recording-credits-v2"},
                {"role": "user", "content": json.dumps({"fields": {"taper": text}, "context": None})}]})
            cache_key = report.sha256(request.encode())
            answer = extraction(text)
            luna = {"state": "extracted", "extraction": answer, "cacheKey": cache_key, "fromCache": True}
            self.outputs.append({"labelId": key, "state": "extracted", "local": answer,
                "luna": luna, "cacheReplay": dict(luna), "currentRouting": {"state": "local", "extraction": answer}})
            self.requests.append({"cacheKey": cache_key, "renderedRequest": request,
                "responseJson": json.dumps({"credits": [{"name": text, "role": "taper", "field": "taper",
                    "quote": text, "start": 0, "end": len(text)}]})})
        self.plan = {"schema_version": "luna-credit-cohort-job-v1", "purpose": purpose,
            "field_count": count, "cap_usd": 30, "maximum_total_attempts": 1200,
            "artist_partition": partition, "family_partition": partition}
        self.save()

    def save(self):
        def data(name, value, jsonl=False):
            raw = ("".join(json.dumps(row) + "\n" for row in value) if jsonl else json.dumps(value)).encode()
            path = self.root / name
            path.write_bytes(raw)
            return {"path": name, "sha256": report.sha256(raw)}
        self.plan["labels"] = data("labels.jsonl", self.labels, True)
        self.plan["fields"] = data("fields.jsonl", self.fields, True)
        self.scope = {"schema_version": "credit-cohort-scope-v1", "items": self.scopes}
        self.plan["scope"] = data("scope.json", self.scope)
        self.plan["receipt"] = data("receipt.json", {"schema_version": "credit-field-cohort-v1",
            "status": "frozen_cohort", "purpose": self.plan["purpose"], "fields": self.count,
            "labels_sha256": self.plan["labels"]["sha256"], "raw_fields_sha256": self.plan["fields"]["sha256"],
            "scope_sha256": self.plan["scope"]["sha256"], "labels_frozen_before_predictions": True})
        data("plan.json", self.plan)
        self.plan_sha = report.sha256((self.root / "plan.json").read_bytes())
        self.selection = {"labelsSha256": self.plan["labels"]["sha256"], "fieldsSha256": self.plan["fields"]["sha256"],
            "receiptSha256": self.plan["receipt"]["sha256"], "items": self.items,
            "cohort": {"planSha256": self.plan_sha, "plan": self.plan, "scope": self.scope}}
        self.comparison = self.root / "comparison"
        self.comparison.mkdir(exist_ok=True)
        (self.comparison / "selection.json").write_text(json.dumps(self.selection))
        results = "".join(json.dumps(row) + "\n" for row in self.outputs).encode()
        receipts = json.dumps({"requests": self.requests, "attempts": self.attempts}).encode()
        (self.comparison / "results.jsonl").write_bytes(results)
        (self.comparison / "receipts.json").write_bytes(receipts)
        self.manifest = {"version": "luna-credit-cohort-v1", "state": "completed", "fields": self.count,
            "selection": self.selection, "states": dict(Counter(row["state"] for row in self.outputs)), "options": {"model": "gpt-6-luna"},
            "resultsSha256": report.sha256(results), "receiptsSha256": report.sha256(receipts),
            "extractorAssemblySha256": "a" * 64, "runnerAssemblySha256": "b" * 64,
            "jobUuid": self.job_uuid, "job": {"uuid": self.job_uuid, "capUsd": 30, "accountedUsd": 0},
            "paidAttempts": len(self.attempts),
            "lunaFromCache": sum(row["luna"] is not None and row["luna"].get("fromCache") is True
                                 for row in self.outputs),
            "keylessReplayFromCache": sum(row["cacheReplay"] is not None
                                           and row["cacheReplay"].get("fromCache") is True for row in self.outputs)}
        (self.comparison / "manifest.json").write_text(json.dumps(self.manifest))

    def authenticated(self):
        return report.CohortPlan(self.root / "plan.json", self.plan_sha)

    def replace_field_with_skipped(self, index, text):
        previous_key = self.outputs[index]["luna"]["cacheKey"]
        self.requests = [request for request in self.requests if request["cacheKey"] != previous_key]
        digest = report.sha256(text.encode())
        self.labels[index].update(field_sha256=digest, field_codepoints=len(text))
        self.fields[index].update(field_text=text, field_sha256=digest)
        self.items[index].update(text=text, fieldSha256=digest, noText=not text.strip())
        self.scopes[index]["field_sha256"] = digest
        local = {"credits": [], "unresolved": []}
        self.labels[index]["answer"]["assertions"] = []
        if text:
            name = text.split()[0]
            local = extraction(name)
            self.labels[index]["answer"]["assertions"] = [{"role": "Taper",
                "name": {"start": 0, "end": len(name), "quote": name},
                "role_basis": {"kind": "structured_field", "field": "taper"}}]
        contact = report.contact_bearing(text)
        self.outputs[index].update(state="not_requested_contact_scope" if contact else "no_text",
            local=local, luna=None, cacheReplay=None,
            currentRouting={"state": "not_evaluated" if contact else "local", "extraction": local,
                            "reason": "contact_scope" if contact else "no_text"})
        self.save()


class CohortComparisonTests(unittest.TestCase):
    def test_all_600_declared_fields_are_scored_without_relabeling_test_partition(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "cohort", 600)
            cohort = fixture.authenticated()
            value = report.compare(cohort.labels_bytes, fixture.selection, fixture.outputs, cohort_plan=cohort)
            self.assertEqual(value["fields"], 600)
            self.assertEqual(value["methods"]["luna"]["true_positive"], 600)
            self.assertEqual(value["cohort"]["purpose"], "untouched_evaluation")
            self.assertEqual(value["cohort"]["artist_partition"], "test")
            self.assertEqual(value["cohort"]["provider_origin_fields"], {"unknown": 600})
            with self.assertRaisesRegex(ValueError, "Held-out"):
                report.compare(cohort.labels_bytes, fixture.selection, fixture.outputs)
            with self.assertRaisesRegex(ValueError, "every frozen field"):
                report.compare(cohort.labels_bytes, fixture.selection, fixture.outputs[:-1], cohort_plan=cohort)

    def test_plan_scope_labels_and_profile_cannot_silently_change_population(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "cohort")
            with self.assertRaisesRegex(ValueError, "approved digest"):
                report.CohortPlan(fixture.root / "plan.json", "f" * 64)
            cohort = fixture.authenticated()
            changed = copy.deepcopy(fixture.selection)
            changed["cohort"]["scope"]["items"].pop()
            with self.assertRaisesRegex(ValueError, "another cohort"):
                report.compare(cohort.labels_bytes, changed, fixture.outputs, cohort_plan=cohort)
            fixture.scopes[0]["provider_origin"]["provider"] = "archive.org"
            fixture.save()
            with self.assertRaisesRegex(ValueError, "provenance"):
                fixture.authenticated()

    def test_native_provider_fields_retain_distinct_plaintext_and_exact_origins_through_report(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            texts = ["Marin Astor", "Corin Vale", "Renée River"]
            fixture = SyntheticCohort(root / "native", field_texts=texts)
            for label, scope, provider in zip(fixture.labels, fixture.scopes,
                                              ("archive.org", "local", "phish.in")):
                label["resource"]["provider"] = "relisten_retained_provider_field"
                scope["provider_origin"] = {
                    "provider": provider, "kind": "retained_provider_input",
                    "evidence_sha256": report.sha256(("synthetic native capture:" + provider).encode()),
                    "resource_key_sha256": report.sha256(("synthetic native resource:" + provider).encode())}
            fixture.save()
            cohort = fixture.authenticated()
            self.assertEqual([item["field_text"] for item in cohort.fields.values()], texts)
            output = root / "report"

            value = report.run(fixture.root / "labels.jsonl", fixture.comparison, output,
                cohort_plan=fixture.root / "plan.json", cohort_plan_sha256=fixture.plan_sha,
                comparison_sha256=report.sha256((fixture.comparison / "manifest.json").read_bytes()))

            self.assertEqual(value["fields"], 3)
            self.assertEqual(value["methods"]["luna"]["true_positive"], 3)
            self.assertEqual(value["cohort"]["provider_origin_fields"],
                             {"archive.org": 1, "local": 1, "phish.in": 1})
            self.assertEqual(value["coverage"]["fields"]["selected"], 3)
            self.assertTrue((output / "report.json").exists())

    def test_native_namespace_refuses_unretained_or_incomplete_origins_even_with_refreshed_hashes(self):
        mutations = ("unknown", "import_execution", "missing_evidence", "missing_resource",
                     "malformed_evidence", "malformed_resource")
        with tempfile.TemporaryDirectory() as directory:
            for mutation in mutations:
                with self.subTest(origin=mutation):
                    fixture = SyntheticCohort(Path(directory) / mutation,
                                              field_texts=["Aster Reed", "Corin Vale", "Mira Brook"])
                    fixture.labels[0]["resource"]["provider"] = "relisten_retained_provider_field"
                    origin = {"provider": "archive.org", "kind": "retained_provider_input",
                              "evidence_sha256": "a" * 64, "resource_key_sha256": "b" * 64}
                    if mutation == "unknown":
                        origin = {"provider": "unknown", "kind": "unknown",
                                  "evidence_sha256": None, "resource_key_sha256": None}
                    elif mutation == "import_execution":
                        origin["kind"] = "import_execution"
                    elif mutation.startswith("missing"):
                        origin["evidence_sha256" if mutation == "missing_evidence"
                               else "resource_key_sha256"] = None
                    else:
                        origin["evidence_sha256" if mutation == "malformed_evidence"
                               else "resource_key_sha256"] = "Not a SHA-256"
                    fixture.scopes[0]["provider_origin"] = origin
                    fixture.save()
                    with self.assertRaisesRegex(ValueError, "provenance"):
                        fixture.authenticated()

    def test_catalog_namespace_still_allows_explicit_unknown_and_import_execution_origins(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "catalog")
            fixture.scopes[1]["provider_origin"] = {
                "provider": "local", "kind": "import_execution",
                "evidence_sha256": "a" * 64, "resource_key_sha256": "b" * 64}
            fixture.save()

            cohort = fixture.authenticated()
            value = report.compare(cohort.labels_bytes, fixture.selection, fixture.outputs,
                                   cohort_plan=cohort)

            self.assertEqual(value["fields"], 3)
            self.assertEqual(value["methods"]["luna"]["true_positive"], 3)
            self.assertEqual(value["cohort"]["provider_origin_fields"], {"unknown": 2, "local": 1})

    def test_tune_requires_adjudication_and_a_plan_relative_complete_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "cohort", purpose="tune")
            self.assertEqual(fixture.authenticated().plan["family_partition"], "validation")
            fixture.labels[0]["answer"].pop("adjudicator")
            fixture.save()
            with self.assertRaisesRegex(ValueError, "adjudicator"):
                fixture.authenticated()
            fixture.labels[0]["answer"]["adjudicator"] = "synthetic-reviewer"
            fixture.save()
            fixture.plan["labels"]["path"] = "../labels.jsonl"
            raw = json.dumps(fixture.plan).encode()
            (fixture.root / "plan.json").write_bytes(raw)
            with self.assertRaisesRegex(ValueError, "escapes"):
                report.CohortPlan(fixture.root / "plan.json", report.sha256(raw))

    def test_new_report_requires_the_pinned_completed_manifest_before_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "cohort")
            output = Path(directory) / "report"
            kwargs = {"cohort_plan": fixture.root / "plan.json", "cohort_plan_sha256": fixture.plan_sha}
            with self.assertRaisesRegex(ValueError, "manifest digest"):
                report.run(fixture.root / "labels.jsonl", fixture.comparison, output, **kwargs)
            self.assertFalse(output.exists())
            manifest_sha = report.sha256((fixture.comparison / "manifest.json").read_bytes())
            value = report.run(fixture.root / "labels.jsonl", fixture.comparison, output,
                               comparison_sha256=manifest_sha, **kwargs)
            self.assertEqual(value["fields"], 3)
            self.assertTrue((output / "report.json").exists())

    def test_contact_guard_matches_unicode_letters_numbers_and_two_letter_suffix(self):
        self.assertTrue(report.contact_bearing("Recorded by Zoë <a+1@例.example>"))
        self.assertTrue(report.contact_bearing("联系: 人@域.中国"))
        self.assertFalse(report.contact_bearing("Device A @ B; no contact"))
        self.assertFalse(report.contact_bearing("a@b.²²"))

    def test_label_order_can_differ_but_scope_and_raw_field_order_cannot(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "cohort")
            fixture.labels.reverse()
            fixture.save()
            cohort = fixture.authenticated()
            cohort.check_selection(cohort.labels_bytes, fixture.selection)
            fixture.scopes.reverse()
            fixture.save()
            with self.assertRaisesRegex(ValueError, "ordered population"):
                fixture.authenticated()

    def test_contact_and_no_text_remain_selected_but_are_not_luna_answers(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = SyntheticCohort(Path(directory) / "cohort")
            contact_text = "Alice <alice@example.org>"
            fixture.replace_field_with_skipped(0, contact_text)
            fixture.replace_field_with_skipped(1, "")
            cohort = fixture.authenticated()
            self.assertEqual(cohort.fields[fixture.labels[0]["label_id"]]["field_text"], contact_text)
            value = report.compare(cohort.labels_bytes, fixture.selection, fixture.outputs, cohort_plan=cohort)
            self.assertEqual(value["fields"], 3)
            self.assertEqual(value["methods"]["luna"]["true_positive"], 1)
            self.assertEqual(value["methods"]["luna"]["unavailable_fields"], 1)
            self.assertEqual(value["methods"]["current_routing"]["unavailable_fields"], 1)
            contact = next(row for row in value["diagnostics"] if row["label_id"] == fixture.labels[0]["label_id"])
            self.assertIsNone(contact["predictions"]["luna"])
            self.assertIsNone(contact["predictions"]["current_routing_with_local_on_failure"])
            self.assertTrue(contact["predictions"]["local"])
            job = str(uuid.uuid4())
            key = fixture.outputs[2]["luna"]["cacheKey"]
            coverage = report.cohort_coverage(cohort, report.indexed(fixture.outputs, "labelId"),
                {"attempts": [{"jobUuid": job, "cacheKey": key}]}, {"jobUuid": job})
            self.assertEqual(coverage["fields"], {"selected": 3, "eligible": 1,
                "attempted_in_job": 1, "luna_available": 1, "contact_excluded": 1, "no_text": 1})
            self.assertEqual(coverage["label_ids"]["contact_excluded"], [fixture.labels[0]["label_id"]])


if __name__ == "__main__":
    unittest.main()
