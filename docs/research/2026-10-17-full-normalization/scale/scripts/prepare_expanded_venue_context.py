#!/usr/bin/env python3
"""Prepare a bounded venue/date query from authenticated original development groups."""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys

from prepare_venue_context import (
    RESEARCH, ROOT, PreparationInputs, bytes_fingerprint, publish_preparation,
    render_queries, require,
)
from project_annotation_metadata import canonical_uuid, load_discovery, source_candidates
from replay_original_groups import read_receipt, reservation_closure


MAX_SMALL_BYTES = 1024 * 1024
MAX_METADATA_BYTES = 64 * 1024 * 1024
MAX_ALLOWLIST_BYTES = 8 * 1024 * 1024
MAX_DISCOVERY_BYTES = 32 * 1024 * 1024
MAX_LINE_BYTES = 64 * 1024
RESOURCE_ROWS = 86655
CLASSIFICATION_ROWS = 122
VERIFIED_ROWS = 29
VERIFIED_ARTISTS = 21
STATUSES = {
    "reserved_or_excluded", "context_only_not_original_source_member",
    "absent_original_captured_source_frame", "original_heldout_partition",
    "original_current_tuple_drift", "missing_original_show_context",
    "missing_current_show_context", "original_development_tuple_verified",
}
CURRENT_FIELDS = ("source_uuid", "source_id", "artist_id", "artist_uuid",
                  "show_id", "show_uuid", "associations")


def read_bounded(inputs, path, maximum):
    require(path.stat().st_size <= maximum, "Input exceeds its byte bound")
    data = inputs.read(path)
    require(len(data) <= maximum, "Input changed beyond its byte bound")
    return data


def read_json(inputs, path, maximum=MAX_SMALL_BYTES):
    return json.loads(read_bounded(inputs, path, maximum))


def read_rows(inputs, path, expected_rows, maximum=MAX_METADATA_BYTES):
    data = read_bounded(inputs, path, maximum)
    lines = data.split(b"\n")
    require(lines[-1] == b"" and len(lines) - 1 == expected_rows,
            "Metadata physical row count differs")
    require(all(0 < len(line) <= MAX_LINE_BYTES for line in lines[:-1]),
            "Empty or oversized metadata physical row")
    return [json.loads(line) for line in lines[:-1]]


def unique(rows, key):
    result = {row[key]: row for row in rows}
    require(len(result) == len(rows), "Duplicate metadata identity")
    return result


def select_sources(classifications, metadata, discovered, venues, reservation, closure,
                   expected_sources=VERIFIED_ROWS, expected_artists=VERIFIED_ARTISTS):
    """A status selects candidates; original facts and reservations decide eligibility."""
    originals = unique(metadata, "evidence_id")
    classified = unique(classifications, "source_uuid")
    current = unique(discovered, "source_uuid")
    require(classified.keys() == current.keys(), "Classification and discovery source sets differ")
    require(all(row["classification"] in STATUSES for row in classifications), "Unknown replay status")
    require(reservation_closure(metadata, reservation) == closure, "Original reservation closure differs")
    blocked = set(closure["blocked_original_groups"])
    artists = {}
    for venue in venues:
        require(artists.setdefault(venue["artist_uuid"], venue["artist_id"]) == venue["artist_id"],
                "Allowed venue artist identity disagrees")
    selected = []
    for row in classifications:
        actual = current[row["source_uuid"]]
        require(all(row[field] == actual[field] for field in CURRENT_FIELDS),
                "Replay and exact discovery tuple differ")
        if row["classification"] != "original_development_tuple_verified":
            continue
        key = "source_sample:" + canonical_uuid(row["source_uuid"])
        original = originals.get(key)
        require(original is not None and row["original_metadata"] == original,
                "Verified source lacks its exact original metadata")
        require(original["resource_kind"] == "source_sample"
                and original["resource_uuid"] == row["source_uuid"], "Wrong original source identity")
        require(original["artist_partition"] == original["family_partition"] == "development",
                "Held-out original cannot supply expanded context")
        require(original["input_duplicate_family"] not in blocked, "Reserved original component")
        require(re.fullmatch(r"[0-9a-f]{64}", original["input_duplicate_family"]) is not None,
                "Invalid original group identity")
        require(row["original_numeric_source_id_verified"] is True
                and row["new_partition_assigned"] is False
                and row["provider_query_executed"] is False, "Unsupported replay verification flags")
        require(type(original["original_id"]) is int and original["original_id"] > 0
                and original["original_id"] == row["source_id"], "Original source ID drift")
        require(type(original["artist_id"]) is int and original["artist_id"] > 0
                and original["artist_id"] == row["artist_id"]
                and original["artist_uuid"] == row["artist_uuid"]
                and artists.get(row["artist_uuid"]) == row["artist_id"], "Original artist identity drift")
        require(type(original["show_id"]) is int and original["show_id"] > 0
                and original["show_id"] == row["show_id"] and row["show_uuid"] is not None,
                "Original/current show context differs")
        canonical_uuid(row["artist_uuid"])
        canonical_uuid(row["show_uuid"])
        require(original["original_show_uuid"] is None, "Unsupported original show UUID claim")
        require(original["known_recording_family"] == f"artist:{row['artist_id']}:show:{row['show_id']}",
                "Original family and catalog tuple disagree")
        selected.append({
            "evidence_id": key, "source_id": original["original_id"], "source_uuid": row["source_uuid"],
            "artist_id": original["artist_id"], "artist_uuid": original["artist_uuid"],
            "expected_show_id": original["show_id"], "family_group": original["input_duplicate_family"],
            "known_recording_family": original["known_recording_family"],
            "artist_partition": original["artist_partition"], "family_partition": original["family_partition"],
            "original_show_uuid": None, "current_discovery_show_uuid": row["show_uuid"],
        })
    require(len(selected) == expected_sources, "Verified development source denominator differs")
    require(len({row["source_id"] for row in selected}) == len(selected), "Duplicate allowed source ID")
    require(len({row["artist_uuid"] for row in selected}) == expected_artists,
            "Verified development artist denominator differs")
    return sorted(selected, key=lambda row: row["evidence_id"])


def prepare(repo, replay, preparation, discovery, output):
    repo = Path(repo).resolve()
    replay, preparation, discovery, output = (
        Path(path).resolve() for path in (replay, preparation, discovery, output)
    )
    require(output.is_relative_to(repo / "local-dev/normalization-data"), "Output must remain ignored")
    require(not output.exists(), "Output already exists")
    inputs = PreparationInputs()
    for name in ("prepare_expanded_venue_context.py", "prepare_venue_context.py",
                 "project_annotation_metadata.py", "replay_original_groups.py"):
        inputs.stamp(Path(__file__).with_name(name).resolve())
    anchor = read_json(inputs, repo / RESEARCH / "scale/evidence/original-group-replay-v1.json")
    require(anchor["schema_version"] == "original-group-replay-local-v1"
            and anchor["state"] == "completed_original_group_replay", "Incomplete tracked replay anchor")
    saved = anchor["replay_receipt"]
    require((repo / saved["path"]).resolve() == replay / "receipt.json", "Wrong frozen replay directory")
    replay_bytes = read_bounded(inputs, replay / "receipt.json", MAX_SMALL_BYTES)
    require(bytes_fingerprint(replay_bytes) == {key: saved[key] for key in ("bytes", "sha256")},
            "Changed completed replay receipt")
    completed = read_receipt(inputs, replay, "original-group-replay-v1", repo)
    require(completed["counts"] == anchor["counts"]
            and completed["counts"]["original_resources"] == RESOURCE_ROWS
            and completed["counts"]["discovered_sources"] == CLASSIFICATION_ROWS,
            "Completed replay cohort differs")
    prerequisite_paths = {Path(item["path"]).resolve() for item in completed["inputs"]}
    require({preparation / "receipt.json", discovery / "query.json", discovery / "snapshot.jsonl"}
            <= prerequisite_paths, "Preparation/discovery do not belong to this replay")
    read_bounded(inputs, preparation / "receipt.json", MAX_SMALL_BYTES)
    prepared = read_receipt(inputs, preparation, "venue-context-preparation-v1", repo)
    for name in ("query.json", "summary.json"):
        read_bounded(inputs, discovery / name, MAX_SMALL_BYTES)
    read_bounded(inputs, discovery / "snapshot.jsonl", MAX_DISCOVERY_BYTES)
    venue_bytes = read_bounded(inputs, preparation / "venue-allowlist.json", MAX_ALLOWLIST_BYTES)
    venues = json.loads(venue_bytes)
    require(len(venues) == 67 and len({row["venue_uuid"] for row in venues}) == 67
            and len({row["venue_id"] for row in venues}) == 67, "Original venue allowlist differs")
    associations, observed_venues = load_discovery(inputs, discovery, preparation, prepared)
    require(observed_venues == venues, "Discovery venue scope differs")
    discovered = source_candidates(associations, venues)
    metadata = read_rows(inputs, replay / "original-resource-metadata.jsonl", RESOURCE_ROWS)
    classifications = read_rows(inputs, replay / "source-classifications.jsonl", CLASSIFICATION_ROWS)
    require(dict(Counter(row["classification"] for row in classifications))
            == completed["counts"]["source_classifications"], "Replay status counts differ")
    reservation = read_json(inputs, preparation / "reservation-index.json", 2 * MAX_SMALL_BYTES)
    closure = read_json(inputs, replay / "reservation-closure.json")
    sources = select_sources(classifications, metadata, discovered, venues, reservation, closure)
    old_sources = read_json(inputs, preparation / "source-allowlist.json", MAX_ALLOWLIST_BYTES)
    require(len(old_sources) == 51, "Original strict source denominator differs")
    old_uuids = {row["source_uuid"] for row in old_sources}
    overlap = sum(row["source_uuid"] in old_uuids for row in sources)
    require(overlap == 2, "Original strict overlap differs")
    sql_dir = repo / RESEARCH / "scale/sql"
    strict, _ = render_queries(
        read_bounded(inputs, sql_dir / "venue-context-expanded.sql", MAX_SMALL_BYTES).decode(),
        read_bounded(inputs, sql_dir / "venue-context-discovery.sql", MAX_SMALL_BYTES).decode(), venues, sources,
    )
    files = {
        "venue-allowlist.json": venue_bytes,
        "source-allowlist.json": (json.dumps(sources, indent=2, sort_keys=True) + "\n").encode(),
        "strict-context.sql": strict.encode(),
    }
    require(all(len(data) <= MAX_ALLOWLIST_BYTES for data in files.values()), "Prepared output exceeds its bound")
    receipt = {
        "schema_version": "expanded-venue-context-preparation-v1", "state": "completed",
        "usage": "development_only", "query_executed": False, "new_partitions_assigned": 0,
        "raw_annotation_or_snapshot_payloads_decoded": 0,
        "counts": {"venue_resources": 67, "source_resources": len(sources),
                   "source_artists": VERIFIED_ARTISTS, "already_in_strict_allowlist": overlap,
                   "additional_verified_recordings": len(sources) - overlap},
        "limits": [
            "Only authenticated original development tuples enter this query; held-out, context-only, absent and reserved resources stay excluded.",
            "Original source/artist IDs and UUIDs, show IDs and groups are preserved. Current discovery show UUIDs are separate observations; original show UUIDs remain unknown.",
            "The schema12 expanded SQL template fences the saved current discovery show UUID before exposing date context, in addition to original numeric show assignment and source/artist identity.",
            "Source and show venue references remain separate. Literal partial display dates are retained; catalog sort dates do not establish date precision or venue lifetime.",
            "Preparation executes no query and proves no venue identity, candidate recall, matching accuracy or active policy.",
        ],
    }
    return publish_preparation(output, files, inputs, receipt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    for name in ("replay", "preparation", "discovery", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        result = prepare(args.repo_root, args.replay, args.preparation, args.discovery, args.output)
    except (ValueError, OSError, KeyError, TypeError):
        print("Expanded venue preparation did not publish a new result.", file=sys.stderr)
        return 2
    print(json.dumps({"state": result["state"], "counts": result["counts"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
