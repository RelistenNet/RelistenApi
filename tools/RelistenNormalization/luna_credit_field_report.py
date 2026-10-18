"""Score frozen development credit fields without treating failed API calls as Luna answers.

This is an offline extraction comparison. It establishes neither global person identity nor
release accuracy. Uncertain fields retain diagnostics and never supply negative labels.
"""

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import uuid


VERSION = "luna-credit-field-report-v1"
PRIORITY_ROLES = {"Taper": "taper", "Transferrer": "transferrer"}


class CohortPlan:
    """Authenticate one complete bounded cohort without changing its real partitions.

    The caller supplies the approved plan digest independently. File references stay inside
    that bundle. This object authorizes offline scoring only, never training or paid work.
    """

    def __init__(self, path, expected_sha256, read=None):
        self.path = Path(path).resolve()
        self.read = read or (lambda path: Path(path).read_bytes())
        data = self.read(self.path)
        require(len(data) <= 1024 * 1024, "Cohort plan exceeds its byte bound")
        require(sha256(data) == expected_sha256, "Cohort plan differs from its approved digest")
        self.sha256 = expected_sha256
        self.plan = closed_json(data)
        closed(self.plan, {"schema_version", "purpose", "field_count", "cap_usd",
                           "maximum_total_attempts", "artist_partition", "family_partition",
                           "labels", "fields", "receipt", "scope"}, "cohort plan")
        plan = self.plan
        require(plan["schema_version"] == "luna-credit-cohort-job-v1", "Unsupported cohort plan")
        require(type(plan["field_count"]) is int and 1 <= plan["field_count"] <= 600,
                "Cohort must declare 1 through 600 complete fields")
        require(type(plan["cap_usd"]) in (int, float) and 0 < plan["cap_usd"] <= 30
                and math.isfinite(plan["cap_usd"])
                and type(plan["maximum_total_attempts"]) is int
                and 1 <= plan["maximum_total_attempts"] <= 1200, "Invalid cohort job bounds")
        partitions = (plan["artist_partition"], plan["family_partition"])
        require(all(value in {"development", "validation", "test"} for value in partitions),
                "Unsupported cohort partition")
        purpose = plan["purpose"]
        require((purpose == "development" and partitions == ("development", "development"))
                or (purpose == "tune" and "validation" in partitions and "test" not in partitions)
                or (purpose == "untouched_evaluation" and "test" in partitions),
                "Cohort purpose disagrees with its partitions")
        payloads = {}
        self.paths = {}
        for name in ("labels", "fields", "receipt", "scope"):
            reference = plan[name]
            closed(reference, {"path", "sha256"}, "cohort file reference")
            require(isinstance(reference["path"], str) and reference["path"], "Missing cohort path")
            relative = Path(reference["path"])
            target = (self.path.parent / relative).resolve()
            require(not relative.is_absolute() and target.is_relative_to(self.path.parent),
                    "Cohort path escapes its bundle")
            require(valid_digest(reference["sha256"]), "Invalid cohort file digest")
            payloads[name] = self.read(target)
            require(len(payloads[name]) <= (128 * 1024 * 1024 if name in {"labels", "fields"} else 1024 * 1024),
                    "Cohort input exceeds its byte bound")
            self.paths[name] = target
            require(sha256(payloads[name]) == reference["sha256"], "Cohort file bytes differ")
        self.labels_bytes = payloads["labels"]
        self.fields_bytes = payloads["fields"]
        self.receipt = json.loads(payloads["receipt"])
        receipt = self.receipt
        require(receipt["schema_version"] == "credit-field-cohort-v1"
                and receipt["status"] == "frozen_cohort" and receipt["purpose"] == purpose
                and type(receipt["fields"]) is int and receipt["fields"] == plan["field_count"]
                and receipt["labels_sha256"] == plan["labels"]["sha256"]
                and receipt["raw_fields_sha256"] == plan["fields"]["sha256"]
                and receipt["scope_sha256"] == plan["scope"]["sha256"]
                and receipt["labels_frozen_before_predictions"] is True,
                "Cohort receipt differs from its frozen plan")
        self.labels = indexed(rows(self.labels_bytes), "label_id")
        self.fields = indexed(rows(self.fields_bytes), "label_id")
        self.scope = closed_json(payloads["scope"])
        closed(self.scope, {"schema_version", "items"}, "cohort scope")
        require(self.scope["schema_version"] == "credit-cohort-scope-v1", "Unsupported cohort scope")
        scopes = indexed(self.scope["items"], "label_id")
        require(len(self.labels) == plan["field_count"]
                and self.labels.keys() == self.fields.keys() == scopes.keys()
                and list(self.fields) == list(scopes),
                "Cohort must retain the complete declared ordered population")
        for scope in self.scope["items"]:
            # C# stores typed Guid values in the retained selection profile.
            scope["source_uuid"] = str(uuid.UUID(scope["source_uuid"]))
            scope["artist_uuid"] = str(uuid.UUID(scope["artist_uuid"]))
        for key, label in self.labels.items():
            self._check_field(key, label, self.fields[key], scopes[key])
        self.provider_counts = dict(Counter(item["provider_origin"]["provider"] for item in self.scope["items"]))
        self.contact_ids = {key for key, raw in self.fields.items() if contact_bearing(raw["field_text"])}
        exclusions = receipt.get("training_excluded_label_ids", [])
        require(isinstance(exclusions, list) and len(exclusions) == len(set(exclusions))
                and set(exclusions) <= self.labels.keys(), "Unknown or duplicate cohort exclusion")
        self.exclusions = exclusions

    def _check_field(self, key, label, raw, scope):
        closed(scope, {"label_id", "evidence_id", "source_uuid", "artist_uuid", "field",
                       "field_sha256", "family_partition", "artist_partition", "provider_origin"},
               "cohort scope item")
        source = str(uuid.UUID(label["resource"]["resource_uuid"]))
        artist = str(uuid.UUID(label["artist"]["uuid"]))
        field = label["field_path"].removeprefix("/raw_record/row/")
        text = raw["field_text"]
        require(field in {"taper", "transferrer", "source", "description", "lineage", "taper_notes"}
                and label["field_path"] == "/raw_record/row/" + field
                and label["resource"]["provider"] == "relisten_catalog_snapshot"
                and label["resource"]["resource_type"] == "source"
                and uuid.UUID(source).int != 0 and uuid.UUID(artist).int != 0
                and key == "credit-field:" + source + ":" + field
                and label["task_id"] == "extract:source_sample:" + source
                and label["evidence_id"] == "source_sample:" + source, "Invalid cohort field identity")
        require(isinstance(text, str) and raw["evidence_id"] == label["evidence_id"]
                and raw["field_path"] == label["field_path"]
                and sha256(text.encode()) == raw["field_sha256"] == label["field_sha256"]
                and type(label["field_codepoints"]) is int and label["field_codepoints"] == len(text),
                "Cohort original field differs")
        expected = {"label_id": key, "evidence_id": label["evidence_id"], "source_uuid": source,
                    "artist_uuid": artist, "field": field,
                    "field_sha256": label["field_sha256"], "family_partition": self.plan["family_partition"],
                    "artist_partition": self.plan["artist_partition"]}
        require(all(scope[name] == value for name, value in expected.items())
                and all(label[name] == self.plan[name] for name in ("artist_partition", "family_partition"))
                and label["scope"] == "complete_single_field", "Cohort field scope differs")
        development = self.plan["purpose"] == "development"
        require(label["status"] == ("draft_development" if development else "adjudicated"),
                "Cohort label status differs")
        answer = label["answer"]
        require(type(answer["exhaustive"]) is bool and type(answer["unresolved"]) is bool,
                "Invalid cohort completeness marker")
        require(development or isinstance(answer.get("adjudicator"), str)
                and answer["adjudicator"].strip(), "Non-development label lacks an adjudicator")
        origin = scope["provider_origin"]
        closed(origin, {"provider", "kind", "evidence_sha256", "resource_key_sha256"}, "provider origin")
        require((origin["provider"] == origin["kind"] == "unknown"
                 and origin["evidence_sha256"] is None and origin["resource_key_sha256"] is None)
                or (origin["provider"] in {"archive.org", "local", "phish.in"}
                    and origin["kind"] in {"retained_provider_input", "import_execution"}
                    and valid_digest(origin["evidence_sha256"]) and valid_digest(origin["resource_key_sha256"])),
                "Cohort provider provenance is incomplete")

    def check_selection(self, labels_bytes, selection):
        require(labels_bytes == self.labels_bytes and selection["cohort"] == {
            "planSha256": self.sha256, "plan": self.plan, "scope": self.scope},
            "Comparison used another cohort plan or scope")
        require(selection["labelsSha256"] == self.plan["labels"]["sha256"]
                and selection["fieldsSha256"] == self.plan["fields"]["sha256"]
                and selection["receiptSha256"] == self.plan["receipt"]["sha256"],
                "Cohort comparison inputs differ")
        items = indexed(selection["items"], "labelId")
        require(list(items) == list(self.fields), "Cohort selection changed its ordered population")
        for key, item in items.items():
            label, raw = self.labels[key], self.fields[key]
            require(item["taskId"] == label["task_id"] and item["evidenceId"] == label["evidence_id"]
                    and item["sourceUuid"] == str(uuid.UUID(label["resource"]["resource_uuid"]))
                    and item["artistUuid"] == str(uuid.UUID(label["artist"]["uuid"]))
                    and item["field"] == label["field_path"].rsplit("/", 1)[-1]
                    and item["text"] == raw["field_text"]
                    and item["fieldSha256"] == label["field_sha256"]
                    and type(item["noText"]) is bool and item["noText"] == (not item["text"].strip()),
                    "Cohort selection field differs")


def closed(value, keys, name):
    require(isinstance(value, dict) and value.keys() == keys, "Unexpected properties in " + name)


def closed_json(data):
    def unique(pairs):
        value = {}
        for key, item in pairs:
            require(key not in value, "Duplicate cohort property")
            value[key] = item
        return value
    return json.loads(data, object_pairs_hook=unique)


def valid_digest(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def contact_bearing(text):
    """Match the runner's Unicode letter/number email pattern without a new regex dependency."""
    for at, character in enumerate(text):
        if character != "@" or at == 0 or not (text[at - 1].isalnum() or text[at - 1] in "._%+-"):
            continue
        end = at + 1
        while end < len(text) and (text[end].isalnum() or text[end] in ".-"):
            end += 1
        for dot in range(at + 2, end - 1):
            if text[dot] == "." and text[dot + 1:dot + 3].isalpha():
                return True
    return False


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


def method_predictions(output, item, *, contact_scope=False):
    """None means unavailable; an empty set is a successful extraction with no priority credits."""
    local = prediction(output["local"], item)
    luna_result, routing_result = output["luna"], output["currentRouting"]
    no_text = output["state"] == "no_text"
    require(no_text == item["noText"] == (not item["text"].strip()), "No-text outcome disagrees")
    if contact_scope:
        require(output["state"] == "not_requested_contact_scope" and luna_result is None
                and output["cacheReplay"] is None and routing_result["state"] == "not_evaluated"
                and routing_result["reason"] == "contact_scope", "Contact field was requested or routed")
        return {"local": local, "luna": None, "current_routing": None,
                "luna_with_local_on_failure": None, "current_routing_with_local_on_failure": None}
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


def compare(labels_bytes, selection, results, explicit_exclusions=(), *, cohort_plan=None):
    if cohort_plan is not None:
        cohort_plan.check_selection(labels_bytes, selection)
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
        if cohort_plan is not None:
            require((label_id in cohort_plan.contact_ids) == (output["state"] == "not_requested_contact_scope"),
                    "Contact field outcome disagrees with its original text")
        if cohort_plan is None:
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
        proposed = method_predictions(output, item,
                                      contact_scope=cohort_plan is not None and label_id in cohort_plan.contact_ids)
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
    value = {"version": VERSION, "status": "development_diagnostic",
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
    if cohort_plan is not None:
        value["status"] = "cohort_diagnostic"
        value["cohort"] = {"plan_sha256": cohort_plan.sha256, "purpose": cohort_plan.plan["purpose"],
                           "artist_partition": cohort_plan.plan["artist_partition"],
                           "family_partition": cohort_plan.plan["family_partition"],
                           "provider_origin_fields": cohort_plan.provider_counts}
    return value


def run(labels_path, comparison_directory, new_output, *, cohort_plan=None, cohort_plan_sha256=None,
        comparison_sha256=None):
    comparison_directory, new_output = Path(comparison_directory), Path(new_output)
    require(not new_output.exists(), "Choose a new offline report directory")
    require((cohort_plan is None) == (cohort_plan_sha256 is None), "Pair the cohort plan and digest")
    cohort = None if cohort_plan is None else CohortPlan(cohort_plan, cohort_plan_sha256)
    labels_bytes = Path(labels_path).read_bytes()
    manifest_bytes = (comparison_directory / "manifest.json").read_bytes()
    if cohort is not None:
        require(comparison_sha256 is not None and sha256(manifest_bytes) == comparison_sha256,
                "Cohort comparison differs from its approved manifest digest")
    manifest = json.loads(manifest_bytes)
    if cohort is not None:
        require(manifest["version"] == "luna-credit-cohort-v1"
                and manifest["fields"] == cohort.plan["field_count"], "Wrong cohort comparison")
    require(manifest["state"] in {"completed", "completed_partial"}, "Run lacks a terminal comparison receipt")
    selection = json.loads((comparison_directory / "selection.json").read_bytes())
    require(selection == manifest["selection"], "Frozen selection differs from completed run")
    label_receipt_bytes = (Path(labels_path).with_name("receipt.json") if cohort is None
                           else cohort.paths["receipt"]).read_bytes()
    require(sha256(label_receipt_bytes) == selection["receiptSha256"], "Label receipt differs from frozen run")
    label_receipt = json.loads(label_receipt_bytes)
    require(label_receipt["labels_sha256"] == sha256(labels_bytes), "Label receipt describes different labels")
    results_bytes = (comparison_directory / "results.jsonl").read_bytes()
    receipts_bytes = (comparison_directory / "receipts.json").read_bytes()
    require(sha256(results_bytes) == manifest["resultsSha256"]
            and sha256(receipts_bytes) == manifest["receiptsSha256"], "Completed output bytes changed")
    exclusions = label_receipt["training_excluded_label_ids"] if cohort is None else cohort.exclusions
    report = compare(labels_bytes, selection, rows(results_bytes), exclusions, cohort_plan=cohort)
    if cohort is not None:
        report["coverage"] = cohort_coverage(cohort, indexed(rows(results_bytes), "labelId"),
                                             json.loads(receipts_bytes), manifest)
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


def cohort_coverage(cohort, outputs, audit, manifest):
    """Count observed outcomes and current-job attempts without turning skipped fields into answers."""
    require(isinstance(audit["attempts"], list), "Cohort attempt audit is absent")
    attempts = audit["attempts"]
    job_uuid = manifest.get("jobUuid", manifest.get("job", {}).get("uuid"))
    require(not attempts or job_uuid is not None, "Cohort attempt audit lacks its job identity")
    own_attempts = [attempt for attempt in attempts if attempt["jobUuid"] == job_uuid]
    attempted_keys = {attempt["cacheKey"] for attempt in own_attempts}
    no_text = {key for key, raw in cohort.fields.items() if not raw["field_text"].strip()}
    available = {key for key, output in outputs.items()
                 if output["luna"] is not None and output["luna"]["state"] == "extracted"}
    attempted = {key for key, output in outputs.items()
                 if output["luna"] is not None and output["luna"].get("cacheKey") in attempted_keys}
    populations = {"selected": set(cohort.labels),
                   "eligible": set(cohort.labels) - no_text - cohort.contact_ids,
                   "attempted_in_job": attempted, "luna_available": available,
                   "contact_excluded": cohort.contact_ids, "no_text": no_text}
    return {"fields": {name: len(keys) for name, keys in populations.items()},
            "label_ids": {name: sorted(keys) for name, keys in populations.items()},
            "attempts_in_job": len(own_attempts),
            "availability": "Successful Luna responses; no-text fields are accounted separately. Attempted fields link returned cache keys to this job's durable attempts."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("labels")
    parser.add_argument("comparison_directory")
    parser.add_argument("new_output")
    parser.add_argument("--cohort-plan", type=Path)
    parser.add_argument("--cohort-plan-sha256")
    parser.add_argument("--comparison-sha256")
    args = parser.parse_args()
    result = run(args.labels, args.comparison_directory, args.new_output,
                 cohort_plan=args.cohort_plan, cohort_plan_sha256=args.cohort_plan_sha256,
                 comparison_sha256=args.comparison_sha256)
    print(json.dumps({key: result[key] for key in ("fields", "scored_resolved_fields", "methods", "job")}, indent=2))
