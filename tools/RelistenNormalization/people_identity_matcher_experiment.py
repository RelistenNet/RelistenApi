"""Compare actual C# rules and trained identity scorers on frozen development drafts.

No scorer publishes identities. All questions, including actor-kind and unknown
questions, establish split groups before resolved identity edges enter fitting.
"""

import argparse
from collections import Counter
from dataclasses import dataclass
import hashlib
import importlib.metadata
import json
import math
from pathlib import Path
import platform
import sys
from types import ModuleType
import warnings


VERSION = "people-identity-development-comparison-v1"
FEATURE_VERSION = "people-mentions-name-artist-five-v1"
CONTEXT_VERSION = "people-identity-source-context-development-comparison-v1"
CONTEXT_FEATURE_VERSION = "people-mentions-source-context-five-v1"
NAME_BASELINE_VERSION = "people-pair-baseline-v1"
CONTEXT_BASELINE_VERSION = "people-pair-context-baseline-v1"
CONTEXT_VERSION_PINS = {
    "contextProjectionVersion": "people-source-context-v1",
    "creditExtractorVersion": "recording-credits-v4/recording-actor-evidence-v1",
    "actorEvidenceVersion": "recording-actor-evidence-v1",
    "actorProjectionVersion": "people-actor-projection-v1",
}
CONTEXT_ARTIFACT_FIELDS = {"contextFile", "contextSha256", "contextRows"}
FEATURE_ORDER = (
    "name_token_similarity", "exact_name", "artist_overlap",
    "contact_agreement", "profile_agreement",
)
IDENTITY_KINDS = {"within_recording_coreference", "cross_recording_identity"}
TARGETS = {"same_entity": 1, "different_entities": 0, "insufficient_evidence": None}
SEED = 173
MAXIMUM_FILE_BYTES = 32 * 1024 * 1024
MAXIMUM_EDGES = 10000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def fingerprint(path):
    size, digest = 0, hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(chunk)
            digest.update(chunk)
    return {"bytes": size, "sha256": digest.hexdigest()}


def read_bytes(path):
    with Path(path).open("rb") as stream:
        value = stream.read(MAXIMUM_FILE_BYTES + 1)
    require(len(value) <= MAXIMUM_FILE_BYTES, f"Input exceeds read bound: {Path(path).name}")
    return value


def json_rows(data):
    # JSON permits Unicode separators within strings; only physical LF delimits records.
    return [json.loads(line) for line in data.split(b"\n") if line.strip()]


def write_json(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")


def write_rows(path, rows):
    with Path(path).open("x", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False, sort_keys=True, allow_nan=False) + "\n")


def pin(path, expected=None):
    path = Path(path).resolve()
    value = fingerprint(path)
    if expected is not None:
        require(value == {key: expected[key] for key in ("bytes", "sha256")},
                f"Changed input: {path.name}")
    return {"path": str(path), **value}


def require_digest(path, expected):
    value = pin(path)
    require(value["sha256"] == expected, f"Unexpected digest: {Path(path).name}")
    return value


def recheck(pins):
    for value in pins:
        pin(value["path"], value)


def import_validator(path, expected_sha256=None):
    """Execute the authenticated source itself, never a timestamp-valid bytecode cache."""
    path = Path(path).resolve()
    source = read_bytes(path)
    if expected_sha256 is not None:
        require(sha256(source) == expected_sha256, "Validator executed-source digest changed")
    module = ModuleType("people_identity_bundle_validator")
    module.__file__ = str(path)
    exec(compile(source, str(path), "exec"), module.__dict__)
    return module


def resolve_input(root, bundle, key):
    path = (bundle / key).resolve() if "/" not in key else (root / key).resolve()
    require(path.is_relative_to(root), "Baseline input escapes the repository")
    return path


def validate_baseline_contract(manifest, bundle_hash):
    """Keep the original name projection distinct from the source-context export."""
    version = manifest.get("version")
    projection = {
        NAME_BASELINE_VERSION: FEATURE_VERSION,
        CONTEXT_BASELINE_VERSION: CONTEXT_FEATURE_VERSION,
    }.get(version)
    require(projection is not None
            and manifest["state"] == "completed"
            and manifest["projectionVersion"] == projection
            and manifest["actorFeaturesVersion"] == "people-actor-context-features-v1"
            and manifest["featureOrder"] == list(FEATURE_ORDER)
            and manifest["bundleManifestSha256"] == bundle_hash
            and manifest["policyActivationPermitted"] is False
            and manifest["baselineFile"] == "baseline.jsonl", "Unsupported baseline contract")
    if version == NAME_BASELINE_VERSION:
        require(not (set(CONTEXT_VERSION_PINS) | CONTEXT_ARTIFACT_FIELDS) & set(manifest),
                "Name-only baseline cannot claim source-context metadata")
        return

    require(all(manifest.get(key) == value for key, value in CONTEXT_VERSION_PINS.items()),
            "Unsupported source-context extractor/projection versions")
    require(manifest.get("contextFile") == "context-diagnostics.jsonl"
            and type(manifest.get("contextRows")) is int and manifest["contextRows"] >= 0
            and type(manifest.get("contextSha256")) is str,
            "Missing or unsupported source-context diagnostics contract")


def load_context_diagnostics(directory, manifest, mention_rows):
    """Authenticate complete inventory diagnostics; never derive model values here."""
    if manifest["version"] == NAME_BASELINE_VERSION:
        return []
    path = directory / manifest["contextFile"]
    context_pin = require_digest(path, manifest["contextSha256"])
    data = read_bytes(path)
    require(sha256(data) == context_pin["sha256"], "Source-context diagnostics changed while reading")
    rows = json_rows(data)
    require(len(rows) == manifest["contextRows"] == len(mention_rows),
            "Source-context diagnostics inventory coverage changed")
    require(all(type(row) is dict and type(row.get("mentionId")) is str for row in rows),
            "Invalid source-context diagnostics mention identity")
    identities = [row["mentionId"] for row in rows]
    require(len(set(identities)) == len(identities)
            and set(identities) == {row["mention_id"] for row in mention_rows},
            "Source-context diagnostics inventory coverage changed")
    return [context_pin]


def projection_receipt(manifest):
    keys = ("version", "projectionVersion", "actorFeaturesVersion")
    if manifest.get("version") == CONTEXT_BASELINE_VERSION:
        keys = (*keys, *CONTEXT_VERSION_PINS, *sorted(CONTEXT_ARTIFACT_FIELDS))
    return {key: manifest[key] for key in keys if key in manifest}


@dataclass(frozen=True)
class Edge:
    edge_id: str
    task_id: str
    task_kind: str
    focus_id: str
    candidate_id: str
    component: str
    target: int | None
    values: tuple
    same_recording: bool
    same_artist: bool
    comparison: dict
    projection_supported: bool = True
    projection_issue: str | None = None


def actual_features(comparison):
    require(comparison["domain"] == "people" and comparison["candidateUuid"] is None,
            "Mention comparison must not claim a canonical candidate")
    require(comparison["policyVersion"] is None and comparison["evaluationReceiptUuid"] is None
            and comparison["disposition"] != "automatic_match", "Baseline contains policy authority")
    agreements = {value["name"]: value["agreement"] for value in comparison["features"]}
    require(len(agreements) == len(comparison["features"]), "Duplicate comparison features")
    values = (comparison["nameTokenSimilarity"],
              *(float(agreements.get(name) == "agrees")
                for name in ("name", "artist_context", "actor_contact", "actor_profile")))
    require(all(type(value) in (float, int) and math.isfinite(value) and 0 <= value <= 1
                for value in values), "Invalid numeric comparison features")
    return values


def build_edges(labels, mentions, baseline, split_components):
    """Join full identity pools only after every task contributed its split bridges."""
    tasks = {task["task_id"]: task for task in labels}
    require(len(tasks) == len(labels), "Duplicate task")
    components = {}
    for members in split_components:
        component = min(members)
        for key in members:
            require(key in tasks and key not in components, "Invalid split membership")
            components[key] = component
    require(components.keys() == tasks.keys(), "Split grouping omitted a question")

    expected = {}
    for task in labels:
        require(task["task_kind"] in IDENTITY_KINDS | {"actor_kind"}, "Unsupported task kind")
        require(task["evaluation_eligible"] is False and task["policy_activation_permitted"] is False,
                "Only development questions are permitted")
        if task["task_kind"] == "actor_kind":
            continue
        candidates = task["candidate_mention_ids"]
        decisions = {value["candidate_mention_id"]: value["decision"]
                     for value in task["answer"]["candidate_decisions"]}
        require(len(set(candidates)) == len(candidates) and len(decisions) == len(candidates)
                and decisions.keys() == set(candidates), "Question has an incomplete candidate pool")
        for candidate in candidates:
            require(decisions[candidate] in TARGETS, "Unsupported identity target")
            expected[(task["task_id"], candidate)] = (task, decisions[candidate])

    require(len(baseline) == len(expected) <= MAXIMUM_EDGES, "Baseline candidate coverage changed")
    result, seen = [], set()
    allowed = {"taskId", "taskKind", "focusMentionId", "candidateMentionId", "focusEvidenceId",
               "candidateEvidenceId", "sameRecording", "declaredCandidateCount", "features", "comparison",
               "projectionSupported", "projectionIssue"}
    for row in baseline:
        require(set(row) == allowed, "Unexpected baseline fields; labels are not features")
        key = (row["taskId"], row["candidateMentionId"])
        require(key in expected and key not in seen, "Unexpected or duplicate baseline candidate")
        seen.add(key)
        task, decision = expected[key]
        focus, candidate = mentions[task["focus_mention_id"]], mentions[key[1]]
        same_recording = focus["evidence_id"] == candidate["evidence_id"]
        supported = focus["mention_kind"] == candidate["mention_kind"] == "name"
        require(row["taskKind"] == task["task_kind"]
                and row["focusMentionId"] == task["focus_mention_id"]
                and row["focusEvidenceId"] == focus["evidence_id"]
                and row["candidateEvidenceId"] == candidate["evidence_id"]
                and type(row["sameRecording"]) is bool and row["sameRecording"] == same_recording
                and type(row["declaredCandidateCount"]) is int
                and row["declaredCandidateCount"] == len(task["candidate_mention_ids"])
                and type(row["projectionSupported"]) is bool and row["projectionSupported"] == supported
                and row["projectionIssue"] == (None if supported else "unsupported_mention_expression"),
                "Baseline identity/pool metadata changed")
        values = actual_features(row["comparison"])
        require(type(row["features"]) is list and len(row["features"]) == len(FEATURE_ORDER)
                and all(type(value) in (int, float) for value in row["features"])
                and tuple(row["features"]) == values, "Exported features differ from actual matcher")
        result.append(Edge(
            f'{key[0]}|{key[1]}', key[0], task["task_kind"], task["focus_mention_id"], key[1],
            components[key[0]], TARGETS[decision], values, same_recording,
            focus["artist"]["uuid"] == candidate["artist"]["uuid"], row["comparison"],
            supported, row["projectionIssue"],
        ))
    return sorted(result, key=lambda row: row.edge_id)


def load_inputs(root, bundle, baseline_directory, manifest_hash, validator_hash, baseline_hash):
    manifest_path = bundle / "manifest.json"
    pins = [require_digest(manifest_path, manifest_hash),
            require_digest(bundle / "validate_bundle.py", validator_hash),
            require_digest(baseline_directory / "manifest.json", baseline_hash)]
    manifest = json.loads(read_bytes(manifest_path))
    require(manifest["usage"] == "development_only" and manifest["policy_activation_permitted"] is False,
            "Bundle is not development-only")
    for name, value in manifest["files"].items():
        pins.append(pin(bundle / name, value))
    for name in ("original_selected_evidence", "original_collision_tasks", "reservation_index"):
        value = manifest[name]
        pins.append(pin(root / value["path"], value))
    review = manifest["independent_review"]["input"]
    pins.append(pin(root / review["path"], review))

    labels_bytes, mentions_bytes = read_bytes(bundle / "labels.jsonl"), read_bytes(bundle / "mentions.jsonl")
    baseline_manifest = json.loads(read_bytes(baseline_directory / "manifest.json"))
    validate_baseline_contract(baseline_manifest, manifest_hash)
    require(bool(baseline_manifest["inputFiles"]) and bool(baseline_manifest["artifacts"]),
            "Baseline lacks input or executable evidence")
    for name, value in baseline_manifest["inputFiles"].items():
        path = resolve_input(root, bundle, name)
        pins.append(pin(path, value))
        if path.suffix == ".jsonl" and value.get("rows") is not None:
            # External files can include held-out records. Count physical lines as bytes only.
            with path.open("rb") as stream:
                rows = sum(bool(line.strip()) for line in stream)
            require(rows == value["rows"], "Baseline input row count changed")
    for artifact in baseline_manifest["artifacts"]:
        pins.append(pin(artifact["path"], artifact))
    executable_hashes = {value["sha256"] for value in baseline_manifest["artifacts"]}
    require({baseline_manifest["matcherAssemblySha256"], baseline_manifest["runnerAssemblySha256"]}
            <= executable_hashes, "Baseline executable hashes lack artifacts")
    baseline_path = baseline_directory / "baseline.jsonl"
    pins.append(require_digest(baseline_path, baseline_manifest["baselineSha256"]))
    baseline_bytes = read_bytes(baseline_path)
    mention_rows = json_rows(mentions_bytes)
    pins.extend(load_context_diagnostics(baseline_directory, baseline_manifest, mention_rows))

    validator = import_validator(bundle / "validate_bundle.py", validator_hash)
    validation = validator.validate_bundle(bundle, root)
    recheck(pins)
    labels = json_rows(labels_bytes)
    mentions = {row["mention_id"]: row for row in mention_rows}
    require(len(mentions) == len(mention_rows), "Duplicate inventoried mention")
    components = validator.build_split_components(labels)
    baseline_rows = json_rows(baseline_bytes)
    edges = build_edges(labels, mentions, baseline_rows, components)
    require(baseline_manifest["allTasks"] == len(labels)
            and baseline_manifest["questions"] == len({row.task_id for row in edges})
            and baseline_manifest["pairs"] == len(edges), "Baseline reported coverage differs")
    return edges, components, labels, pins, validation, baseline_bytes, baseline_manifest


def sigmoid(value):
    return 1 / (1 + math.exp(-value)) if value >= 0 else math.exp(value) / (1 + math.exp(value))


def validate_portable(model):
    require(model["featureOrder"] == list(FEATURE_ORDER) and model["classes"] == [0, 1],
            "Unsupported portable feature/class contract")
    require(model.get("projectionVersion", FEATURE_VERSION)
            in (FEATURE_VERSION, CONTEXT_FEATURE_VERSION), "Unsupported portable projection version")
    if model["version"] == "people-five-feature-logistic-v1":
        require(len(model["coefficients"]) == len(FEATURE_ORDER)
                and all(type(v) in (int, float) and math.isfinite(v)
                        for v in [model["intercept"], *model["coefficients"]]),
                "Invalid logistic parameters")
        return
    require(model["version"] == "people-five-feature-boosted-v1"
            and math.isfinite(model["initialLogit"]) and 0 < model["learningRate"] <= 1
            and 0 < len(model["trees"]) <= 100, "Invalid boosted parameters")
    for tree in model["trees"]:
        count = len(tree["values"])
        require(0 < count <= 1024 and all(len(tree[key]) == count
                for key in ("left", "right", "feature", "threshold")), "Invalid tree dimensions")
        require(all(type(v) in (int, float) and math.isfinite(v)
                    for v in [*tree["values"], *tree["threshold"]]), "Invalid tree numbers")
        visited, pending = set(), [0]
        while pending:
            index = pending.pop()
            require(type(index) is int and 0 <= index < count and index not in visited,
                    "Tree has a cycle, repeated parent or invalid child")
            visited.add(index)
            left, right = tree["left"][index], tree["right"][index]
            if left == right == -1:
                continue
            require(type(tree["feature"][index]) is int and 0 <= tree["feature"][index] < len(FEATURE_ORDER),
                    "Invalid tree feature")
            pending.extend([left, right])
        require(len(visited) == count, "Tree contains unreachable nodes")


def portable_logit(model, values):
    """Evaluate exported JSON without importing NumPy, joblib or scikit-learn."""
    validate_portable(model)
    require(len(values) == len(FEATURE_ORDER)
            and all(type(v) in (int, float) and math.isfinite(v) and 0 <= v <= 1 for v in values),
            "Invalid inference vector")
    if model["version"] == "people-five-feature-logistic-v1":
        return model["intercept"] + sum(a * b for a, b in zip(model["coefficients"], values))
    # sklearn trees route float32 input values; preserve that contract without NumPy.
    import struct
    inputs = [struct.unpack("f", struct.pack("f", value))[0] for value in values]
    score = model["initialLogit"]
    for tree in model["trees"]:
        index = 0
        while tree["left"][index] != -1:
            index = (tree["left"][index] if inputs[tree["feature"][index]] <= tree["threshold"][index]
                     else tree["right"][index])
        score += model["learningRate"] * tree["values"][index]
    return score


def new_estimators():
    from sklearn.ensemble import GradientBoostingClassifier
    from sklearn.linear_model import LogisticRegression
    return {
        "logistic": LogisticRegression(C=1, l1_ratio=0, solver="lbfgs", max_iter=1000, random_state=SEED),
        "boosted_tree": GradientBoostingClassifier(n_estimators=20, max_depth=1, min_samples_leaf=2,
                                                   learning_rate=0.05, random_state=SEED),
    }


def export_model(name, model, training, projection_version=FEATURE_VERSION):
    result = {"featureOrder": list(FEATURE_ORDER), "classes": model.classes_.tolist(),
              "trainingEdgeIds": [row.edge_id for row in training], "scoreKind": "raw_logit",
              "projectionVersion": projection_version}
    if name == "logistic":
        require(model.n_iter_.max() < model.max_iter, "Logistic fit did not converge")
        coefficients = model.coef_[0].tolist()
        result.update(version="people-five-feature-logistic-v1", intercept=float(model.intercept_[0]),
                      coefficients=coefficients, parameters={"C": 1, "penalty": "l2", "fitIntercept": True},
                      runtimeScorerParameters=dict(zip(
                          ("intercept", "nameSimilarityWeight", "exactNameWeight", "artistOverlapWeight",
                           "contactAgreementWeight", "profileAgreementWeight"),
                          [float(model.intercept_[0]), *coefficients])))
    else:
        prior = model.init_.class_prior_
        result.update(version="people-five-feature-boosted-v1", initialLogit=float(math.log(prior[1] / prior[0])),
                      learningRate=float(model.learning_rate),
                      parameters={"nEstimators": 20, "maxDepth": 1, "minSamplesLeaf": 2}, trees=[{
                          "left": tree.tree_.children_left.tolist(), "right": tree.tree_.children_right.tolist(),
                          "feature": tree.tree_.feature.tolist(), "threshold": tree.tree_.threshold.tolist(),
                          "values": tree.tree_.value[:, 0, 0].tolist(),
                      } for tree in model.estimators_[:, 0]])
    validate_portable(result)
    return result


def fit_models(training, checked, projection_version=FEATURE_VERSION):
    if {row.target for row in training} != {0, 1}:
        return None
    import numpy as np
    from sklearn.exceptions import ConvergenceWarning
    models = {}
    for name, estimator in new_estimators().items():
        with warnings.catch_warnings():
            warnings.simplefilter("error", ConvergenceWarning)
            estimator.fit(np.asarray([row.values for row in training]), [row.target for row in training])
        model = export_model(name, estimator, training, projection_version)
        # A JSON round trip is part of export verification, not estimator reuse.
        model = json.loads(json.dumps(model, allow_nan=False))
        values = np.asarray([row.values for row in checked])
        raw = estimator.decision_function(values)
        probabilities = estimator.predict_proba(values)[:, 1]
        for row, expected_raw, expected_probability in zip(checked, raw, probabilities):
            actual = portable_logit(model, row.values)
            require(math.isclose(actual, float(expected_raw), abs_tol=1e-12, rel_tol=1e-12)
                    and math.isclose(sigmoid(actual), float(expected_probability), abs_tol=1e-12, rel_tol=1e-12),
                    "Portable JSON inference differs from fitted estimator")
        models[name] = model
    return models


def compare(edges, components, projection_version=FEATURE_VERSION):
    require(projection_version in (FEATURE_VERSION, CONTEXT_FEATURE_VERSION),
            "Unsupported training projection version")
    resolved = [row for row in edges if row.target is not None and row.projection_supported]
    final = fit_models(resolved, edges, projection_version)
    models = {name: {"final": final[name] if final else None, "folds": {}}
              for name in ("logistic", "boosted_tree")}
    folds, predictions = [], []
    by_component = {}
    for row in edges:
        by_component.setdefault(row.component, []).append(row)
    for members in components:
        component = min(members)
        held = by_component.get(component, [])
        training = [row for row in resolved if row.component != component]
        if not held:
            state, fitted = "no_identity_edges", None
        elif not any(row.projection_supported for row in held):
            state, fitted = "unsupported_projection_only", None
        elif any(row.target is not None and row.projection_supported for row in held):
            fitted = fit_models(training, held, projection_version)
            state = "held_component_fit" if fitted else "unscorable_one_class_training"
        else:
            fitted = final
            state = "all_resolved_fit_no_resolved_held_edges" if fitted else "unscorable_one_class_training"
        folds.append({"component": component, "allTaskIds": members,
                      "heldEdgeIds": [row.edge_id for row in held],
                      "trainingEdgeIds": [row.edge_id for row in training],
                      "trainingClassCounts": dict(Counter(str(row.target) for row in training)), "state": state})
        if fitted and state == "held_component_fit":
            for name in models:
                models[name]["folds"][component] = fitted[name]
        for row in held:
            scores = {}
            for name in models:
                raw = portable_logit(fitted[name], row.values) if fitted and row.projection_supported else None
                scores[name] = {"rawLogit": raw, "uncalibratedSigmoid": sigmoid(raw) if raw is not None else None}
            predictions.append({"edgeId": row.edge_id, "taskId": row.task_id,
                                "projectionVersion": projection_version,
                                "candidateMentionId": row.candidate_id, "component": component,
                                "taskKind": row.task_kind, "sameRecording": row.same_recording,
                                "sameArtist": row.same_artist, "target": row.target,
                                "projectionSupported": row.projection_supported, "projectionIssue": row.projection_issue,
                                "targetStatus": "unknown_not_negative" if row.target is None else "draft_resolved",
                                "fitState": state, "scores": scores,
                                "ruleComparison": row.comparison})
    return models, folds, sorted(predictions, key=lambda row: row["edgeId"])


def summarize_pools(predictions):
    questions = {}
    for row in predictions:
        questions.setdefault(row["taskId"], []).append(row)
    result = []
    for task, rows in sorted(questions.items()):
        by_model = {}
        for name in ("logistic", "boosted_tree"):
            scored = [row for row in rows if row["scores"][name]["rawLogit"] is not None]
            maximum = max((row["scores"][name]["rawLogit"] for row in scored), default=None)
            positives = [row["scores"][name]["rawLogit"] for row in scored if row["target"] == 1]
            best_positive = max(positives, default=None)
            unknowns = [row for row in scored if row["target"] is None]
            by_model[name] = {
                "topCandidateMentionIds": [row["candidateMentionId"] for row in scored
                                           if row["scores"][name]["rawLogit"] == maximum],
                "unscorableCandidates": len(rows) - len(scored),
                "unknownCandidatesAtDiagnosticHalf": sum(row["scores"][name]["rawLogit"] >= 0 for row in unknowns),
                "unknownRivalsAtOrAboveBestKnownPositive": None if best_positive is None else
                    sum(row["scores"][name]["rawLogit"] >= best_positive for row in unknowns),
            }
        result.append({"taskId": task, "taskKind": rows[0]["taskKind"], "candidateCount": len(rows),
                       "targets": dict(Counter("unknown" if row["target"] is None else str(row["target"]) for row in rows)),
                       "models": by_model})
    return result


def cohort_counts(edges):
    def counts(rows):
        return {"edges": len(rows), "same": sum(row.target == 1 for row in rows),
                "different": sum(row.target == 0 for row in rows),
                "unknown": sum(row.target is None for row in rows)}
    return {"allIdentityEdges": counts(edges),
            "crossRecordingQuestionPools": counts([row for row in edges if row.task_kind == "cross_recording_identity"]),
            "trueCrossRecordingEdges": counts([row for row in edges if not row.same_recording]),
            "crossArtistEdges": counts([row for row in edges if not row.same_artist])}


def diagnostics(predictions):
    result = {}
    for name in ("logistic", "boosted_tree"):
        resolved = [row for row in predictions if row["target"] is not None and row["projectionSupported"]]
        scored = [row for row in resolved if row["scores"][name]["rawLogit"] is not None]
        loss = sum(max(raw := row["scores"][name]["rawLogit"], 0)
                   - row["target"] * raw + math.log1p(math.exp(-abs(raw))) for row in scored)
        result[name] = {"resolvedDraftEdges": len(resolved), "scoredDraftEdges": len(scored),
                        "unsupportedResolvedEdgesExcluded": sum(row["target"] is not None
                            and not row["projectionSupported"] for row in predictions),
                        "unscorableDraftEdges": len(resolved) - len(scored),
                        "draftDisagreementsAtDiagnosticHalf": sum(
                            int(row["scores"][name]["rawLogit"] >= 0) != row["target"] for row in scored),
                        "draftMeanLogLoss": loss / len(scored) if scored else None}
    return result


def environment(root, install_hash, lock_hash):
    base = root / "local-dev/normalization-data/venue-matcher-environment-v1"
    pins = [require_digest(base / "install-report.json", install_hash),
            require_digest(base / "requirements.lock.txt", lock_hash), pin(Path(sys.executable).resolve())]
    expected = {}
    for line in read_bytes(base / "requirements.lock.txt").decode().splitlines():
        if line.strip():
            name, version = line.split("==")
            expected[name] = version
    require(expected.get("scikit-learn") == "1.9.1", "Unsupported scikit-learn environment")
    installed = {name: importlib.metadata.version(name) for name in expected}
    require(installed == expected and Path(sys.prefix).resolve() == base.resolve(),
            "Use the pinned venue matcher environment")
    return {"python": platform.python_version(), "platform": platform.platform(),
            "executable": str(Path(sys.executable).resolve()), "packages": installed}, pins


def retain_replay_inputs(output, pins):
    """Keep small executed inputs recoverable; large frozen captures stay external."""
    retained = []
    for value in pins:
        reference = dict(value)
        if value["bytes"] <= MAXIMUM_FILE_BYTES:
            relative = Path("retained-inputs") / value["sha256"] / Path(value["path"]).name
            target = output / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.exists():
                data = read_bytes(value["path"])
                require(len(data) == value["bytes"] and sha256(data) == value["sha256"],
                        "Input changed before its retained copy")
                with target.open("xb") as stream:
                    stream.write(data)
            pin(target, value)
            reference.update(retainedPath=str(relative), retention="copied_exact_bytes")
        else:
            reference["retention"] = "external_frozen_capture_hash_reference"
        retained.append(reference)
    return retained


def run(edges, components, labels, output, pins, validation, baseline_bytes, baseline_manifest, installed):
    output.mkdir(parents=True, exist_ok=False)
    source_pin = pin(Path(__file__))
    pins = [*pins, source_pin]
    recheck(pins)
    retained = retain_replay_inputs(output, pins)
    projection_version = baseline_manifest.get("projectionVersion", FEATURE_VERSION)
    models, folds, predictions = compare(edges, components, projection_version)
    features = [{"edgeId": row.edge_id, "taskId": row.task_id, "candidateMentionId": row.candidate_id,
                 "featureVersion": projection_version, "values": list(row.values)} for row in edges]
    write_rows(output / "features.jsonl", features)
    write_json(output / "splits.json", {"allTaskCount": len(labels), "components": folds})
    write_json(output / "models.json", models)
    write_rows(output / "predictions.jsonl", predictions)
    write_rows(output / "unknown-predictions.jsonl", [row for row in predictions if row["target"] is None])
    write_rows(output / "candidate-pools.jsonl", summarize_pools(predictions))
    write_json(output / "environment.json", installed)
    (output / "baseline.jsonl").write_bytes(baseline_bytes)
    write_json(output / "baseline-manifest.json", baseline_manifest)
    write_json(output / "inputs.json", {"files": retained, "validation": validation})
    artifacts = {str(path.relative_to(output)): fingerprint(path)
                 for path in sorted(output.rglob("*")) if path.is_file()}
    report = {
        "status": "completed_development_diagnostic",
        "version": CONTEXT_VERSION if projection_version == CONTEXT_FEATURE_VERSION else VERSION,
        "featureOrder": list(FEATURE_ORDER), "featureVersion": projection_version,
        "baselineContract": projection_receipt(baseline_manifest), "seed": SEED, "counts": cohort_counts(edges),
        "allTasks": len(labels), "actorKindTasksExcludedFromFitting": sum(task["task_kind"] == "actor_kind" for task in labels),
        "components": len(components), "identityQuestionPools": len({row.task_id for row in edges}),
        "trueCrossRecordingPositiveComponents": len({row.component for row in edges
                                                     if not row.same_recording and row.target == 1}),
        "diagnostics": diagnostics(predictions),
        "rules": {"origin": "actual_CSharp_ComparePeople_without_policy",
                  "dispositions": dict(Counter(row.comparison["disposition"] for row in edges)),
                  "supportedRules": dict(Counter(rule for row in edges for rule in row.comparison["supportedRules"]))},
        "artifacts": artifacts, "inputsSha256": artifacts["inputs.json"]["sha256"],
        "identityAccuracyEstablished": False, "probabilitiesCalibrated": False,
        "policyActivationPermitted": False, "creationImplemented": False,
        "limits": [
            "All labels remain development drafts with adjudication pending; correlated edges are not independent identities.",
            "The true cross-recording positives cover one Aadam project-owner family and contain no different-person negatives.",
            ("Source-derived contacts and profiles preserve local attachment uncertainty and do not establish identity or account ownership."
             if projection_version == CONTEXT_FEATURE_VERSION else
             "Names and artist context do not supply qualified identity, contact, profile or complete global retrieval evidence."),
            "Unknown edges never enter fitting or binary losses; their complete pools and diagnostic scores remain visible.",
            "One-class training folds are unscorable, never replaced with a fit that includes their held-out component.",
            "The 0.5 sigmoid cutoff is a fixed development diagnostic, not an acceptance threshold or identity assertion.",
            "Portable logistic raw weights have no publication threshold, runner-up margin, creation capability or evaluation attestation.",
        ],
    }
    write_json(output / "report.pending.json", report)
    recheck(pins)
    for value in retained:
        if "retainedPath" in value:
            pin(output / value["retainedPath"], value)
    for name, value in artifacts.items():
        pin(output / name, value)
    (output / "report.pending.json").rename(output / "report.json")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--manifest-sha256", required=True)
    parser.add_argument("--validator-sha256", required=True)
    parser.add_argument("--baseline-manifest-sha256", required=True)
    parser.add_argument("--environment-install-sha256", required=True)
    parser.add_argument("--environment-lock-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root, bundle, baseline = args.root.resolve(), args.bundle.resolve(), args.baseline.resolve()
    require(bundle.is_relative_to(root) and baseline.is_relative_to(root), "Inputs must remain in the repository")
    source_pin = pin(Path(__file__))
    installed, environment_pins = environment(root, args.environment_install_sha256, args.environment_lock_sha256)
    inputs = load_inputs(root, bundle, baseline, args.manifest_sha256, args.validator_sha256,
                         args.baseline_manifest_sha256)
    edges, components, labels, pins, validation, baseline_bytes, baseline_manifest = inputs
    report = run(edges, components, labels, args.output, [*pins, *environment_pins, source_pin],
                 validation, baseline_bytes, baseline_manifest, installed)
    print(json.dumps({"report": str(args.output / "report.json"), "status": report["status"],
                      "counts": report["counts"], "diagnostics": report["diagnostics"]}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, KeyError, TypeError, RuntimeError) as error:
        raise SystemExit(f"Experiment failed without completion: {error}")
