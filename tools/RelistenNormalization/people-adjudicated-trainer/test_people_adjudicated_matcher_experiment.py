"""Synthetic boundary tests for combined people identity experiments."""

import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import people_adjudicated_matcher_experiment as adapter


def repository_root():
    candidates = [Path(os.environ["RELISTEN_REPOSITORY"])] if "RELISTEN_REPOSITORY" in os.environ else []
    candidates.extend([Path.cwd(), *Path.cwd().parents])
    for root in candidates:
        if (root / "tools/RelistenNormalization/people_identity_matcher_experiment.py").is_file():
            return root
    raise RuntimeError("Run from the repository or set RELISTEN_REPOSITORY")


def comparison(values=(1.0, 1, 0, 0, 0)):
    return {"domain": "people", "candidateUuid": None, "policyVersion": None,
        "evaluationReceiptUuid": None, "disposition": "needs_evidence", "isCandidate": True,
        "nameTokenSimilarity": values[0], "features": [
            {"name": name, "agreement": "agrees" if value else "unknown", "reason": "synthetic source fact"}
            for name, value in zip(("name", "artist_context", "actor_contact", "actor_profile"), values[1:])],
        "supportedRules": [], "proposedRule": None}


def endpoint(key, evidence, quote="Ada", start=0):
    selector = {"kind": "actor_name", "field_path": "/raw_record/row/taper", "field_sha256": "a" * 64,
                "span": {"start": start, "end": start + len(quote), "quote": quote, "unit": "unicode_code_points"}}
    return {"endpoint_id": key, "evidence_id": evidence, "artist_uuid": "artist-" + evidence,
            "selectors": [selector], "mention_kind": "name"}


def diagnostic(value, reference=None, status="name_only", issue=None):
    evidence = adapter.selector_evidence(value["selectors"][0])
    actor = ({"evidenceId": value["evidence_id"], "field": "taper", "referenceUuid": reference}
             if reference is not None else None)
    return {"endpointId": value["endpoint_id"], "evidenceId": value["evidence_id"], "artistUuid": value["artist_uuid"],
        "status": status, "projectionIssue": issue,
        "selectors": [{"selectorIndex": 0, "kind": "actor_name", "evidence": evidence,
                       "status": "matched" if status == "projected" else status, "issues": []}],
        "variants": [{"selectorIndex": 0, "actorReference": actor, "name": evidence["quote"],
                      "attachment": None, "projection": {}, "facts": None}]}


def question(key, focus="a", candidates=None):
    return {"task_id": key, "task_kind": "cross_recording_identity", "focus_endpoint_id": focus,
            "candidate_endpoint_ids": ["b"] if candidates is None else candidates,
            "split_group_ids": ["task-source:" + key]}


def baseline(q, endpoints, values=(1.0, 1, 0, 0, 0), supported=True):
    focus, candidate = endpoints[q["focus_endpoint_id"]], endpoints[q["candidate_endpoint_ids"][0]]
    return {"taskId": q["task_id"], "taskKind": q["task_kind"], "focusEndpointId": focus["endpoint_id"],
        "candidateEndpointId": candidate["endpoint_id"], "focusEvidenceId": focus["evidence_id"],
        "candidateEvidenceId": candidate["evidence_id"], "sameRecording": focus["evidence_id"] == candidate["evidence_id"],
        "declaredCandidateCount": len(q["candidate_endpoint_ids"]), "features": list(values) if supported else None,
        "comparison": comparison(values) if supported else None, "projectionSupported": supported,
        "projectionIssue": None if supported else "focus_source_actor_not_found"}


def target(q, decision):
    return {"task_id": q["task_id"], "candidate_endpoint_id": q["candidate_endpoint_ids"][0],
            "decision": decision, "label": adapter.TARGETS[decision], "annotation_status": "original_draft"}


class TestCombinedPeopleExperiment(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.experiment_path = repository_root() / "tools/RelistenNormalization/people_identity_matcher_experiment.py"
        cls.experiment = adapter.import_experiment(cls.experiment_path,
            hashlib.sha256(cls.experiment_path.read_bytes()).hexdigest())

    def setUp(self):
        self.endpoints = {"a": endpoint("a", "source-a"), "b": endpoint("b", "source-b")}
        self.diagnostics = {key: diagnostic(value) for key, value in self.endpoints.items()}

    def build(self, questions, rows=None, bridges=None, groups=None):
        questions = {q["task_id"]: q for q in questions}
        rows = rows if rows is not None else [baseline(q, self.endpoints) for q in questions.values()
                                             if q["candidate_endpoint_ids"]]
        groups = groups if groups is not None else [{"taskIds": [key]} for key in questions]
        return adapter.source_edges(self.experiment, questions, self.endpoints, self.diagnostics,
                                    rows, groups, bridges or [])

    def unit_edges(self, decisions, values=None):
        questions = [question("q" + str(index)) for index in range(len(decisions))]
        rows = [baseline(q, self.endpoints, values[index] if values else (1.0, 1, 0, 0, 0))
                for index, q in enumerate(questions)]
        edges, components = self.build(questions, rows)
        adapter.join_targets(edges, [target(q, decision) for q, decision in zip(questions, decisions)])
        return edges, components, *adapter.fit_units(self.experiment, edges)

    def test_reversed_pair_is_one_fit_unit_and_grouped_before_answers(self):
        first, reverse = question("forward"), question("reverse", "b", ["a"])
        edges, components = self.build([first, reverse])
        self.assertEqual(components, [["forward", "reverse"]])
        adapter.join_targets(edges, [target(first, "same_entity"), target(reverse, "same_entity")])
        units, records = adapter.fit_units(self.experiment, edges)
        self.assertEqual(len(units), 1)
        self.assertEqual(records[0]["declaredRows"], 2)
        self.assertEqual(records[0]["status"], "eligible")

    def test_unknown_duplicate_keeps_unknown_row_and_adds_no_fit_weight(self):
        edges, _, units, records = self.unit_edges(["same_entity", "insufficient_evidence", "same_entity"])
        self.assertEqual(len(units), 1)
        self.assertEqual([edge.target for edge in edges], [1, None, 1])
        self.assertEqual(records[0]["unknownRows"], 1)

    def test_conflicting_targets_exclude_pair_without_preferred_annotation(self):
        _, _, units, records = self.unit_edges(["same_entity", "different_entities"])
        self.assertEqual(units, [])
        self.assertEqual(records[0]["status"], "conflicting_resolved_targets")
        self.assertEqual(records[0]["resolvedLabels"], [0, 1])

    def test_distinct_feature_views_are_visible_and_not_averaged_or_selected(self):
        _, _, units, records = self.unit_edges(["same_entity", "same_entity"],
                                              [(1.0, 1, 0, 0, 0), (0.6, 0, 0, 0, 0)])
        self.assertEqual(units, [])
        self.assertEqual(records[0]["status"], "multiple_feature_views")
        self.assertEqual(records[0]["distinctFeatureViews"], 2)

    def test_actor_reference_uuid_alone_cannot_deduplicate_across_sources(self):
        reference = "11111111-1111-1111-1111-111111111111"
        diagnostics = {key: diagnostic(value, reference, "projected") for key, value in self.endpoints.items()}
        keys, _ = adapter.endpoint_keys(self.endpoints, diagnostics)
        self.assertNotEqual(keys["a"], keys["b"])
        self.assertEqual(keys["a"][1:3], ("source-a", "taper"))

    def test_source_alias_endpoints_share_scoped_actor_key(self):
        alias = endpoint("alias", "source-a", "A", 20)
        endpoints = {"a": self.endpoints["a"], "alias": alias}
        reference = "11111111-1111-1111-1111-111111111111"
        diagnostics = {key: diagnostic(value, reference, "projected") for key, value in endpoints.items()}
        keys, _ = adapter.endpoint_keys(endpoints, diagnostics)
        self.assertEqual(keys["a"], keys["alias"])

    def test_incomplete_feature_context_does_not_erase_exact_source_actor_key(self):
        reference = "11111111-1111-1111-1111-111111111111"
        row = diagnostic(self.endpoints["a"], reference, "projected", "context_query_incomplete")
        keys, _ = adapter.endpoint_keys({"a": self.endpoints["a"]}, {"a": row})
        self.assertEqual(keys["a"], ("source_actor", "source-a", "taper", reference))

    def test_ambiguous_actor_variants_do_not_select_first_reference(self):
        first = diagnostic(self.endpoints["a"], "11111111-1111-1111-1111-111111111111", "ambiguous", "multiple_source_actors")
        second = copy.deepcopy(first["variants"][0])
        second["actorReference"]["referenceUuid"] = "22222222-2222-2222-2222-222222222222"
        first["variants"].append(second)
        keys, references = adapter.endpoint_keys({"a": self.endpoints["a"]}, {"a": first})
        self.assertEqual(keys["a"][0], "source_selector")
        self.assertEqual(len(references["a"]), 2)

    def test_unknown_and_actor_kind_tasks_remain_in_source_groups(self):
        identity, actor = question("identity"), question("actor", candidates=[])
        actor["task_kind"] = "actor_kind"
        edges, components = self.build([identity, actor], groups=[{"taskIds": ["actor", "identity"]}])
        adapter.join_targets(edges, [target(identity, "insufficient_evidence")])
        self.assertEqual(components, [["actor", "identity"]])
        self.assertIsNone(edges[0].target)

    def test_shared_unused_inventory_endpoints_allow_empty_bridge_tasks(self):
        ref = ("unused-source", "taper", "11111111-1111-1111-1111-111111111111")
        q = question("q", candidates=[])
        q["task_kind"] = "actor_kind"
        groups, _ = adapter.components_for({"q": q}, [{"taskIds": ["q"]}],
            [{"reference": dict(zip(("evidenceId", "field", "referenceUuid"), ref)),
              "endpointIds": ["unused-a", "unused-b"], "taskIds": []}],
            {"unused-a": {ref}, "unused-b": {ref}}, {})
        self.assertEqual(groups, [["q"]])

    def test_foreign_actor_bridge_members_fail(self):
        ref = ("source-a", "taper", "11111111-1111-1111-1111-111111111111")
        q = question("q")
        with self.assertRaisesRegex(ValueError, "incomplete or foreign"):
            adapter.components_for({"q": q}, [{"taskIds": ["q"]}],
                [{"reference": dict(zip(("evidenceId", "field", "referenceUuid"), ref)),
                  "endpointIds": ["a", "foreign"], "taskIds": ["q"]}],
                {"a": {ref}, "unused-a": {ref}}, {})

    def test_unsupported_projection_is_retained_without_zero_features(self):
        self.diagnostics["a"]["status"] = "not_found"
        self.diagnostics["a"]["projectionIssue"] = "source_actor_not_found"
        q = question("q")
        edges, _ = self.build([q], [baseline(q, self.endpoints, supported=False)])
        adapter.join_targets(edges, [target(q, "same_entity")])
        units, records = adapter.fit_units(self.experiment, edges)
        self.assertEqual(edges[0].values, ())
        self.assertEqual(units, [])
        self.assertEqual(records[0]["status"], "unsupported_projection_only")

    def test_target_change_cannot_change_features_pair_key_or_source_components(self):
        edges, components = self.build([question("q1"), question("q2")])
        before = [(row.values, row.pair_id, row.component) for row in edges]
        adapter.join_targets(edges, [target(question("q1"), "same_entity"), target(question("q2"), "same_entity")])
        adapter.join_targets(edges, [target(question("q1"), "different_entities"), target(question("q2"), "insufficient_evidence")])
        self.assertEqual(before, [(row.values, row.pair_id, row.component) for row in edges])
        self.assertEqual(components, [["q1", "q2"]])

    def test_partial_candidate_pool_cannot_disappear_from_predictions(self):
        q = question("q", candidates=["a", "b"])
        with self.assertRaisesRegex(ValueError, "coverage changed"):
            self.build([q], [baseline(q, self.endpoints)])

    def test_forged_feature_vector_does_not_override_actual_comparison(self):
        q = question("q")
        row = baseline(q, self.endpoints)
        row["features"][0] = 0.1
        with self.assertRaisesRegex(ValueError, "actual C#"):
            self.build([q], [row])

    def test_unknown_target_cannot_be_encoded_as_negative(self):
        q = question("q")
        edges, _ = self.build([q])
        answer = target(q, "insufficient_evidence")
        answer["label"] = 0
        with self.assertRaisesRegex(ValueError, "unknown is not negative"):
            adapter.join_targets(edges, [answer])

    def test_adjudication_hash_covers_original_row_with_lone_final_cr(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            raw = b'{"question_id":"q","decision":"same_entity"}\r'
            path = root / "adjudications.jsonl"
            path.write_bytes(raw)
            sha = hashlib.sha256(raw).hexdigest()
            receipt = root / "receipt.json"
            receipt.write_text(json.dumps({"state": "validated_adjudication_files",
                "version": "people-identity-review-validation:3", "accepted_data_writes": 0,
                "policy_activation_permitted": False, "inputs": [{"sha256": sha}]}))
            receipt_sha = hashlib.sha256(receipt.read_bytes()).hexdigest()
            package = {"inputs": [self.experiment.pin(path), self.experiment.pin(receipt)]}
            target_row = {"task_id": "q", "candidate_endpoint_id": "b", "decision": "same_entity", "label": 1,
                "annotation_status": "agent_adjudicated_development", "adjudication_sha256": sha,
                "adjudication_row_sha256": sha, "validation_receipt_sha256": receipt_sha}
            adapter.authenticate_targets(adapter.Inputs(self.experiment), package, [target_row])
            target_row["adjudication_row_sha256"] = hashlib.sha256(raw[:-1]).hexdigest()
            with self.assertRaisesRegex(ValueError, "physical row"):
                adapter.authenticate_targets(adapter.Inputs(self.experiment), package, [target_row])

    def test_empty_bridge_artifact_is_a_valid_bounded_export(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "source-bridges.jsonl"
            path.write_bytes(b"")
            self.assertEqual(adapter.Inputs(self.experiment).rows(path, allow_empty=True), [])
            with self.assertRaisesRegex(ValueError, "bounded JSONL"):
                adapter.Inputs(self.experiment).rows(path)

    def test_actual_fitting_holds_out_duplicate_units_and_scores_all_rows(self):
        edges = []
        for index, value in enumerate((0, 1, 0, 1)):
            pair = "pair-" + str(index)
            values = (0.1 + 0.2 * index, int(value), 0, int(index == 3), 0)
            edge = adapter.DeclaredEdge("edge-" + str(index), "q" + str(index), "candidate", "focus",
                "cross_recording_identity", "q" + str(index), pair, value, values,
                False, False, comparison(values), True, None, {"annotation_status": "agent_adjudicated_development"})
            edges.append(edge)
        duplicate = copy.deepcopy(edges[0])
        duplicate.edge_id = "edge-duplicate"
        duplicate.target = None
        edges.append(duplicate)
        units, records = adapter.fit_units(self.experiment, edges)
        models, folds, predictions, primary, final = adapter.compare(self.experiment, edges,
            [["q" + str(index)] for index in range(4)], units)
        self.assertEqual(len(predictions), 5)
        self.assertEqual(len(primary), 4)
        self.assertEqual(len(final), 5)
        self.assertIsNone(next(row for row in predictions if row["edgeId"] == "edge-duplicate")["target"])
        for fold in folds:
            self.assertNotIn("pair-" + fold["component"][1:], fold["trainingUnitIds"])
            self.assertEqual(fold["state"], "held_component_fit")
        self.assertEqual(len(models["logistic"]["final"]["trainingEdgeIds"]), 4)
        metrics = adapter.primary_metrics(primary, records)
        self.assertEqual(metrics["logistic"]["scoredIndependentUnits"], 4)

    def test_one_class_fold_is_explicit_and_never_scores_with_its_held_target(self):
        edges = [adapter.DeclaredEdge("edge", "q", "candidate", "focus", "cross_recording_identity", "q",
            "pair", 1, (1.0, 1, 0, 0, 0), False, False, comparison(), True, None,
            {"annotation_status": "original_draft"})]
        units, _ = adapter.fit_units(self.experiment, edges)
        _, folds, predictions, primary, _ = adapter.compare(self.experiment, edges, [["q"]], units)
        self.assertEqual(folds[0]["state"], "unscorable_one_class_training")
        self.assertIsNone(predictions[0]["scores"]["logistic"]["rawLogit"])
        self.assertIsNone(primary[0]["scores"]["boosted_tree"])

    def test_modified_retained_copy_prevents_completion_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            original = root / "input"
            original.write_bytes(b"frozen source")
            inputs = adapter.Inputs(self.experiment)
            inputs.pin(original)
            output = root / "output"
            real_retain = self.experiment.retain_replay_inputs

            def retain_then_tamper(*args):
                records = real_retain(*args)
                (output / records[0]["retainedPath"]).write_bytes(b"tampered copy")
                return records

            versions = {key: "synthetic" for key in ("version", "projectionVersion", "actorFeaturesVersion",
                "creditExtractorVersion", "actorEvidenceVersion", "actorProjectionVersion", "sourceContextVersion", "sourceReferenceVersion")}
            with patch.object(self.experiment, "retain_replay_inputs", side_effect=retain_then_tamper):
                with self.assertRaisesRegex(ValueError, "Changed input"):
                    adapter.run(self.experiment, inputs, [], [], versions, output, {})
            self.assertFalse((output / "report.json").exists())


if __name__ == "__main__":
    unittest.main()
