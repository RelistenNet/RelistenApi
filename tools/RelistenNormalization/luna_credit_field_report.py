"""Score frozen development credit fields without treating failed API calls as Luna answers.

This is an offline extraction comparison. It establishes neither global person identity nor
release accuracy. Uncertain fields retain diagnostics and never supply negative labels.
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


VERSION = "luna-credit-field-report-v1"
PRIORITY_ROLES = {"Taper": "taper", "Transferrer": "transferrer"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def rows(data):
    # JSONL has physical LF separators; str.splitlines also splits literal Unicode in fields.
    return [json.loads(line) for line in data.decode("utf-8").split("\n") if line]


def indexed(items, key):
    result = {}
    for item in items:
        require(item[key] not in result, f"Duplicate {key} in comparison inputs")
        result[item[key]] = item
    return result


def prediction(extraction, item):
    """Validate each returned citation against the exact original single field."""
    result = set()
    for credit in extraction["credits"]:
        evidence = credit["evidence"]
        start, end = evidence["startCodePoint"], evidence["endCodePoint"]
        require(isinstance(start, int) and isinstance(end, int)
                and 0 <= start <= end <= len(item["text"]), "Invalid prediction coordinates")
        require(evidence["field"] == item["field"]
                and item["text"][start:end] == evidence["quote"]
                and credit["name"] in evidence["quote"], "Prediction lacks exact original support")
        if credit["role"] in PRIORITY_ROLES.values():
            result.add((credit["name"], credit["role"]))
    return result


def counts(gold, proposed):
    return Counter(true_positive=len(gold & proposed), false_positive=len(proposed - gold),
                   false_negative=len(gold - proposed))


def metrics(totals):
    result = dict(totals)
    tp, fp, fn = (totals[key] for key in ("true_positive", "false_positive", "false_negative"))
    result["precision"] = tp / (tp + fp) if tp + fp else None
    result["recall"] = tp / (tp + fn) if tp + fn else None
    return result


def serialized(values):
    return [{"name": name, "role": role} for name, role in sorted(values)]


def method_predictions(output, item):
    """None means unavailable; an empty set is a successful extraction with no priority credits."""
    local = prediction(output["local"], item)
    luna_result, routing_result = output["luna"], output["currentRouting"]
    no_text = output["state"] == "no_text"
    require(no_text == item["noText"] == (not item["text"].strip()), "No-text outcome disagrees")
    luna = None
    if no_text:
        luna = set()
    elif luna_result is not None and luna_result["state"] == "extracted":
        luna = prediction(luna_result["extraction"], item)
    routing = (prediction(routing_result["extraction"], item)
               if routing_result["state"] in {"local", "extracted"} else None)
    return {"local": local, "luna": luna, "current_routing": routing,
            "luna_with_local_on_failure": local if luna is None else luna,
            "current_routing_with_local_on_failure": local if routing is None else routing}


def transition_counts(gold, local, luna):
    result = Counter(paired_fields=1, recovered_gold_assertions=len((gold - local) & luna),
                     lost_gold_assertions=len((gold & local) - luna),
                     removed_extra_predictions=len((local - gold) - luna),
                     introduced_extra_predictions=len((luna - gold) - local))
    if local == gold and luna == gold:
        result["both_correct_fields"] = 1
    elif local != gold and luna == gold:
        result["corrected_fields"] = 1
    elif local == gold and luna != gold:
        result["new_error_fields"] = 1
    else:
        result["both_wrong_unchanged_fields" if local == luna else "both_wrong_changed_fields"] = 1
    return result


def compare(labels_bytes, selection, results, explicit_exclusions=()):
    require(sha256(labels_bytes) == selection["labelsSha256"], "Labels differ from frozen run")
    labels = indexed(rows(labels_bytes), "label_id")
    items = indexed(selection["items"], "labelId")
    outputs = indexed(results, "labelId")
    require(labels.keys() == items.keys() == outputs.keys(), "Comparison must cover every frozen field")
    require(set(explicit_exclusions) <= labels.keys(), "Receipt excludes a field outside the frozen bundle")
    methods = ("local", "luna", "current_routing", "luna_with_local_on_failure",
               "current_routing_with_local_on_failure")
    totals = {method: Counter(true_positive=0, false_positive=0, false_negative=0)
              for method in methods}
    available, gold_counts, exact = Counter(), Counter(), Counter()
    transitions = Counter()
    paired = {method: Counter(true_positive=0, false_positive=0, false_negative=0)
              for method in ("local", "luna")}
    by_role = {role: {method: Counter(true_positive=0, false_positive=0, false_negative=0)
                     for method in methods} for role in PRIORITY_ROLES.values()}
    states, routing_states = Counter(), Counter()
    diagnostics, excluded, scored = [], [], 0
    for label_id, item in items.items():
        label, output = labels[label_id], outputs[label_id]
        require(label["family_partition"] == label["artist_partition"] == "development",
                "Held-out field supplied to development comparison")
        require(label["field_sha256"] == item["fieldSha256"] == sha256(item["text"].encode()),
                "Original field bytes disagree")
        require(label["field_path"] == "/raw_record/row/" + item["field"], "Field path disagrees")
        gold = set()
        for assertion in label["answer"]["assertions"]:
            name = assertion["name"]
            require(item["text"][name["start"]:name["end"]] == name["quote"],
                    "Gold name lacks original support")
            if assertion["role"] in PRIORITY_ROLES:
                gold.add((name["quote"], PRIORITY_ROLES[assertion["role"]]))
        proposed = method_predictions(output, item)
        local, luna = proposed["local"], proposed["luna"]
        states[output["state"]] += 1
        routing_states[output["currentRouting"]["state"]] += 1
        answer = label["answer"]
        eligible = (answer["exhaustive"] and not answer["unresolved"] and not answer["unknowns"]
                    and label_id not in explicit_exclusions)
        diagnostic = {"label_id": label_id, "scored": bool(eligible), "gold": serialized(gold),
                      "predictions": {method: None if values is None else serialized(values)
                                      for method, values in proposed.items()}, "state": output["state"]}
        if not eligible:
            excluded.append(label_id)
            diagnostics.append(diagnostic)
            continue
        scored += 1
        for method, values in proposed.items():
            if values is None:
                continue
            available[method] += 1
            gold_counts[method] += len(gold)
            totals[method].update(counts(gold, values))
            exact[method] += gold == values
            for role in PRIORITY_ROLES.values():
                by_role[role][method].update(counts({v for v in gold if v[1] == role},
                                                   {v for v in values if v[1] == role}))
        if luna is not None:
            transitions.update(transition_counts(gold, local, luna))
            paired["local"].update(counts(gold, local))
            paired["luna"].update(counts(gold, luna))
        if any(values is None or values != gold for values in proposed.values()):
            diagnostics.append(diagnostic)
    return {"version": VERSION, "status": "development_diagnostic",
            "identity_accuracy_established": False, "policy_activation_permitted": False,
            "fields": len(items), "scored_resolved_fields": scored,
            "excluded_fields": len(excluded), "excluded_label_ids": excluded,
            "explicit_receipt_exclusions": sorted(explicit_exclusions),
            "states_all_fields": dict(states), "routing_states_all_fields": dict(routing_states),
            "methods": {method: {**metrics(totals[method]), "scored_fields": available[method],
                                 "unavailable_fields": scored - available[method],
                                 "gold_assertions": gold_counts[method], "exact_fields": exact[method]}
                        for method in methods},
            "paired_local_luna": {"transitions": dict(transitions),
                                   "methods": {method: metrics(value) for method, value in paired.items()}},
            "by_role": {role: {method: metrics(value) for method, value in values.items()}
                        for role, values in by_role.items()}, "diagnostics": diagnostics}


def run(labels_path, comparison_directory, new_output):
    comparison_directory, new_output = Path(comparison_directory), Path(new_output)
    require(not new_output.exists(), "Choose a new offline report directory")
    labels_bytes = Path(labels_path).read_bytes()
    manifest_bytes = (comparison_directory / "manifest.json").read_bytes()
    manifest = json.loads(manifest_bytes)
    require(manifest["state"] in {"completed", "completed_partial"}, "Run lacks a terminal comparison receipt")
    selection = json.loads((comparison_directory / "selection.json").read_bytes())
    require(selection == manifest["selection"], "Frozen selection differs from completed run")
    label_receipt_bytes = Path(labels_path).with_name("receipt.json").read_bytes()
    require(sha256(label_receipt_bytes) == selection["receiptSha256"], "Label receipt differs from frozen run")
    label_receipt = json.loads(label_receipt_bytes)
    require(label_receipt["labels_sha256"] == sha256(labels_bytes), "Label receipt describes different labels")
    results_bytes = (comparison_directory / "results.jsonl").read_bytes()
    receipts_bytes = (comparison_directory / "receipts.json").read_bytes()
    require(sha256(results_bytes) == manifest["resultsSha256"]
            and sha256(receipts_bytes) == manifest["receiptsSha256"], "Completed output bytes changed")
    report = compare(labels_bytes, selection, rows(results_bytes), label_receipt["training_excluded_label_ids"])
    report["provenance"] = {"labels_sha256": sha256(labels_bytes), "manifest_sha256": sha256(manifest_bytes),
                            "results_sha256": sha256(results_bytes), "receipts_sha256": sha256(receipts_bytes),
                            "label_receipt_sha256": sha256(label_receipt_bytes),
                            "scorer_sha256": sha256(Path(__file__).read_bytes())}
    report["job"] = manifest["job"]
    report["paid_attempts_for_job"] = manifest["paidAttempts"]
    report["cached_audit_fields"] = manifest["lunaFromCache"]
    report["cached_keyless_replay_fields"] = manifest["keylessReplayFromCache"]
    new_output.mkdir()
    # This is the completion marker; validation and scoring finish before creating the directory.
    staging = new_output / "report.json.writing"
    with staging.open("x") as output:
        json.dump(report, output, ensure_ascii=False, indent=2)
        output.write("\n")
    staging.rename(new_output / "report.json")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("labels")
    parser.add_argument("comparison_directory")
    parser.add_argument("new_output")
    args = parser.parse_args()
    result = run(args.labels, args.comparison_directory, args.new_output)
    print(json.dumps({key: result[key] for key in ("fields", "scored_resolved_fields", "methods", "job")}, indent=2))
