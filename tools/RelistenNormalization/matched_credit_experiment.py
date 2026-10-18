#!/usr/bin/env python3
"""Train and compare credit extractors on the same frozen development fields.

This command reads retained outputs only. It starts no API, database or provider work.
All predictions and model weights belong in ignored storage; they can repeat source prose.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import io
from importlib.metadata import version as package_version
import json
import os
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace
import uuid

from luna_credit_field_report import CohortPlan, cohort_coverage

VERSION = "matched-credit-development-v1"
LOCAL_VERSION = "recording-credits-v4/recording-actor-evidence-v1"
LUNA_VERSION = "luna-recording-credits-v2"
PRIORITY_ROLES = {"Taper", "Transferrer"}
METHODS = ("perceptron", "logistic", "local", "luna")
CODE_PATHS = {
    "RelistenApi/Services/Normalization/RecordingCreditExtractor.cs",
    "RelistenApi/Services/Normalization/RecordingActorEvidenceExtractor.cs",
    "RelistenApi/Services/Normalization/LunaRecordingCreditExtractor.cs",
    "RelistenApi/Services/Normalization/LunaRecordingCreditClient.cs",
    "RelistenApi/Services/Data/NormalizationExtractionCacheService.cs",
    "tools/RelistenNormalization/LunaCreditFieldEvaluationBatch.cs",
    "tools/RelistenNormalization/bin/Debug/net10.0/RelistenApi.dll",
    "tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def jsonl(data):
    # Only physical LF terminates a row. Unicode separators inside fields are ordinary text.
    return [json.loads(line) for line in io.BytesIO(data)]


def indexed(rows, key):
    result = {}
    for row in rows:
        require(row[key] not in result, "Duplicate " + key)
        result[row[key]] = row
    return result


def write_json(path, value):
    with path.open("x", encoding="utf-8") as target:
        json.dump(value, target, ensure_ascii=False, sort_keys=True, indent=2)
        target.write("\n")


class InputSnapshot:
    """Keep the exact checked bytes through fitting and final report publication."""

    def __init__(self):
        self.files = {}

    def read(self, path):
        path = Path(path).resolve()
        data = path.read_bytes()
        require(path not in self.files or self.files[path] == data, "Input changed during reading")
        self.files[path] = data
        return data

    def json(self, path):
        return json.loads(self.read(path))

    def verify(self):
        for path, data in self.files.items():
            require(path.read_bytes() == data, "Input or source changed before completion")

    def receipts(self):
        return [{"path": str(path), "bytes": len(data), "sha256": sha(data)}
                for path, data in sorted(self.files.items())]


def load_trainer(snapshot):
    path = Path(__file__).with_name("credit_tagger_experiment.py")
    source = snapshot.read(path)
    # Compile the snapshotted bytes: a later file receipt must describe the trainer we ran.
    module = ModuleType("matched_credit_trainer_snapshot")
    module.__file__ = str(path)
    sys.modules[module.__name__] = module
    exec(compile(source, str(path), "exec"), module.__dict__)
    return module


class MatchedFields:
    """Authenticate the approved field population, its uncertainty and retained model outputs."""

    def __init__(self, args):
        self.snapshot = InputSnapshot()
        self.snapshot.read(Path(__file__))
        self.trainer = load_trainer(self.snapshot)
        self.labels_bytes = self.snapshot.read(args.labels)
        self.receipt = self.snapshot.json(args.receipt)
        require(sha(self.labels_bytes) == args.labels_sha256 == self.receipt["labels_sha256"],
                "Frozen labels differ")
        self.labels = indexed(jsonl(self.labels_bytes), "label_id")
        require(self.receipt["status"] == "draft_development"
                and self.receipt["schema_version"] == "credit-field-draft-v1"
                and self.receipt["fields"] == len(self.labels) == 100,
                "Use the complete 100-field development bundle")
        self._check_reservations(args)
        self.exclusions = self._read_exclusions(args)
        fields_bytes = self.snapshot.read(args.fields)
        require(sha(fields_bytes) == self.receipt["raw_fields_sha256"], "Original fields differ")
        self.raw_fields = indexed(jsonl(fields_bytes), "label_id")
        require(self.labels.keys() == self.raw_fields.keys(), "Field population differs")
        self.examples = self.trainer.load_examples(args.labels, args.fields, self.exclusions)
        require(len(self.examples) == len(self.labels)
                and {example.key for example in self.examples} == self.labels.keys(),
                "Consumed example population differs")
        for example in self.examples:
            require(example.label == self.labels[example.key]
                    and example.text == self.raw_fields[example.key]["field_text"],
                    "Consumed example differs from authenticated snapshot")
            require(type(example.label["answer"]["exhaustive"]) is bool
                    and type(example.label["answer"]["unresolved"]) is bool,
                    "Invalid completeness marker")
            for start, end, role in example.gold:
                require(type(start) is int and type(end) is int
                        and role in self.trainer.ROLE_NAMES.values(), "Invalid gold span or role")
        self.snapshot.verify()
        self._read_comparison(args)
        self._check_code_receipt(args)
        self.snapshot.verify()

    def _check_reservations(self, args):
        receipt = self.snapshot.json(args.reservation_receipt)
        index_path = args.source_root / receipt["index"]["path"]
        index_bytes = self.snapshot.read(index_path)
        require(sha(index_bytes) == receipt["index"]["sha256"]
                and len(index_bytes) == receipt["index"]["bytes"], "Reservation index changed")
        index = json.loads(index_bytes)
        require(index["raw_snapshot_sha256"] == self.receipt["snapshot_sha256"],
                "Reservations describe a different snapshot")
        eligible = indexed(index["eligible_source_metadata"], "evidence_id")
        # Check metadata before reading the raw fields. No held-out or reserved original is needed.
        for label in self.labels.values():
            require(label["family_partition"] == label["artist_partition"] == "development"
                    and label["status"] == "draft_development"
                    and label["scope"] == "complete_single_field", "Non-development field")
            require(label["evidence_id"] in eligible, "Reserved or ineligible source")
            source = eligible[label["evidence_id"]]
            require(source["artist_partition"] == source["family_partition"] == "development"
                    and source["artist_uuid"] == label["artist"]["uuid"]
                    and source["family_group"] == label["input_duplicate_family"]
                    and source["family"] == label["known_recording_family"], "Source grouping differs")

    def _read_exclusions(self, args):
        data = self.snapshot.read(args.exclusions)
        require(sha(data) == args.exclusions_sha256, "Exclusion receipt changed")
        exclusion = json.loads(data)
        require(exclusion["provenance"]["labels_sha256"] == sha(self.labels_bytes)
                and exclusion["sensitivity"]["kind"] == "conservative_whole_field_exclusion",
                "Exclusions describe another task or labels")
        excluded = exclusion["explicit_receipt_exclusions"]
        additional = exclusion["sensitivity"]["additional_excluded_label_ids"]
        require(len(excluded) == len(set(excluded)) and set(excluded) <= self.labels.keys()
                and set(self.receipt["training_excluded_label_ids"]) <= set(excluded)
                and additional and set(additional) <= set(excluded), "Missing or unknown exclusion")
        return sorted(excluded)

    def _read_comparison(self, args, *, expected_fields=100, expected_version="luna-credit-fields-v1"):
        path = args.comparison
        manifest_bytes = self.snapshot.read(path / "manifest.json")
        require(sha(manifest_bytes) == args.comparison_sha256, "Comparison manifest differs")
        self.manifest = json.loads(manifest_bytes)
        require(self.manifest["version"] == expected_version
                and self.manifest["state"] in {"completed", "completed_partial"}
                and self.manifest["fields"] == expected_fields, "Incomplete comparison population")
        selection = self.snapshot.json(path / "selection.json")
        require(selection == self.manifest["selection"], "Saved selection differs")
        require(selection["labelsSha256"] == sha(self.labels_bytes)
                and selection["fieldsSha256"] == sha(self.snapshot.read(args.fields))
                and selection["receiptSha256"] == sha(self.snapshot.read(args.receipt)),
                "Comparison inputs differ")
        self.items = indexed(selection["items"], "labelId")
        results_bytes = self.snapshot.read(path / "results.jsonl")
        audit_bytes = self.snapshot.read(path / "receipts.json")
        require(sha(results_bytes) == self.manifest["resultsSha256"]
                and sha(audit_bytes) == self.manifest["receiptsSha256"], "Retained output changed")
        self.outputs = indexed(jsonl(results_bytes), "labelId")
        require(self.items.keys() == self.outputs.keys() == self.labels.keys(),
                "Comparison omitted, duplicated or added a field")
        audit = json.loads(audit_bytes)
        self.audit = audit
        self.requests = indexed(audit["requests"], "cacheKey")
        for request in self.requests.values():
            require(sha(request["renderedRequest"].encode()) == request["cacheKey"],
                    "Rendered request hash differs")
        for key, item in self.items.items():
            label, raw = self.labels[key], self.raw_fields[key]
            source_uuid = str(uuid.UUID(label["resource"]["resource_uuid"]))
            field = label["field_path"].removeprefix("/raw_record/row/")
            require(field in {"taper", "transferrer", "source", "description", "lineage", "taper_notes"}
                    and label["field_path"] == "/raw_record/row/" + field,
                    "Unsupported raw field")
            require(label["resource"]["provider"] == "relisten_catalog_snapshot"
                    and label["resource"]["resource_type"] == "source"
                    and key == "credit-field:" + source_uuid + ":" + field
                    and item["taskId"] == label["task_id"] == "extract:source_sample:" + source_uuid
                    and item["sourceUuid"] == source_uuid
                    and item["artistUuid"] == (label["artist"]["uuid"] if expected_version == "luna-credit-fields-v1"
                                              else str(uuid.UUID(label["artist"]["uuid"])))
                    and item["evidenceId"] == label["evidence_id"] == "source_sample:" + source_uuid,
                    "Source or artist identity differs")
            require(item["field"] == field and item["text"] == raw["field_text"]
                    and item["fieldSha256"] == raw["field_sha256"] == label["field_sha256"]
                    and sha(item["text"].encode()) == item["fieldSha256"], "Field bytes differ")
            require(type(item["noText"]) is bool
                    and item["noText"] == (not item["text"].strip()), "Incorrect no-text marker")
        states = Counter(output["state"] for output in self.outputs.values())
        require(dict(states) == self.manifest["states"], "Outcome counts differ from manifest")

    def _check_code_receipt(self, args):
        data = self.snapshot.read(args.code_receipt)
        require(sha(data) == args.code_sha256, "Code receipt changed")
        receipt = json.loads(data)
        require(receipt["local_extractor_version"] == LOCAL_VERSION
                and receipt["luna_extractor_version"] == LUNA_VERSION, "Wrong extractor versions")
        files = indexed(receipt["files"], "path")
        require(files.keys() == CODE_PATHS, "Code receipt lacks the approved sources/binaries")
        for name, value in files.items():
            require(sha(self.snapshot.read(args.code_root / name)) == value["sha256"],
                    "Code receipt source or binary changed")
        require(self.manifest["extractorAssemblySha256"] == files[
            "tools/RelistenNormalization/bin/Debug/net10.0/RelistenApi.dll"]["sha256"]
                and self.manifest["runnerAssemblySha256"] == files[
            "tools/RelistenNormalization/bin/Debug/net10.0/RelistenNormalization.dll"]["sha256"],
                "Saved comparison used different binaries")

    def saved_predictions(self, example, *, require_primary_cached=True):
        item, output = self.items[example.key], self.outputs[example.key]
        no_text = item["noText"]
        require((output["state"] == "no_text") == no_text, "No-text outcome differs")
        local = extraction_predictions(output["local"], item, self.trainer)
        luna = None
        reason = None
        if no_text:
            require(output["luna"] is None and output["cacheReplay"] is None,
                    "Empty field unexpectedly has a model answer")
            luna = []
        elif output["luna"] is not None and output["luna"]["state"] == "extracted":
            result = output["luna"]
            replay = output["cacheReplay"]
            require(output["state"] == "extracted"
                    and (result["fromCache"] is True
                         or not require_primary_cached and result["fromCache"] is False)
                    and replay["state"] == "extracted" and replay["fromCache"] is True
                    and replay["cacheKey"] == result["cacheKey"]
                    and replay["extraction"] == result["extraction"],
                    "Successful answer lacks equal key-free cached replay")
            require(result["cacheKey"] in self.requests, "Successful Luna answer lacks request receipt")
            request = self.requests[result["cacheKey"]]
            rendered = json.loads(request["renderedRequest"])
            user = [entry for entry in rendered["input"] if entry["role"] == "user"]
            require(len(user) == 1 and json.loads(user[0]["content"]) == {
                "fields": {item["field"]: item["text"]}, "context": None},
                "Luna request used a different field or extra context")
            require(rendered["model"] == self.manifest["options"]["model"]
                    and any(LUNA_VERSION in entry["content"] for entry in rendered["input"]
                            if entry["role"] == "system"), "Wrong Luna request version/model")
            luna = extraction_predictions(result["extraction"], item, self.trainer)
            validate_response_binding(request, luna, item, self.trainer)
        else:
            require(output["luna"] is not None and output["state"] == output["luna"]["state"],
                    "Unavailable provider outcome differs")
            reason = (output["luna"] or {}).get("reason", "provider_output_unavailable")
            # Error results can embed local fallback. That is never scored as a Luna answer.
        return local, luna, reason


def evidence_span(evidence, item):
    start, end = evidence["startCodePoint"], evidence["endCodePoint"]
    require(type(start) is int and type(end) is int and 0 <= start < end <= len(item["text"])
            and evidence["field"] == item["field"]
            and evidence["jsonPointer"] == "/" + item["field"].replace("~", "~0").replace("/", "~1")
            and item["text"][start:end] == evidence["quote"], "Invalid original prediction citation")
    return start, end


def extraction_predictions(extraction, item, trainer):
    validate_nested_citations(extraction, item)
    predictions = []
    for credit in extraction["credits"]:
        require(credit["role"] in trainer.ROLE_NAMES, "Unknown saved credit role")
        start, end = evidence_span(credit["evidence"], item)
        name = credit["name"]
        require(isinstance(name, str) and name.strip(), "Missing credit wording")
        actor = credit.get("actorFacts")
        anchor = None
        literal = None
        if actor is not None:
            anchor = evidence_span(actor["anchor"], item)
            require(start <= anchor[0] < anchor[1] <= end, "Actor anchor escaped credited clause")
            literal = item["text"][anchor[0]:anchor[1]]
        # A supporting phrase is not a name anchor. No unique-string search repairs its boundary.
        predictions.append({"display_name": name, "role": trainer.ROLE_NAMES[credit["role"]],
                            "span": list(anchor) if anchor else None, "literal": literal,
                            "support_span": [start, end], "support_quote": credit["evidence"]["quote"]})
    return predictions


def validate_nested_citations(value, item):
    if isinstance(value, list):
        for child in value:
            validate_nested_citations(child, item)
    elif isinstance(value, dict):
        if "startCodePoint" in value or "endCodePoint" in value:
            evidence_span(value, item)
        for child in value.values():
            validate_nested_citations(child, item)


def validate_response_binding(request, predictions, item, trainer):
    require(request["responseJson"] is not None, "Successful cached response absent")
    response = json.loads(request["responseJson"])
    expected = []
    for credit in response["credits"]:
        require(credit["field"] == item["field"] and credit["role"] in {"taper", "transferrer"},
                "Cached credit task differs")
        quote = credit["quote"]
        require(credit["name"] in quote, "Cached name has no literal support")
        start, end = credit["start"], credit["end"]
        if start is None and end is None:
            start = item["text"].find(quote)
            require(start >= 0 and item["text"].find(quote, start + 1) < 0,
                    "Cached supporting quote is not unique")
            end = start + len(quote)
        require(type(start) is int and type(end) is int and 0 <= start < end <= len(item["text"])
                and item["text"][start:end] == quote, "Cached support coordinates differ")
        expected.append((credit["name"], trainer.ROLE_NAMES[credit["role"]], start, end, quote))
    actual = [(p["display_name"], p["role"], *p["support_span"], p["support_quote"]) for p in predictions]
    require(set(expected) == set(actual), "Luna prediction differs from its cached response")


def trained_predictions(models, example, trainer):
    return [{"display_name": example.text[start:end], "literal": example.text[start:end],
             "span": [start, end], "role": role, "support_span": [start, end],
             "support_quote": example.text[start:end]}
            for start, end, role in trainer.predict(models, example)]


def training_environment(snapshot):
    lock = Path(sys.prefix) / "requirements.lock.txt"
    require(lock.is_file(), "Run in the retained locked experiment environment")
    packages = {}
    for line in snapshot.read(lock).decode().split("\n"):
        if line:
            name, pinned = line.split("==")
            require(package_version(name) == pinned, "Installed package differs from environment lock")
            packages[name] = pinned
    return {"python": sys.version.split()[0], "executable": sys.executable, "packages": packages}


def partial_positive_diagnostics(rows):
    incomplete = [row for row in rows if not row["complete_scoring"]]
    known = Counter(fields=len(incomplete), known_priority_assertions=0)
    recovered, available = Counter(), Counter()
    for row in incomplete:
        gold = {(v["name"], v["role"]) for v in row["gold"] if v["role"] in PRIORITY_ROLES}
        known["known_priority_assertions"] += len(gold)
        for method, values in row["methods"].items():
            if values is not None:
                available[method] += 1
                recovered[method] += len(gold & {(v["display_name"], v["role"]) for v in values})
    return {**known, "methods": {method: {"recovered_labeled_positives": recovered[method],
                                           "available_fields": available[method],
                                           "unavailable_fields": len(incomplete) - available[method]}
                                 for method in METHODS}, "negative_labels_permitted": False,
            "interpretation": "Positive recovery only; extra predictions are unscored and unavailable outputs remain unavailable."}


def score(rows, trainer, *, shared=False, priority=True):
    totals = {method: Counter(true_positive=0, false_positive=0, false_negative=0) for method in METHODS}
    available, exact, method_gold = Counter(), Counter(), Counter()
    span_totals = {method: Counter(matched_positive=0, missed_positive=0,
                                  extra_anchored_prediction=0, unanchored_prediction=0,
                                  display_literal_differences=0,
                                  exact_gold_span_with_different_display=0) for method in METHODS}
    complete_fields = gold_count = 0
    for row in rows:
        if not row["complete_scoring"] or shared and row["methods"]["luna"] is None:
            continue
        complete_fields += 1
        gold = {(v["name"], v["role"]) for v in row["gold"]
                if not priority or v["role"] in PRIORITY_ROLES}
        gold_spans = {(tuple(v["span"]), v["role"]) for v in row["gold"]
                      if not priority or v["role"] in PRIORITY_ROLES}
        gold_anchor_names = {(tuple(v["span"]), v["role"], v["name"]) for v in row["gold"]
                             if not priority or v["role"] in PRIORITY_ROLES}
        gold_count += len(gold)
        for method, values in row["methods"].items():
            if values is None or not priority and method == "luna":
                continue
            predictions = [v for v in values if not priority or v["role"] in PRIORITY_ROLES]
            proposed = {(v["display_name"], v["role"]) for v in predictions}
            available[method] += 1
            method_gold[method] += len(gold)
            exact[method] += proposed == gold
            totals[method].update(trainer.count_scores(gold, proposed))
            spans = {(tuple(v["span"]), v["role"]) for v in predictions if v["span"] is not None}
            span_totals[method].update({
                "matched_positive": len(gold_spans & spans), "missed_positive": len(gold_spans - spans),
                "extra_anchored_prediction": len(spans - gold_spans),
                "unanchored_prediction": sum(v["span"] is None for v in predictions),
                "display_literal_differences": sum(v["literal"] is not None and v["literal"] != v["display_name"] for v in predictions),
                "exact_gold_span_with_different_display": sum(
                    v["span"] is not None and (tuple(v["span"]), v["role"]) in gold_spans
                    and (tuple(v["span"]), v["role"], v["display_name"]) not in gold_anchor_names
                    for v in predictions),
            })
    return {"resolved_fields": complete_fields, "gold_assertions": gold_count,
            "methods": {method: ({"status": "not_evaluated", "reason": "Luna supports only the two priority roles"}
                                  if not priority and method == "luna" else
                                  {**trainer.summarize(totals[method]), "available_fields": available[method],
                                   "gold_assertions": method_gold[method],
                                   "unavailable_fields": complete_fields - available[method], "exact_fields": exact[method]})
                        for method in METHODS},
            "boundary_diagnostics": {method: dict(value) for method, value in span_totals.items()}}


def run(args):
    require(not args.output.exists(), "Choose a new experiment output directory")
    inputs = MatchedFields(args)
    trainer = inputs.trainer
    environment = training_environment(inputs.snapshot)
    # Group the entire population, including uncertain/excluded rows, before selecting fit data.
    assignments, groups = trainer.grouped_folds(inputs.examples, args.folds)
    roles = sorted({role for example in inputs.examples for _, _, role in example.gold})
    saved = {example.key: inputs.saved_predictions(example) for example in inputs.examples}
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "splits.json", {"grouping": "artist+duplicate+known-family+exact-lexical-name",
                                             "groups": groups, "exclusions": inputs.exclusions})
    rows, folds = [], []
    for fold in range(args.folds):
        training = [e for e in inputs.examples if assignments[e.key] != fold and e.complete]
        development = [e for e in inputs.examples if assignments[e.key] == fold]
        require(training, "Fold has no resolved training fields")
        audits = trainer.split_audit(training, development)
        perceptron = trainer.fit_models(training, roles, args.epochs, args.seed + fold)
        logistic = trainer.fit_logistic_models(training, roles, seed=args.seed + fold)
        write_json(args.output / f"models-fold-{fold}.json", {
            "fold": fold, "training_label_ids": sorted(e.key for e in training),
            "perceptron": {role: model.weights for role, model in perceptron.items()},
            "logistic": {role: model.to_json() for role, model in logistic.items()},
            "production_authorized": False,
        })
        folds.append({"fold": fold, "training_label_ids": sorted(e.key for e in training),
                      "development_label_ids": sorted(e.key for e in development),
                      "training_fields": len(training), "grouping_audit": audits,
                      "training_assertions_by_role": dict(Counter(role for e in training for _, _, role in e.gold))})
        for example in development:
            local, luna, reason = saved[example.key]
            rows.append({"label_id": example.key, "field_sha256": example.label["field_sha256"],
                         "field": example.label["field_path"].rsplit("/", 1)[-1], "fold": fold,
                         "complete_scoring": example.complete, "excluded_by_receipt": example.receipt_excluded,
                         "gold": [{"name": example.text[start:end], "span": [start, end], "role": role}
                                  for start, end, role in example.gold],
                         "methods": {"perceptron": trained_predictions(perceptron, example, trainer),
                                     "logistic": trained_predictions(logistic, example, trainer),
                                     "local": local, "luna": luna},
                         "local_unresolved": inputs.outputs[example.key]["local"]["unresolved"],
                         "luna_unavailable_reason": reason})
    rows.sort(key=lambda row: row["label_id"])
    require(len(rows) == len(inputs.examples), "Missing out-of-fold prediction")
    with (args.output / "predictions.jsonl").open("x", encoding="utf-8") as target:
        for row in rows:
            target.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
    report = {
        "version": VERSION, "state": "completed_development_only", "production_authorized": False,
        "policy_activation_permitted": False, "independent_accuracy_evaluation": False,
        "fields": len(rows), "resolved_fields": sum(e.complete for e in inputs.examples),
        "excluded_label_ids": sorted(e.key for e in inputs.examples if not e.complete),
        "explicit_exclusions": inputs.exclusions, "folds": folds, "group_components": len(groups),
        "primary_contract": "Exact literal gold name versus emitted display name, plus role, with no normalization. Boundary diagnostics use only explicit raw actor/model anchors; supporting phrases are not searched for a name.",
        "full_resolved_priority": score(rows, trainer),
        "shared_luna_available_priority": score(rows, trainer, shared=True),
        "all_role_diagnostics": score(rows, trainer, priority=False),
        "unresolved_positive_diagnostics": partial_positive_diagnostics(rows),
        "provider_outcomes": dict(Counter(inputs.outputs[row["label_id"]]["state"] for row in rows)),
        "local_outcomes": dict(Counter(
            ("credits" if row["methods"]["local"] else "no_credits")
            + ("_with_unresolved" if row["local_unresolved"] else "") for row in rows)),
        "versions": {"local": LOCAL_VERSION, "luna": LUNA_VERSION, "perceptron": trainer.VERSION,
                     "logistic": trainer.LogisticBioTagger.VERSION, "tokenizer": trainer.TOKENIZER_VERSION,
                     "features": trainer.FEATURES_VERSION},
        "environment": environment, "epochs": args.epochs, "seed": args.seed,
        "input_and_source_files": inputs.snapshot.receipts(),
        "limits": [
            "Draft development labels, with prior model/parser exposure; no untouched evaluation or identity accuracy claim.",
            "Only complete resolved fields train or supply negative/scored labels. Excluded and unknown fields retain predictions and known positives only.",
            "Folds group artists, duplicate/recording families and exact names, not verified complete alias identities. Separate unseen-identity, temporal and format suites remain required.",
            "All-role diagnostics include roles Luna's two-role schema does not support and are not a fair Luna comparison.",
            "Boundary misses can reflect unavailable explicit anchors or raw/display differences. They are separate from exact emitted-name/role results; no string-based anchor repair is performed.",
            "Unavailable Luna results are not empty predictions and their embedded local fallback is not scored as Luna. This command changes no fallback routing or policy.",
            "Code receipts bind the declared source snapshot and saved-run DLL hashes; they do not independently prove source-to-binary compilation.",
        ],
    }
    inputs.snapshot.verify()
    write_json(args.output / "report.pending.json", report)
    artifacts = {("report.json" if path.name == "report.pending.json" else path.name):
                 {"bytes": len(path.read_bytes()), "sha256": sha(path.read_bytes())}
                 for path in sorted(args.output.iterdir()) if path.is_file()}
    write_json(args.output / "artifacts.json", artifacts)
    # Diagnostics/model files are durable first. A late input/source edit suppresses completion.
    inputs.snapshot.verify()
    os.link(args.output / "report.pending.json", args.output / "report.json")
    return report


class CohortFields(MatchedFields):
    """Read predictions for an approved new population, with no training partition rewrite."""

    def __init__(self, args, reference):
        self.snapshot, self.trainer = reference.snapshot, reference.trainer
        # The shared authenticator is also executable scoring source, so retain its bytes.
        self.snapshot.read(Path(__file__).with_name("luna_credit_field_report.py"))
        self.cohort = CohortPlan(args.cohort_plan, args.cohort_plan_sha256, self.snapshot.read)
        self.labels_bytes, self.labels = self.cohort.labels_bytes, self.cohort.labels
        self.raw_fields, self.receipt = self.cohort.fields, self.cohort.receipt
        self.exclusions = self.cohort.exclusions
        comparison_args = SimpleNamespace(
            comparison=args.cohort_comparison, comparison_sha256=args.cohort_comparison_sha256,
            labels=self.cohort.paths["labels"], fields=self.cohort.paths["fields"],
            receipt=self.cohort.paths["receipt"])
        self._read_comparison(comparison_args, expected_fields=self.cohort.plan["field_count"],
                              expected_version="luna-credit-cohort-v1")
        self.cohort.check_selection(self.labels_bytes, self.manifest["selection"])
        self.examples = []
        # The training loader deliberately accepts development only. Build evaluation
        # features directly rather than disguising validation/test labels as development.
        for key, label in self.labels.items():
            text = self.raw_fields[key]["field_text"]
            for assertion in label["answer"]["assertions"]:
                self.trainer.validate_span(text, assertion["name"])
                require(assertion["role"] in self.trainer.ROLE_NAMES.values(), "Invalid cohort role")
                basis = assertion["role_basis"]
                if basis["kind"] == "prose":
                    self.trainer.validate_span(text, basis["span"])
                else:
                    require(basis["kind"] == "structured_field"
                            and basis["field"] == label["field_path"].rsplit("/", 1)[-1],
                            "Invalid cohort role evidence")
            for unknown in label["answer"]["unknowns"]:
                self.trainer.validate_span(text, unknown["span"])
            tokens = self.trainer.tokenize(text)
            example = self.trainer.Example(label, text, tokens,
                self.trainer.token_features(tokens, label["field_path"].rsplit("/", 1)[-1]),
                receipt_excluded=key in self.exclusions)
            for role in {role for _, _, role in example.gold}:
                self.trainer.gold_tags(example, role)
            self.examples.append(example)
        self.snapshot.verify()

    def saved_predictions(self, example):
        if example.key not in self.cohort.contact_ids:
            # The first cohort comparison may own a fresh successful paid attempt.
            # Its equal successful keyless replay and retained response are still required.
            return super().saved_predictions(example, require_primary_cached=False)
        item, output = self.items[example.key], self.outputs[example.key]
        require(not item["noText"] and output["state"] == "not_requested_contact_scope"
                and output["luna"] is None and output["cacheReplay"] is None
                and output["currentRouting"]["state"] == "not_evaluated"
                and output["currentRouting"]["reason"] == "contact_scope",
                "Contact field was requested or routed")
        return extraction_predictions(output["local"], item, self.trainer), None, "contact_scope"


def fit_cohort_reference(reference, args):
    """The new labels cannot affect the training population, role heads or vocabulary."""
    training = [example for example in reference.examples if example.complete]
    require(len(training) == 84, "Cohort scoring requires the frozen 84-field training reference")
    roles = sorted({role for example in training for _, _, role in example.gold})
    perceptron = reference.trainer.fit_models(training, roles, args.epochs, args.seed)
    logistic = reference.trainer.fit_logistic_models(training, roles, seed=args.seed)
    return training, perceptron, logistic


def run_cohort(args):
    require(not args.output.exists(), "Choose a new experiment output directory")
    reference = MatchedFields(args)
    inputs = CohortFields(args, reference)
    require(inputs.cohort.plan["purpose"] == "development"
            or not (reference.labels.keys() & inputs.labels.keys()),
            "Tune/evaluation cohort overlaps an already exposed old development field")
    trainer = reference.trainer
    environment = training_environment(reference.snapshot)
    saved = {example.key: inputs.saved_predictions(example) for example in inputs.examples}
    training, perceptron, logistic = fit_cohort_reference(reference, args)
    training_ids = sorted(example.key for example in training)
    rows = []
    for example in inputs.examples:
        local, luna, reason = saved[example.key]
        rows.append({"label_id": example.key, "field_sha256": example.label["field_sha256"],
                     "field": example.label["field_path"].rsplit("/", 1)[-1],
                     "complete_scoring": example.complete, "excluded_by_receipt": example.receipt_excluded,
                     "gold": [{"name": example.text[start:end], "span": [start, end], "role": role}
                              for start, end, role in example.gold],
                     "methods": {"perceptron": trained_predictions(perceptron, example, trainer),
                                 "logistic": trained_predictions(logistic, example, trainer),
                                 "local": local, "luna": luna},
                     "local_unresolved": inputs.outputs[example.key]["local"]["unresolved"],
                     "luna_unavailable_reason": reason})
    rows.sort(key=lambda row: row["label_id"])
    reference.snapshot.verify()
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "models-frozen-development.json", {
        "training_label_ids": training_ids,
        "perceptron": {role: model.weights for role, model in perceptron.items()},
        "logistic": {role: model.to_json() for role, model in logistic.items()},
        "production_authorized": False,
    })
    with (args.output / "predictions.jsonl").open("x", encoding="utf-8") as target:
        for row in rows:
            target.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
    report = {
        "version": "matched-credit-cohort-v1", "state": "completed_cohort_scoring",
        "production_authorized": False, "policy_activation_permitted": False,
        "independent_accuracy_evaluation": False, "fields": len(rows),
        "resolved_fields": sum(example.complete for example in inputs.examples),
        "cohort": {"plan_sha256": inputs.cohort.sha256, "purpose": inputs.cohort.plan["purpose"],
                   "artist_partition": inputs.cohort.plan["artist_partition"],
                   "family_partition": inputs.cohort.plan["family_partition"],
                   "provider_origin_fields": inputs.cohort.provider_counts},
        "training_reference": {"fields": len(reference.examples), "fit_fields": len(training),
            "labels_sha256": sha(reference.labels_bytes), "training_label_ids": training_ids,
            "training_label_ids_sha256": sha(json.dumps(training_ids, separators=(",", ":")).encode()),
            "roles": sorted(perceptron), "new_cohort_labels_used_for_fitting": False},
        "full_resolved_priority": score(rows, trainer),
        "shared_luna_available_priority": score(rows, trainer, shared=True),
        "all_role_diagnostics": score(rows, trainer, priority=False),
        "unresolved_positive_diagnostics": partial_positive_diagnostics(rows),
        "provider_outcomes": dict(Counter(inputs.outputs[row["label_id"]]["state"] for row in rows)),
        "coverage": cohort_coverage(inputs.cohort, inputs.outputs, inputs.audit, inputs.manifest),
        "cohort_declared_assemblies": {key: inputs.manifest[key] for key in
                                      ("extractorAssemblySha256", "runnerAssemblySha256")},
        "environment": environment, "epochs": args.epochs, "seed": args.seed,
        "input_and_source_files": reference.snapshot.receipts(),
        "limits": ["Only the old 84 complete nonexcluded development fields fit these models. New labels supply scoring only.",
                   "Declared cohort partitions remain unchanged; this command performs no tuning or model selection.",
                   "Tune/evaluation label IDs cannot overlap the old100, including excluded fields. This does not prove complete unseen-identity disjointness; source/family partitions and unknown links remain operator evidence declarations.",
                   "The old training comparison code receipt does not authenticate the new runner's compilation. The new comparison is bound by its approved manifest digest and retained request/output hashes.",
                   "Known positives on incomplete fields are diagnostic only. Unavailable Luna outputs remain unavailable; fallback predictions are not Luna answers.",
                   "Contact fields retain their full local/trained predictions but are not sent to Luna or evaluated through current routing; they are unavailable outputs, not empty answers.",
                   "These are extraction measurements, not global identity accuracy or permission to activate a production policy."],
    }
    reference.snapshot.verify()
    write_json(args.output / "report.pending.json", report)
    artifacts = {("report.json" if path.name == "report.pending.json" else path.name):
                 {"bytes": len(path.read_bytes()), "sha256": sha(path.read_bytes())}
                 for path in sorted(args.output.iterdir()) if path.is_file()}
    write_json(args.output / "artifacts.json", artifacts)
    reference.snapshot.verify()
    os.link(args.output / "report.pending.json", args.output / "report.json")
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("labels", "receipt", "fields", "comparison", "code-receipt", "code-root",
                 "exclusions", "reservation-receipt", "source-root", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    for name in ("labels-sha256", "comparison-sha256", "code-sha256", "exclusions-sha256"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--folds", type=int, default=5)
    parser.add_argument("--epochs", type=int, default=12)
    parser.add_argument("--seed", type=int, default=173)
    parser.add_argument("--cohort-plan", type=Path)
    parser.add_argument("--cohort-plan-sha256")
    parser.add_argument("--cohort-comparison", type=Path)
    parser.add_argument("--cohort-comparison-sha256")
    args = parser.parse_args(argv)
    try:
        require(1 <= args.epochs <= 100, "Epoch count is outside the experiment bound")
        cohort_values = (args.cohort_plan, args.cohort_plan_sha256,
                         args.cohort_comparison, args.cohort_comparison_sha256)
        require(all(value is None for value in cohort_values) or all(value is not None for value in cohort_values),
                "Supply both cohort plan/comparison paths and their approved digests")
        report = run(args) if args.cohort_plan is None else run_cohort(args)
    except (ValueError, OSError, KeyError, TypeError, RuntimeError, ImportError) as error:
        print("Matched credit experiment failed: " + type(error).__name__ + ": " + str(error), file=sys.stderr)
        return 1
    print(json.dumps({"state": report["state"], "fields": report["fields"],
                      "resolved_fields": report["resolved_fields"],
                      "full_resolved_priority": report["full_resolved_priority"],
                      "shared_luna_available_priority": report["shared_luna_available_priority"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
