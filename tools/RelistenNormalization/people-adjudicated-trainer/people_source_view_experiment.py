"""Fit one development unit per overlapping bag of source-view relationships.

Every eligible leaf is an actual C# comparison, not an identity label. Source
pair overlaps establish units and fold grouping before bag targets are read.
Complete, numerically uniform units can fit once; all other observations remain
diagnostics without a chosen field, actor, rule winner or inherited leaf target.
Historical v1 views retain their original shape. V2 also retains role-free source
alias assertions separately from recording-credit facts, without combining either.
"""

import argparse
from collections import Counter, defaultdict
from dataclasses import dataclass, field
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import types


VERSION = "people-source-view-development-comparison-v1"
EXPORT_VERSION = "people-view-baseline-v1"
PROJECTION = "people-independent-source-views-v1"
EXPORT_PROFILES = {
    (EXPORT_VERSION, PROJECTION): {"key", "selectorIndexes", "projection", "facts"},
    ("people-view-baseline-v2", "people-independent-source-views-v2"):
        {"key", "selectorIndexes", "projection", "facts", "sourceAssertion"},
}
MAX_VIEWS = 100000
MAX_PAIRS = 1000000
MAX_BYTES = 134217728
MAX_LINE_BYTES = 1024 * 1024
FEATURES = ("name_token_similarity", "exact_name", "artist_overlap", "contact_agreement", "profile_agreement")
COVERAGE = {"complete", "partial", "ambiguous", "unattributed", "unsupported"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)


def hash_value(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def load_combined(path, expected_sha):
    path = Path(path).resolve()
    with path.open("rb") as stream:
        source = stream.read(1024 * 1024 + 1)
    require(len(source) <= 1024 * 1024 and hashlib.sha256(source).hexdigest() == expected_sha,
            "Combined adapter source digest changed")
    name = "pinned_combined_people_adapter_" + expected_sha[:16]
    module = types.ModuleType(name)
    module.__file__ = str(path)
    sys.modules[name] = module
    exec(compile(source, str(path), "exec"), module.__dict__)
    require(tuple(module.FEATURES) == FEATURES, "Shared runtime feature contract changed")
    return module


def count(value, maximum, message):
    require(type(value) is int and 0 <= value <= maximum, message)
    return value


def artifact(inputs, directory, record, name, maximum_rows, nullable_rows=False):
    require(set(record) == {"path", "bytes", "sha256", "rows"} and record["path"] == name,
            "Unexpected source-view artifact profile")
    count(record["bytes"], MAX_BYTES, "Source-view artifact exceeds byte bound")
    if nullable_rows:
        require(record["rows"] is None, "Manifest artifact must use a null row count")
    else:
        count(record["rows"], maximum_rows, "Source-view artifact row count exceeds bound")
    path = (directory / name).resolve()
    require(path.is_relative_to(directory.resolve()), "Source-view artifact escapes output directory")
    inputs.pin(path, record)
    return path


def edge_coordinates(edge_id):
    result = json.loads(edge_id)
    require(type(result) is list and len(result) == 2 and all(type(item) is str and item for item in result),
            "Edge ID must encode exactly task ID and candidate endpoint ID")
    return tuple(result)


def source_view_key(base, key, endpoint, indexes, variants):
    require(set(key) == {"kind", "evidenceId", "field", "referenceUuid", "anchor", "fieldSha256"}
            and key["evidenceId"] == endpoint["evidence_id"], "Source view key lost original ownership")
    require(indexes and len(indexes) == len(set(indexes)) and all(type(index) is int
            and 0 <= index < len(endpoint["selectors"]) for index in indexes), "Invalid source view selector membership")
    selectors = [endpoint["selectors"][index] for index in indexes]
    require(all(base.selector_evidence(selector)["field"] == key["field"] for selector in selectors),
            "Source view key crosses fields")
    if key["kind"] == "source_actor":
        require(key["anchor"] is None and key["fieldSha256"] is None, "Actor view cannot invent selector fallback coordinates")
        ref = base.scoped_reference({name: key[name] for name in ("evidenceId", "field", "referenceUuid")}, endpoint["evidence_id"])
        for index in indexes:
            require(any(variant["selectorIndex"] == index and variant["actorReference"] is not None
                        and base.scoped_reference(variant["actorReference"], endpoint["evidence_id"]) == ref
                        for variant in variants), "Actor view lacks its original scoped variant")
    else:
        require(key["kind"] == "source_selector" and key["referenceUuid"] is None,
                "Unknown source view identity kind")
        require(all(selector["kind"] == "actor_name" and base.selector_evidence(selector) == key["anchor"]
                    and selector["field_sha256"] == key["fieldSha256"] for selector in selectors),
                "Name-only view must retain exact original selector coordinates")
    return canonical(key)


def read_endpoint_views(base, prepared, diagnostics, rows, *, export_version=EXPORT_VERSION,
                        projection_version=PROJECTION):
    profile = (export_version, projection_version)
    require(profile in EXPORT_PROFILES, "Unsupported source-view export/projection pair")
    view_fields = EXPORT_PROFILES[profile]
    retains_assertions = "sourceAssertion" in view_fields
    endpoints = base.unique(rows, "endpointId")
    require(endpoints.keys() == prepared.endpoints.keys(), "Source view endpoint coverage changed")
    all_views, view_count = {}, 0
    for endpoint_id, row in endpoints.items():
        original, old = prepared.endpoints[endpoint_id], diagnostics[endpoint_id]
        require(row["evidenceId"] == original["evidence_id"] and row["artistUuid"] == original["artist_uuid"]
                and row["mentionKind"] == original["mention_kind"] and row["selectors"] == old["selectors"]
                and row["variants"] == old["variants"] and row["coverage"] in COVERAGE,
                "Source view endpoint or original diagnostics changed")
        views, owned_indexes = {}, set()
        records = set()
        for view in row["views"]:
            require(set(view) == view_fields
                    and view["projection"]["complete"] is True, "Eligible view requires a complete source projection")
            key = source_view_key(base, view["key"], original, view["selectorIndexes"], row["variants"])
            require(key not in views and not owned_indexes.intersection(view["selectorIndexes"]),
                    "A selector has multiple eligible source views")
            owned_indexes.update(view["selectorIndexes"])
            original_variants = []
            for index in view["selectorIndexes"]:
                expected = "matched" if view["key"]["kind"] == "source_actor" else "name_only"
                selected = [variant for variant in row["variants"] if variant["selectorIndex"] == index]
                require(row["selectors"][index]["status"] == expected
                        and "ambiguous_source_actor" not in row["selectors"][index]["issues"]
                        and len(selected) == 1 and selected[0]["projection"]["complete"] is True,
                        "Uncertain selector cannot supply an eligible view")
                original_variants.extend(selected)
            require(all(("sourceAssertion" in variant) == retains_assertions for variant in original_variants),
                    "Source variant assertion profile differs from its export")
            require(any(view["projection"] == variant["projection"] and view["facts"] == variant["facts"]
                        and (not retains_assertions or view["sourceAssertion"] == variant["sourceAssertion"])
                        for variant in original_variants), "Source view must preserve one original projection without union")
            if retains_assertions and view["sourceAssertion"] is not None:
                assertion = view["sourceAssertion"]
                # Role-free alias evidence cannot borrow recording facts or leave its source-local actor.
                require(type(assertion) is dict and view["facts"] is None
                        and view["key"]["kind"] == "source_actor"
                        and assertion.get("actorReferenceUuid") == view["key"]["referenceUuid"],
                        "Source assertion must retain its scoped actor without recording facts")
            views[key] = view
            records.add(canonical(view["projection"]["record"]))
        require(row["matchRecordsAgree"] == (len(records) == 1 if views else None),
                "Source view record-agreement diagnostic changed")
        complete = bool(views) and owned_indexes == set(range(len(original["selectors"])))
        require((row["coverage"] == "complete") == complete, "Complete endpoint must cover every selector")
        all_views[endpoint_id] = views
        view_count += len(views)
        require(view_count <= MAX_VIEWS, "Source view count exceeds bound")
    return endpoints, all_views, view_count


@dataclass
class Bag:
    row: dict
    leaves: list = field(default_factory=list)
    component: str = ""
    unit_id: str = ""
    target: int | None = None
    provenance: dict = field(default_factory=dict)


@dataclass
class Leaf:
    observation_id: str
    edge_id: str
    left_key: dict
    right_key: dict
    full_pair_id: str
    values: tuple
    comparison: dict


def read_bags(base, prepared, endpoint_rows, views, rows):
    bags, seen, total_pairs = {}, set(), 0
    expected = {(task_id, candidate) for task_id, question in prepared.questions.items()
                for candidate in question["candidate_endpoint_ids"]}
    for row in rows:
        coordinates = edge_coordinates(row["edgeId"])
        require(coordinates in expected and coordinates not in seen and row["edgeId"] not in bags,
                "Unknown or duplicate declared relationship bag")
        seen.add(coordinates)
        task, candidate = coordinates
        question = prepared.questions[task]
        left, right = question["focus_endpoint_id"], candidate
        require(row["taskId"] == task and row["taskKind"] == question["task_kind"]
                and row["focusEndpointId"] == left and row["candidateEndpointId"] == right
                and row["leftCoverage"] == endpoint_rows[left]["coverage"]
                and row["rightCoverage"] == endpoint_rows[right]["coverage"], "Bag source ownership or coverage changed")
        left_count = count(row["leftViewCount"], MAX_VIEWS, "Invalid bag view count")
        right_count = count(row["rightViewCount"], MAX_VIEWS, "Invalid bag view count")
        require(left_count == len(views[left]) and right_count == len(views[right]), "Bag endpoint view counts changed")
        product = left_count * right_count
        require(product <= MAX_PAIRS - total_pairs, "Expected view-pair expansion exceeds bound")
        require(type(row["expectedLeafPairs"]) is int and row["expectedLeafPairs"] == product,
                "Bag has a truncated or invalid Cartesian view-pair count")
        complete = bool(product) and row["leftCoverage"] == row["rightCoverage"] == "complete"
        require(type(row["complete"]) is bool and row["complete"] == complete, "Bag completeness changed")
        total_pairs += product
        bags[row["edgeId"]] = Bag(row)
    require(seen == expected, "Declared bags omit original candidate-pool members")
    return bags, total_pairs


def read_leaves(experiment, path, expected_rows, bags, views):
    """Read the bounded stream completely; never treat a truncated bag as complete."""
    leaves, seen = [], set()
    with path.open("rb") as stream:
        while physical := stream.readline(MAX_LINE_BYTES + 1):
            require(len(physical) <= MAX_LINE_BYTES and physical.strip(), "Leaf line exceeds bound or is blank")
            require(len(leaves) < MAX_PAIRS and stream.tell() <= MAX_BYTES, "Leaf stream exceeds bound")
            row = json.loads(physical)
            require(set(row) == {"edgeId", "taskId", "focusEndpointId", "candidateEndpointId", "leftViewKey", "rightViewKey", "comparison", "features"},
                    "Leaf comparison contains unsupported fields or identity targets")
            require(row["edgeId"] in bags, "Leaf belongs to an unknown bag")
            bag = bags[row["edgeId"]]
            require(all(row[key] == bag.row[key] for key in ("taskId", "focusEndpointId", "candidateEndpointId")),
                    "Leaf source coordinates differ from its declared bag")
            left, right = canonical(row["leftViewKey"]), canonical(row["rightViewKey"])
            require(left in views[bag.row["focusEndpointId"]] and right in views[bag.row["candidateEndpointId"]],
                    "Leaf references a foreign or ineligible source view")
            observation = canonical([row["edgeId"], row["leftViewKey"], row["rightViewKey"]])
            require(observation not in seen, "Duplicate leaf observation")
            seen.add(observation)
            values = experiment.actual_features(row["comparison"])
            require(row["features"] == list(values), "Leaf features differ from actual C# comparison")
            pair_id = "full-pair:" + hash_value(sorted((row["leftViewKey"], row["rightViewKey"]), key=canonical))
            leaf = Leaf(observation, row["edgeId"], row["leftViewKey"], row["rightViewKey"], pair_id, values, row["comparison"])
            leaves.append(leaf)
            bag.leaves.append(leaf)
    require(len(leaves) == expected_rows and all(len(bag.leaves) == bag.row["expectedLeafPairs"] for bag in bags.values()),
            "Leaf stream did not cover every complete Cartesian bag")
    return leaves


def relationship_units(bags):
    """Merge relationship bags transitively only when the full actor pair overlaps."""
    parents = {edge_id: edge_id for edge_id in bags}

    def find(key):
        while parents[key] != key:
            parents[key] = parents[parents[key]]
            key = parents[key]
        return key

    first = {}
    for edge_id, bag in bags.items():
        for leaf in bag.leaves:
            if leaf.full_pair_id in first:
                parents[find(edge_id)] = find(first[leaf.full_pair_id])
            else:
                first[leaf.full_pair_id] = edge_id
    groups = defaultdict(list)
    for edge_id in bags:
        groups[find(edge_id)].append(edge_id)
    units = {}
    for members in groups.values():
        pair_ids = sorted({leaf.full_pair_id for edge_id in members for leaf in bags[edge_id].leaves})
        unit_id = "relationship:" + hash_value(pair_ids if pair_ids else ["empty-bag", *sorted(members)])
        require(unit_id not in units, "Duplicate relationship unit key")
        units[unit_id] = sorted(members)
        for edge_id in members:
            bags[edge_id].unit_id = unit_id
    return units


def group_source_tasks(base, prepared, comparator, diagnostics, bags, units):
    # Verify the retained v1 comparator without using its single-view endpoint
    # choice to define the new relationship bags or their fit units.
    _, references = base.endpoint_keys(prepared.endpoints, diagnostics)
    memberships = {unit_id: {bags[edge_id].row["taskId"] for edge_id in members}
                   for unit_id, members in units.items()}
    components, by_task = base.components_for(prepared.questions, comparator["Grouping"], comparator["Bridge"], references, memberships)
    for bag in bags.values():
        bag.component = by_task[bag.row["taskId"]]
    return components


def join_bag_targets(base, bags, targets):
    answers = {(row["task_id"], row["candidate_endpoint_id"]): row for row in targets}
    require(len(answers) == len(targets) and set(answers) == {edge_coordinates(edge_id) for edge_id in bags},
            "Bag targets do not cover original candidate pools exactly once")
    for edge_id, bag in bags.items():
        target = answers[edge_coordinates(edge_id)]
        require(target["decision"] in base.TARGETS and target["label"] == base.TARGETS[target["decision"]]
                and (target["label"] is None or type(target["label"]) is int), "Unknown bag targets must not become negatives")
        bag.target = target["label"]
        bag.provenance = {key: value for key, value in target.items() if key not in {"label", "decision"}}


def rule_outcome(comparison):
    return {"isCandidate": comparison["isCandidate"], "disposition": comparison["disposition"],
            "supportedRules": sorted(comparison["supportedRules"]), "proposedRule": comparison["proposedRule"],
            "features": sorted(({"name": feature["name"], "agreement": feature["agreement"]}
                                for feature in comparison["features"]), key=canonical)}


def fit_relationships(base, experiment, prepared, bags, units):
    fitted, records = [], []
    for unit_id, members in sorted(units.items()):
        unit_bags = [bags[edge_id] for edge_id in members]
        leaves = [leaf for bag in unit_bags for leaf in bag.leaves]
        components = {bag.component for bag in unit_bags}
        require(len(components) == 1, "Relationship overlap escaped source grouping")
        labels = {bag.target for bag in unit_bags if bag.target is not None}
        vectors = {leaf.values for leaf in leaves}
        if len(labels) > 1:
            status = "conflicting_resolved_targets"
        elif any(bag.row[side] == "ambiguous" for bag in unit_bags for side in ("leftCoverage", "rightCoverage")):
            status = "ambiguous_source_selector"
        elif not leaves:
            status = "unsupported_bags_only"
        elif not all(bag.row["complete"] for bag in unit_bags):
            status = "incomplete_bag_coverage"
        elif len(vectors) != 1:
            status = "multiple_numeric_views"
        elif not labels:
            status = "unknown_only"
        else:
            status = "eligible"
        outcomes = {canonical(rule_outcome(leaf.comparison)) for leaf in leaves}
        record = {"unitId": unit_id, "status": status, "component": next(iter(components)),
            "declaredEdgeIds": members, "declaredBags": len(members), "leafObservations": len(leaves),
            "leafObservationIds": [leaf.observation_id for leaf in leaves],
            "distinctFullPairs": len({leaf.full_pair_id for leaf in leaves}), "distinctNumericViews": len(vectors),
            "resolvedLabels": sorted(labels), "unknownBags": sum(bag.target is None for bag in unit_bags),
            "annotationStatuses": dict(Counter(bag.provenance["annotation_status"] for bag in unit_bags)),
            "trainingWeight": 1 if status == "eligible" else 0,
            "ruleAggregation": "consistent" if len(outcomes) == 1 else "disagreement_withheld" if outcomes else "no_views",
            "aggregateRuleOutcome": json.loads(next(iter(outcomes))) if len(outcomes) == 1 else None}
        records.append(record)
        if status == "eligible":
            bag = unit_bags[0]
            endpoints = prepared.endpoints
            left, right = endpoints[bag.row["focusEndpointId"]], endpoints[bag.row["candidateEndpointId"]]
            fitted.append(experiment.Edge(unit_id, bag.row["taskId"], bag.row["taskKind"], bag.row["focusEndpointId"],
                bag.row["candidateEndpointId"], bag.component, next(iter(labels)), next(iter(vectors)),
                left["evidence_id"] == right["evidence_id"], left["artist_uuid"] == right["artist_uuid"], {}))
    return fitted, records


def load_inputs(base, experiment, package, package_sha, directory, manifest_sha):
    inputs, prepared = base.read_prepared_sources(experiment, package, package_sha)
    manifest = inputs.json(directory / "manifest.json", expected_sha=manifest_sha)
    require(manifest["state"] == "completed"
            and (manifest.get("version"), manifest.get("projectionVersion")) in EXPORT_PROFILES
            and manifest["featureOrder"] == list(FEATURES)
            and manifest["packageManifestSha256"] == package_sha and manifest["policyActivationPermitted"] is False,
            "Expected the completed TOP source-view export; a nested comparator is insufficient")
    require(manifest["limits"] == {"maximumViews": MAX_VIEWS, "maximumLeafPairs": MAX_PAIRS,
                                    "maximumOutputBytesPerArtifact": MAX_BYTES}, "Source-view bound contract changed")
    count_limits = {"tasks": base.MAX_ROWS, "endpoints": base.MAX_ROWS, "edges": base.MAX_ROWS,
                    "views": MAX_VIEWS, "leafPairs": MAX_PAIRS, "completeBags": base.MAX_ROWS,
                    "partialBags": base.MAX_ROWS, "unsupportedBags": base.MAX_ROWS}
    require(set(manifest["counts"]) == set(count_limits), "Source-view count profile changed")
    for name, maximum in count_limits.items():
        count(manifest["counts"][name], maximum, "Source-view manifest count exceeds bound")
    require(set(manifest["inputFiles"]) == {"manifest.json", *base.SOURCE_FILES}, "View export must consume only the source package")
    for name, record in manifest["inputFiles"].items():
        inputs.pin(base.relative_file(package, name), record)
    nested = artifact(inputs, directory, manifest["comparatorManifest"], "comparator/manifest.json", base.MAX_ROWS, nullable_rows=True)
    exported, comparator, diagnostics = base.read_comparator(inputs, prepared, nested.parent, manifest["comparatorManifest"]["sha256"])
    base.source_edges(experiment, prepared.questions, prepared.endpoints, diagnostics, comparator["Baseline"], comparator["Grouping"], comparator["Bridge"])
    require(manifest["runtimeArtifacts"] == exported["artifacts"], "View and comparator runtime assemblies differ")
    expected_versions = {"comparatorVersion": exported["version"], "comparatorProjectionVersion": exported["projectionVersion"],
        **{name: exported[name] for name in ("actorFeaturesVersion", "creditExtractorVersion", "actorEvidenceVersion",
                                           "actorProjectionVersion", "sourceContextVersion", "sourceReferenceVersion")}}
    require(manifest["versions"] == expected_versions, "View and comparator source versions differ")
    for name, filename, rows_key in (("baseline", "baseline", "Baseline"), ("grouping", "groups", "Grouping"), ("bridges", "source-bridges", "Bridge")):
        record = manifest[name]
        artifact(inputs, directory, record, "comparator/" + filename + ".jsonl", base.MAX_ROWS)
        require(record["rows"] == len(comparator[rows_key]), "Nested comparator row metadata changed")
    endpoint_path = artifact(inputs, directory, manifest["endpointViews"], "endpoint-views.jsonl", base.MAX_ROWS)
    bag_path = artifact(inputs, directory, manifest["bags"], "view-bags.jsonl", base.MAX_ROWS)
    leaf_path = artifact(inputs, directory, manifest["leafPairs"], "view-pairs.jsonl", MAX_PAIRS)
    endpoints, views, view_count = read_endpoint_views(base, prepared, diagnostics,
        inputs.rows(endpoint_path, manifest["endpointViews"]), export_version=manifest["version"],
        projection_version=manifest["projectionVersion"])
    bags, expected_pairs = read_bags(base, prepared, endpoints, views, inputs.rows(bag_path, manifest["bags"]))
    require(manifest["leafPairs"]["rows"] == expected_pairs, "Leaf artifact count truncates a declared bag")
    leaves = read_leaves(experiment, leaf_path, expected_pairs, bags, views)
    expected_counts = {"tasks": len(prepared.questions), "endpoints": len(endpoints), "edges": len(bags),
        "views": view_count, "leafPairs": len(leaves), "completeBags": sum(bag.row["complete"] for bag in bags.values()),
        "partialBags": sum(not bag.row["complete"] and bool(bag.leaves) for bag in bags.values()),
        "unsupportedBags": sum(not bag.leaves for bag in bags.values())}
    require(manifest["counts"] == expected_counts, "Source-view manifest counts changed")
    units = relationship_units(bags)
    components = group_source_tasks(base, prepared, comparator, diagnostics, bags, units)
    # Labels cannot change leaf membership, full-pair overlaps, units or folds.
    targets = base.read_targets(inputs, prepared)
    join_bag_targets(base, bags, targets)
    inputs.recheck()
    return inputs, prepared, manifest, exported, bags, leaves, units, components


def score_observations(base, experiment, prepared, bags, leaves, fitted_units, components):
    observations = []
    for leaf in leaves:
        bag = bags[leaf.edge_id]
        left = prepared.endpoints[bag.row["focusEndpointId"]]
        right = prepared.endpoints[bag.row["candidateEndpointId"]]
        observations.append(base.DeclaredEdge(leaf.observation_id, bag.row["taskId"], bag.row["candidateEndpointId"],
            bag.row["focusEndpointId"], bag.row["taskKind"], bag.component, bag.unit_id, None, leaf.values,
            left["evidence_id"] == right["evidence_id"], left["artist_uuid"] == right["artist_uuid"],
            leaf.comparison, True, None, {"annotation_status": "unlabeled_source_view"}))
    models, folds, predicted, primary, final = base.compare(experiment, observations, components, fitted_units)
    for fold in folds:
        fold["heldLeafObservationIds"] = fold.pop("heldDeclaredEdgeIds")
        fold["heldDeclaredEdgeIds"] = sorted(edge_id for edge_id, bag in bags.items() if bag.component == fold["component"])
        if fold["heldDeclaredEdgeIds"] and not fold["heldLeafObservationIds"]:
            fold["state"] = "unsupported_projection_only"
    by_observation = {leaf.observation_id: leaf for leaf in leaves}
    require(len(predicted) == len(leaves), "Scoring omitted source leaf observations")
    for row in predicted:
        leaf = by_observation[row["edgeId"]]
        row.update(observationId=leaf.observation_id, edgeId=leaf.edge_id,
                   leftViewKey=leaf.left_key, rightViewKey=leaf.right_key,
                   target=None, targetStatus="no_leaf_identity_target")
        row["candidateEndpointId"] = row.pop("candidateMentionId")
        row["relationshipUnitId"] = row.pop("pairUnitId")
        row.pop("targetProvenance")
    require(len(final) == len(leaves), "Final scoring omitted source leaf observations")
    for row, leaf in zip(final, leaves):
        row.update(observationId=leaf.observation_id, edgeId=leaf.edge_id,
                   leftViewKey=leaf.left_key, rightViewKey=leaf.right_key)
    return models, folds, predicted, primary, final


def declared_predictions(bags, leaf_predictions, folds):
    scored = {row["observationId"]: row for row in leaf_predictions}
    fit_states = {fold["component"]: fold["state"] for fold in folds}
    result = []
    for edge_id, bag in sorted(bags.items()):
        vectors = {leaf.values for leaf in bag.leaves}
        uniform = bag.row["complete"] and len(vectors) == 1
        outcomes = {canonical(rule_outcome(leaf.comparison)) for leaf in bag.leaves}
        models = {}
        for name in ("logistic", "boosted_tree"):
            scores = {scored[leaf.observation_id]["scores"][name]["rawLogit"] for leaf in bag.leaves}
            require(not uniform or len(scores) == 1, "Identical complete bag vectors produced inconsistent scores")
            raw = next(iter(scores)) if uniform else None
            models[name] = {"rawLogit": raw, "aggregation": "identical_complete_views" if uniform
                            else "withheld_incomplete_or_differing_views"}
        result.append({"edgeId": edge_id, **{key: bag.row[key] for key in ("taskId", "taskKind", "focusEndpointId", "candidateEndpointId")},
            "unitId": bag.unit_id, "component": bag.component, "fitState": fit_states[bag.component],
            "target": bag.target, "targetStatus": "unknown_not_negative" if bag.target is None else bag.provenance["annotation_status"],
            "targetProvenance": bag.provenance, "complete": bag.row["complete"],
            "leftCoverage": bag.row["leftCoverage"], "rightCoverage": bag.row["rightCoverage"],
            "leafObservationIds": [leaf.observation_id for leaf in bag.leaves], "scores": models,
            "ruleAggregation": "consistent" if len(outcomes) == 1 else "disagreement_withheld" if outcomes else "no_views",
            "aggregateRuleOutcome": json.loads(next(iter(outcomes))) if len(outcomes) == 1 else None})
    return result


def pool_diagnostics(declared):
    by_task = defaultdict(list)
    for row in declared:
        by_task[row["taskId"]].append(row)
    result = []
    for task_id, rows in sorted(by_task.items()):
        models = {}
        for name in ("logistic", "boosted_tree"):
            scored = [row for row in rows if row["scores"][name]["rawLogit"] is not None]
            maximum = max((row["scores"][name]["rawLogit"] for row in scored), default=None)
            positive = max((row["scores"][name]["rawLogit"] for row in scored if row["target"] == 1), default=None)
            models[name] = {"topCandidateEndpointIds": [row["candidateEndpointId"] for row in scored
                               if row["scores"][name]["rawLogit"] == maximum],
                "unscorableCandidates": len(rows) - len(scored),
                "unknownRivalsAtOrAboveBestKnownPositive": None if positive is None else sum(row["target"] is None
                    and row["scores"][name]["rawLogit"] >= positive for row in scored)}
        result.append({"taskId": task_id, "declaredCandidates": len(rows),
            "targets": dict(Counter("unknown" if row["target"] is None else str(row["target"]) for row in rows)), "models": models})
    return result


def metrics(base, primary, records):
    result = base.primary_metrics(primary, [])
    eligible = [record for record in records if record["status"] == "eligible"]
    consistent = [record for record in eligible if record["ruleAggregation"] == "consistent"]
    result["rules"] = {"eligibleNumericalUnits": len(eligible), "consistentRuleUnits": len(consistent),
        "disagreeingRuleUnitsWithheld": len(eligible) - len(consistent),
        "positiveConsistentUnits": sum(record["resolvedLabels"] == [1] for record in consistent),
        "negativeConsistentUnits": sum(record["resolvedLabels"] == [0] for record in consistent),
        "candidateUnits": sum(record["aggregateRuleOutcome"]["isCandidate"] for record in consistent),
        "reviewUnits": sum(record["aggregateRuleOutcome"]["disposition"] == "review" for record in consistent),
        "meaning": "Actual consistent rule/candidate views only; disagreement has no aggregate winner."}
    return result


def write_json(experiment, output, name, value):
    data = (canonical(value) + "\n").encode()
    require(len(data) <= MAX_BYTES, "Output JSON artifact exceeds byte bound")
    with (output / name).open("xb") as stream:
        stream.write(data)
    return {"path": name, **experiment.fingerprint(output / name)}


def write_rows(experiment, output, name, rows):
    with (output / name).open("xb") as stream:
        for row in rows:
            stream.write((canonical(row) + "\n").encode())
            require(stream.tell() <= MAX_BYTES, "Output JSONL artifact exceeds byte bound")
    return {"path": name, **experiment.fingerprint(output / name)}


def run(base, experiment, inputs, prepared, manifest, comparator_manifest, bags, leaves, units, components, output, environment):
    fitted, records = fit_relationships(base, experiment, prepared, bags, units)
    inputs.recheck()
    output.mkdir(parents=False, exist_ok=False)
    models, folds, leaf_predictions, primary, final = score_observations(base, experiment, prepared, bags, leaves, fitted, components)
    declared = declared_predictions(bags, leaf_predictions, folds)
    artifacts = [write_json(experiment, output, "models.json", models), write_json(experiment, output, "folds.json", folds),
        write_json(experiment, output, "relationship-units.json", records), write_json(experiment, output, "independent-predictions.json", primary),
        write_json(experiment, output, "question-pools.json", pool_diagnostics(declared)),
        write_rows(experiment, output, "declared-predictions.jsonl", declared),
        write_rows(experiment, output, "leaf-predictions.jsonl", leaf_predictions),
        write_rows(experiment, output, "final-leaf-scores.jsonl", final)]
    retained = experiment.retain_replay_inputs(output, list(inputs.pins.values()))
    report = {"state": "completed_development_comparison", "version": VERSION,
        "inputProjectionVersion": manifest["projectionVersion"],
        "portableModelProjectionVersion": experiment.CONTEXT_FEATURE_VERSION, "featureOrder": list(FEATURES),
        "counts": {"allTasks": len(prepared.questions), "declaredEdges": len(bags), "leafObservations": len(leaves),
            "distinctFullActorPairs": len({leaf.full_pair_id for leaf in leaves}), "sourceComponents": len(components),
            "relationshipUnits": len(units), "eligibleFitUnits": len(fitted),
            "fitUnitClassCounts": dict(Counter(str(unit.target) for unit in fitted)),
            "unitStatuses": dict(Counter(record["status"] for record in records)),
            "completeBags": sum(bag.row["complete"] for bag in bags.values()),
            "numericEligibleButRuleDisagreeingUnits": sum(record["status"] == "eligible"
                and record["ruleAggregation"] == "disagreement_withheld" for record in records)},
        "primaryMetrics": metrics(base, primary, records), "sourceVersions": manifest["versions"],
        "retainedComparatorVersion": comparator_manifest["version"], "artifacts": artifacts, "inputs": retained,
        "environment": environment, "identityAccuracyEstablished": False, "policyActivationPermitted": False, "acceptedDataWrites": 0,
        "limits": ["Every leaf is an unlabeled source comparison; bag judgments are never copied onto leaf targets.",
            "Full actor-pair overlap joins relationship bags, not actors or canonical identities.",
            "Only complete uniform numerical units fit once; partial, ambiguous, conflicting and differing views stay diagnostic.",
            "Rule disagreement has no aggregate winner, including when identical numerical views permit fitting.",
            "Exposed agent development labels and diagnostic sigmoid halves do not establish release accuracy or activate a policy."]}
    write_json(experiment, output, "report.partial.json", report)
    inputs.recheck()
    for record in artifacts:
        experiment.pin(output / record["path"], record)
    for record in retained:
        if "retainedPath" in record:
            experiment.pin(base.relative_file(output, record["retainedPath"]), record)
    os.replace(output / "report.partial.json", output / "report.json")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("root", "package", "export", "combined-adapter", "experiment", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    for name in ("package-sha256", "export-sha256", "combined-adapter-sha256", "experiment-sha256", "install-sha256", "lock-sha256"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    base = load_combined(args.combined_adapter, args.combined_adapter_sha256)
    experiment = base.import_experiment(args.experiment, args.experiment_sha256)
    inputs, prepared, manifest, comparator, bags, leaves, units, components = load_inputs(base, experiment,
        args.package.resolve(), args.package_sha256, args.export.resolve(), args.export_sha256)
    inputs.pin(args.combined_adapter, expected_sha=args.combined_adapter_sha256)
    inputs.pin(args.experiment, expected_sha=args.experiment_sha256)
    inputs.pin(Path(__file__))
    environment, pins = experiment.environment(args.root.resolve(), args.install_sha256, args.lock_sha256)
    for record in pins:
        inputs.pin(record["path"], record)
    report = run(base, experiment, inputs, prepared, manifest, comparator, bags, leaves, units, components,
                 args.output.resolve(), environment)
    print(json.dumps({"state": report["state"], "counts": report["counts"],
                      "reportSha256": experiment.fingerprint(args.output / "report.json")["sha256"]}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError) as error:
        raise SystemExit(f"Source-view comparison failed without completion: {error}")
