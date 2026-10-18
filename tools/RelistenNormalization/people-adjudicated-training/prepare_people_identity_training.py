"""Prepare exposed development selectors and separate identity targets.

This adapter authenticates existing source/review contracts. It does not extract
actor facts, invent candidate names, train a matcher, or authorize identities.
The C# projector must resolve reference selectors before these new pairs can be
used as executable matcher inputs.
"""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import sys
import types


VERSION = "people-adjudicated-training-inputs:1"
ORIGINAL_MANIFEST_SHA = "866ba35da197984f6b5e2df5285f84cd62102f67392e514171115c4f5898bd5c"
ORIGINAL_VALIDATOR_SHA = "ef49b3864315f9b7288142c6a9519abe7a528412cd52f2918e57ef4b8b22953a"
REVIEW_VALIDATOR_SHA = "5a517b1db67487bb8e29bd4e8f5420e5092174efcc96a003459ec7394b359dca"
PACKET_TOOL_SHA = "20ac70e6b999b03049141852127334a31426ebc048071d650663aaa7d0e3a5c2"
MAX_BYTES = 32 * 1024 * 1024
MAX_ROWS = 10000
DECISIONS = {"same_entity": 1, "different_entities": 0, "insufficient_evidence": None}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def encoded(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()


class Inputs:
    """Pin each decoded input and recheck the same bytes before completion."""

    def __init__(self):
        self.records = {}

    def read(self, path, expected_sha=None, expected_bytes=None):
        path = Path(path).resolve()
        with path.open("rb") as stream:
            data = stream.read(MAX_BYTES + 1)
        require(len(data) <= MAX_BYTES, f"Input exceeds bound: {path.name}")
        record = {"path": str(path), "bytes": len(data), "sha256": digest(data)}
        require(expected_sha is None or record["sha256"] == expected_sha,
                f"Input digest changed: {path.name}")
        require(expected_bytes is None or len(data) == expected_bytes,
                f"Input length changed: {path.name}")
        require(str(path) not in self.records or self.records[str(path)] == record,
                f"Input changed during preparation: {path.name}")
        self.records[str(path)] = record
        return data

    def json(self, path, expected_sha=None):
        return json.loads(self.read(path, expected_sha))

    def rows(self, path):
        rows = self.read(path).split(b"\n")
        if rows[-1] == b"":
            rows.pop()
        require(0 < len(rows) <= MAX_ROWS and all(row.strip() for row in rows),
                "JSONL input must contain bounded nonempty physical rows")
        return [json.loads(row) for row in rows]

    def pin_bytes(self, path, expected_sha):
        """Hash large provenance-only files without decoding their payloads."""
        path = Path(path).resolve()
        hasher, size = hashlib.sha256(), 0
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                hasher.update(block)
                size += len(block)
        record = {"path": str(path), "bytes": size, "sha256": hasher.hexdigest()}
        require(record["sha256"] == expected_sha, f"Provenance input changed: {path.name}")
        require(str(path) not in self.records or self.records[str(path)] == record,
                f"Input changed during preparation: {path.name}")
        self.records[str(path)] = record

    def recheck(self):
        for record in list(self.records.values()):
            self.pin_bytes(record["path"], record["sha256"])


def import_captured(inputs, path, expected_sha, name):
    data = inputs.read(path, expected_sha)
    module = types.ModuleType(name)
    module.__file__ = str(Path(path).resolve())
    sys.modules[name] = module
    # Do not let a timestamp-valid .pyc replace the authenticated source bytes.
    exec(compile(data, str(path), "exec"), module.__dict__)
    inputs.recheck()
    return module


def source_metadata(original):
    return {"evidence_id": original["evidence_id"], "artist_uuid": original["artist"]["uuid"],
            "family_group": original["input_duplicate_family"],
            "known_recording_family": original["known_recording_family"]}


class Preparation:
    def __init__(self, original_validator):
        self.validator = original_validator
        self.sources = {}
        self.endpoints = {}
        self.questions = {}
        self.targets = []

    def add_source(self, original):
        key = original["evidence_id"]
        require(original["artist_partition"] == original["family_partition"] == "development",
                "Source must remain development-only")
        require(key not in self.sources or self.sources[key] == original,
                "Shared evidence ID has conflicting source content")
        self.sources[key] = original

    def add_endpoint(self, evidence_id, selectors, endpoint_id=None, mention_kind=None):
        original = self.sources[evidence_id]
        selectors = sorted(selectors, key=lambda item: encoded(item))
        require(bool(selectors), "Endpoint needs an exact source selector")
        for selector in selectors:
            citation = {"evidence_id": evidence_id, "field_path": selector["field_path"],
                        "field_sha256": selector["field_sha256"], "span": selector["span"]}
            # Use the existing raw checker; it also validates original citation IDs.
            field = citation["field_path"].rsplit("/", 1)[-1]
            citation["citation_id"] = "citation:" + digest(
                f'{evidence_id}|{field}|{citation["span"]["start"]}|{citation["span"]["quote"]}'.encode())
            self.validator.validate_citation(citation, self.sources)
        key = endpoint_id or "selector:" + digest(encoded({"version": VERSION,
                                                           "evidence_id": evidence_id,
                                                           "selectors": selectors}))
        endpoint = {"endpoint_id": key, **source_metadata(original), "selectors": selectors,
                    "mention_kind": mention_kind, "actor_reference_resolution": "not_projected"}
        require(key not in self.endpoints or self.endpoints[key] == endpoint,
                "Endpoint ID has conflicting coordinates")
        self.endpoints[key] = endpoint
        return key

    def add_question(self, question):
        key = question["task_id"]
        require(key not in self.questions, "Duplicate task ID across input versions")
        require(len(question["candidate_endpoint_ids"]) == len(set(question["candidate_endpoint_ids"])),
                "Duplicate declared candidate")
        require({question["focus_endpoint_id"], *question["candidate_endpoint_ids"]} <= self.endpoints.keys(),
                "Question references an absent endpoint")
        self.questions[key] = question

    def add_target(self, task_id, candidate, decision, provenance):
        require(candidate in self.questions[task_id]["candidate_endpoint_ids"],
                "Answer is outside the declared candidate pool")
        require(decision in DECISIONS, "Unsupported identity decision")
        self.targets.append({"task_id": task_id, "candidate_endpoint_id": candidate,
                             "decision": decision, "label": DECISIONS[decision], **provenance})

    def validate_coverage(self):
        expected = {(key, candidate) for key, item in self.questions.items()
                    for candidate in item["candidate_endpoint_ids"]}
        actual = [(item["task_id"], item["candidate_endpoint_id"]) for item in self.targets]
        require(len(actual) == len(set(actual)) and set(actual) == expected,
                "Targets must cover each declared identity edge exactly once")
        # Include actor-kind tasks and unknown targets before any later fit filter.
        return self.validator.build_split_components(list(self.questions.values()))


def selector(citation, kind, raw_reference_kind=None):
    result = {key: citation[key] for key in ("field_path", "field_sha256", "span")}
    result["kind"] = kind
    if raw_reference_kind is not None:
        result["raw_reference_kind"] = raw_reference_kind
    return result


def endpoint_selectors(side, is_control):
    """Copy source coordinates; a reference token does not identify its owner."""
    require(side["actor_kind"] is None and side["actor_identity"] is None,
            "Source endpoint must retain unknown actor identity and kind")
    if is_control:
        return [selector(side["reported_actor_anchor"], "actor_name")]
    result = []
    for occurrence in side["focal_reference_occurrences"]:
        kind = occurrence["kind"]
        require(kind in {"email", "obfuscated_email", "url"}, "Unsupported reference selector kind")
        citation = {"field_path": "/raw_record/row/" + occurrence["field"],
                    "field_sha256": occurrence["field_sha256"], "span": occurrence["match_span"]}
        result.append(selector(citation, "source_url_reference" if kind == "url"
                               else "source_contact_reference", kind))
    return result


def prepare_original(preparation, inputs, root, bundle):
    manifest = inputs.json(bundle / "manifest.json", ORIGINAL_MANIFEST_SHA)
    for name, record in manifest["files"].items():
        inputs.read(bundle / name, record["sha256"], record["bytes"])
    for name in ("reservation_index", "original_selected_evidence", "original_collision_tasks"):
        record = manifest[name]
        inputs.read(root / record["path"], record["sha256"], record["bytes"])
    annotation = root / preparation.validator.ANNOTATION
    for filename, key in (("evidence.jsonl", "source_annotation_evidence_sha256"),
                          ("tasks.jsonl", "source_annotation_tasks_sha256"),
                          ("manifest.json", "source_annotation_manifest_sha256")):
        inputs.pin_bytes(annotation / filename, manifest[key])
    reservation = inputs.json(bundle / "reservation-receipt.json")
    inputs.pin_bytes(root / "docs/research/2026-10-17-full-normalization/evaluation/cases.jsonl",
                     reservation["source_hashes"]["seed_cases"])
    inputs.pin_bytes(root / "local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl",
                     reservation["source_hashes"]["snapshot"])
    original_review = inputs.json(bundle / "review-receipt.json")
    for key in ("source_receipt", "original_judgments", "input"):
        record = original_review[key]
        inputs.read(root / record["path"], record["sha256"], record["bytes"])
    preparation.validator.validate_bundle(bundle, root)
    citations = {row["citation_id"]: row for row in inputs.rows(bundle / "citations.jsonl")}
    for wrapper in inputs.rows(root / manifest["original_selected_evidence"]["path"]):
        preparation.add_source(wrapper["original_record"])
    for mention in inputs.rows(bundle / "mentions.jsonl"):
        preparation.add_endpoint(mention["evidence_id"],
                                 [selector(citations[mention["citation_id"]],
                                           "actor_name" if mention["mention_kind"] == "name" else "source_expression")],
                                 mention["mention_id"], mention["mention_kind"])
    labels = inputs.rows(bundle / "labels.jsonl")
    for row in labels:
        preparation.add_question({"task_id": row["task_id"], "task_kind": row["task_kind"],
            "focus_endpoint_id": row["focus_mention_id"],
            "candidate_endpoint_ids": row.get("candidate_mention_ids", []),
            "split_group_ids": row["split_group_ids"], "origin": "original_draft",
            "candidate_scope": "original_validated_candidate_pool"})
        for decision in row["answer"].get("candidate_decisions", []):
            preparation.add_target(row["task_id"], decision["candidate_mention_id"], decision["decision"],
                {"annotation_status": "original_draft", "labels_sha256": manifest["files"]["labels.jsonl"]["sha256"]})


def validated_review(preparation, inputs, review, packet_tool, packet, receipt_path, receipt_sha):
    receipt = inputs.json(receipt_path, receipt_sha)
    require(receipt["state"] == "validated_adjudication_files"
            and receipt["version"] == review.VERSION
            and receipt["accepted_data_writes"] == 0
            and receipt["policy_activation_permitted"] is False,
            "Expected a non-activating completed adjudication validation")
    records = receipt["inputs"]
    for record in records:
        inputs.read(record["path"], record["sha256"], record["bytes"])
        retained = (receipt_path.parent / record["retained_path"]).resolve()
        require(retained.is_relative_to(receipt_path.parent.resolve()), "Retained input escapes receipt")
        inputs.read(retained, record["sha256"], record["bytes"])

    def find(parent, filename):
        matches = [record for record in records
                   if Path(record["path"]).name == filename and Path(record["path"]).parent.name == parent]
        require(len(matches) == 1, "Validation receipt must identify one exact review input")
        return matches[0]

    manifest_record = find(packet.name, "manifest.json")
    require(Path(manifest_record["path"]).resolve() == (packet / "manifest.json").resolve(),
            "Packet path differs from authenticated review receipt")
    a, b = find("reader-a", "first-answers.jsonl"), find("reader-b", "first-answers.jsonl")
    adjudication = find("adjudication-v1", "adjudications.jsonl")
    summary, _ = review.validate_files(packet_tool, packet, manifest_record["sha256"],
        a["path"], a["sha256"], b["path"], b["sha256"], adjudication["path"], adjudication["sha256"])
    require(summary == receipt["summary"], "Current review validation differs from frozen receipt")
    questions, originals, _, manifest, _ = review.load_packet(packet_tool, packet, manifest_record["sha256"])
    for wrapper in originals.values():
        preparation.add_source(wrapper["original_record"])
    answers = {row["question_id"]: (row, row_hash)
               for row, row_hash in review.physical_rows(inputs.read(adjudication["path"]))}
    for key, row in questions.items():
        endpoints = []
        for side in (row["left"], row["right"]):
            selectors = endpoint_selectors(side, manifest["schema_version"] == review.CONTROLS_VERSION)
            endpoints.append(preparation.add_endpoint(side["evidence_id"], selectors))
        groups = set()
        for endpoint_id in endpoints:
            endpoint = preparation.endpoints[endpoint_id]
            groups.update(("recording:" + endpoint["evidence_id"], "family:" + endpoint["family_group"]))
            for item in endpoint["selectors"]:
                if item["kind"] == "actor_name":
                    groups.add("name_hypothesis:" + digest(preparation.validator.fold(item["span"]["quote"]).encode()))
        group_kind, group_id = (("review_hypothesis", row["hypothesis_id"]) if "hypothesis_id" in row
                                else ("control", row["control_id"]))
        groups.add(group_kind + ":" + group_id)
        same_source = row["left"]["evidence_id"] == row["right"]["evidence_id"]
        preparation.add_question({"task_id": key,
            "task_kind": "within_recording_coreference" if same_source else "cross_recording_identity",
            "focus_endpoint_id": endpoints[0], "candidate_endpoint_ids": [endpoints[1]],
            "split_group_ids": sorted(groups), "origin": manifest["schema_version"],
            "candidate_scope": "reviewed_declared_pairs"})
        answer, row_hash = answers[key]
        preparation.add_target(key, endpoints[1], answer["decision"],
            {"annotation_status": "agent_adjudicated_development", "adjudication_row_sha256": row_hash,
             "adjudication_sha256": adjudication["sha256"], "validation_receipt_sha256": receipt_sha})


def write_rows(output, name, rows):
    path = output / name
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    data = b"".join(encoded(row) + b"\n" for row in rows)
    with path.open("xb") as stream:
        stream.write(data)
    path.chmod(0o600)
    return {"path": name, "bytes": len(data), "sha256": digest(data), "rows": len(rows)}


def publish(preparation, inputs, output):
    components = preparation.validate_coverage()
    inputs.recheck()
    output = Path(output).resolve()
    output.mkdir(mode=0o700, parents=False, exist_ok=False)
    artifacts = [
        write_rows(output, "source/records.jsonl", [preparation.sources[key] for key in sorted(preparation.sources)]),
        write_rows(output, "source/endpoints.jsonl", [preparation.endpoints[key] for key in sorted(preparation.endpoints)]),
        write_rows(output, "source/questions.jsonl", [preparation.questions[key] for key in sorted(preparation.questions)]),
        write_rows(output, "targets/decisions.jsonl", preparation.targets),
        write_rows(output, "source/grouping.jsonl", [{"task_ids": group} for group in components]),
    ]
    manifest = {"version": VERSION, "state": "prepared_source_selectors_and_separate_targets",
        "projection_status": "pending_actual_CSharp_reference_selector_projection",
        "grouping_status": "all_tasks_grouped_before_filtering_projector_bridges_still_required",
        "counts": {"sources": len(preparation.sources), "endpoints": len(preparation.endpoints),
                   "tasks": len(preparation.questions), "identity_edges": len(preparation.targets),
                   "source_components_before_projection": len(components),
                   "decisions": dict(Counter(row["decision"] for row in preparation.targets))},
        "artifacts": artifacts, "inputs": list(inputs.records.values()),
        "eligible_for_release_evaluation": False, "policy_activation_permitted": False,
        "identity_accuracy_established": False, "accepted_data_writes": 0}
    # Partial files are useful diagnostics but must never be mistaken for a
    # complete input package. Publish the manifest only after all pins recheck.
    inputs.recheck()
    for record in artifacts:
        data = (output / record["path"]).read_bytes()
        require(len(data) == record["bytes"] and digest(data) == record["sha256"], "Prepared output changed")
    temporary = output / "manifest.partial.json"
    with temporary.open("xb") as stream:
        stream.write(encoded(manifest) + b"\n")
    temporary.chmod(0o600)
    inputs.recheck()
    os.replace(temporary, output / "manifest.json")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--spec", type=Path, required=True)
    parser.add_argument("--spec-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    inputs = Inputs()
    spec = inputs.json(args.spec, args.spec_sha256)
    root = args.root.resolve()
    bundle = root / "docs/research/2026-10-17-full-normalization/training/people-development-v1"
    validator = import_captured(inputs, bundle / "validate_bundle.py", ORIGINAL_VALIDATOR_SHA,
                                "pinned_original_people_training_validator")
    review_path = root / "tools/RelistenNormalization/people-identity-reviews/review_people_identity_packet.py"
    review = import_captured(inputs, review_path, REVIEW_VALIDATOR_SHA, "pinned_people_review_validator")
    packet_path = root / "tools/RelistenNormalization/people-identity-packet/people_identity_packet.py"
    packet = review.load_packet_tool(packet_path, PACKET_TOOL_SHA)
    inputs.read(packet_path, PACKET_TOOL_SHA)
    inputs.read(Path(__file__))
    require(spec["version"] == VERSION and len(spec["review_packets"]) == 2, "Expected the two known review packets")
    preparation = Preparation(validator)
    prepare_original(preparation, inputs, root, bundle)
    for item in spec["review_packets"]:
        validated_review(preparation, inputs, review, packet, root / item["packet"],
                         root / item["validation_receipt"], item["validation_receipt_sha256"])
    protected = [bundle.resolve(), *(root / item["packet"] for item in spec["review_packets"]),
                 *((root / item["validation_receipt"]).parent for item in spec["review_packets"])]
    output = args.output.resolve()
    require(not any(output.is_relative_to(path) for path in protected), "Output must not be inside an input directory")
    result = publish(preparation, inputs, output)
    print(json.dumps({"state": result["state"], "counts": result["counts"],
                      "manifest_sha256": digest((output / "manifest.json").read_bytes())}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError) as error:
        raise SystemExit(f"Training input preparation failed without completion: {error}")
