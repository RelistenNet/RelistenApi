"""Fit two small venue-pair scorers on frozen development drafts, never a publication policy."""

import argparse
import contextlib
from dataclasses import dataclass
import hashlib
import importlib.metadata
import importlib.util
import io
import json
import math
from pathlib import Path
import platform
import unicodedata

VERSION = "venue-development-comparison-v1"
FEATURE_VERSION = "venue-raw-wording-six-v1"
SKLEARN_VERSION = "1.9.1"
SEED = 173
FEATURE_NAMES = (
    "name_key_equal", "name_token_jaccard", "name_trigram_dice",
    "both_raw_locations_present", "raw_location_key_equal", "raw_location_trigram_dice",
)
REPO = Path(__file__).resolve().parents[2]
DEFAULT_BUNDLE = REPO / "docs/research/2026-10-17-full-normalization/training/venues-development-v1"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_rows(path):
    with path.open() as stream:
        return [json.loads(line) for line in stream]


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True, allow_nan=False) + "\n")


def write_rows(path, values):
    path.write_text("".join(json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False) + "\n" for value in values))


def bundle_validator(bundle=DEFAULT_BUNDLE):
    spec = importlib.util.spec_from_file_location("venue_draft_validator", bundle / "validate_labels.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@dataclass(frozen=True)
class VenueWording:
    name: str
    raw_location: str


@dataclass(frozen=True)
class Example:
    task_id: str
    component: str
    target: int | None
    values: tuple[float, ...]
    evidence: tuple[dict, ...]
    missingness: dict


def wording_key(text):
    """A lexical key only. It resolves no city, address, country or physical venue."""
    text = unicodedata.normalize("NFC", text)
    words, current = [], []
    for character in text:
        category = unicodedata.category(character)
        if character.isalpha() or character.isdigit() or category == "Mn":
            current.append(character.lower())
        elif current:
            words.append("".join(current))
            current = []
    if current:
        words.append("".join(current))
    return " ".join(words)


def trigram_dice(left, right):
    if not left or not right:
        return 0.0
    # Boundary markers let short names contribute without equating absent strings.
    def grams(text):
        padded = "^^" + text + "$$"
        return {padded[index:index + 3] for index in range(len(padded) - 2)}
    a, b = grams(left), grams(right)
    return 2 * len(a & b) / (len(a) + len(b))


def raw_location_key(text):
    key = wording_key(text)
    return "" if key == "unknown location" else key


def lexical_features(left, right):
    a, b = wording_key(left.name), wording_key(right.name)
    tokens_a, tokens_b = set(a.split()), set(b.split())
    union = tokens_a | tokens_b
    location_a, location_b = raw_location_key(left.raw_location), raw_location_key(right.raw_location)
    both_locations = bool(location_a and location_b)
    return (
        float(bool(a and b) and a == b),
        len(tokens_a & tokens_b) / len(union) if union else 0.0,
        trigram_dice(a, b),
        float(both_locations),
        float(both_locations and location_a == location_b),
        trigram_dice(location_a, location_b) if both_locations else 0.0,
    )


def build_examples(labels, evidence, links, validator):
    # Unknown tasks participate in grouping before resolved training rows are selected.
    components = validator.build_split_components(labels, links)
    examples = []
    for label in labels:
        require(label["usage"] == "development_experiments_only", "Non-development label")
        require(label["evaluation_eligible"] is False, "Scored holdout supplied to development fit")
        require(label["family_partition"] == "development", "Held-out task")
        pair, provenance, missingness = [], [], {}
        for side, evidence_id in enumerate(label["evidence_ids"]):
            item = evidence[evidence_id]
            require(item["artist_partition"] == item["family_partition"] == "development", "Held-out evidence")
            fields = {field["path"]: field for field in item["fields"]}
            name, location = fields["/raw_record/row/name"], fields["/raw_record/row/location"]
            for field in [name, location]:
                require(hashlib.sha256(field["text"].encode()).hexdigest() == field["text_sha256"], "Changed field text")
            wording = VenueWording(name["text"], location["text"])
            require(bool(wording_key(wording.name)), "Blank venue names are outside this narrow fitting contract")
            pair.append(wording)
            missingness[f"side_{side}_name"] = not bool(wording_key(wording.name))
            missingness[f"side_{side}_raw_location"] = not bool(raw_location_key(wording.raw_location))
            provenance.append({
                "evidence_id": evidence_id, "resource": item["resource"],
                "source_line_sha256": item["source_line_sha256"],
                "fields": [{"path": field["path"], "text_sha256": field["text_sha256"]} for field in [name, location]],
            })
        require(len(pair) == 2, "Venue scorer requires exactly two observations")
        decision = label["answer"]["decision"]
        require(decision in {"same_entity", "different_entities", "insufficient_evidence"}, "Unknown label decision")
        target = {"same_entity": 1, "different_entities": 0, "insufficient_evidence": None}[decision]
        examples.append(Example(label["task_id"], components[label["task_id"]], target,
                                lexical_features(*pair), tuple(provenance), missingness))
    require(len({row.task_id for row in examples}) == len(examples), "Duplicate task")
    return examples


def load_examples(bundle, frozen_manifest, frozen_labels, frozen_links):
    pins = {"manifest.json": frozen_manifest, "labels.jsonl": frozen_labels,
            "split-links.jsonl": frozen_links}
    manifest_bytes = (bundle / "manifest.json").read_bytes()
    require(hashlib.sha256(manifest_bytes).hexdigest() == frozen_manifest, "Frozen input changed: manifest.json")
    manifest = json.loads(manifest_bytes)
    captured = {name: (bundle / name).read_bytes() for name in manifest["files"]}
    captured["manifest.json"] = manifest_bytes
    for name, expected in pins.items():
        require(hashlib.sha256(captured[name]).hexdigest() == expected, f"Frozen input changed: {name}")
    for name, expected in manifest["files"].items():
        require(hashlib.sha256(captured[name]).hexdigest() == expected["sha256"], f"Changed bundle file: {name}")

    validator_bytes = (bundle / "validate_labels.py").read_bytes()
    validator = bundle_validator(bundle)
    validation_output = io.StringIO()
    with contextlib.redirect_stdout(validation_output):
        validator.validate_bundle(bundle, validator.RAW, REPO / manifest["local_supplement"])
    # Validation reads the original annotation pack. Refuse a changed bundle, then
    # derive all training records from the exact bytes pinned before validation.
    for name, original in captured.items():
        require((bundle / name).read_bytes() == original, f"Frozen bundle changed during validation: {name}")
    require((bundle / "validate_labels.py").read_bytes() == validator_bytes, "Bundle validator changed during validation")
    def captured_rows(name):
        return [json.loads(line) for line in io.BytesIO(captured[name])]
    labels = captured_rows("labels.jsonl")
    evidence = {row["evidence_id"]: row for row in captured_rows("evidence.jsonl")}
    links = captured_rows("split-links.jsonl")
    examples = build_examples(labels, evidence, links, validator)
    provenance = {
        "bundle": str(bundle),
        "files": {name: hashlib.sha256(captured[name]).hexdigest() for name in manifest["files"]},
        "manifest_sha256": frozen_manifest, "requested_frozen_hashes": pins,
        "validator_sha256": hashlib.sha256(validator_bytes).hexdigest(),
        "bundle_validation": json.loads(validation_output.getvalue()),
    }
    return examples, provenance


def load_baseline(directory, examples, provenance):
    manifest_bytes = (directory / "manifest.json").read_bytes()
    manifest = json.loads(manifest_bytes)
    require(manifest["state"] == "completed" and manifest["version"] == "venue-pair-baseline-v1", "Incomplete or unsupported C# baseline")
    require(manifest["projectionVersion"] == "venue-retained-names-only-v1", "Unsupported baseline projection")
    require(manifest["bundleManifestSha256"] == provenance["manifest_sha256"], "Baseline used another venue bundle")
    for name in ["labels.jsonl", "evidence.jsonl", "split-links.jsonl"]:
        require(manifest["inputFiles"][name]["sha256"] == provenance["files"][name], f"Baseline input differs: {name}")
    require(manifest["outputFile"] == "baseline.jsonl", "Unexpected baseline output file")
    data = (directory / "baseline.jsonl").read_bytes()
    require(hashlib.sha256(data).hexdigest() == manifest["outputSha256"], "Baseline output hash differs")
    rows = [json.loads(line) for line in io.BytesIO(data)]
    by_task = {row["task_id"]: row for row in rows}
    require(len(rows) == len(by_task) == len(examples) == manifest["pairs"], "Baseline coverage differs")
    require(by_task.keys() == {row.task_id for row in examples}, "Baseline task identities differ")
    for example in examples:
        row = by_task[example.task_id]
        require(row["evidence_ids"] == [item["evidence_id"] for item in example.evidence], "Baseline evidence identities differ")
        comparison = row["comparison"]
        require(comparison["domain"] == "venues" and comparison["candidateUuid"] is None, "Unexpected baseline domain or canonical identity")
        require(comparison["policyVersion"] is None and comparison["evaluationReceiptUuid"] is None, "Baseline unexpectedly activated a policy")
    return {"rows": rows, "bytes": data, "manifest_bytes": manifest_bytes, "receipt": {
        "status": "actual_csharp_export_verified", "manifest_sha256": hashlib.sha256(manifest_bytes).hexdigest(),
        "output_sha256": manifest["outputSha256"], "pairs": len(rows),
        "version": manifest["version"], "projection_version": manifest["projectionVersion"],
        "matcher_assembly_sha256": manifest["matcherAssemblySha256"],
        "runner_assembly_sha256": manifest["runnerAssemblySha256"],
    }}


def new_models():
    from sklearn.ensemble import GradientBoostingClassifier
    from sklearn.linear_model import LogisticRegression
    return {
        "logistic": LogisticRegression(C=1.0, l1_ratio=0.0, solver="lbfgs", max_iter=1000, random_state=SEED),
        "boosted_tree": GradientBoostingClassifier(n_estimators=20, max_depth=1, min_samples_leaf=2,
                                                   learning_rate=0.05, random_state=SEED),
    }


def export_model(model):
    result = {"class": type(model).__name__, "parameters": model.get_params(), "classes": model.classes_.tolist()}
    if hasattr(model, "coef_"):
        result.update({"coefficients": model.coef_.tolist(), "intercept": model.intercept_.tolist(),
                       "iterations": model.n_iter_.tolist()})
    else:
        result["initial_class_prior"] = model.init_.class_prior_.tolist()
        result["trees"] = [{
            "children_left": tree.tree_.children_left.tolist(), "children_right": tree.tree_.children_right.tolist(),
            "feature": tree.tree_.feature.tolist(), "threshold": tree.tree_.threshold.tolist(),
            "value": tree.tree_.value.tolist(), "node_samples": tree.tree_.n_node_samples.tolist(),
        } for tree in model.estimators_[:, 0]]
    return result


def fit_comparison(examples):
    import numpy as np
    resolved = [row for row in examples if row.target is not None]
    require({row.target for row in resolved} == {0, 1}, "Fitting requires both resolved classes")
    folds, models, final_models = [], {}, new_models()
    for name, model in final_models.items():
        model.fit(np.asarray([row.values for row in resolved]), [row.target for row in resolved])
        models[name] = {"final": export_model(model), "folds": {}}
    scores = {row.task_id: {} for row in examples}
    for component in sorted({row.component for row in examples}):
        held = [row for row in examples if row.component == component]
        train = [row for row in resolved if row.component != component]
        has_resolved_peers = any(row.target is not None for row in held)
        require({row.target for row in train} == {0, 1}, "A component leaves fewer than two training classes")
        fold_models = new_models() if has_resolved_peers else final_models
        if has_resolved_peers:
            for name, model in fold_models.items():
                model.fit(np.asarray([row.values for row in train]), [row.target for row in train])
                models[name]["folds"][component] = export_model(model)
        folds.append({
            "component": component, "held_tasks": [row.task_id for row in held],
            "training_tasks": [row.task_id for row in train],
            "training_positive": sum(row.target == 1 for row in train),
            "training_negative": sum(row.target == 0 for row in train),
            "model": "held_component_fit" if has_resolved_peers else "all_resolved_fit_no_resolved_peers_in_component",
        })
        x = np.asarray([row.values for row in held])
        for name, model in fold_models.items():
            for row, score in zip(held, model.predict_proba(x)[:, 1]):
                require(math.isfinite(float(score)), "Non-finite model score")
                scores[row.task_id][name] = float(score)
    predictions = [{
        "task_id": row.task_id, "component": row.component, "target": row.target,
        "target_status": "unknown_not_negative" if row.target is None else "draft_resolved",
        "scores": scores[row.task_id],
        "diagnostic_at_half": None if row.target is None else {name: int(score >= 0.5) for name, score in scores[row.task_id].items()},
    } for row in examples]
    errors = [row for row in predictions if row["target"] is not None and
              any(prediction != row["target"] for prediction in row["diagnostic_at_half"].values())]
    losses = {}
    for name in final_models:
        rows = [row for row in predictions if row["target"] is not None]
        loss = sum(-(row["target"] * math.log(max(row["scores"][name], 1e-15)) +
                     (1 - row["target"]) * math.log(max(1 - row["scores"][name], 1e-15))) for row in rows) / len(rows)
        losses[name] = {"draft_target_mean_log_loss": loss,
                        "draft_target_disagreements_at_half": sum(row["diagnostic_at_half"][name] != row["target"] for row in rows)}
    return {"models": models, "final_estimators": final_models, "splits": folds,
            "predictions": predictions, "errors": errors, "diagnostics": losses}


def environment():
    versions = {name: importlib.metadata.version(name) for name in
                ["scikit-learn", "numpy", "scipy", "joblib", "threadpoolctl", "narwhals"]}
    require(versions["scikit-learn"] == SKLEARN_VERSION, f"Requires scikit-learn=={SKLEARN_VERSION}")
    return {"python": platform.python_version(), "platform": platform.platform(), "packages": versions}


def run(examples, provenance, output, install_receipt=None, baseline=None):
    import joblib
    installed = environment()
    # No existing artifact is overwritten. A failed run can leave partial files, but
    # only a final report indicates completion; retry in another new directory.
    output.mkdir(parents=True, exist_ok=False)
    result = fit_comparison(examples)
    write_rows(output / "features.jsonl", [{
        "task_id": row.task_id, "component": row.component, "target": row.target,
        "features": dict(zip(FEATURE_NAMES, row.values)), "missingness": row.missingness,
        "evidence": row.evidence, "resolved_geographic_identity_used": False,
    } for row in examples])
    write_json(output / "splits.json", result["splits"])
    write_json(output / "models.json", result["models"])
    joblib.dump(result["final_estimators"], output / "final-models.joblib")
    write_rows(output / "predictions.jsonl", result["predictions"])
    write_rows(output / "errors.jsonl", result["errors"])
    write_rows(output / "unknown-predictions.jsonl", [row for row in result["predictions"] if row["target"] is None])
    write_json(output / "environment.json", installed)
    if baseline is not None:
        (output / "baseline.jsonl").write_bytes(baseline["bytes"])
        (output / "baseline-manifest.json").write_bytes(baseline["manifest_bytes"])
    resolved = [row for row in examples if row.target is not None]
    report = {
        "status": "completed_development_diagnostic", "version": VERSION, "feature_version": FEATURE_VERSION,
        "features": FEATURE_NAMES, "seed": SEED, "source": provenance,
        "experiment_sha256": digest(Path(__file__)), "environment": installed,
        "install_receipt": None if install_receipt is None else {"path": str(install_receipt), "sha256": digest(install_receipt)},
        "counts": {"pairs": len(examples), "components": len({row.component for row in examples}),
                   "resolved_pairs": len(resolved), "resolved_components": len({row.component for row in resolved}),
                   "positive_pairs": sum(row.target == 1 for row in resolved),
                   "negative_pairs": sum(row.target == 0 for row in resolved),
                   "unknown_pairs": len(examples) - len(resolved)},
        "diagnostics": result["diagnostics"],
        "unknown_score_diagnostics": {
            name: {"scored_pairs": len([row for row in result["predictions"] if row["target"] is None]),
                   "scores_at_or_above_half": sum(row["scores"][name] >= 0.5 for row in result["predictions"] if row["target"] is None),
                   "interpretation": "Unresolved evidence, not false-positive or negative labels"}
            for name in result["models"]
        },
        "baseline": {"status": "pending_actual_csharp_export"} if baseline is None else baseline["receipt"],
        "accuracy_estimated": False, "probabilities_calibrated": False, "policy_activation_permitted": False,
        "limits": ["Eleven resolved draft pairs are not independent accuracy evidence or a model-selection sample.",
                   "Only raw names and raw location wording enter these six features; no resolved geography is inferred.",
                   "Official corroboration, rationale, identifiers and split hypotheses are not model features.",
                   "Unknown targets never enter fitting, loss, or binary diagnostic denominators.",
                   "The score is a development ranking output, not permission to merge, create or publish a venue.",
                   "These are imported-row pairs, not complete canonical-candidate retrieval or recall evaluation."],
    }
    artifact_names = ["features.jsonl", "splits.json", "models.json", "final-models.joblib", "predictions.jsonl", "errors.jsonl", "unknown-predictions.jsonl", "environment.json"]
    if baseline is not None:
        artifact_names.extend(["baseline.jsonl", "baseline-manifest.json"])
    write_json(output / "artifacts.json", {name: digest(output / name) for name in artifact_names})
    report["artifacts_manifest_sha256"] = digest(output / "artifacts.json")
    temporary = output / "report.json.tmp"
    write_json(temporary, report)
    temporary.replace(output / "report.json")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, default=DEFAULT_BUNDLE)
    parser.add_argument("--frozen-manifest-sha256", required=True)
    parser.add_argument("--frozen-labels-sha256", required=True)
    parser.add_argument("--frozen-split-links-sha256", required=True)
    parser.add_argument("--install-receipt", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    examples, provenance = load_examples(args.bundle, args.frozen_manifest_sha256,
                                         args.frozen_labels_sha256, args.frozen_split_links_sha256)
    baseline = load_baseline(args.baseline, examples, provenance) if args.baseline else None
    report = run(examples, provenance, args.output, args.install_receipt, baseline)
    print(json.dumps({"report": str(args.output / "report.json"), "counts": report["counts"],
                      "diagnostics": report["diagnostics"], "baseline": report["baseline"]}, sort_keys=True))


if __name__ == "__main__":
    main()
