"""Compare people rules and trained scorers on deduplicated development pairs.

Source-only C# exports establish features, actor keys and grouping before this
adapter opens separate targets. It retains every declared diagnostic edge while
fitting and measuring one consistent, single-view unit per unordered actor pair.
"""

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import types
import uuid


VERSION = "people-adjudicated-development-comparison-v1"
PROJECTION = "people-adjudicated-source-endpoints-v1"
BASELINE = "people-adjudicated-baseline-v1"
PACKAGE = "people-adjudicated-training-inputs:1"
SOURCE_FILES = ("source/records.jsonl", "source/endpoints.jsonl", "source/questions.jsonl", "source/grouping.jsonl")
TARGET_FILE = "targets/decisions.jsonl"
FEATURES = ("name_token_similarity", "exact_name", "artist_overlap", "contact_agreement", "profile_agreement")
TARGETS = {"same_entity": 1, "different_entities": 0, "insufficient_evidence": None}
MAX_BYTES = 128 * 1024 * 1024
MAX_ROWS = 10000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def digest(data):
    return hashlib.sha256(data).hexdigest()


class Inputs:
    def __init__(self, experiment):
        self.experiment = experiment
        self.pins = {}

    def pin(self, path, expected=None, expected_sha=None):
        record = self.experiment.pin(path, expected)
        require(expected_sha is None or record["sha256"] == expected_sha, "Input digest changed")
        previous = self.pins.setdefault(record["path"], record)
        require(previous == record, "Input changed during comparison")
        return record

    def read(self, path, expected=None, expected_sha=None):
        record = self.pin(path, expected, expected_sha)
        require(record["bytes"] <= MAX_BYTES, "Decoded input exceeds bound")
        with Path(path).open("rb") as stream:
            data = stream.read(MAX_BYTES + 1)
        require(len(data) == record["bytes"] and digest(data) == record["sha256"], "Input changed before decoding")
        return data

    def json(self, path, expected=None, expected_sha=None):
        return json.loads(self.read(path, expected, expected_sha))

    def rows(self, path, expected=None, allow_empty=False):
        data = self.read(path, expected)
        physical = data.split(b"\n")
        if physical[-1] == b"":
            physical.pop()
        require((allow_empty or bool(physical)) and len(physical) <= MAX_ROWS and all(row.strip() for row in physical),
                "Invalid bounded JSONL rows")
        require(expected is None or expected.get("rows") is None or expected["rows"] == len(physical),
                "Artifact row count changed")
        return [json.loads(row) for row in physical]

    def recheck(self):
        self.experiment.recheck(list(self.pins.values()))


def import_experiment(path, expected_sha):
    path = Path(path).resolve()
    with path.open("rb") as stream:
        source = stream.read(1024 * 1024 + 1)
    require(len(source) <= 1024 * 1024 and digest(source) == expected_sha, "Experiment executed-source digest changed")
    name = "pinned_people_identity_experiment"
    module = types.ModuleType(name)
    module.__file__ = str(path)
    sys.modules[name] = module
    exec(compile(source, str(path), "exec"), module.__dict__)
    require(tuple(module.FEATURE_ORDER) == FEATURES, "Runtime five-feature contract changed")
    return module


def unique(rows, key):
    result = {}
    for row in rows:
        require(row[key] not in result, f"Duplicate {key}")
        result[row[key]] = row
    return result


def relative_file(directory, name):
    path = (directory / name).resolve()
    require(path.is_relative_to(directory.resolve()), "Artifact path escapes its directory")
    return path


def scoped_reference(value, evidence_id):
    require(set(value) == {"evidenceId", "field", "referenceUuid"}
            and value["evidenceId"] == evidence_id and bool(value["field"]), "Actor reference lost source scope")
    require(str(uuid.UUID(value["referenceUuid"])) == value["referenceUuid"], "Invalid actor reference UUID")
    return (value["evidenceId"], value["field"], value["referenceUuid"])


def selector_evidence(selector):
    prefix = "/raw_record/row/"
    require(selector["field_path"].startswith(prefix), "Source selector is not a raw-field pointer")
    encoded_field = selector["field_path"][len(prefix):]
    field = encoded_field.replace("~1", "/").replace("~0", "~")
    span = selector["span"]
    return {"field": field, "jsonPointer": "/" + encoded_field, "quote": span["quote"],
            "startCodePoint": span["start"], "endCodePoint": span["end"]}


def endpoint_keys(endpoints, diagnostics):
    """Choose source-only pair keys, never actor names or target identities."""
    require(endpoints.keys() == diagnostics.keys(), "Endpoint diagnostics coverage changed")
    keys, references = {}, {}
    for key, endpoint in endpoints.items():
        row = diagnostics[key]
        require(row["evidenceId"] == endpoint["evidence_id"] and row["artistUuid"] == endpoint["artist_uuid"],
                "Endpoint diagnostic source ownership changed")
        require(len(row["selectors"]) == len(endpoint["selectors"]), "Selector diagnostics coverage changed")
        for index, (selector, original) in enumerate(zip(row["selectors"], endpoint["selectors"])):
            require(selector["selectorIndex"] == index and selector["kind"] == original["kind"]
                    and selector["evidence"] == selector_evidence(original), "Selector diagnostic coordinates changed")
        refs = set()
        for variant in row["variants"]:
            index = variant["selectorIndex"]
            require(type(index) is int and 0 <= index < len(endpoint["selectors"]), "Variant has an unknown selector")
            if variant["actorReference"] is not None:
                ref = scoped_reference(variant["actorReference"], endpoint["evidence_id"])
                require(ref[1] == selector_evidence(endpoint["selectors"][index])["field"], "Actor reference moved fields")
                refs.add(ref)
        references[key] = refs
        if row["status"] == "projected":
            require(len(refs) == 1 and all(item["status"] == "matched" for item in row["selectors"]),
                    "Projected actor must have one consistently matched scoped reference")
            # Incomplete matching context can withhold a feature vector without
            # undoing the exact source-local actor attribution used for grouping.
            keys[key] = ("source_actor", *next(iter(refs)))
        else:
            # Exact selector coordinates are weaker than an extracted actor reference.
            # Keep that limitation visible rather than joining names across records.
            anchors = sorted(canonical({name: selector[name] for name in ("field_path", "field_sha256", "span")})
                             for selector in endpoint["selectors"])
            keys[key] = ("source_selector", endpoint["evidence_id"], *anchors)
    return keys, references


def components_for(questions, exported_groups, bridges, references, pair_tasks):
    """Union all source tasks and duplicate pairs before opening answer targets."""
    parent = {key: key for key in questions}

    def find(key):
        while parent[key] != key:
            parent[key] = parent[parent[key]]
            key = parent[key]
        return key

    def merge(members):
        require(bool(members) and len(members) == len(set(members)) and set(members) <= questions.keys(),
                "Invalid source grouping membership")
        for key in members[1:]:
            parent[find(key)] = find(members[0])

    seen = set()
    for group in exported_groups:
        members = group["taskIds"]
        require(not seen.intersection(members), "Original source grouping repeats a task")
        seen.update(members)
        merge(members)
    require(seen == questions.keys(), "Original source grouping omitted tasks")
    # Check original task-key grouping is represented before adding C# bridges.
    first = {}
    for key, question in questions.items():
        for group in question["split_group_ids"]:
            if group in first:
                require(find(key) == find(first[group]), "Source grouping split an original grouping key")
            else:
                first[group] = key

    tasks_by_endpoint = defaultdict(set)
    for key, question in questions.items():
        for endpoint in [question["focus_endpoint_id"], *question["candidate_endpoint_ids"]]:
            tasks_by_endpoint[endpoint].add(key)
    endpoints_by_ref = defaultdict(set)
    for endpoint, refs in references.items():
        for ref in refs:
            endpoints_by_ref[ref].add(endpoint)
    expected = {}
    for ref, endpoints in endpoints_by_ref.items():
        tasks = set().union(*(tasks_by_endpoint[key] for key in endpoints))
        if len(endpoints) > 1 or len(tasks) > 1:
            expected[ref] = (endpoints, tasks)
    seen_refs = set()
    for bridge in bridges:
        reference = bridge["reference"]
        ref = scoped_reference(reference, reference["evidenceId"])
        require(ref in expected and ref not in seen_refs, "Unexpected or duplicate source actor bridge")
        seen_refs.add(ref)
        endpoints, tasks = expected[ref]
        require(len(bridge["endpointIds"]) == len(endpoints) and set(bridge["endpointIds"]) == endpoints
                and len(bridge["taskIds"]) == len(tasks) and set(bridge["taskIds"]) == tasks,
                "Source actor bridge has incomplete or foreign members")
        if bridge["taskIds"]:
            merge(bridge["taskIds"])
    require(seen_refs == expected.keys(), "Source actor bridge coverage changed")
    for tasks in pair_tasks.values():
        merge(sorted(tasks))
    groups = defaultdict(list)
    for key in questions:
        groups[find(key)].append(key)
    components = sorted((sorted(members) for members in groups.values()), key=lambda members: members[0])
    by_task = {key: members[0] for members in components for key in members}
    return components, by_task


@dataclass
class DeclaredEdge:
    edge_id: str
    task_id: str
    candidate_id: str
    focus_id: str
    task_kind: str
    component: str
    pair_id: str
    target: int | None
    values: tuple
    same_recording: bool
    same_artist: bool
    comparison: dict | None
    projection_supported: bool
    projection_issue: str | None
    provenance: dict


def source_edges(experiment, questions, endpoints, diagnostics, baseline, groups, bridges):
    keys, references = endpoint_keys(endpoints, diagnostics)
    expected = {(key, candidate) for key, question in questions.items()
                for candidate in question["candidate_endpoint_ids"]}
    require(len(expected) <= MAX_ROWS and len(baseline) == len(expected), "Declared edge coverage changed")
    rows, seen, pair_tasks = [], set(), defaultdict(set)
    fields = {"taskId", "taskKind", "focusEndpointId", "candidateEndpointId", "focusEvidenceId",
              "candidateEvidenceId", "sameRecording", "declaredCandidateCount", "features", "comparison",
              "projectionSupported", "projectionIssue"}
    for row in baseline:
        require(set(row) == fields, "Unexpected baseline fields; labels are not features")
        edge_key = (row["taskId"], row["candidateEndpointId"])
        require(edge_key in expected and edge_key not in seen, "Unknown or repeated declared edge")
        seen.add(edge_key)
        question = questions[edge_key[0]]
        focus, candidate = endpoints[question["focus_endpoint_id"]], endpoints[edge_key[1]]
        same_recording = focus["evidence_id"] == candidate["evidence_id"]
        require(row["focusEndpointId"] == question["focus_endpoint_id"] and row["taskKind"] == question["task_kind"]
                and row["focusEvidenceId"] == focus["evidence_id"] and row["candidateEvidenceId"] == candidate["evidence_id"]
                and type(row["sameRecording"]) is bool and row["sameRecording"] == same_recording
                and type(row["declaredCandidateCount"]) is int
                and row["declaredCandidateCount"] == len(question["candidate_endpoint_ids"]), "Baseline edge ownership changed")
        supported = all(diagnostics[key]["status"] in {"projected", "name_only"}
                        and diagnostics[key]["projectionIssue"] is None
                        for key in (question["focus_endpoint_id"], edge_key[1]))
        require(type(row["projectionSupported"]) is bool and row["projectionSupported"] == supported,
                "Baseline projection availability changed")
        if supported:
            values = experiment.actual_features(row["comparison"])
            require(row["features"] == list(values) and row["projectionIssue"] is None,
                    "Baseline features differ from actual C# comparison")
        else:
            require(row["features"] is None and row["comparison"] is None and bool(row["projectionIssue"]),
                    "Unsupported projection must have no fabricated feature vector")
            values = ()
        pair = sorted((keys[question["focus_endpoint_id"]], keys[edge_key[1]]))
        pair_id = "pair:" + digest(canonical(pair).encode())
        pair_tasks[pair_id].add(edge_key[0])
        rows.append(DeclaredEdge("|".join(edge_key), edge_key[0], edge_key[1], question["focus_endpoint_id"],
                    question["task_kind"], "", pair_id, None, values, same_recording,
                    focus["artist_uuid"] == candidate["artist_uuid"], row["comparison"], supported,
                    row["projectionIssue"], {}))
    require(seen == expected, "Missing declared candidate")
    components, by_task = components_for(questions, groups, bridges, references, pair_tasks)
    for row in rows:
        row.component = by_task[row.task_id]
    return sorted(rows, key=lambda row: row.edge_id), components


def join_targets(edges, target_rows):
    targets = {(row["task_id"], row["candidate_endpoint_id"]): row for row in target_rows}
    require(len(targets) == len(target_rows) and set(targets) == {(row.task_id, row.candidate_id) for row in edges},
            "Targets do not cover every declared edge exactly once")
    for row in edges:
        target = targets[(row.task_id, row.candidate_id)]
        require(target["decision"] in TARGETS and target["label"] == TARGETS[target["decision"]]
                and (target["label"] is None or type(target["label"]) is int), "Invalid target; unknown is not negative")
        row.target = target["label"]
        row.provenance = {key: value for key, value in target.items() if key not in {"label", "decision"}}


def fit_units(experiment, edges):
    by_pair = defaultdict(list)
    for row in edges:
        by_pair[row.pair_id].append(row)
    units, records = [], []
    for pair_id, members in sorted(by_pair.items()):
        components = {row.component for row in members}
        require(len(components) == 1, "Duplicate pair escaped source grouping")
        resolved = {row.target for row in members if row.target is not None}
        views = {row.values for row in members if row.projection_supported}
        supported_resolved = [row for row in members if row.projection_supported and row.target is not None]
        if len(resolved) > 1:
            status = "conflicting_resolved_targets"
        elif not views:
            status = "unsupported_projection_only"
        elif len(views) > 1:
            status = "multiple_feature_views"
        elif not resolved:
            status = "unknown_only"
        elif not supported_resolved:
            status = "resolved_target_without_supported_view"
        else:
            status = "eligible"
        record = {"unitId": pair_id, "status": status, "component": next(iter(components)),
            "declaredEdgeIds": [row.edge_id for row in members], "declaredRows": len(members),
            "unknownRows": sum(row.target is None for row in members), "resolvedLabels": sorted(resolved),
            "supportedRows": sum(row.projection_supported for row in members), "distinctFeatureViews": len(views),
            "annotationStatuses": dict(Counter(row.provenance["annotation_status"] for row in members)),
            "ruleViews": [json.loads(value) for value in sorted({canonical(row.comparison) for row in members
                                                               if row.projection_supported})]}
        records.append(record)
        if status == "eligible":
            # Every supported view is numerically identical; selecting its tuple
            # does not choose an actor, a direction or a preferred judgment.
            representative = min(supported_resolved, key=lambda row: row.edge_id)
            units.append(experiment.Edge(pair_id, representative.task_id, representative.task_kind,
                representative.focus_id, representative.candidate_id, representative.component,
                next(iter(resolved)), next(iter(views)), representative.same_recording,
                representative.same_artist, representative.comparison))
    return units, records


def compare(experiment, edges, components, units):
    supported = [row for row in edges if row.projection_supported]
    # Model serialization retains the existing portable five-feature semantics.
    # The new input projection is recorded separately and bound to its exporter.
    portable_version = experiment.CONTEXT_FEATURE_VERSION
    final = experiment.fit_models(units, supported, portable_version)
    models = {name: {"final": final[name] if final else None, "folds": {}}
              for name in ("logistic", "boosted_tree")}
    final_scores = [{"taskId": row.task_id, "candidateEndpointId": row.candidate_id,
        "projectionSupported": row.projection_supported,
        "scores": {name: experiment.portable_logit(final[name], row.values)
                   if final and row.projection_supported else None for name in models}}
        for row in edges]
    folds, predictions, primary = [], [], []
    for tasks in components:
        component = tasks[0]
        held = [row for row in edges if row.component == component]
        checked = [row for row in held if row.projection_supported]
        training = [unit for unit in units if unit.component != component]
        fitted = experiment.fit_models(training, checked, portable_version) if checked else None
        state = ("no_identity_edges" if not held else "unsupported_projection_only" if not checked else
                 "held_component_fit" if fitted else "unscorable_one_class_training")
        folds.append({"component": component, "allTaskIds": tasks, "heldDeclaredEdgeIds": [row.edge_id for row in held],
                      "trainingUnitIds": [unit.edge_id for unit in training],
                      "trainingClassCounts": dict(Counter(str(unit.target) for unit in training)), "state": state})
        if fitted:
            for name in models:
                models[name]["folds"][component] = fitted[name]
        for row in held:
            scores = {}
            for name in models:
                raw = experiment.portable_logit(fitted[name], row.values) if fitted and row.projection_supported else None
                scores[name] = {"rawLogit": raw, "uncalibratedSigmoid": experiment.sigmoid(raw) if raw is not None else None}
            predictions.append({"edgeId": row.edge_id, "taskId": row.task_id, "candidateMentionId": row.candidate_id,
                "candidateEndpointId": row.candidate_id, "focusEndpointId": row.focus_id, "pairUnitId": row.pair_id,
                "component": component, "taskKind": row.task_kind, "sameRecording": row.same_recording,
                "sameArtist": row.same_artist, "target": row.target,
                "targetStatus": "unknown_not_negative" if row.target is None else row.provenance["annotation_status"],
                "projectionSupported": row.projection_supported, "projectionIssue": row.projection_issue,
                "fitState": state, "scores": scores, "ruleComparison": row.comparison, "targetProvenance": row.provenance})
        for unit in (unit for unit in units if unit.component == component):
            primary.append({"unitId": unit.edge_id, "component": component, "target": unit.target, "fitState": state,
                "scores": {name: experiment.portable_logit(fitted[name], unit.values) if fitted else None for name in models}})
    return models, folds, sorted(predictions, key=lambda row: row["edgeId"]), primary, final_scores


def primary_metrics(primary, unit_records):
    result = {}
    for name in ("logistic", "boosted_tree"):
        scored = [unit for unit in primary if unit["scores"][name] is not None]
        loss = sum(max(raw := unit["scores"][name], 0) - unit["target"] * raw
                   + math.log1p(math.exp(-abs(raw))) for unit in scored)
        result[name] = {"eligibleIndependentUnits": len(primary), "scoredIndependentUnits": len(scored),
            "unscorableIndependentUnits": len(primary) - len(scored),
            "diagnosticHalfDisagreements": sum(int(unit["scores"][name] >= 0) != unit["target"] for unit in scored),
            "meanLogLoss": loss / len(scored) if scored else None}
    eligible = [unit for unit in unit_records if unit["status"] == "eligible"]
    result["rules"] = {"eligibleIndependentUnits": len(eligible),
        "positiveUnits": sum(unit["resolvedLabels"] == [1] for unit in eligible),
        "negativeUnits": sum(unit["resolvedLabels"] == [0] for unit in eligible),
        "unitsWithCandidateInEveryView": sum(all(view["isCandidate"] for view in unit["ruleViews"]) for unit in eligible),
        "positiveUnitsWithCandidateInEveryView": sum(unit["resolvedLabels"] == [1] and all(
            view["isCandidate"] for view in unit["ruleViews"]) for unit in eligible),
        "negativeUnitsWithCandidateInAnyView": sum(unit["resolvedLabels"] == [0] and any(
            view["isCandidate"] for view in unit["ruleViews"]) for unit in eligible),
        "unitsWithReviewDisposition": sum(any(view["disposition"] == "review" for view in unit["ruleViews"]) for unit in eligible),
        "automaticMatches": sum(any(view["disposition"] == "automatic_match" for view in unit["ruleViews"]) for unit in eligible),
        "meaning": "Actual C# candidate/rule diagnostics; retrieval candidacy is not an accepted identity prediction."}
    return result


def authenticate_targets(inputs, package_manifest, target_rows):
    """Verify targets against their pinned original rows, without creating features."""
    by_sha = defaultdict(list)
    for record in package_manifest["inputs"]:
        by_sha[record["sha256"]].append(record)
    parsed = {}

    def input_data(sha):
        require(sha in by_sha, "Target provenance is absent from prepared inputs")
        record = by_sha[sha][0]
        if sha not in parsed:
            parsed[sha] = inputs.read(record["path"], record)
        return parsed[sha]

    original_cache, adjudication_cache, receipt_cache = {}, {}, {}
    for target in target_rows:
        if target["annotation_status"] == "original_draft":
            sha = target["labels_sha256"]
            if sha not in original_cache:
                labels = unique([json.loads(line) for line in input_data(sha).split(b"\n") if line], "task_id")
                original_cache[sha] = {(task["task_id"], decision["candidate_mention_id"]): decision["decision"]
                    for task in labels.values() for decision in task["answer"].get("candidate_decisions", [])}
            require(original_cache[sha].get((target["task_id"], target["candidate_endpoint_id"])) == target["decision"],
                    "Original target differs from its pinned draft row")
        else:
            require(target["annotation_status"] == "agent_adjudicated_development", "Unknown annotation authority")
            sha = target["adjudication_sha256"]
            if sha not in adjudication_cache:
                rows = {}
                data = input_data(sha)
                physical_rows = data.split(b"\n")
                if physical_rows[-1] == b"":
                    physical_rows.pop()
                for index, physical in enumerate(physical_rows):
                    require(bool(physical.strip()), "Blank adjudication row")
                    followed_by_lf = index < len(physical_rows) - 1 or data.endswith(b"\n")
                    payload = physical[:-1] if followed_by_lf and physical.endswith(b"\r") else physical
                    row = json.loads(payload)
                    require(row["question_id"] not in rows, "Repeated adjudication question")
                    rows[row["question_id"]] = (row["decision"], digest(payload))
                adjudication_cache[sha] = rows
            require(adjudication_cache[sha].get(target["task_id"]) == (target["decision"], target["adjudication_row_sha256"]),
                    "Adjudicated target differs from its pinned original physical row")
            receipt_sha = target["validation_receipt_sha256"]
            if receipt_sha not in receipt_cache:
                receipt = json.loads(input_data(receipt_sha))
                require(receipt["state"] == "validated_adjudication_files" and receipt["version"] == "people-identity-review-validation:3"
                        and receipt["accepted_data_writes"] == 0 and receipt["policy_activation_permitted"] is False,
                        "Target does not name a completed non-activating review validation")
                receipt_cache[receipt_sha] = {item["sha256"] for item in receipt["inputs"]}
            require(sha in receipt_cache[receipt_sha], "Adjudication file was not covered by its validation receipt")


def load_inputs(experiment, package, package_sha, baseline, baseline_sha):
    inputs = Inputs(experiment)
    manifest = inputs.json(package / "manifest.json", expected_sha=package_sha)
    require(manifest["version"] == PACKAGE and manifest["state"] == "prepared_source_selectors_and_separate_targets"
            and manifest["policy_activation_permitted"] is False and manifest["eligible_for_release_evaluation"] is False
            and manifest["identity_accuracy_established"] is False and manifest["accepted_data_writes"] == 0,
            "Expected a prepared, non-activating development package")
    artifacts = unique(manifest["artifacts"], "path")
    require(set(artifacts) == {*SOURCE_FILES, TARGET_FILE}, "Prepared source/target profile changed")
    for record in manifest["inputs"]:
        inputs.pin(record["path"], record)
    sources = unique(inputs.rows(package / SOURCE_FILES[0], artifacts[SOURCE_FILES[0]]), "evidence_id")
    endpoints = unique(inputs.rows(package / SOURCE_FILES[1], artifacts[SOURCE_FILES[1]]), "endpoint_id")
    questions = unique(inputs.rows(package / SOURCE_FILES[2], artifacts[SOURCE_FILES[2]]), "task_id")
    package_groups = inputs.rows(package / SOURCE_FILES[3], artifacts[SOURCE_FILES[3]])
    exported = inputs.json(baseline / "manifest.json", expected_sha=baseline_sha)
    require(exported["state"] == "completed" and exported["version"] == BASELINE
            and exported["projectionVersion"] == PROJECTION and exported["featureOrder"] == list(FEATURES)
            and exported["actorFeaturesVersion"] == "people-actor-context-features-v1"
            and exported["packageManifestSha256"] == package_sha and exported["policyActivationPermitted"] is False,
            "C# baseline input/projection contract changed")
    require(set(exported["inputFiles"]) == {"manifest.json", *SOURCE_FILES}, "Baseline must consume exactly the source package")
    for name, record in exported["inputFiles"].items():
        inputs.pin(relative_file(package, name), record)
    for record in exported["artifacts"]:
        inputs.pin(record["path"], record)
    require(len(exported["artifacts"]) == 2, "Expected API and exporter assembly snapshots")
    output_names = {"Baseline": "baseline.jsonl", "Endpoint": "endpoints.jsonl", "Grouping": "groups.jsonl", "Bridge": "source-bridges.jsonl"}
    loaded = {}
    for key, expected_name in output_names.items():
        prefix = key[0].lower() + key[1:]
        require(exported[prefix + "File"] == expected_name, "Unsupported C# output path")
        path = relative_file(baseline, expected_name)
        inputs.pin(path, expected_sha=exported[prefix + "Sha256"])
        loaded[key] = inputs.rows(path, allow_empty=key == "Bridge")
    diagnostics = unique(loaded["Endpoint"], "endpointId")
    require(sorted(sorted(group["taskIds"]) for group in loaded["Grouping"])
            == sorted(sorted(group["task_ids"]) for group in package_groups), "Baseline original task grouping changed")
    require(exported["pairs"] == len(loaded["Baseline"]) and exported["allTasks"] == len(questions)
            and exported["endpoints"] == len(endpoints) and exported["sources"] == len(sources)
            and exported["groups"] == len(package_groups) and exported["bridges"] == len(loaded["Bridge"]),
            "C# baseline counts changed")
    require(exported["questions"] == sum(question["task_kind"] != "actor_kind" for question in questions.values())
            and exported["supportedPairs"] == sum(row["projectionSupported"] for row in loaded["Baseline"]),
            "C# identity/supported pair counts changed")
    # Source actor keys, pair membership and split groups are fixed before targets
    # are opened. Altering a judgment cannot move a pair to another fold.
    edges, components = source_edges(experiment, questions, endpoints, diagnostics, loaded["Baseline"],
                                    loaded["Grouping"], loaded["Bridge"])
    target_rows = inputs.rows(package / TARGET_FILE, artifacts[TARGET_FILE])
    authenticate_targets(inputs, manifest, target_rows)
    join_targets(edges, target_rows)
    inputs.recheck()
    return inputs, edges, components, manifest, exported


def run(experiment, inputs, edges, components, baseline_manifest, output, environment):
    units, unit_records = fit_units(experiment, edges)
    inputs.recheck()
    output.mkdir(parents=False, exist_ok=False)
    models, folds, predictions, primary, final_scores = compare(experiment, edges, components, units)
    files = {"models.json": models, "folds.json": folds, "fit-units.json": unit_records,
             "independent-predictions.json": primary, "question-pools.json": experiment.summarize_pools(predictions)}
    artifacts = []
    for name, value in files.items():
        experiment.write_json(output / name, value)
        artifacts.append({"path": name, **experiment.fingerprint(output / name)})
    experiment.write_rows(output / "declared-predictions.jsonl", predictions)
    artifacts.append({"path": "declared-predictions.jsonl", **experiment.fingerprint(output / "declared-predictions.jsonl")})
    experiment.write_rows(output / "final-declared-scores.jsonl", final_scores)
    artifacts.append({"path": "final-declared-scores.jsonl", **experiment.fingerprint(output / "final-declared-scores.jsonl")})
    retained = experiment.retain_replay_inputs(output, list(inputs.pins.values()))
    report = {"version": VERSION, "state": "completed_development_comparison", "inputProjectionVersion": PROJECTION,
        "portableModelProjectionVersion": experiment.CONTEXT_FEATURE_VERSION, "featureOrder": list(FEATURES),
        "counts": {"allTasks": sum(len(group) for group in components), "declaredEdges": len(edges),
            "supportedDeclaredEdges": sum(row.projection_supported for row in edges), "sourceComponents": len(components),
            "independentPairs": len(unit_records), "eligibleFitUnits": len(units),
            "eligibleFitUnitClassCounts": dict(Counter(str(unit.target) for unit in units)),
            "repeatedDeclaredPairRows": sum(max(0, unit["declaredRows"] - 1) for unit in unit_records),
            "unitStatuses": dict(Counter(row["status"] for row in unit_records)),
            "declaredTargetStatuses": dict(Counter("unknown" if row.target is None else str(row.target) for row in edges))},
        "primaryMetrics": primary_metrics(primary, unit_records), "declaredCohorts": experiment.cohort_counts(edges),
        "baselineProjection": {key: baseline_manifest[key] for key in ("version", "projectionVersion", "actorFeaturesVersion",
            "creditExtractorVersion", "actorEvidenceVersion", "actorProjectionVersion", "sourceContextVersion", "sourceReferenceVersion")},
        "artifacts": artifacts, "inputs": retained,
        "environment": environment, "identityAccuracyEstablished": False, "policyActivationPermitted": False,
        "acceptedDataWrites": 0,
        "limits": ["Agent-reviewed exposed development judgments; no release accuracy attestation.",
            "Conflicted and multiview pairs are excluded from primary fitting/loss; every declared row remains diagnostic.",
            "Unknown rows retain unknown targets even when another row resolves the same pair.",
            "Name-only pairs use exact source selectors, not certified canonical people.",
            "Diagnostic sigmoid halves are not publication thresholds; no policy or creation behavior is activated."]}
    inputs.recheck()
    for artifact in artifacts:
        experiment.pin(output / artifact["path"], artifact)
    experiment.write_json(output / "report.partial.json", report)
    inputs.recheck()
    for record in retained:
        if "retainedPath" in record:
            experiment.pin(relative_file(output, record["retainedPath"]), record)
    os.replace(output / "report.partial.json", output / "report.json")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("root", "package", "baseline", "experiment", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    for name in ("package-sha256", "baseline-sha256", "experiment-sha256", "install-sha256", "lock-sha256"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    experiment = import_experiment(args.experiment, args.experiment_sha256)
    inputs, edges, components, _, baseline_manifest = load_inputs(experiment,
        args.package.resolve(), args.package_sha256, args.baseline.resolve(), args.baseline_sha256)
    inputs.pin(args.experiment, expected_sha=args.experiment_sha256)
    inputs.pin(Path(__file__))
    environment, environment_pins = experiment.environment(args.root.resolve(), args.install_sha256, args.lock_sha256)
    for record in environment_pins:
        inputs.pin(record["path"], record)
    report = run(experiment, inputs, edges, components, baseline_manifest, args.output.resolve(), environment)
    print(json.dumps({"state": report["state"], "counts": report["counts"],
                      "reportSha256": experiment.fingerprint(args.output / "report.json")["sha256"]}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError) as error:
        raise SystemExit(f"Adjudicated comparison failed without completion: {error}")
