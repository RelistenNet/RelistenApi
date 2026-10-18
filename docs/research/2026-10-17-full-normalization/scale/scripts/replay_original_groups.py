#!/usr/bin/env python3
"""Replay the original frozen split algorithm; export metadata, never source prose.

The standalone CLI is an offline process. It decodes original credit strings to
reproduce exact duplicate groups, but performs no extraction, annotation, model
inference, training, database work or network requests.
"""

import argparse
from collections import Counter, defaultdict, deque
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import sys
import types

from prepare_venue_context import (
    ANNOTATION, RESEARCH, ROOT, PreparationInputs, bytes_fingerprint,
    publish_preparation, require,
)
from project_annotation_metadata import canonical_uuid, load_discovery, source_candidates


RESOURCE_COUNTS = {
    "source_sample": 32593, "track_sample": 34861, "venue_sample": 17073,
    "song_sample": 1696, "setlist_sample": 432,
}
SNAPSHOT_BYTES = 176666741
SNAPSHOT_ROWS = 104228
HEADER_COUNTS = {
    "source_sample": 6000, "track_sample": 4500, "venue_sample": 6000,
    "song_sample": 1696, "setlist_sample": 432,
}
SNAPSHOT = Path("local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl")
MAX_METADATA_BYTES = 64 * 1024 * 1024


def read_receipt(inputs, directory, schema, repo):
    """Authenticate saved inputs byte-only; original payload files stay unparsed here."""
    receipt = json.loads(inputs.read(directory / "receipt.json"))
    require(receipt["schema_version"] == schema and receipt["state"] == "completed",
            "Unsupported or incomplete prerequisite receipt")
    for saved in receipt["inputs"]:
        path = Path(saved["path"]).resolve()
        require(path.is_relative_to(repo), "Prerequisite input escaped this checkout")
        inputs.stamp(path)
        require(inputs.stamps[path] == {key: saved[key] for key in ("bytes", "sha256")},
                "Changed prerequisite input")
    for name, saved in receipt["files"].items():
        require(Path(name).name == name, "Unsafe prerequisite artifact name")
        path = directory / name
        inputs.stamp(path)
        require(inputs.stamps[path] == saved, "Changed prerequisite output")
    return receipt


def read_metadata_rows(inputs, path):
    require(path.stat().st_size <= MAX_METADATA_BYTES, "Metadata artifact exceeds its byte bound")
    data = inputs.read(path)
    require(len(data) <= MAX_METADATA_BYTES, "Metadata artifact changed beyond its byte bound")
    # Split only physical LF records; Unicode separators are valid inside strings.
    lines = data.split(b"\n")
    require(lines[-1] == b"", "Metadata JSONL lacks its terminal newline")
    require(all(lines[:-1]), "Metadata JSONL has an empty physical row")
    return [json.loads(line) for line in lines[:-1]]


def load_builder(path, source, manifest):
    require(hashlib.sha256(source).hexdigest() == manifest["builder_sha256"],
            "Original builder source differs")
    require(manifest["builder_version"] == "blind-annotation-v1"
            and manifest["state"] == "complete_unlabeled", "Unsupported original annotation version")
    module = types.ModuleType("frozen_original_group_builder")
    module.__file__ = str(path)
    exec(compile(source, str(path), "exec"), module.__dict__)
    require(module.VERSION == manifest["builder_version"], "Builder version differs")
    return module


def authenticate_snapshot(path, expected, expected_rows):
    """Bound and authenticate all bytes before the original loader decodes them."""
    require(path.stat().st_size == expected["bytes"], "Original snapshot byte count differs")
    hasher, size, rows = hashlib.sha256(), 0, 0
    bounded = SnapshotReplayInput(path, expected["bytes"], expected_rows)
    with bounded.open("rb") as stream:
        for line in stream:
            hasher.update(line)
            size += len(line)
            rows += 1
            require(size <= expected["bytes"] and rows <= expected_rows,
                    "Original snapshot exceeds its frozen bounds")
    require({"bytes": size, "sha256": hasher.hexdigest()} == expected,
            "Original snapshot hash differs")
    require(rows == expected_rows, "Original snapshot physical row count differs")


def positive_id(value):
    require(type(value) is int and value > 0, "Invalid original numeric identity")
    return value


class SnapshotReplayInput:
    """Keep the unchanged original loader inside the authenticated byte/row bounds."""

    def __init__(self, path, expected_bytes, expected_rows):
        self.path, self.expected_bytes, self.expected_rows = path, expected_bytes, expected_rows

    @contextmanager
    def open(self, mode):
        require(mode == "rb", "Original loader requested unsupported snapshot access")
        with self.path.open(mode) as stream:
            yield self.lines(stream)

    def lines(self, stream):
        size, rows = 0, 0
        while True:
            line = stream.readline(self.expected_bytes - size + 1)
            if not line:
                break
            size += len(line)
            rows += 1
            require(size <= self.expected_bytes and rows <= self.expected_rows,
                    "Snapshot changed beyond its frozen bounds during original loading")
            yield line
        require(size == self.expected_bytes and rows == self.expected_rows,
                "Snapshot was truncated during original loading")


def replay_snapshot(builder, path, manifest, expected_bytes=SNAPSHOT_BYTES,
                    expected_rows=SNAPSHOT_ROWS, expected_counts=None):
    expected_counts = RESOURCE_COUNTS if expected_counts is None else expected_counts
    require(manifest["input_bytes"] == expected_bytes, "Wrong frozen snapshot size")
    expected = {"bytes": expected_bytes, "sha256": manifest["input_sha256"]}
    authenticate_snapshot(path, expected, expected_rows)
    bounded = SnapshotReplayInput(path, expected_bytes, expected_rows)
    header, completion, artists, records, loaded_hash = builder.load_snapshot(bounded)
    require(loaded_hash == expected["sha256"], "Snapshot changed during original loading")
    require(header == manifest["snapshot"] and completion == manifest["snapshot_complete"],
            "Original snapshot envelope differs")
    require({kind: len(rows) for kind, rows in records.items()} == expected_counts,
            "Original resource cohort differs")
    all_records = [record for rows in records.values() for record in rows]
    # These are the original algorithm and its complete input. Never rebuild
    # groups from only the selected annotation rows or discovered current sources.
    groups = builder.family_assignments(all_records)
    require(len(groups) == sum(expected_counts.values()), "Original group map is incomplete")
    metadata = []
    for record in all_records:
        row, kind = record["row"], record["kind"]
        artist_id = positive_id(row["artist_id"])
        artist_uuid = canonical_uuid(artists[artist_id]["row"]["uuid"])
        resource_uuid = canonical_uuid(row["uuid"])
        original_id = positive_id(row["id"])
        family = record.get("family")
        show_id = row["show_id"] if kind == "source_sample" else None
        if kind == "source_sample":
            if show_id is not None:
                positive_id(show_id)
            require(family == (None if show_id is None else f"artist:{artist_id}:show:{show_id}"),
                    "Original source row and literal family disagree")
        evidence_id = builder.evidence_key(record)
        group = groups[evidence_id]
        metadata.append({
            "evidence_id": evidence_id, "resource_kind": kind, "resource_uuid": resource_uuid,
            "original_id": original_id, "artist_id": artist_id, "artist_uuid": artist_uuid,
            "show_id": show_id, "original_show_uuid": None,
            "parent_source_id": row.get("source_id") if kind == "track_sample" else None,
            "known_recording_family": family, "input_duplicate_family": group,
            "family_partition": builder.partition(group),
            "artist_partition": builder.partition("artist:" + artist_uuid),
        })
    return sorted(metadata, key=lambda item: item["evidence_id"]), records


def authenticate_headers(metadata, headers, expected_counts=None):
    expected_counts = HEADER_COUNTS if expected_counts is None else expected_counts
    require(dict(Counter(item["resource_kind"] for item in headers)) == expected_counts,
            "Saved annotation header cohort differs")
    by_id = {item["evidence_id"]: item for item in metadata}
    seen = set()
    fields = ("resource_kind", "resource_uuid", "artist_uuid", "known_recording_family",
              "input_duplicate_family", "family_partition", "artist_partition")
    for header in headers:
        key = header["evidence_id"]
        require(key not in seen, "Duplicate saved annotation header")
        seen.add(key)
        require(key in by_id and all(by_id[key][field] == header[field] for field in fields),
                "Replayed metadata or partition differs from a saved original header")


def reservation_closure(metadata, reservation):
    references = set(reservation["reserved_references"])
    references.update(reservation.get("known_exposed_track_references", []))
    excluded = {item["evidence_id"] for item in reservation["excluded_annotation_metadata"]}
    families = set(reservation["reserved_known_families"])
    linked = reservation["metadata_matches"] + reservation["metadata_linked_parent_sources"]
    linked_ids = {item["kind"] + "_sample:" + item["uuid"] for item in linked if item.get("uuid")}
    references.update(item["kind"] + ":" + str(item["id"]) for item in linked)
    groups = set(reservation["reserved_annotation_family_groups"])
    seeds, matched = [], set()
    for item in metadata:
        reference = item["resource_kind"].removesuffix("_sample") + ":" + str(item["original_id"])
        if (reference in references or item["evidence_id"] in excluded | linked_ids
                or item["known_recording_family"] in families):
            groups.add(item["input_duplicate_family"])
            seeds.append(item["evidence_id"])
            matched.add(reference)
    sources, tracks_by_group = {}, defaultdict(list)
    for item in metadata:
        if item["resource_kind"] == "source_sample":
            sources[item["original_id"]] = item
        elif item["resource_kind"] == "track_sample":
            tracks_by_group[item["input_duplicate_family"]].append(item)

    # Every track in an excluded component also excludes its actual stored
    # parent. Inconsistent artist/show fields can put that parent in another
    # component, whose tracks may lead to further parents. Visit each group once.
    pending = deque(sorted(groups))
    explicit_seeds, derived_parents = set(seeds), set()
    while pending:
        group = pending.popleft()
        for track in tracks_by_group.get(group, []):
            parent = sources.get(track["parent_source_id"])
            if parent is None:
                continue
            if parent["evidence_id"] not in explicit_seeds:
                derived_parents.add(parent["evidence_id"])
            parent_group = parent["input_duplicate_family"]
            if parent_group not in groups:
                groups.add(parent_group)
                pending.append(parent_group)
    return {
        "blocked_original_groups": sorted(groups), "original_seed_evidence_ids": sorted(set(seeds)),
        "derived_parent_source_evidence_ids": sorted(derived_parents),
        "unmatched_original_references": sorted(references - matched),
        "exposure_note": reservation.get("exposure_note"),
        "original_reservation_index_unchanged": True,
    }


def classify_sources(candidates, metadata, records, reservation, closure):
    originals = {item["resource_uuid"]: item for item in metadata
                 if item["resource_kind"] == "source_sample"}
    requested = {item["source_uuid"] for item in candidates}
    context_only = {
        record["source_context"]["uuid"] for record in records.get("track_sample", [])
        if isinstance(record.get("source_context"), dict)
        and record["source_context"].get("uuid") in requested
    }
    blocked = set(closure["blocked_original_groups"])
    reserved_refs = set(reservation["reserved_references"])
    reserved_source_uuids = {
        item["uuid"] for item in reservation["metadata_matches"] + reservation["metadata_linked_parent_sources"]
        if item["kind"] == "source" and item.get("uuid")
    }
    reserved_source_uuids.update(
        item["evidence_id"].removeprefix("source_sample:")
        for item in reservation["excluded_annotation_metadata"]
        if item["evidence_id"].startswith("source_sample:")
    )
    results = []
    for current in candidates:
        original = originals.get(current["source_uuid"])
        same_source_id = original is not None and original["original_id"] == current["source_id"]
        if ("source:" + str(current["source_id"]) in reserved_refs
                or current["source_uuid"] in reserved_source_uuids):
            state = "reserved_or_excluded"
        elif original is None:
            state = ("context_only_not_original_source_member" if current["source_uuid"] in context_only
                     else "absent_original_captured_source_frame")
        elif original["input_duplicate_family"] in blocked:
            state = "reserved_or_excluded"
        elif original["artist_partition"] != "development" or original["family_partition"] != "development":
            state = "original_heldout_partition"
        elif not same_source_id or any(original[key] != current[key] for key in ("artist_id", "artist_uuid")):
            state = "original_current_tuple_drift"
        elif original["show_id"] is None:
            state = "missing_original_show_context"
        elif current["show_id"] is None or current["show_uuid"] is None:
            state = "missing_current_show_context"
        elif original["show_id"] != current["show_id"]:
            state = "original_current_tuple_drift"
        else:
            state = "original_development_tuple_verified"
        results.append({
            **current, "classification": state, "original_metadata": original,
            "original_numeric_source_id_verified": same_source_id,
            "new_partition_assigned": False, "provider_query_executed": False,
        })
    require(len(results) == len(candidates), "Lost a discovered source classification")
    return results


def replay(repo, projection, preparation, discovery, output):
    repo = Path(repo).resolve()
    projection, preparation, discovery, output = (
        Path(path).resolve() for path in (projection, preparation, discovery, output)
    )
    require(output.is_relative_to(repo / "local-dev/normalization-data"), "Output must remain ignored")
    require(not output.exists(), "Output already exists")
    inputs = PreparationInputs()
    for name in ("replay_original_groups.py", "prepare_venue_context.py", "project_annotation_metadata.py"):
        inputs.stamp(Path(__file__).with_name(name).resolve())
    anchor = json.loads(inputs.read(repo / RESEARCH / "scale/evidence/venue-annotation-metadata-v1.json"))
    saved_projection = anchor["projection_receipt"]
    require((repo / saved_projection["path"]).resolve() == projection / "receipt.json",
            "Wrong frozen metadata projection")
    require(bytes_fingerprint(inputs.read(projection / "receipt.json")) == {
        key: saved_projection[key] for key in ("bytes", "sha256")
    }, "Changed frozen metadata projection receipt")
    projected = read_receipt(inputs, projection, "venue-annotation-metadata-projection-v1", repo)
    prerequisite_paths = {Path(item["path"]).resolve() for item in projected["inputs"]}
    require({preparation / "receipt.json", discovery / "query.json", discovery / "snapshot.jsonl"}
            <= prerequisite_paths, "Preparation/discovery do not belong to this projection")
    prepared = read_receipt(inputs, preparation, "venue-context-preparation-v1", repo)
    annotation = repo / ANNOTATION
    manifest = json.loads(inputs.read(annotation / "manifest.json"))
    builder_path = repo / RESEARCH / "scale/annotation/build_tasks.py"
    builder = load_builder(builder_path, inputs.read(builder_path), manifest)
    reservation = json.loads(inputs.read(preparation / "reservation-index.json"))
    require(reservation["raw_snapshot_sha256"] == manifest["input_sha256"]
            and reservation["annotation_evidence_sha256"] == manifest["files"]["evidence.jsonl"]["sha256"],
            "Original reservation frame differs")
    associations, venues = load_discovery(inputs, discovery, preparation, prepared)
    candidates = source_candidates(associations, venues)
    headers = read_metadata_rows(inputs, projection / "annotation-metadata.jsonl")
    snapshot = repo / SNAPSHOT
    inputs.stamp(snapshot)
    metadata, records = replay_snapshot(builder, snapshot, manifest)
    authenticate_headers(metadata, headers)
    closure = reservation_closure(metadata, reservation)
    classifications = classify_sources(candidates, metadata, records, reservation, closure)
    files = {
        "original-resource-metadata.jsonl": b"".join(
            (json.dumps(item, sort_keys=True) + "\n").encode() for item in metadata
        ),
        "source-classifications.jsonl": b"".join(
            (json.dumps(item, sort_keys=True) + "\n").encode() for item in classifications
        ),
        "reservation-closure.json": (json.dumps(closure, indent=2, sort_keys=True) + "\n").encode(),
    }
    require(all(len(data) <= MAX_METADATA_BYTES for data in files.values()), "Output metadata exceeds its bound")
    receipt = {
        "schema_version": "original-group-replay-v1", "state": "completed", "usage": "development_only",
        "python": {"version": sys.version, "implementation": sys.implementation.name},
        "processing": {
            "full_frozen_payloads_programmatically_decoded": True,
            "purpose": "Replay only the original exact duplicate/family grouping algorithm",
            "source_prose_exported": False, "model_requests": 0, "training_performed": False,
        },
        "counts": {
            "original_resources": len(metadata), "original_sources": RESOURCE_COUNTS["source_sample"],
            "original_physical_records": SNAPSHOT_ROWS, "authenticated_saved_headers": len(headers),
            "discovered_sources": len(candidates),
            "source_classifications": dict(Counter(item["classification"] for item in classifications)),
            "blocked_original_groups": len(closure["blocked_original_groups"]),
        },
        "limits": [
            "Original credit strings are decoded only inside this offline process for exact duplicate hashing; no semantic annotation, inference or training occurs.",
            "Original partitions are reproduced from the full frozen input, not assigned from current tuples or a new split seed.",
            "Context-only nested sources have no standalone source group assignment. Missing standalone sources remain explicit.",
            "Original show UUIDs are unavailable in standalone source rows; current show UUIDs remain current discovery evidence.",
            "All saved annotation headers must match before publication. The original Python runtime was not recorded; this receipt pins the replay runtime.",
            "Existing reservations and first-exposure notes remain unchanged; derived full-component exclusions are additive.",
            "No provider query, label change, policy activation or accepted catalog write occurred.",
        ],
    }
    return publish_preparation(output, files, inputs, receipt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    for name in ("projection", "preparation", "discovery", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        result = replay(args.repo_root, args.projection, args.preparation, args.discovery, args.output)
    except (ValueError, OSError, KeyError, TypeError, RecursionError):
        # Do not render chained parser exceptions or source values to a terminal.
        print("Original grouping replay did not publish a new result.", file=sys.stderr)
        return 2
    print(json.dumps({"state": result["state"], "counts": result["counts"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
