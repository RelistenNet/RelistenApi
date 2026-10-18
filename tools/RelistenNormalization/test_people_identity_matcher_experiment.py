"""Synthetic contract checks; these never fit or inspect the real people bundle."""

from copy import deepcopy
import importlib.util
import json
import os
from pathlib import Path
import py_compile
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace


PATH = Path(__file__).with_name("people_identity_matcher_experiment.py")
SPEC = importlib.util.spec_from_file_location("people_identity_experiment", PATH)
EXP = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = EXP
SPEC.loader.exec_module(EXP)

# Reuse only the source-of-truth grouping function on synthetic tasks.
ROOT = Path(os.environ["RELISTEN_NORMALIZATION_ROOT"]).resolve() if "RELISTEN_NORMALIZATION_ROOT" in os.environ else next(
    path for path in [Path.cwd(), *PATH.resolve().parents] if (path / "RelistenApi.sln").is_file())
VALIDATOR = EXP.import_validator(ROOT / "docs/research/2026-10-17-full-normalization/training/people-development-v1/validate_bundle.py")


def task(key, candidates=(), decisions=(), groups=(), kind="cross_recording_identity", focus="focus"):
    return {"task_id": key, "task_kind": kind, "focus_mention_id": focus,
            "candidate_mention_ids": list(candidates), "split_group_ids": list(groups),
            "evaluation_eligible": False, "policy_activation_permitted": False,
            "answer": {"candidate_decisions": [
                {"candidate_mention_id": candidate, "decision": decision}
                for candidate, decision in zip(candidates, decisions)]}}


def mention(key, source, artist="artist", kind="name"):
    return {"mention_id": key, "evidence_id": source, "artist": {"uuid": artist},
            "mention_kind": kind, "name": "Élodie\u2028Name"}


def comparison(values=(1, 1, 0, 0, 0)):
    return {"domain": "people", "candidateUuid": None, "policyVersion": None,
            "evaluationReceiptUuid": None, "disposition": "needs_evidence", "isCandidate": True,
            "nameTokenSimilarity": values[0], "supportedRules": [],
            "features": [{"name": name, "agreement": "agrees" if value else "missing"}
                         for name, value in zip(("name", "artist_context", "actor_contact", "actor_profile"), values[1:])]}


def baseline(question, candidates, mentions, values=(1, 1, 0, 0, 0)):
    rows = []
    for candidate in candidates:
        focus = mentions[question["focus_mention_id"]]
        other = mentions[candidate]
        supported = focus["mention_kind"] == other["mention_kind"] == "name"
        rows.append({"taskId": question["task_id"], "taskKind": question["task_kind"],
                     "focusMentionId": question["focus_mention_id"], "candidateMentionId": candidate,
                     "focusEvidenceId": focus["evidence_id"], "candidateEvidenceId": other["evidence_id"],
                     "sameRecording": focus["evidence_id"] == other["evidence_id"],
                     "declaredCandidateCount": len(candidates), "features": list(values),
                     "comparison": comparison(values), "projectionSupported": supported,
                     "projectionIssue": None if supported else "unsupported_mention_expression"})
    return rows


def edge(key, component, target, value, task_id=None, same_recording=False, kind="cross_recording_identity", supported=True):
    values = (value, float(value > 0.5), 0.0, 0.0, 0.0)
    return EXP.Edge(key, task_id or key, kind, "focus", key, component, target, values,
                    same_recording, False, comparison(values), supported,
                    None if supported else "unsupported_mention_expression")


def fitting_edges():
    return [edge(f"edge-{i}", f"group-{i}", target, value)
            for i, (target, value) in enumerate([(0, 0.1), (0, 0.2), (0, 0.3),
                                                (1, 0.7), (1, 0.8), (1, 0.9)])]


def fitted_logistic():
    return {"version": "people-five-feature-logistic-v1", "featureOrder": list(EXP.FEATURE_ORDER),
            "classes": [0, 1], "intercept": -2.0, "coefficients": [3.0, 1.0, 0.5, 0.25, -0.5]}


def input_packet(root, source_context=False):
    """Small authenticated source/export packet with no real catalog content."""
    root = root.resolve()
    bundle, exported = root / "bundle", root / "exported"
    bundle.mkdir()
    exported.mkdir()
    label = task("question", ["candidate"], ["insufficient_evidence"], ["group"])
    inventory = {"focus": mention("focus", "s1"), "candidate": mention("candidate", "s2")}
    labels = [label]
    if source_context:
        # Diagnostics cover the whole inventory, including mentions outside identity edges.
        inventory["observer"] = mention("observer", "s3", kind="credit_expression")
        labels.append(task("actor", groups=["group"], kind="actor_kind", focus="observer"))
    EXP.write_rows(bundle / "labels.jsonl", labels)
    EXP.write_rows(bundle / "mentions.jsonl", list(inventory.values()))
    EXP.write_json(bundle / "candidate-pool.json", {"nested": {"candidate": True}})
    (bundle / "validate_bundle.py").write_text("# Synthetic validator entrypoint\n")
    manifest = {"usage": "development_only", "policy_activation_permitted": False,
                "files": {name: EXP.fingerprint(bundle / name)
                          for name in ("labels.jsonl", "mentions.jsonl", "candidate-pool.json")}}
    for name in ("original_selected_evidence", "original_collision_tasks", "reservation_index", "review"):
        path = root / f"{name}.json"
        EXP.write_json(path, {"synthetic": True})
        reference = {"path": path.name, **EXP.fingerprint(path)}
        if name == "review":
            manifest["independent_review"] = {"input": reference}
        else:
            manifest[name] = reference
    EXP.write_json(bundle / "manifest.json", manifest)
    values = (1, 1, 0, 1, 1) if source_context else (1, 1, 0, 0, 0)
    EXP.write_rows(exported / "baseline.jsonl", baseline(label, ["candidate"], inventory, values))
    assembly = root / "immutable-api.dll"
    assembly.write_bytes(b"synthetic assembly bytes")
    exported_manifest = {
        "state": "completed", "version": "people-pair-baseline-v1",
        "projectionVersion": EXP.FEATURE_VERSION, "actorFeaturesVersion": "people-actor-context-features-v1",
        "featureOrder": list(EXP.FEATURE_ORDER), "bundleManifestSha256": EXP.fingerprint(bundle / "manifest.json")["sha256"],
        "policyActivationPermitted": False, "baselineFile": "baseline.jsonl",
        "baselineSha256": EXP.fingerprint(exported / "baseline.jsonl")["sha256"],
        "allTasks": len(labels), "questions": 1, "pairs": 1,
        "inputFiles": {name: {**EXP.fingerprint(bundle / name), "rows": len(EXP.json_rows((bundle / name).read_bytes()))
                             if name.endswith(".jsonl") else None}
                       for name in manifest["files"]},
        "artifacts": [{"path": str(assembly), **EXP.fingerprint(assembly)}],
        "matcherAssemblySha256": EXP.fingerprint(assembly)["sha256"],
        "runnerAssemblySha256": EXP.fingerprint(assembly)["sha256"],
    }
    if source_context:
        context = exported / "context-diagnostics.jsonl"
        EXP.write_rows(context, [{"mentionId": key, "synthetic": True} for key in inventory])
        exported_manifest.update(
            version=EXP.CONTEXT_BASELINE_VERSION,
            projectionVersion=EXP.CONTEXT_FEATURE_VERSION,
            contextFile=context.name, contextSha256=EXP.fingerprint(context)["sha256"],
            contextRows=len(inventory), **EXP.CONTEXT_VERSION_PINS)
    EXP.write_json(exported / "manifest.json", exported_manifest)
    args = (root, bundle, exported, EXP.fingerprint(bundle / "manifest.json")["sha256"],
            EXP.fingerprint(bundle / "validate_bundle.py")["sha256"],
            EXP.fingerprint(exported / "manifest.json")["sha256"])
    validator = SimpleNamespace(validate_bundle=lambda *_: {"synthetic": True},
                                build_split_components=VALIDATOR.build_split_components)
    return args, validator


def rewrite_export_manifest(args, mutate):
    """Repin an intentionally altered synthetic contract, rather than hiding digest failures."""
    path = args[2] / "manifest.json"
    manifest = json.loads(path.read_text())
    mutate(manifest)
    path.write_text(json.dumps(manifest))
    return (*args[:-1], EXP.fingerprint(path)["sha256"])


class TestPeopleIdentityMatcherExperiment(unittest.TestCase):
    def test_validator_executes_current_authenticated_source_despite_valid_stale_bytecode(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "validator.py"
            original, current = b"VALUE = 'older'\n", b"VALUE = 'newer'\n"
            self.assertEqual(len(original), len(current))
            path.write_bytes(original)
            os.utime(path, (1_700_000_000, 1_700_000_000))
            timestamp = path.stat().st_mtime_ns
            py_compile.compile(str(path), doraise=True,
                               invalidation_mode=py_compile.PycInvalidationMode.TIMESTAMP)
            path.write_bytes(current)
            os.utime(path, ns=(timestamp, timestamp))

            # Establish that an ordinary loader would accept the old timestamp/size cache.
            spec = importlib.util.spec_from_file_location("stale_validator_probe", path)
            cached = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(cached)
            self.assertEqual(cached.VALUE, "older")
            actual = EXP.import_validator(path, EXP.sha256(current))
            self.assertEqual(actual.VALUE, "newer")

    def test_source_changed_after_initial_pin_is_rejected_before_validator_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            args, _ = input_packet(root)
            original_read = EXP.read_bytes

            def change_before_import(path):
                data = original_read(path)
                if Path(path) == root / "exported/baseline.jsonl":
                    (root / "bundle/validate_bundle.py").write_text(
                        "raise AssertionError('Unreviewed validator must not execute')\n")
                return data

            with patch.object(EXP, "read_bytes", side_effect=change_before_import):
                with self.assertRaisesRegex(ValueError, "executed-source digest changed"):
                    EXP.load_inputs(*args)

    def test_authenticated_export_loads_complete_pool_with_json_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            args, validator = input_packet(Path(directory))
            with patch.object(EXP, "import_validator", return_value=validator):
                edges, components, labels, pins, *_ = EXP.load_inputs(*args)
            self.assertEqual((len(edges), len(components), len(labels)), (1, 1, 1))
            self.assertIsNone(edges[0].target)
            self.assertTrue(any(Path(pin["path"]).name == "immutable-api.dll" for pin in pins))

    def test_authenticated_source_context_keeps_actual_five_features_and_all_inventory_diagnostics(self):
        with tempfile.TemporaryDirectory() as directory:
            args, validator = input_packet(Path(directory), source_context=True)
            with patch.object(EXP, "import_validator", return_value=validator):
                edges, components, labels, pins, _, _, manifest = EXP.load_inputs(*args)
            self.assertEqual(edges[0].values, (1, 1, 0, 1, 1))
            self.assertIsNone(edges[0].target)
            self.assertEqual(components, [["actor", "question"]])
            self.assertEqual(len(labels), 2)
            self.assertEqual(manifest["contextRows"], 3)
            self.assertEqual(EXP.projection_receipt(manifest)["projectionVersion"], EXP.CONTEXT_FEATURE_VERSION)
            self.assertTrue(any(Path(value["path"]).name == "context-diagnostics.jsonl" for value in pins))

    def test_changed_or_missing_context_artifact_rejects_before_validator_execution(self):
        for missing in (False, True):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                args, _ = input_packet(Path(directory), source_context=True)
                context = args[2] / "context-diagnostics.jsonl"
                if missing:
                    context.unlink()
                else:
                    context.write_text('{"mentionId":"tampered"}\n')
                with patch.object(EXP, "import_validator") as execute:
                    with self.assertRaises((ValueError, OSError)):
                        EXP.load_inputs(*args)
                    execute.assert_not_called()

    def test_context_contract_requires_artifact_metadata_and_exact_extractor_versions(self):
        changes = {**{key: "unreviewed-version" for key in EXP.CONTEXT_VERSION_PINS},
                   "projectionVersion": EXP.FEATURE_VERSION, "actorFeaturesVersion": "unknown",
                   "contextFile": "../other-context.jsonl", "contextRows": True,
                   "contextSha256": None}
        for key, value in changes.items():
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                args, _ = input_packet(Path(directory), source_context=True)
                args = rewrite_export_manifest(args, lambda manifest: manifest.update({key: value}))
                with patch.object(EXP, "import_validator") as execute:
                    with self.assertRaises(ValueError):
                        EXP.load_inputs(*args)
                    execute.assert_not_called()
        for key in (*EXP.CONTEXT_VERSION_PINS, "contextFile", "contextRows", "contextSha256"):
            with self.subTest(missing=key), tempfile.TemporaryDirectory() as directory:
                args, _ = input_packet(Path(directory), source_context=True)
                args = rewrite_export_manifest(args, lambda manifest: manifest.pop(key))
                with self.assertRaises(ValueError):
                    EXP.load_inputs(*args)

    def test_legacy_contract_cannot_silently_claim_context_projection_or_diagnostics(self):
        for key, value in (("projectionVersion", EXP.CONTEXT_FEATURE_VERSION),
                           ("contextFile", "context-diagnostics.jsonl"),
                           ("actorProjectionVersion", EXP.CONTEXT_VERSION_PINS["actorProjectionVersion"]),
                           ("version", "people-pair-baseline-v99")):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                args, _ = input_packet(Path(directory))
                args = rewrite_export_manifest(args, lambda manifest: manifest.update({key: value}))
                with self.assertRaises(ValueError):
                    EXP.load_inputs(*args)

    def test_rehashed_context_still_requires_complete_unique_mention_coverage(self):
        for identities in (("focus", "candidate"), ("focus", "candidate", "candidate"),
                           ("focus", "candidate", "foreign")):
            with self.subTest(identities=identities), tempfile.TemporaryDirectory() as directory:
                args, _ = input_packet(Path(directory), source_context=True)
                context = args[2] / "context-diagnostics.jsonl"
                context.write_text(''.join(json.dumps({"mentionId": key}) + '\n' for key in identities))
                args = rewrite_export_manifest(args, lambda manifest: manifest.update(
                    contextSha256=EXP.fingerprint(context)["sha256"], contextRows=len(identities)))
                with self.assertRaisesRegex(ValueError, "inventory coverage"):
                    EXP.load_inputs(*args)

    def test_validator_time_context_change_is_rechecked_before_edges_are_returned(self):
        with tempfile.TemporaryDirectory() as directory:
            args, validator = input_packet(Path(directory), source_context=True)
            def mutate(*_):
                (args[2] / "context-diagnostics.jsonl").write_text("changed during validation\n")
                return {}
            validator.validate_bundle = mutate
            with patch.object(EXP, "import_validator", return_value=validator):
                with self.assertRaisesRegex(ValueError, "Changed input"):
                    EXP.load_inputs(*args)

    def test_source_context_diagnostics_never_supply_feature_values_or_answers(self):
        with tempfile.TemporaryDirectory() as directory:
            args, validator = input_packet(Path(directory), source_context=True)
            context = args[2] / "context-diagnostics.jsonl"
            rows = EXP.json_rows(context.read_bytes())
            for row in rows:
                row.update(features=[0, 0, 0, 0, 0], answer="different_entities")
            context.write_text(''.join(json.dumps(row) + '\n' for row in rows))
            args = rewrite_export_manifest(args, lambda manifest: manifest.update(
                contextSha256=EXP.fingerprint(context)["sha256"]))
            with patch.object(EXP, "import_validator", return_value=validator):
                edges, *_ = EXP.load_inputs(*args)
            self.assertEqual(edges[0].values, (1, 1, 0, 1, 1))
            self.assertIsNone(edges[0].target)

    def test_source_context_report_features_predictions_and_replay_keep_consumed_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            args, validator = input_packet(Path(directory), source_context=True)
            with patch.object(EXP, "import_validator", return_value=validator):
                edges, components, labels, pins, validation, data, manifest = EXP.load_inputs(*args)
            output = Path(directory) / "result"
            # This packet has only an unknown identity target: no estimator is fitted.
            with patch.object(EXP, "new_estimators") as estimators:
                report = EXP.run(edges, components, labels, output, pins, validation, data, manifest, {})
                estimators.assert_not_called()
            self.assertEqual(report["version"], EXP.CONTEXT_VERSION)
            self.assertEqual(report["featureVersion"], EXP.CONTEXT_FEATURE_VERSION)
            for key, expected in EXP.CONTEXT_VERSION_PINS.items():
                self.assertEqual(report["baselineContract"][key], expected)
            self.assertEqual(report["baselineContract"]["contextSha256"], manifest["contextSha256"])
            self.assertEqual(EXP.json_rows((output / "features.jsonl").read_bytes())[0]["featureVersion"],
                             EXP.CONTEXT_FEATURE_VERSION)
            prediction = EXP.json_rows((output / "predictions.jsonl").read_bytes())[0]
            self.assertEqual(prediction["projectionVersion"], EXP.CONTEXT_FEATURE_VERSION)
            self.assertIsNone(prediction["target"])
            inputs = json.loads((output / "inputs.json").read_text())["files"]
            context = next(value for value in inputs if Path(value["path"]).name == "context-diagnostics.jsonl")
            self.assertEqual(context["sha256"], manifest["contextSha256"])
            self.assertEqual(EXP.fingerprint(output / context["retainedPath"])["sha256"], manifest["contextSha256"])

    def test_model_export_names_training_projection_without_changing_portable_five_values(self):
        fitted = SimpleNamespace(
            classes_=SimpleNamespace(tolist=lambda: [0, 1]),
            n_iter_=SimpleNamespace(max=lambda: 1), max_iter=1000, intercept_=[-2.0],
            coef_=[SimpleNamespace(tolist=lambda: [3.0, 1.0, 0.5, 0.25, -0.5])])
        legacy = EXP.export_model("logistic", fitted, [])
        context = EXP.export_model("logistic", fitted, [], EXP.CONTEXT_FEATURE_VERSION)
        self.assertEqual(legacy["projectionVersion"], EXP.FEATURE_VERSION)
        self.assertEqual(context["projectionVersion"], EXP.CONTEXT_FEATURE_VERSION)
        self.assertEqual(context["featureOrder"], list(EXP.FEATURE_ORDER))
        self.assertEqual(context["runtimeScorerParameters"], legacy["runtimeScorerParameters"])
        self.assertEqual(EXP.portable_logit(context, [1, 1, 0, 1, 1]),
                         EXP.portable_logit(legacy, [1, 1, 0, 1, 1]))
        context["projectionVersion"] = "unreviewed-projection"
        with self.assertRaisesRegex(ValueError, "projection version"):
            EXP.validate_portable(context)

    def test_context_projection_reaches_final_and_held_component_models_for_both_methods(self):
        def fixed_models(training, checked, projection_version=EXP.FEATURE_VERSION):
            # Fixed scorer parameters isolate projection forwarding from estimator fitting.
            return {name: {**fitted_logistic(), "projectionVersion": projection_version}
                    for name in ("logistic", "boosted_tree")}

        rows = fitting_edges()
        with patch.object(EXP, "fit_models", side_effect=fixed_models) as fit:
            models, _, predictions = EXP.compare(
                rows, [[row.component] for row in rows], EXP.CONTEXT_FEATURE_VERSION)
        self.assertEqual(fit.call_count, 1 + len(rows))
        self.assertTrue(all(call.args[2] == EXP.CONTEXT_FEATURE_VERSION for call in fit.call_args_list))
        for result in models.values():
            self.assertEqual(result["final"]["projectionVersion"], EXP.CONTEXT_FEATURE_VERSION)
            self.assertEqual(len(result["folds"]), len(rows))
            self.assertTrue(all(model["projectionVersion"] == EXP.CONTEXT_FEATURE_VERSION
                                for model in result["folds"].values()))
        self.assertTrue(all(row["projectionVersion"] == EXP.CONTEXT_FEATURE_VERSION for row in predictions))

    def test_validation_time_source_change_or_changed_executable_rejects_export(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args, validator = input_packet(root)
            def mutate(*_):
                (root / "bundle/labels.jsonl").write_text("changed after captured bytes\n")
                return {}
            validator.validate_bundle = mutate
            with patch.object(EXP, "import_validator", return_value=validator):
                with self.assertRaisesRegex(ValueError, "Changed input"):
                    EXP.load_inputs(*args)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args, validator = input_packet(root)
            (root / "immutable-api.dll").write_bytes(b"different executable")
            with patch.object(EXP, "import_validator", return_value=validator):
                with self.assertRaisesRegex(ValueError, "Changed input"):
                    EXP.load_inputs(*args)

    def test_actor_kind_bridge_and_unknown_task_join_components_before_fitting(self):
        labels = [task("left", ["candidate"], ["same_entity"], ["family:left"]),
                  task("actor", groups=["family:left", "family:unknown"], kind="actor_kind"),
                  task("unknown", ["candidate"], ["insufficient_evidence"], ["family:unknown", "family:right"]),
                  task("right", ["candidate"], ["different_entities"], ["family:right"])]
        components = VALIDATOR.build_split_components(labels)
        self.assertEqual(components, [["actor", "left", "right", "unknown"]])
        inventory = {"focus": mention("focus", "s1"), "candidate": mention("candidate", "s2")}
        rows = sum((baseline(label, label["candidate_mention_ids"], inventory)
                    for label in labels if label["task_kind"] != "actor_kind"), [])
        actual = EXP.build_edges(labels, inventory, rows, components)
        self.assertEqual({row.component for row in actual}, {"actor"})
        self.assertEqual([row.target for row in actual], [1, 0, None])

    def test_all_candidates_are_required_even_when_unknown(self):
        label = task("question", ["known", "unknown"], ["same_entity", "insufficient_evidence"], ["g"])
        inventory = {"focus": mention("focus", "s1"), "known": mention("known", "s2"),
                     "unknown": mention("unknown", "s3")}
        rows = baseline(label, label["candidate_mention_ids"], inventory)
        with self.assertRaisesRegex(ValueError, "coverage"):
            EXP.build_edges([label], inventory, rows[:1], [["question"]])
        edges = EXP.build_edges([label], inventory, rows, [["question"]])
        self.assertEqual(len(edges), 2)
        self.assertIsNone(edges[1].target)

    def test_answer_metadata_never_changes_feature_values(self):
        label = task("question", ["candidate"], ["same_entity"])
        inventory = {"focus": mention("focus", "s1"), "candidate": mention("candidate", "s2")}
        rows = baseline(label, ["candidate"], inventory)
        original = EXP.build_edges([label], inventory, rows, [["question"]])[0]
        label["answer"]["candidate_decisions"][0]["decision"] = "different_entities"
        label["rationale"] = "Different rationale and identity hypothesis"
        inventory["focus"]["name"] = "Unrelated spelling should be read by C#, not Python"
        changed = EXP.build_edges([label], inventory, rows, [["question"]])[0]
        self.assertEqual(original.values, changed.values)
        self.assertEqual((original.target, changed.target), (1, 0))
        rows[0]["answer"] = "same_entity"
        with self.assertRaisesRegex(ValueError, "Unexpected baseline fields"):
            EXP.build_edges([label], inventory, rows, [["question"]])

    def test_feature_vector_cannot_disagree_with_actual_csharp_comparison(self):
        label = task("question", ["candidate"], ["same_entity"])
        inventory = {"focus": mention("focus", "s1"), "candidate": mention("candidate", "s2")}
        rows = baseline(label, ["candidate"], inventory)
        rows[0]["features"][3] = 1.0
        with self.assertRaisesRegex(ValueError, "actual matcher"):
            EXP.build_edges([label], inventory, rows, [["question"]])

    def test_policy_bearing_baseline_cannot_become_rules_comparison(self):
        value = comparison()
        value["policyVersion"] = "enabled"
        with self.assertRaisesRegex(ValueError, "authority"):
            EXP.actual_features(value)

    def test_unsupported_mentions_stay_in_pool_but_are_not_fitted_or_scored(self):
        label = task("question", ["candidate"], ["same_entity"])
        inventory = {"focus": mention("focus", "s1", kind="credit_expression"),
                     "candidate": mention("candidate", "s2")}
        rows = baseline(label, ["candidate"], inventory)
        actual = EXP.build_edges([label], inventory, rows, [["question"]])
        self.assertFalse(actual[0].projection_supported)
        training = fitting_edges()
        models, folds, predictions = EXP.compare(training + actual,
                                                [[row.component] for row in training] + [["question"]])
        self.assertNotIn(actual[0].edge_id, models["logistic"]["final"]["trainingEdgeIds"])
        prediction = next(row for row in predictions if row["taskId"] == "question")
        self.assertIsNone(prediction["scores"]["logistic"]["rawLogit"])
        self.assertEqual(EXP.diagnostics(predictions)["logistic"]["unsupportedResolvedEdgesExcluded"], 1)

    def test_unknowns_do_not_train_or_enter_loss_and_keep_component_holdout(self):
        edges = fitting_edges() + [edge("unknown", "group-0", None, 0.95)]
        models, folds, predictions = EXP.compare(edges, [[f"group-{i}"] for i in range(6)])
        final = models["logistic"]["final"]
        self.assertNotIn("unknown", final["trainingEdgeIds"])
        self.assertNotIn("edge-0", models["logistic"]["folds"]["group-0"]["trainingEdgeIds"])
        unknown = next(row for row in predictions if row["edgeId"] == "unknown")
        self.assertEqual(unknown["fitState"], "held_component_fit")
        self.assertIsNone(unknown["target"])
        self.assertEqual(EXP.diagnostics(predictions)["logistic"]["resolvedDraftEdges"], 6)
        self.assertEqual(len(predictions), 7)

    def test_one_class_fold_is_unscorable_and_never_uses_held_positive(self):
        edges = [edge("negative-a", "negatives", 0, 0.1), edge("negative-b", "negatives", 0, 0.2),
                 edge("positive-a", "positive-family", 1, 0.8),
                 edge("unknown-rival", "positive-family", None, 0.9)]
        models, folds, predictions = EXP.compare(edges, [["negatives"], ["positive-family"]])
        self.assertIsNotNone(models["logistic"]["final"])
        self.assertEqual(models["logistic"]["folds"], {})
        self.assertTrue(all(row["fitState"] == "unscorable_one_class_training" for row in predictions))
        self.assertTrue(all(row["scores"]["logistic"]["rawLogit"] is None for row in predictions))
        self.assertEqual(EXP.diagnostics(predictions)["logistic"]["scoredDraftEdges"], 0)

    def test_unknown_only_component_can_use_all_resolved_fit_without_target_leakage(self):
        edges = fitting_edges() + [edge("unknown", "unknown-group", None, 0.5)]
        _, _, predictions = EXP.compare(edges, [[f"group-{i}"] for i in range(6)] + [["unknown-group"]])
        unknown = next(row for row in predictions if row["edgeId"] == "unknown")
        self.assertEqual(unknown["fitState"], "all_resolved_fit_no_resolved_held_edges")
        self.assertIsNotNone(unknown["scores"]["logistic"]["rawLogit"])

    def test_exported_raw_logit_is_unscaled_and_matches_runtime_parameter_mapping(self):
        models = EXP.fit_models(fitting_edges(), fitting_edges())
        restored = json.loads(json.dumps(models["logistic"]))
        row = fitting_edges()[0]
        parameters = restored["runtimeScorerParameters"]
        expected = parameters["intercept"] + sum(value * parameters[name] for value, name in zip(row.values,
            ["nameSimilarityWeight", "exactNameWeight", "artistOverlapWeight", "contactAgreementWeight", "profileAgreementWeight"]))
        self.assertEqual(EXP.portable_logit(restored, row.values), expected)
        self.assertNotIn("matchThreshold", parameters)
        self.assertNotIn("minimumRunnerUpMargin", parameters)
        self.assertEqual(restored["scoreKind"], "raw_logit")

    def test_logistic_json_inference_has_no_sklearn_dependency_and_handles_extreme_logits(self):
        model = fitted_logistic()
        with patch.dict(sys.modules, {"numpy": None, "sklearn": None}):
            self.assertEqual(EXP.portable_logit(model, [1, 1, 0, 0, 0]), 2)
            self.assertEqual(EXP.sigmoid(1000), 1)
            self.assertEqual(EXP.sigmoid(-1000), 0)
        model["coefficients"][0] = float("nan")
        with self.assertRaisesRegex(ValueError, "parameters"):
            EXP.portable_logit(model, [1] * 5)

    def test_boosted_json_inference_respects_float32_and_inclusive_tree_threshold(self):
        model = {"version": "people-five-feature-boosted-v1", "featureOrder": list(EXP.FEATURE_ORDER),
                 "classes": [0, 1], "initialLogit": 0.5, "learningRate": 0.1,
                 "trees": [{"left": [1, -1, -1], "right": [2, -1, -1], "feature": [0, -2, -2],
                            "threshold": [0.5, -2, -2], "values": [0, -2, 3]}]}
        with patch.dict(sys.modules, {"numpy": None, "sklearn": None}):
            self.assertAlmostEqual(EXP.portable_logit(model, [0.5, 0, 0, 0, 0]), 0.3)
            self.assertAlmostEqual(EXP.portable_logit(model, [0.500000000001, 0, 0, 0, 0]), 0.3)
            self.assertAlmostEqual(EXP.portable_logit(model, [0.6, 0, 0, 0, 0]), 0.8)
        model["trees"][0]["left"][0] = 0
        with self.assertRaisesRegex(ValueError, "cycle"):
            EXP.portable_logit(model, [0.5] * 5)

    def test_whole_pool_retains_unresolved_rival_without_declaring_winner_truth(self):
        rows = []
        for key, target, score in [("known", 1, 0.7), ("unknown", None, 0.9), ("negative", 0, 0.1)]:
            rows.append({"taskId": "question", "taskKind": "cross_recording_identity", "candidateMentionId": key,
                         "target": target, "scores": {name: {"rawLogit": score} for name in ("logistic", "boosted_tree")}})
        pool = EXP.summarize_pools(rows)[0]
        self.assertEqual(pool["candidateCount"], 3)
        self.assertEqual(pool["models"]["logistic"]["topCandidateMentionIds"], ["unknown"])
        self.assertEqual(pool["models"]["logistic"]["unknownRivalsAtOrAboveBestKnownPositive"], 1)
        self.assertNotIn("accepted", pool)

    def test_true_cross_record_edges_are_distinct_from_cross_question_pools(self):
        rows = [edge("cross-same", "g", 1, 1), edge("neighbor", "g", 1, 1, same_recording=True),
                edge("cross-unknown", "g", None, 1), edge("neighbor-unknown", "g", None, 1, same_recording=True)]
        counts = EXP.cohort_counts(rows)
        self.assertEqual(counts["crossRecordingQuestionPools"], {"edges": 4, "same": 2, "different": 0, "unknown": 2})
        self.assertEqual(counts["trueCrossRecordingEdges"], {"edges": 2, "same": 1, "different": 0, "unknown": 1})

    def test_missing_task_in_split_membership_is_rejected(self):
        labels = [task("actor", kind="actor_kind")]
        with self.assertRaisesRegex(ValueError, "omitted"):
            EXP.build_edges(labels, {}, [], [])

    def test_existing_output_is_preserved_and_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "existing"
            output.mkdir()
            sentinel = output / "sentinel"
            sentinel.write_text("original")
            with self.assertRaises(FileExistsError):
                EXP.run([], [], [], output, [], {}, b"", {}, {})
            self.assertEqual(sentinel.read_text(), "original")

    def test_completed_report_authenticates_retained_source_and_all_output_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "manifest.json"
            source.write_text('{"synthetic":true}\n')
            output = root / "complete"
            report = EXP.run([], [], [], output, [EXP.pin(source)], {}, b"", {}, {})
            self.assertTrue((output / "report.json").exists())
            inputs = json.loads((output / "inputs.json").read_text())
            for value in inputs["files"]:
                self.assertEqual(value["retention"], "copied_exact_bytes")
                self.assertEqual(EXP.fingerprint(output / value["retainedPath"]),
                                 {key: value[key] for key in ("bytes", "sha256")})
            self.assertTrue(any(Path(value["path"]).name == PATH.name for value in inputs["files"]))
            for relative, expected in report["artifacts"].items():
                self.assertEqual(EXP.fingerprint(output / relative), expected)

    def test_changed_retained_copy_prevents_report_completion(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "changed-copy"
            original = EXP.retain_replay_inputs
            def mutate(destination, pins):
                retained = original(destination, pins)
                (destination / retained[0]["retainedPath"]).write_text("tampered")
                return retained
            with patch.object(EXP, "retain_replay_inputs", side_effect=mutate):
                # Original pins still match. The copy is checked against its original input,
                # rather than merely hashing its later bytes into the report.
                with self.assertRaisesRegex(ValueError, "Changed input"):
                    EXP.run([], [], [], output, [], {}, b"", {}, {})
            self.assertFalse((output / "report.json").exists())

    def test_changed_input_or_failed_write_cannot_publish_final_report(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            source = base / "input"
            source.write_text("original")
            pinned = EXP.pin(source)
            source.write_text("changed")
            output = base / "changed-input"
            with self.assertRaisesRegex(ValueError, "Changed input"):
                EXP.run([], [], [], output, [pinned], {}, b"", {}, {})
            self.assertFalse((output / "report.json").exists())
            output = base / "failed-write"
            original = EXP.write_json

            def fail(path, value):
                if path.name == "models.json":
                    raise OSError("disk failure")
                original(path, value)

            with patch.object(EXP, "write_json", side_effect=fail):
                with self.assertRaisesRegex(OSError, "disk failure"):
                    EXP.run([], [], [], output, [], {}, b"", {}, {})
            self.assertFalse((output / "report.json").exists())

    def test_unicode_separators_remain_inside_physical_jsonl_record(self):
        data = (json.dumps({"name": "Élodie\u2028\u2029Name"}, ensure_ascii=False) + "\n").encode()
        self.assertEqual(EXP.json_rows(data), [{"name": "Élodie\u2028\u2029Name"}])


if __name__ == "__main__":
    unittest.main()
