#!/usr/bin/env python3
"""Development-only credit extraction experiment over frozen, original-field labels.

Each role has a constrained BIO sequence head, so one name can have several roles.
Weights are ordinary JSON, and predictions cite original Unicode codepoint offsets.
This module has no production writer, API client, external dependency or identity model.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import random
import re
import sys
import unicodedata

VERSION = "credit-bio-perceptron-development-v1"
TOKENIZER_VERSION = "unicode-word-or-punctuation-v1"
FEATURES_VERSION = "token-window-3-shape-affix-field-v1"
TAGS = ("O", "B", "I")
ROLE_NAMES = {
    "taper": "Taper", "transferrer": "Transferrer",
    "mastering_engineer": "MasteringEngineer", "uploader": "Uploader",
    "musician": "Musician", "recording_engineer": "RecordingEngineer",
    "matrix_builder": "MatrixBuilder", "collector": "Collector", "other": "Other",
}


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_jsonl(path: Path):
    # JSONL is delimited by LF, not Unicode's broader splitlines definition.
    # U+2028/U+0085 can occur inside a valid retained JSON string.
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                yield json.loads(line)


def write_json(path: Path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n")


def write_jsonl(path: Path, values):
    with path.open("w", encoding="utf-8") as stream:
        for value in values:
            stream.write(json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n")


@dataclass(frozen=True)
class Token:
    text: str
    start: int
    end: int


def tokenize(text: str) -> list[Token]:
    return [Token(match.group(), match.start(), match.end())
            for match in re.finditer(r"\w+|[^\w\s]", text, re.UNICODE)]


@dataclass
class Example:
    label: dict
    text: str
    tokens: list[Token]
    features: list[tuple[str, ...]]
    receipt_excluded: bool = False

    @property
    def key(self):
        return self.label["label_id"]

    @property
    def complete(self):
        answer = self.label["answer"]
        # Unknown/nonexhaustive regions are never converted into negative token labels.
        # Excluding the whole field is conservative and avoids a partial-label objective here.
        return (answer["exhaustive"] and not answer["unresolved"]
                and not answer["unknowns"] and not self.receipt_excluded)

    @property
    def gold(self):
        return [(claim["name"]["start"], claim["name"]["end"], claim["role"])
                for claim in self.label["answer"]["assertions"]]


def shape(word: str):
    result = "".join("X" if ch.isupper() else "x" if ch.islower()
                     else "d" if ch.isdigit() else ch for ch in word)
    return re.sub(r"(.)\1{2,}", r"\1\1", result)


def token_features(tokens: list[Token], field: str) -> list[tuple[str, ...]]:
    result = []
    for index, token in enumerate(tokens):
        word = token.text.casefold()
        features = {"bias", "field=" + field, "word=" + word,
                    "shape=" + shape(token.text), "field_shape=" + field + "/" + shape(token.text),
                    "capital=" + str(token.text[:1].isupper()),
                    "digit=" + str(any(ch.isdigit() for ch in token.text))}
        for size in (1, 2, 3):
            features.add(f"prefix{size}=" + word[:size])
            features.add(f"suffix{size}=" + word[-size:])
        for offset in (-3, -2, -1, 1, 2, 3):
            neighbor = index + offset
            value = tokens[neighbor].text.casefold() if 0 <= neighbor < len(tokens) else "<boundary>"
            features.add(f"word{offset:+}=" + value)
        result.append(tuple(sorted(features)))
    return result


def validate_span(text: str, span: dict):
    start, end = span["start"], span["end"]
    if not isinstance(start, int) or not isinstance(end, int) or not 0 <= start < end <= len(text):
        raise ValueError("Invalid original-codepoint span")
    if text[start:end] != span["quote"]:
        raise ValueError("Quote does not match original codepoint span")


def gold_tags(example: Example, role: str) -> list[str]:
    tags = ["O"] * len(example.tokens)
    for start, end, claim_role in example.gold:
        if claim_role != role:
            continue
        indices = [i for i, token in enumerate(example.tokens) if start <= token.start and token.end <= end]
        if not indices or example.tokens[indices[0]].start != start or example.tokens[indices[-1]].end != end:
            raise ValueError(f"Gold name is not representable by tokenizer: {example.key}")
        if any(tags[i] != "O" for i in indices):
            raise ValueError(f"Overlapping same-role spans require a different model: {example.key}")
        for ordinal, index in enumerate(indices):
            tags[index] = "B" if ordinal == 0 else "I"
    return tags


def load_examples(labels: Path, fields: Path, excluded_label_ids=()) -> list[Example]:
    originals = {}
    for row in read_jsonl(fields):
        if row["label_id"] in originals:
            raise ValueError("Duplicate raw field label ID")
        originals[row["label_id"]] = row
    result = []
    seen = set()
    for label in read_jsonl(labels):
        key = label["label_id"]
        if key in seen:
            raise ValueError("Duplicate label ID")
        seen.add(key)
        raw = originals[key]
        text = raw["field_text"]
        if label["field_sha256"] != hashlib.sha256(text.encode()).hexdigest() or raw["field_sha256"] != label["field_sha256"]:
            raise ValueError("Original field hash mismatch")
        if raw["field_path"] != label["field_path"] or raw["evidence_id"] != label["evidence_id"]:
            raise ValueError("Raw field identity mismatch")
        if label["field_codepoints"] != len(text):
            raise ValueError("Original codepoint length mismatch")
        if label["family_partition"] != "development" or label["artist_partition"] != "development":
            raise ValueError("Only development-partition labels are permitted")
        for assertion in label["answer"]["assertions"]:
            validate_span(text, assertion["name"])
            basis = assertion["role_basis"]
            if basis["kind"] == "prose":
                validate_span(text, basis["span"])
            elif basis["kind"] != "structured_field" or basis["field"] != label["field_path"].rsplit("/", 1)[-1]:
                raise ValueError("Unsupported or mismatched role evidence")
        for unknown in label["answer"]["unknowns"]:
            validate_span(text, unknown["span"])
        tokens = tokenize(text)
        example = Example(label, text, tokens, token_features(tokens, label["field_path"].rsplit("/", 1)[-1]))
        for role in {role for _, _, role in example.gold}:
            gold_tags(example, role)
        example.receipt_excluded = key in excluded_label_ids
        result.append(example)
    if not set(excluded_label_ids) <= seen:
        raise ValueError("Receipt exclusion refers to an unknown label")
    if seen != originals.keys():
        raise ValueError("Labels and raw fields are not the same complete set")
    return result


def normalized_name(text: str):
    return " ".join(unicodedata.normalize("NFKC", text).casefold().split())


def grouped_folds(examples: list[Example], folds: int) -> tuple[dict[str, int], list[dict]]:
    if not 2 <= folds <= 10:
        raise ValueError("Development folds must be between 2 and 10")
    parent = list(range(len(examples)))

    def root(index):
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    owners = {}
    for index, example in enumerate(examples):
        label = example.label
        keys = ["artist:" + label["artist"]["uuid"]]
        for field in ("input_duplicate_family", "known_recording_family"):
            if label.get(field):
                keys.append(field + ":" + label[field])
        # Exact lexical grouping is a leakage precaution, not an assertion that these people
        # or aliases share an identity. Unknown aliases can still cross groups.
        keys.extend("name:" + normalized_name(example.text[start:end]) for start, end, _ in example.gold)
        for key in keys:
            if key in owners:
                parent[root(index)] = root(owners[key])
            else:
                owners[key] = index
    groups = defaultdict(list)
    for index, example in enumerate(examples):
        groups[root(index)].append(example.key)
    ordered = sorted(groups.values(), key=lambda keys: (-len(keys), hashlib.sha256("\n".join(sorted(keys)).encode()).hexdigest()))
    loads = [0] * folds
    assigned, receipt = {}, []
    for keys in ordered:
        fold = min(range(folds), key=lambda value: (loads[value], value))
        loads[fold] += len(keys)
        group_id = hashlib.sha256("\n".join(sorted(keys)).encode()).hexdigest()
        receipt.append({"group": group_id, "fold": fold, "label_ids": sorted(keys)})
        assigned.update({key: fold for key in keys})
    if any(load == 0 for load in loads):
        raise ValueError("Too few independent grouped components for requested folds")
    return assigned, receipt


class BioTagger:
    """Averaged structured perceptron with BIO-constrained Viterbi decoding."""

    def __init__(self, weights=None):
        self.weights = weights or {tag: {} for tag in TAGS}
        self.totals = defaultdict(float)
        self.timestamps = defaultdict(int)
        self.step = 0

    def decode(self, features):
        if not features:
            return []
        scores, paths = {"<start>": 0.0}, {"<start>": []}
        for token in features:
            next_scores, next_paths = {}, {}
            for tag in TAGS:
                choices = []
                emission = sum(self.weights[tag].get(feature, 0.0) for feature in token)
                for previous in scores:
                    if tag == "I" and previous not in ("B", "I"):
                        continue
                    score = scores[previous] + emission + self.weights[tag].get("previous=" + previous, 0.0)
                    choices.append((score, previous))
                if not choices:
                    continue
                best, previous = max(choices, key=lambda value: value[0])
                next_scores[tag] = best
                next_paths[tag] = paths[previous] + [tag]
            scores, paths = next_scores, next_paths
        return paths[max(scores, key=lambda tag: scores[tag])]

    def update(self, tag, feature, amount):
        key = (tag, feature)
        weight = self.weights[tag].get(feature, 0.0)
        self.totals[key] += (self.step - self.timestamps[key]) * weight
        self.timestamps[key] = self.step
        self.weights[tag][feature] = weight + amount

    def fit(self, examples: list[Example], role: str, epochs: int, seed: int):
        order = list(examples)
        rng = random.Random(seed)
        for _ in range(epochs):
            rng.shuffle(order)
            for example in order:
                self.step += 1
                gold = gold_tags(example, role)
                predicted = self.decode(example.features)
                previous_gold = previous_predicted = "<start>"
                for features, wanted, actual in zip(example.features, gold, predicted):
                    if wanted != actual:
                        for feature in features:
                            self.update(wanted, feature, 1)
                            self.update(actual, feature, -1)
                    if (wanted, previous_gold) != (actual, previous_predicted):
                        self.update(wanted, "previous=" + previous_gold, 1)
                        self.update(actual, "previous=" + previous_predicted, -1)
                    previous_gold, previous_predicted = wanted, actual
        if self.step:
            for tag in TAGS:
                for feature, weight in list(self.weights[tag].items()):
                    key = (tag, feature)
                    average = (self.totals[key] + (self.step - self.timestamps[key]) * weight) / self.step
                    if average:
                        self.weights[tag][feature] = average
                    else:
                        del self.weights[tag][feature]
        return self


def predict(models: dict[str, BioTagger], example: Example):
    result = []
    for role, model in sorted(models.items()):
        start = None
        last = None
        for token, tag in zip(example.tokens + [Token("", len(example.text), len(example.text))], model.decode(example.features) + ["O"]):
            if tag != "I" and start is not None:
                result.append((start, last, role))
                start = None
            if tag == "B":
                start = token.start
            if tag != "O":
                last = token.end
    return sorted(result)


def fit_models(examples, roles, epochs, seed):
    return {role: BioTagger().fit(examples, role, epochs, seed) for role in roles}


def count_scores(gold, predicted):
    gold, predicted = Counter(gold), Counter(predicted)
    matched = sum((gold & predicted).values())
    return {"true_positive": matched, "false_positive": sum(predicted.values()) - matched,
            "false_negative": sum(gold.values()) - matched}


def summarize(counts):
    tp, fp, fn = (counts.get(key, 0) for key in ("true_positive", "false_positive", "false_negative"))
    return {"true_positive": tp, "false_positive": fp, "false_negative": fn, "precision": tp / (tp + fp) if tp + fp else None,
            "recall": tp / (tp + fn) if tp + fn else None,
            "f1": 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else None}


def baseline_predictions(path: Path, examples):
    wanted = {example.label["resource"]["resource_uuid"] for example in examples}
    rows = {}
    for row in read_jsonl(path):
        if row.get("kind") == "source_sample" and row.get("resourceUuid") in wanted:
            if row["resourceUuid"] in rows:
                raise ValueError("Duplicate baseline source row")
            rows[row["resourceUuid"]] = row
    result = {}
    for example in examples:
        field = example.label["field_path"].rsplit("/", 1)[-1]
        row = rows[example.label["resource"]["resource_uuid"]]
        if row["inputs"].get(field) != example.text:
            raise ValueError("Baseline did not consume the exact original field")
        proposals = []
        for credit in row["creditExtraction"]["credits"]:
            evidence = credit["evidence"]
            if evidence["field"] != field:
                continue
            start, end = evidence["startCodePoint"], evidence["endCodePoint"]
            if example.text[start:end] != evidence["quote"]:
                raise ValueError("Baseline supporting phrase has invalid original span")
            # The parser cites a phrase, not an exact name. Derive a name anchor only when
            # the unchanged original phrase contains exactly one literal occurrence.
            matches = list(re.finditer(re.escape(credit["name"]), example.text[start:end]))
            anchor = (start + matches[0].start(), start + matches[0].end()) if len(matches) == 1 else None
            proposals.append({"name": credit["name"], "role": ROLE_NAMES[credit["role"]],
                              "evidence": evidence, "name_span": list(anchor) if anchor else None})
        result[example.key] = proposals
    return result


def grouping_keys(example):
    label = example.label
    return {
        "artists": {label["artist"]["uuid"]},
        "duplicate_families": {label["input_duplicate_family"]} if label.get("input_duplicate_family") else set(),
        "recording_families": {label["known_recording_family"]} if label.get("known_recording_family") else set(),
        "exact_lexical_names": {normalized_name(example.text[start:end]) for start, end, _ in example.gold},
    }


def split_audit(train, development):
    result = {}
    for kind in ("artists", "duplicate_families", "recording_families", "exact_lexical_names"):
        left = set().union(*(grouping_keys(example)[kind] for example in train))
        right = set().union(*(grouping_keys(example)[kind] for example in development))
        result[kind + "_overlap"] = len(left & right)
    if any(result.values()):
        raise ValueError("Development split leaked a declared grouping key")
    return result


def prediction_record(example, fold, models, parser):
    predicted = predict(models, example)
    field = example.label["field_path"].rsplit("/", 1)[-1]
    return {
        "label_id": example.key, "fold": fold, "field": field,
        "selection_stratum": example.label["selection_stratum"],
        "complete_scoring": example.complete, "receipt_excluded": example.receipt_excluded,
        "gold": [{"start": start, "end": end, "role": role, "quote": example.text[start:end]}
                 for start, end, role in example.gold],
        "trained": [{"start": start, "end": end, "role": role, "quote": example.text[start:end],
                     "role_basis": {"kind": "structured_field", "field": field}
                     if (field, role) in (("taper", "Taper"), ("transferrer", "Transferrer"))
                     else {"kind": "model_inferred_unvalidated"}}
                    for start, end, role in predicted],
        "parser": parser,
    }


def development_predictions(examples, assignments, baseline, roles, *, folds, epochs, seed):
    receipts, predictions = [], []
    for fold in range(folds):
        train = [example for example in examples if assignments[example.key] != fold and example.complete]
        development = [example for example in examples if assignments[example.key] == fold]
        if not train:
            raise ValueError("No complete resolved fields available for training")
        models = fit_models(train, roles, epochs, seed + fold)
        receipts.append({
            "fold": fold, "training_fields": len(train),
            "training_artists": len({example.label['artist']['uuid'] for example in train}),
            "training_assertions_by_role": dict(Counter(role for example in train for _, _, role in example.gold)),
            "development_fields": len(development),
            "scored_complete_fields": sum(example.complete for example in development),
            "grouping_audit": split_audit(train, development),
        })
        predictions.extend(prediction_record(example, fold, models, baseline[example.key]) for example in development)
    return receipts, predictions


def score_predictions(predictions):
    methods = ("trained_exact_span_role", "trained_exact_literal_name_role", "parser_exact_literal_name_role")
    counts = {method: Counter() for method in methods}
    by_role = {role: {method: Counter() for method in methods} for role in ROLE_NAMES.values()}
    by_field = defaultdict(Counter)
    field_counts = defaultdict(Counter)
    positive = {"labeled_positives": 0, "recovered_exact_span_role": 0}
    errors = []
    for row in predictions:
        gold = [(value["start"], value["end"], value["role"]) for value in row["gold"]]
        predicted = [(value["start"], value["end"], value["role"]) for value in row["trained"]]
        field_counts[row["field"]]["fields"] += 1
        field_counts[row["field"]]["labeled_assertions"] += len(gold)
        if not row["complete_scoring"]:
            positive["labeled_positives"] += len(gold)
            positive["recovered_exact_span_role"] += sum((Counter(gold) & Counter(predicted)).values())
            continue
        field_counts[row["field"]]["scored_complete_fields"] += 1
        span_counts = count_scores(gold, predicted)
        counts["trained_exact_span_role"].update(span_counts)
        gold_names = [(value["quote"], value["role"]) for value in row["gold"]]
        counts["trained_exact_literal_name_role"].update(count_scores(gold_names, [(value["quote"], value["role"]) for value in row["trained"]]))
        counts["parser_exact_literal_name_role"].update(count_scores(gold_names, [(value["name"], value["role"]) for value in row["parser"]]))
        by_field[row["field"]].update(span_counts)
        for role in ROLE_NAMES.values():
            role_gold = [value for value in gold if value[2] == role]
            role_names = [value for value in gold_names if value[1] == role]
            by_role[role]["trained_exact_span_role"].update(count_scores(role_gold, [value for value in predicted if value[2] == role]))
            by_role[role]["trained_exact_literal_name_role"].update(count_scores(role_names,
                [(value["quote"], role) for value in row["trained"] if value["role"] == role]))
            by_role[role]["parser_exact_literal_name_role"].update(count_scores(role_names,
                [(value["name"], role) for value in row["parser"] if value["role"] == role]))
        if span_counts["false_positive"] or span_counts["false_negative"]:
            errors.append(row)
    return {
        "development_diagnostics": {key: summarize(value) for key, value in counts.items()},
        "by_role": {role: {
            "status": "development_observed" if value["trained_exact_span_role"]["true_positive"] + value["trained_exact_span_role"]["false_negative"] else "not_evaluated",
            "scored_gold_assertions": value["trained_exact_span_role"]["true_positive"] + value["trained_exact_span_role"]["false_negative"],
            "methods": {method: summarize(counts) for method, counts in value.items()},
        } for role, value in sorted(by_role.items())},
        "trained_exact_span_role_by_field": {key: {**dict(field_counts[key]), **summarize(by_field[key])}
            for key in sorted(field_counts)},
        "incomplete_field_positive_diagnostics_only": positive,
        "trained_error_fields": len(errors),
    }, errors


def validate_receipts(args):
    receipt = json.loads(args.receipt.read_text())
    hashes = {"labels": digest(args.labels), "raw_fields": digest(args.fields),
              "labels_receipt": digest(args.receipt), "baseline": digest(args.baseline),
              "baseline_receipt": digest(args.baseline_receipt)}
    if hashes["labels"] != args.frozen_labels_sha256 or receipt["labels_sha256"] != hashes["labels"]:
        raise ValueError("The reviewed/frozen label hash does not match both input and receipt")
    if receipt["raw_fields_sha256"] != hashes["raw_fields"] or receipt["policy_activation_permitted"]:
        raise ValueError("Invalid raw receipt or unsupported policy authority")
    baseline_receipt = json.loads(args.baseline_receipt.read_text())
    if baseline_receipt["state"] != "completed" or baseline_receipt["inputSha256"] != receipt["snapshot_sha256"]:
        raise ValueError("Baseline extraction and labels do not share a completed source snapshot")
    output_hash = baseline_receipt.get("outputSha256")
    if output_hash is not None and output_hash != hashes["baseline"]:
        raise ValueError("Baseline output hash does not match its completed receipt")
    repository = Path(__file__).resolve().parents[2]
    current_sources = ["RelistenApi/Services/Normalization/RecordingCreditExtractor.cs",
                       "RelistenApi/Services/Normalization/RetainedRecordingCreditExtractor.cs",
                       "RelistenApi/Models/Normalization/RecordingExtraction.cs",
                       "tools/RelistenNormalization/NormalizationExtractionBatch.cs"]
    provenance = {
        "historical_baseline_output_hash_verification": "verified" if output_hash else "unavailable_in_original_manifest",
        "historical_baseline_extractor_versions": baseline_receipt["extractorVersions"],
        "current_extractor_source_hashes": {path: digest(repository / path) for path in current_sources},
        "historical_baseline_current_code_equivalence": "not_verified_no_historical_source_hashes",
    }
    hashes["experiment_source"] = digest(Path(__file__).resolve())
    return hashes, receipt, provenance


def run(args):
    hashes, receipt, baseline_provenance = validate_receipts(args)
    examples = load_examples(args.labels, args.fields, receipt.get("training_excluded_label_ids", []))
    assignments, groups = grouped_folds(examples, args.folds)
    roles = sorted({role for example in examples for _, _, role in example.gold})
    baseline = baseline_predictions(args.baseline, examples)
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "splits.json", {"grouping": "artist+duplicate+known-family+exact-lexical-name", "groups": groups})

    folds, predictions = development_predictions(examples, assignments, baseline, roles,
                                                 folds=args.folds, epochs=args.epochs, seed=args.seed)
    diagnostics, errors = score_predictions(predictions)
    final_train = [example for example in examples if example.complete]
    final = fit_models(final_train, roles, args.epochs, args.seed)
    write_json(args.output / "weights.json", {
        "version": VERSION, "tokenizer": TOKENIZER_VERSION, "features": FEATURES_VERSION,
        "roles": {role: model.weights for role, model in final.items()},
        "training_label_ids": [example.key for example in final_train],
        "input_hashes": hashes, "production_authorized": False,
    })
    write_jsonl(args.output / "predictions.jsonl", sorted(predictions, key=lambda row: row["label_id"]))
    write_jsonl(args.output / "errors.jsonl", sorted(errors, key=lambda row: row["label_id"]))
    report = {
        "version": VERSION, "state": "completed_development_only", "production_authorized": False,
        "independent_accuracy_evaluation": False, "python": sys.version.split()[0], "epochs": args.epochs, "seed": args.seed,
        "input_hashes": hashes, "tokenizer": TOKENIZER_VERSION, "features": FEATURES_VERSION,
        "role_ontology": ROLE_NAMES, "baseline_provenance": baseline_provenance,
        "fields": len(examples), "artists": len({example.label['artist']['uuid'] for example in examples}),
        "training_eligible_fields": len(final_train), "excluded_fields": len(examples) - len(final_train),
        "receipt_excluded_label_ids": receipt.get("training_excluded_label_ids", []),
        "assertions": sum(len(example.gold) for example in examples), "folds": folds, "group_components": len(groups),
        **diagnostics,
        "parser_unanchored_mentions": sum(value["name_span"] is None for values in baseline.values() for value in values),
        "limitations": ["Small manually selected Archive-configured artist cohort; no population accuracy claim.",
                        "Labels are development drafts; independent adjudication is incomplete.",
                        "Exact-name grouping prevents lexical name overlap, not undiscovered alias leakage.",
                        "Parser name/role and trained exact-span/role diagnostics have different boundary contracts.",
                        "Unknown/nonexhaustive fields supply no negative labels; positive recovery is not full precision/recall.",
                        "No paid model comparison, policy activation, canonical identity matching or production integration."],
    }
    partial_report = args.output / "report.partial.json"
    write_json(partial_report, report)
    artifact_hashes = {name: digest(args.output / name) for name in
                       ("weights.json", "splits.json", "predictions.jsonl", "errors.jsonl")}
    artifact_hashes["report.json"] = digest(partial_report)
    write_json(args.output / "artifacts.json", {"sha256": artifact_hashes})
    partial_report.replace(args.output / "report.json")
    print(json.dumps({"state": report["state"], "fields": len(examples), "eligible": len(final_train),
                      "error_fields": len(errors), "output": str(args.output)}, sort_keys=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("labels", "receipt", "fields", "baseline", "baseline-receipt", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--frozen-labels-sha256", required=True)
    parser.add_argument("--folds", type=int, default=5)
    parser.add_argument("--epochs", type=int, default=12)
    parser.add_argument("--seed", type=int, default=173)
    args = parser.parse_args()
    if not 1 <= args.epochs <= 100:
        parser.error("epochs must be between 1 and 100")
    run(args)


if __name__ == "__main__":
    main()
