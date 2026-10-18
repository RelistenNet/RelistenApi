"""Synthetic contracts for relationship bags, source views and their denominators."""

import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest.mock import patch

import people_source_view_experiment as views


HERE = Path(__file__).resolve().parent


def repository_root():
    candidates = [Path(os.environ["RELISTEN_REPOSITORY"])] if "RELISTEN_REPOSITORY" in os.environ else []
    candidates.extend([Path.cwd(), *Path.cwd().parents])
    for root in candidates:
        if (root / "tools/RelistenNormalization/people_identity_matcher_experiment.py").is_file():
            return root
    raise RuntimeError("Run from the repository or set RELISTEN_REPOSITORY")


def actor(name, evidence="source-a", field="taper"):
    return {"kind": "source_actor", "evidenceId": evidence, "field": field,
            "referenceUuid": str(uuid.uuid5(uuid.NAMESPACE_URL, name)),
            "anchor": None, "fieldSha256": None}


def comparison(values=(0.8, 1, 0, 1, 0), conflict=False):
    features = [{"name": name, "agreement": "agrees" if value else "unknown", "reason": "synthetic"}
                for name, value in zip(("name", "artist_context", "actor_contact", "actor_profile"), values[1:])]
    if conflict:
        features.append({"name": "actor_kind", "agreement": "conflicts", "reason": "explicit source conflict"})
    return {"domain": "people", "candidateUuid": None, "policyVersion": None, "evaluationReceiptUuid": None,
            "nameTokenSimilarity": values[0], "disposition": "review" if conflict else "needs_evidence",
            "isCandidate": not conflict, "features": features, "supportedRules": [], "proposedRule": None}


def leaf(edge_id, left, right, values=(0.8, 1, 0, 1, 0), conflict=False):
    pair = "full-pair:" + views.hash_value(sorted((left, right), key=views.canonical))
    return views.Leaf(views.canonical([edge_id, left, right]), edge_id, left, right, pair, values,
                      comparison(values, conflict))


def bag(task, pair_list=None, complete=True, target=1, left_coverage=None):
    edge_id = json.dumps([task, "candidate"], separators=(",", ":"))
    row = {"edgeId": edge_id, "taskId": task, "taskKind": "cross_recording_identity",
           "focusEndpointId": "focus", "candidateEndpointId": "candidate", "leftCoverage": left_coverage or
           ("complete" if complete else "partial"), "rightCoverage": "complete", "complete": complete,
           "leftViewCount": len({views.canonical(pair[0]) for pair in pair_list or []}),
           "rightViewCount": len({views.canonical(pair[1]) for pair in pair_list or []}),
           "expectedLeafPairs": len(pair_list or [])}
    result = views.Bag(row, component="component", target=target,
                       provenance={"annotation_status": "agent_adjudicated_development"})
    result.leaves = [leaf(edge_id, *pair) for pair in (pair_list or [])]
    return result


def prepared(base):
    endpoints = {"focus": {"evidence_id": "source-a", "artist_uuid": "artist-a"},
                 "candidate": {"evidence_id": "source-b", "artist_uuid": "artist-b"}}
    return base.PreparedSources(Path("/unused"), "unused", {}, {}, {}, endpoints, {}, [])


def name_endpoint(base, name="a", selector_count=1):
    selectors = [{"kind": "actor_name", "field_path": "/raw_record/row/" + ("taper" if i == 0 else "transferrer"),
        "field_sha256": "a" * 64, "span": {"start": 0, "end": 3, "quote": "Ada", "unit": "unicode_code_points"}}
        for i in range(selector_count)]
    endpoint = {"endpoint_id": name, "evidence_id": "source-a", "artist_uuid": "artist-a", "mention_kind": "name",
                "selectors": selectors}
    diagnostics = {"endpointId": name, "evidenceId": "source-a", "artistUuid": "artist-a", "mentionKind": "name",
        "coverage": "complete", "matchRecordsAgree": True, "issues": [], "selectors": [], "variants": [], "views": []}
    for index, selector in enumerate(selectors):
        anchor = base.selector_evidence(selector)
        projection = {"query": None, "record": {"names": ["Ada"]}, "complete": True}
        diagnostics["selectors"].append({"selectorIndex": index, "kind": "actor_name", "evidence": anchor,
                                         "status": "name_only", "issues": []})
        diagnostics["variants"].append({"selectorIndex": index, "actorReference": None, "name": "Ada",
                                        "attachment": None, "projection": projection, "facts": None})
        diagnostics["views"].append({"key": {"kind": "source_selector", "evidenceId": "source-a", "field": anchor["field"],
            "referenceUuid": None, "anchor": anchor, "fieldSha256": "a" * 64}, "selectorIndexes": [index],
            "projection": projection, "facts": None})
    old = {key: diagnostics[key] for key in ("endpointId", "evidenceId", "artistUuid", "mentionKind", "selectors", "variants")}
    old.update(status="name_only", projectionIssue=None)
    return endpoint, diagnostics, old


def source_alias_endpoint(base):
    endpoint, row, old = name_endpoint(base)
    key = actor("Ada")
    reference = {name: key[name] for name in ("evidenceId", "field", "referenceUuid")}
    assertion = {"version": "source-actor-aliases-v1", "actorReferenceUuid": key["referenceUuid"],
                 "name": "Ada", "aliases": [{"wording": "A", "evidence": []}]}
    row["selectors"][0]["status"] = "matched"
    row["variants"][0].update(actorReference=reference, sourceAssertion=assertion)
    row["views"][0].update(key=key, sourceAssertion=assertion)
    return endpoint, row, old


class TestSourceViews(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = HERE / "people_adjudicated_matcher_experiment.py"
        cls.base = views.load_combined(source, hashlib.sha256(source.read_bytes()).hexdigest())
        experiment = repository_root() / "tools/RelistenNormalization/people_identity_matcher_experiment.py"
        cls.experiment = cls.base.import_experiment(experiment, hashlib.sha256(experiment.read_bytes()).hexdigest())

    def test_overlap_chain_merges_transitively_by_full_pair(self):
        a, b, c, d, right = actor("a"), actor("b"), actor("c"), actor("d"), actor("right", "source-b")
        bags = [bag("first", [(a, right), (b, right)]), bag("second", [(b, right), (c, right)]),
                bag("third", [(c, right), (d, right)])]
        units = views.relationship_units({item.row["edgeId"]: item for item in bags})
        self.assertEqual(len(units), 1)
        self.assertEqual(len(next(iter(units.values()))), 3)

    def test_single_shared_actor_does_not_merge_relationship_units(self):
        shared, first, second = actor("shared"), actor("first", "source-b"), actor("second", "source-b")
        items = [bag("one", [(shared, first)]), bag("two", [(shared, second)])]
        units = views.relationship_units({item.row["edgeId"]: item for item in items})
        self.assertEqual(len(units), 2)

    def test_reversed_full_pair_is_one_unit(self):
        left, right = actor("left"), actor("right", "source-b")
        first, reverse = bag("one", [(left, right)]), bag("two", [(right, left)])
        units = views.relationship_units({item.row["edgeId"]: item for item in (first, reverse)})
        self.assertEqual(len(units), 1)

    def test_naked_uuid_in_different_source_or_field_never_forms_same_pair(self):
        left, right = actor("left"), actor("right", "source-b")
        moved_source, moved_field = copy.deepcopy(left), copy.deepcopy(left)
        moved_source["evidenceId"], moved_field["field"] = "different-source", "description"
        items = [bag("one", [(left, right)]), bag("two", [(moved_source, right)]), bag("three", [(moved_field, right)])]
        self.assertEqual(len(views.relationship_units({item.row["edgeId"]: item for item in items})), 3)

    def test_identical_complete_leaf_vectors_fit_once_even_when_rules_disagree(self):
        left, other, right = actor("left"), actor("other", field="transferrer"), actor("right", "source-b")
        item = bag("one", [(left, right), (other, right)])
        item.leaves[1].comparison = comparison(conflict=True)
        bags = {item.row["edgeId"]: item}
        units = views.relationship_units(bags)
        fit, records = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, units)
        self.assertEqual(len(fit), 1)
        self.assertEqual(records[0]["trainingWeight"], 1)
        self.assertEqual(records[0]["ruleAggregation"], "disagreement_withheld")
        self.assertIsNone(records[0]["aggregateRuleOutcome"])
        metrics = views.metrics(self.base, [], records)
        self.assertEqual(metrics["rules"]["disagreeingRuleUnitsWithheld"], 1)

    def test_differing_numeric_views_do_not_fit_or_choose_a_maximum(self):
        item = bag("one", [(actor("a"), actor("right", "source-b")), (actor("b"), actor("right", "source-b"))])
        item.leaves[1].values = (0.2, 0, 0, 0, 0)
        bags = {item.row["edgeId"]: item}
        units = views.relationship_units(bags)
        fit, records = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, units)
        self.assertEqual(fit, [])
        self.assertEqual(records[0]["status"], "multiple_numeric_views")

    def test_partial_uniform_bag_remains_diagnostic_and_excluded(self):
        item = bag("one", [(actor("a"), actor("right", "source-b"))], complete=False)
        bags = {item.row["edgeId"]: item}
        fit, records = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, views.relationship_units(bags))
        self.assertEqual(fit, [])
        self.assertEqual(records[0]["status"], "incomplete_bag_coverage")
        self.assertEqual(records[0]["leafObservations"], 1)

    def test_within_selector_ambiguity_cannot_be_ignored_because_other_views_exist(self):
        item = bag("one", [(actor("a"), actor("right", "source-b"))], complete=False, left_coverage="ambiguous")
        bags = {item.row["edgeId"]: item}
        fit, records = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, views.relationship_units(bags))
        self.assertEqual(fit, [])
        self.assertEqual(records[0]["status"], "ambiguous_source_selector")

    def test_overlapping_resolved_conflict_excludes_whole_unit_without_precedence(self):
        pair = (actor("a"), actor("right", "source-b"))
        first, second = bag("first", [pair], target=1), bag("second", [pair], target=0)
        bags = {item.row["edgeId"]: item for item in (first, second)}
        fit, records = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, views.relationship_units(bags))
        self.assertEqual(fit, [])
        self.assertEqual(records[0]["status"], "conflicting_resolved_targets")
        self.assertEqual(records[0]["resolvedLabels"], [0, 1])

    def test_unknown_bag_is_not_rewritten_when_overlap_has_a_resolved_target(self):
        pair = (actor("a"), actor("right", "source-b"))
        first, unknown = bag("first", [pair], target=1), bag("unknown", [pair], target=None)
        bags = {item.row["edgeId"]: item for item in (first, unknown)}
        fit, records = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, views.relationship_units(bags))
        self.assertEqual(len(fit), 1)
        self.assertIsNone(unknown.target)
        self.assertEqual(records[0]["unknownBags"], 1)

    def test_target_changes_do_not_move_unit_membership(self):
        pair = (actor("a"), actor("right", "source-b"))
        items = [bag("first", [pair]), bag("second", [pair])]
        bags = {item.row["edgeId"]: item for item in items}
        before = views.relationship_units(bags)
        answers = [{"task_id": item.row["taskId"], "candidate_endpoint_id": "candidate", "decision": "different_entities",
                    "label": 0, "annotation_status": "original_draft"} for item in items]
        views.join_bag_targets(self.base, bags, answers)
        self.assertEqual(before, views.relationship_units(bags))

    def test_name_only_views_retain_exact_coordinates_without_fabricated_reference(self):
        endpoint, row, old = name_endpoint(self.base)
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        _, all_views, total = views.read_endpoint_views(self.base, package, {"a": old}, [row])
        self.assertEqual(total, 1)
        key = json.loads(next(iter(all_views["a"])))
        self.assertIsNone(key["referenceUuid"])
        self.assertEqual(key["anchor"]["quote"], "Ada")

    def test_v2_retains_the_original_role_free_assertion_without_recording_facts(self):
        endpoint, row, old = source_alias_endpoint(self.base)
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        _, all_views, total = views.read_endpoint_views(self.base, package, {"a": old}, [row],
            export_version="people-view-baseline-v2", projection_version="people-independent-source-views-v2")
        retained = next(iter(all_views["a"].values()))
        self.assertEqual(total, 1)
        self.assertEqual(retained["sourceAssertion"], old["variants"][0]["sourceAssertion"])
        self.assertIsNone(retained["facts"])
        self.assertEqual(retained["key"]["referenceUuid"], retained["sourceAssertion"]["actorReferenceUuid"])

    def test_v2_name_fallback_retains_an_explicit_null_assertion(self):
        endpoint, row, old = name_endpoint(self.base)
        row["variants"][0]["sourceAssertion"] = None
        row["views"][0]["sourceAssertion"] = None
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        _, all_views, _ = views.read_endpoint_views(self.base, package, {"a": old}, [row],
            export_version="people-view-baseline-v2", projection_version="people-independent-source-views-v2")
        self.assertIsNone(next(iter(all_views["a"].values()))["sourceAssertion"])

    def test_recording_facts_remain_supported_in_both_export_versions(self):
        for version in (1, 2):
            with self.subTest(version=version):
                endpoint, row, old = source_alias_endpoint(self.base)
                for original in (row["variants"][0], row["views"][0]):
                    original["facts"] = {"roles": ["taper"]}
                    if version == 1:
                        original.pop("sourceAssertion")
                    else:
                        original["sourceAssertion"] = None
                package = prepared(self.base)
                package.endpoints = {"a": endpoint}
                _, all_views, _ = views.read_endpoint_views(self.base, package, {"a": old}, [row],
                    export_version=f"people-view-baseline-v{version}",
                    projection_version=f"people-independent-source-views-v{version}")
                self.assertEqual(next(iter(all_views["a"].values()))["facts"], old["variants"][0]["facts"])

    def test_v1_rejects_a_new_assertion_field_instead_of_silently_discarding_it(self):
        endpoint, row, old = source_alias_endpoint(self.base)
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        with self.assertRaisesRegex(ValueError, "complete source projection"):
            views.read_endpoint_views(self.base, package, {"a": old}, [row])

    def test_v2_cannot_add_an_alias_to_the_original_assertion(self):
        endpoint, row, old = source_alias_endpoint(self.base)
        row["views"][0]["sourceAssertion"] = copy.deepcopy(row["views"][0]["sourceAssertion"])
        row["views"][0]["sourceAssertion"]["aliases"].append({"wording": "invented", "evidence": []})
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        with self.assertRaisesRegex(ValueError, "without union"):
            views.read_endpoint_views(self.base, package, {"a": old}, [row],
                export_version="people-view-baseline-v2", projection_version="people-independent-source-views-v2")

    def test_v2_assertion_cannot_borrow_recording_facts_or_a_different_actor(self):
        for tamper in ("recording_facts", "actor_reference", "name_fallback", "malformed_assertion"):
            with self.subTest(tamper=tamper):
                endpoint, row, old = source_alias_endpoint(self.base)
                variant, view = row["variants"][0], row["views"][0]
                if tamper == "recording_facts":
                    variant["facts"] = view["facts"] = {"roles": ["taper"]}
                elif tamper == "actor_reference":
                    view["sourceAssertion"]["actorReferenceUuid"] = actor("other")["referenceUuid"]
                elif tamper == "malformed_assertion":
                    variant["sourceAssertion"] = view["sourceAssertion"] = "not an assertion"
                else:
                    _, fallback, _ = name_endpoint(self.base)
                    variant["actorReference"] = None
                    row["selectors"][0]["status"] = "name_only"
                    view["key"] = fallback["views"][0]["key"]
                package = prepared(self.base)
                package.endpoints = {"a": endpoint}
                with self.assertRaisesRegex(ValueError, "scoped actor without recording facts"):
                    views.read_endpoint_views(self.base, package, {"a": old}, [row],
                        export_version="people-view-baseline-v2", projection_version="people-independent-source-views-v2")

    def test_v2_requires_the_assertion_field_on_both_view_and_original_variant(self):
        for missing in ("views", "variants"):
            with self.subTest(missing=missing):
                endpoint, row, old = source_alias_endpoint(self.base)
                row[missing][0].pop("sourceAssertion")
                package = prepared(self.base)
                package.endpoints = {"a": endpoint}
                with self.assertRaises(ValueError):
                    views.read_endpoint_views(self.base, package, {"a": old}, [row],
                        export_version="people-view-baseline-v2", projection_version="people-independent-source-views-v2")

    def test_mixed_or_unknown_export_profiles_fail_before_opening_targets(self):
        profiles = [("people-view-baseline-v1", "people-independent-source-views-v2"),
                    ("people-view-baseline-v2", "people-independent-source-views-v1"),
                    ("people-view-baseline-v3", "people-independent-source-views-v3")]
        for version, projection in profiles:
            with self.subTest(version=version, projection=projection), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                raw = json.dumps({"state": "completed", "version": version, "projectionVersion": projection}).encode()
                (directory / "manifest.json").write_bytes(raw)
                inputs = self.base.Inputs(self.experiment)
                with patch.object(self.base, "read_prepared_sources", return_value=(inputs, prepared(self.base))), \
                     patch.object(self.base, "read_targets", side_effect=AssertionError("Targets must remain unopened")):
                    with self.assertRaisesRegex(ValueError, "TOP"):
                        views.load_inputs(self.base, self.experiment, directory, "unused", directory,
                                          hashlib.sha256(raw).hexdigest())

    def test_report_retains_the_actual_input_projection_without_training(self):
        for projection in ("people-independent-source-views-v1", "people-independent-source-views-v2"):
            with self.subTest(projection=projection), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary) / "output"
                inputs = self.base.Inputs(self.experiment)
                # This exercises report publication only; no fitted model is needed for an empty export.
                with patch.object(views, "score_observations", return_value=({}, [], [], [], [])):
                    report = views.run(self.base, self.experiment, inputs, prepared(self.base),
                        {"projectionVersion": projection, "versions": {}}, {"version": "synthetic"},
                        {}, [], {}, [], directory, {})
                saved = json.loads((directory / "report.json").read_text())
                self.assertEqual(report["inputProjectionVersion"], projection)
                self.assertEqual(saved["inputProjectionVersion"], projection)

    def test_source_view_cannot_union_a_new_contact_into_original_projection(self):
        endpoint, row, old = name_endpoint(self.base)
        row["views"][0]["projection"] = copy.deepcopy(row["views"][0]["projection"])
        row["views"][0]["projection"]["record"]["contacts"] = ["invented"]
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        with self.assertRaisesRegex(ValueError, "without union"):
            views.read_endpoint_views(self.base, package, {"a": old}, [row])

    def test_ambiguous_selector_is_not_an_eligible_view(self):
        endpoint, row, old = name_endpoint(self.base)
        row["selectors"][0]["issues"] = ["ambiguous_source_actor"]
        old["selectors"] = row["selectors"]
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        with self.assertRaisesRegex(ValueError, "Uncertain selector"):
            views.read_endpoint_views(self.base, package, {"a": old}, [row])

    def test_complete_endpoint_cannot_hide_a_missing_selector(self):
        endpoint, row, old = name_endpoint(self.base, selector_count=2)
        row["views"].pop()
        package = prepared(self.base)
        package.endpoints = {"a": endpoint}
        with self.assertRaisesRegex(ValueError, "every selector"):
            views.read_endpoint_views(self.base, package, {"a": old}, [row])

    def test_edge_id_json_coordinates_are_not_split_on_punctuation(self):
        task, candidate = 'task|["x"]', "candidate|with,separators"
        self.assertEqual(views.edge_coordinates(json.dumps([task, candidate])), (task, candidate))

    def test_leaf_cartesian_coverage_requires_every_distinct_combination(self):
        left, right = actor("a"), actor("right", "source-b")
        item = bag("one", [(left, right)])
        item.row["expectedLeafPairs"] = 2
        raw = {"edgeId": item.row["edgeId"], "taskId": "one", "focusEndpointId": "focus", "candidateEndpointId": "candidate",
               "leftViewKey": left, "rightViewKey": right, "comparison": comparison(), "features": [0.8, 1, 0, 1, 0]}
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "leaves.jsonl"
            path.write_text(json.dumps(raw) + "\n")
            item.leaves.clear()
            with self.assertRaisesRegex(ValueError, "Cartesian bag"):
                views.read_leaves(self.experiment, path, 2, {item.row["edgeId"]: item},
                    {"focus": {views.canonical(left): {}}, "candidate": {views.canonical(right): {}}})

    def test_leaf_identity_target_field_is_rejected(self):
        item = bag("one")
        raw = {"edgeId": item.row["edgeId"], "taskId": "one", "focusEndpointId": "focus", "candidateEndpointId": "candidate",
               "leftViewKey": actor("a"), "rightViewKey": actor("b"), "comparison": comparison(),
               "features": [0.8, 1, 0, 1, 0], "target": 1}
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "leaves.jsonl"
            path.write_text(json.dumps(raw) + "\n")
            with self.assertRaisesRegex(ValueError, "identity targets"):
                views.read_leaves(self.experiment, path, 1, {item.row["edgeId"]: item}, {})

    def test_expansion_overflow_fails_before_reading_or_fitting_partial_bag(self):
        package = prepared(self.base)
        package.questions = {"one": {"focus_endpoint_id": "focus", "candidate_endpoint_ids": ["candidate"],
                                      "task_kind": "cross_recording_identity"}}
        row = bag("one").row
        row.update(leftViewCount=1001, rightViewCount=1000, expectedLeafPairs=1001000)
        endpoint_rows = {key: {"coverage": "complete"} for key in ("focus", "candidate")}
        source_views = {"focus": dict.fromkeys(range(1001)), "candidate": dict.fromkeys(range(1000))}
        with self.assertRaisesRegex(ValueError, "expansion exceeds"):
            views.read_bags(self.base, package, endpoint_rows, source_views, [row])

    def test_missing_declared_candidate_cannot_disappear_into_view_expansion(self):
        package = prepared(self.base)
        package.questions = {"one": {"focus_endpoint_id": "focus", "candidate_endpoint_ids": ["candidate", "second"],
                                      "task_kind": "cross_recording_identity"}}
        row = bag("one").row
        row.update(leftViewCount=1, rightViewCount=1, expectedLeafPairs=1)
        endpoint_rows = {key: {"coverage": "complete"} for key in ("focus", "candidate", "second")}
        source_views = {key: {"one": {}} for key in endpoint_rows}
        with self.assertRaisesRegex(ValueError, "candidate-pool"):
            views.read_bags(self.base, package, endpoint_rows, source_views, [row])

    def test_nested_old_comparator_marker_cannot_complete_the_new_mode(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            raw = json.dumps({"state": "completed", "version": "people-adjudicated-baseline-v1"}).encode()
            (directory / "manifest.json").write_bytes(raw)
            inputs = self.base.Inputs(self.experiment)
            with patch.object(self.base, "read_prepared_sources", return_value=(inputs, prepared(self.base))), \
                 patch.object(self.base, "read_targets", side_effect=AssertionError("Targets must remain unopened")):
                with self.assertRaisesRegex(ValueError, "TOP"):
                    views.load_inputs(self.base, self.experiment, directory, "unused", directory, hashlib.sha256(raw).hexdigest())

    def test_all_source_tasks_and_actor_kind_bridges_group_before_labels(self):
        first, _, first_old = name_endpoint(self.base, "focus")
        second, _, second_old = name_endpoint(self.base, "candidate")
        second["evidence_id"], second["artist_uuid"] = "source-b", "artist-b"
        second_old["evidenceId"], second_old["artistUuid"] = "source-b", "artist-b"
        package = prepared(self.base)
        package.endpoints = {"focus": first, "candidate": second}
        package.questions = {
            "q1": {"focus_endpoint_id": "focus", "candidate_endpoint_ids": ["candidate"], "split_group_ids": ["source-one"]},
            "actor": {"focus_endpoint_id": "focus", "candidate_endpoint_ids": [], "split_group_ids": ["source-one"]},
            "q2": {"focus_endpoint_id": "focus", "candidate_endpoint_ids": ["candidate"], "split_group_ids": ["source-two"]}}
        pair = (actor("a"), actor("right", "source-b"))
        first_bag, second_bag = bag("q1", [pair]), bag("q2", [pair], target=None)
        bags = {item.row["edgeId"]: item for item in (first_bag, second_bag)}
        units = views.relationship_units(bags)
        components = views.group_source_tasks(self.base, package,
            {"Grouping": [{"taskIds": ["actor", "q1"]}, {"taskIds": ["q2"]}], "Bridge": []},
            {"focus": first_old, "candidate": second_old}, bags, units)
        self.assertEqual(components, [["actor", "q1", "q2"]])
        self.assertEqual({item.component for item in bags.values()}, {"actor"})

    def test_zero_leaf_identity_component_is_distinct_from_actor_only_component(self):
        item = bag("identity", complete=False, left_coverage="unsupported")
        item.component = "identity"
        bags = {item.row["edgeId"]: item}
        _, folds, predicted, primary, final = views.score_observations(self.base, self.experiment,
            prepared(self.base), bags, [], [], [["actor-only"], ["identity"]])
        self.assertEqual({fold["component"]: fold["state"] for fold in folds},
                         {"actor-only": "no_identity_edges", "identity": "unsupported_projection_only"})
        self.assertEqual(predicted, [])
        self.assertEqual(primary, [])
        self.assertEqual(final, [])

    def test_leaf_predictions_never_inherit_known_bag_target(self):
        item = bag("one", [(actor("a"), actor("right", "source-b"))], target=1)
        item.component = "one"
        bags = {item.row["edgeId"]: item}
        units = views.relationship_units(bags)
        fit, _ = views.fit_relationships(self.base, self.experiment, prepared(self.base), bags, units)
        _, folds, predicted, _, final = views.score_observations(self.base, self.experiment,
            prepared(self.base), bags, item.leaves, fit, [["one"]])
        self.assertIsNone(predicted[0]["target"])
        self.assertEqual(predicted[0]["targetStatus"], "no_leaf_identity_target")
        self.assertEqual(predicted[0]["edgeId"], item.row["edgeId"])
        self.assertEqual(final[0]["observationId"], item.leaves[0].observation_id)
        declared = views.declared_predictions(bags, predicted, folds)
        self.assertEqual(declared[0]["target"], 1)

    def test_actual_numerical_fit_uses_units_and_keeps_bag_leaf_denominators_separate(self):
        package = prepared(self.base)
        package.endpoints = {}
        bags = {}
        all_leaves = []
        for index, label in enumerate((0, 1, 0, 1)):
            task, focus, candidate = "q" + str(index), "focus" + str(index), "candidate" + str(index)
            edge_id = json.dumps([task, candidate], separators=(",", ":"))
            left = actor("left" + str(index), "source-a" + str(index))
            other = actor("other" + str(index), "source-a" + str(index), "transferrer")
            right = actor("right" + str(index), "source-b" + str(index))
            values = (0.1 + 0.2 * index, label, 0, int(index == 3), 0)
            item = bag(task, target=label)
            item.row.update(edgeId=edge_id, focusEndpointId=focus, candidateEndpointId=candidate,
                            leftViewCount=2, rightViewCount=1, expectedLeafPairs=2)
            item.component = task
            item.leaves = [leaf(edge_id, left, right, values), leaf(edge_id, other, right, values, conflict=index == 1)]
            package.endpoints[focus] = {"evidence_id": left["evidenceId"], "artist_uuid": "artist-a" + str(index)}
            package.endpoints[candidate] = {"evidence_id": right["evidenceId"], "artist_uuid": "artist-b" + str(index)}
            bags[edge_id] = item
            all_leaves.extend(item.leaves)
        units = views.relationship_units(bags)
        fit, records = views.fit_relationships(self.base, self.experiment, package, bags, units)
        models, folds, predicted, primary, final = views.score_observations(self.base, self.experiment, package,
            bags, all_leaves, fit, [["q" + str(index)] for index in range(4)])
        declared = views.declared_predictions(bags, predicted, folds)
        self.assertEqual((len(fit), len(primary), len(declared), len(predicted), len(final)), (4, 4, 4, 8, 8))
        self.assertEqual(len(models["logistic"]["final"]["trainingEdgeIds"]), 4)
        self.assertTrue(all(row["target"] is None for row in predicted))
        self.assertTrue(all(pool["declaredCandidates"] == 1 for pool in views.pool_diagnostics(declared)))
        self.assertEqual(views.metrics(self.base, primary, records)["logistic"]["scoredIndependentUnits"], 4)
        self.assertEqual(sum(record["trainingWeight"] for record in records), 4)

    def test_single_view_mode_deduplicates_without_rewriting_unknown_targets(self):
        rows = [self.base.DeclaredEdge("a", "q1", "b", "a", "cross_recording_identity", "component", "pair", 1,
            (0.8, 1, 0, 1, 0), False, False, comparison(), True, None, {"annotation_status": "original_draft"}),
            self.base.DeclaredEdge("b", "q2", "a", "b", "cross_recording_identity", "component", "pair", None,
            (0.8, 1, 0, 1, 0), False, False, comparison(), True, None, {"annotation_status": "agent_adjudicated_development"})]
        fit, records = self.base.fit_units(self.experiment, rows)
        self.assertEqual(len(fit), 1)
        self.assertEqual((fit[0].edge_id, fit[0].values, fit[0].target), ("pair", (0.8, 1, 0, 1, 0), 1))
        self.assertEqual([row.target for row in rows], [1, None])
        self.assertEqual(records[0]["status"], "eligible")
        self.assertEqual(records[0]["declaredRows"], 2)
        self.assertEqual(records[0]["unknownRows"], 1)
        self.assertEqual(records[0]["annotationStatuses"], {"original_draft": 1, "agent_adjudicated_development": 1})


if __name__ == "__main__":
    unittest.main()
