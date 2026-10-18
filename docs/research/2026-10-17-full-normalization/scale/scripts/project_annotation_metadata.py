#!/usr/bin/env python3
"""Project frozen annotation metadata without decoding any raw_record payload.

This adapter supports only the pinned canonical-v1 envelope. It does not assign
partitions, read source prose, verify original numeric source IDs, or query a DB.
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import uuid

from prepare_venue_context import (
    ANNOTATION, RESEARCH, ROOT, PreparationInputs, bytes_fingerprint,
    fingerprint, publish_preparation, require,
)


MAX_PREFIX_BYTES = 16 * 1024
MAX_NESTING = 8
MAX_FRAME_BYTES = 64 * 1024 * 1024
MAX_DISCOVERY_BYTES = 32 * 1024 * 1024
FRAME_ROWS = 18628
FRAME_KINDS = {
    "source_sample": 6000, "venue_sample": 6000, "track_sample": 4500,
    "song_sample": 1696, "setlist_sample": 432,
}
PARTITIONS = {"development", "validation", "test"}


def canonical_uuid(value):
    require(isinstance(value, str) and str(uuid.UUID(value)) == value, "Invalid metadata UUID")
    return value


class MetadataPrefixReader:
    """Read a closed envelope grammar; the raw value is outside this grammar.

    Only the artist object nests, and it contains two strings. Rejecting all
    other value shapes avoids a general JSON parser that could wander into a
    malformed envelope's payload while searching for a closing delimiter.
    """

    def __init__(self, line):
        require(isinstance(line, bytes), "Annotation rows must remain bytes")
        self.line = line
        self.position = 0
        self.depth = 0

    def byte(self):
        require(self.position < min(len(self.line), MAX_PREFIX_BYTES),
                "Truncated or oversized metadata prefix")
        return self.line[self.position]

    def whitespace(self):
        while self.byte() in b" \t\r\n":
            self.position += 1

    def expect(self, token):
        self.whitespace()
        require(self.byte() == ord(token), "Unexpected metadata delimiter")
        self.position += 1

    def open_object(self):
        self.depth += 1
        require(self.depth <= MAX_NESTING, "Metadata nesting bound exceeded")
        self.expect("{")

    def string(self):
        self.whitespace()
        require(self.byte() == ord('"'), "Expected a metadata string")
        start = self.position
        self.position += 1
        while True:
            char = self.byte()
            self.position += 1
            if char == ord('"'):
                break
            if char == ord("\\"):
                self.byte()
                self.position += 1
            else:
                require(char >= 32, "Unescaped metadata control character")
        try:
            return json.loads(self.line[start:self.position])
        except (ValueError, UnicodeError) as failure:
            raise ValueError("Invalid JSON/UTF8 in metadata string") from failure

    def key(self, expected):
        require(self.string() == expected, "Unexpected or reordered metadata key")
        self.expect(":")

    def artist(self):
        self.open_object()
        self.key("name")
        self.string()  # Public artist metadata is not needed in the projection.
        self.expect(",")
        self.key("uuid")
        artist_uuid = canonical_uuid(self.string())
        self.expect("}")
        self.depth -= 1
        return artist_uuid

    def read(self):
        self.open_object()
        self.key("artist")
        artist_uuid = self.artist()
        values = {}
        for name in (
            "artist_partition", "evidence_id", "family_partition",
            "input_duplicate_family", "known_recording_family",
        ):
            self.expect(",")
            self.key(name)
            self.whitespace()
            if name == "known_recording_family" and self.byte() == ord("n"):
                for char in b"null":
                    require(self.byte() == char, "Invalid null metadata value")
                    self.position += 1
                values[name] = None
            else:
                values[name] = self.string()
        self.expect(",")
        self.key("raw_record")
        # Do not skip whitespace, inspect the next byte, decode the line, or
        # validate the raw value. This colon is the payload boundary.
        barrier = self.position
        require(values["artist_partition"] in PARTITIONS
                and values["family_partition"] in PARTITIONS, "Unknown original partition")
        require(re.fullmatch(r"[0-9a-f]{64}", values["input_duplicate_family"]) is not None,
                "Invalid original duplicate-family group")
        kind, separator, resource_uuid = values["evidence_id"].partition(":")
        require(separator and kind in FRAME_KINDS, "Unknown annotation resource kind")
        canonical_uuid(resource_uuid)
        family = values["known_recording_family"]
        require(family is None or re.fullmatch(r"artist:\d+:show:\d+", family) is not None,
                "Unsupported original known family")
        return {
            **values, "artist_uuid": artist_uuid, "resource_kind": kind,
            "resource_uuid": resource_uuid, "raw_record_value_offset": barrier,
        }


def read_metadata_prefix(line):
    return MetadataPrefixReader(line).read()


def project_frame(path, expected, expected_rows=FRAME_ROWS, expected_kinds=None):
    """Authenticate complete frozen bytes, then decode only each bounded header."""
    require(expected["bytes"] <= MAX_FRAME_BYTES, "Frame exceeds the supported bound")
    require(fingerprint(path) == expected, "Changed frozen annotation frame")
    headers, seen, kinds = [], set(), Counter()
    hasher, size = hashlib.sha256(), 0
    with path.open("rb") as stream:
        for number, line in enumerate(stream, 1):
            hasher.update(line)
            size += len(line)
            require(size <= MAX_FRAME_BYTES, "Frame changed beyond the byte bound")
            item = read_metadata_prefix(line)
            require(item["evidence_id"] not in seen, "Duplicate annotation metadata identity")
            seen.add(item["evidence_id"])
            kinds[item["resource_kind"]] += 1
            headers.append({
                **item, "physical_line_number": number,
                "physical_line_sha256": hashlib.sha256(line.rstrip(b"\r\n")).hexdigest(),
            })
    require({"bytes": size, "sha256": hasher.hexdigest()} == expected,
            "Frame changed during projection")
    require(len(headers) == expected_rows, "Incomplete annotation metadata projection")
    require(dict(kinds) == (FRAME_KINDS if expected_kinds is None else expected_kinds),
            "Annotation resource-kind denominator differs")
    return headers


def source_candidates(associations, venues, expected_sources=122):
    """Preserve every current source and validate its metadata-only discovery scope."""
    allowed = {venue["evidence_id"]: venue for venue in venues}
    by_source = {}
    fields = {
        "venue_evidence_id", "venue_id", "venue_uuid", "artist_id", "artist_uuid",
        "association_kind", "source_id", "source_uuid", "show_id", "show_uuid",
    }
    for row in associations:
        require(set(row) == fields, "Discovery contains unexpected fields")
        venue = allowed.get(row["venue_evidence_id"])
        require(venue is not None, "Discovery escaped the selected venue scope")
        require(all(row[key] == venue[key] for key in (
            "venue_id", "venue_uuid", "artist_id", "artist_uuid"
        )), "Discovery venue/artist tuple differs")
        require(row["association_kind"] in {"source_venue", "show_venue", "show_source"},
                "Unknown discovery association kind")
        require(type(row["artist_id"]) is int and row["artist_id"] > 0,
                "Malformed current artist ID")
        canonical_uuid(row["artist_uuid"])
        require((row["show_id"] is None) == (row["show_uuid"] is None),
                "Incomplete current show identity")
        if row["show_uuid"] is not None:
            canonical_uuid(row["show_uuid"])
            require(type(row["show_id"]) is int and row["show_id"] > 0,
                    "Malformed current show ID")
        if row["source_uuid"] is None:
            require(row["source_id"] is None and row["association_kind"] == "show_venue",
                    "Malformed source-free association")
            continue
        require(row["association_kind"] != "show_venue", "Show association unexpectedly owns a source")
        canonical_uuid(row["source_uuid"])
        require(type(row["source_id"]) is int and row["source_id"] > 0,
                "Malformed current source ID")
        current = {key: row[key] for key in (
            "source_uuid", "source_id", "artist_uuid", "artist_id", "show_id", "show_uuid"
        )}
        previous = by_source.setdefault(row["source_uuid"], {**current, "associations": []})
        require(all(previous[key] == value for key, value in current.items()),
                "Current source has contradictory discovery tuples")
        previous["associations"].append({
            "venue_evidence_id": row["venue_evidence_id"],
            "association_kind": row["association_kind"],
        })
    require(len(by_source) == expected_sources, "Discovered source denominator differs")
    return sorted(by_source.values(), key=lambda item: item["source_uuid"])


def classify_sources(candidates, headers, reservation):
    """Keep original partitions; eligible means ready for a separate raw-ID check."""
    source_headers = {item["resource_uuid"]: item for item in headers
                      if item["resource_kind"] == "source_sample"}
    excluded = {item["evidence_id"] for item in reservation["excluded_annotation_metadata"]}
    reserved_refs = set(reservation["reserved_references"])
    reserved_families = set(reservation["reserved_known_families"])
    reserved_groups = set(reservation["reserved_annotation_family_groups"])
    reserved_uuids = {
        item["uuid"] for item in (
            reservation["metadata_matches"] + reservation["metadata_linked_parent_sources"]
        ) if item["kind"] == "source" and item.get("uuid") is not None
    }
    results = []
    for candidate in candidates:
        header = source_headers.get(candidate["source_uuid"])
        reasons = []
        if "source:" + str(candidate["source_id"]) in reserved_refs:
            reasons.append("reserved_current_source_id")
        if candidate["source_uuid"] in reserved_uuids:
            reasons.append("reserved_source_uuid_or_parent")
        if header is not None:
            if header["evidence_id"] in excluded:
                reasons.append("excluded_original_evidence")
            if header["known_recording_family"] in reserved_families:
                reasons.append("reserved_original_known_family")
            if header["input_duplicate_family"] in reserved_groups:
                reasons.append("reserved_original_duplicate_family")
        if reasons:
            state = "reserved_or_excluded"
        elif header is None:
            state = "absent_annotation_source_frame"
        elif header["artist_partition"] != "development" or header["family_partition"] != "development":
            state = "original_heldout_partition"
        elif header["artist_uuid"] != candidate["artist_uuid"]:
            state = "known_tuple_drift"
        elif header["known_recording_family"] is None:
            state = "missing_original_family_context"
        else:
            family = re.fullmatch(r"artist:(\d+):show:(\d+)", header["known_recording_family"])
            original_artist, original_show = map(int, family.groups())
            if candidate["show_id"] is None or candidate["show_uuid"] is None:
                state = "missing_current_show_context"
            elif (original_artist, original_show) != (candidate["artist_id"], candidate["show_id"]):
                state = "known_tuple_drift"
            else:
                state = "frozen_development_pending_raw_id_verification"
        results.append({
            **candidate, "classification": state, "reservation_reasons": reasons,
            "original_metadata": header,
            "original_numeric_source_id_in_prefix": False,
            "original_numeric_source_id_verified": False,
            "raw_payload_decoded": False,
            "expanded_raw_capture_authorized": False,
        })
    require(len(results) == len(candidates), "Lost a discovered source classification")
    return results


def load_discovery(inputs, discovery, preparation, prepared):
    query = json.loads(inputs.read(discovery / "query.json"))
    executed = inputs.read(discovery / "executed.sql")
    require(executed == inputs.read(preparation / "metadata-discovery.sql"),
            "Discovery executed a different query")
    require(hashlib.sha256(executed).hexdigest() == query["sql_sha256"], "Discovery query hash differs")
    require(query["read_only"] is True and query["database"] == "app"
            and query["endpoint"] == "relisten-db-ro.tail09dbf.ts.net:5432",
            "Unexpected discovery endpoint or mode")
    summary = json.loads(inputs.read(discovery / "summary.json"))
    snapshot = discovery / "snapshot.jsonl"
    require(snapshot.stat().st_size <= MAX_DISCOVERY_BYTES, "Discovery metadata exceeds the byte bound")
    raw = inputs.read(snapshot)
    require(len(raw) <= MAX_DISCOVERY_BYTES, "Discovery metadata exceeds the byte bound")
    require(hashlib.sha256(raw).hexdigest() == summary["snapshot_sha256"], "Discovery snapshot changed")
    rows = [json.loads(line) for line in raw.split(b"\n") if line]
    require([row["kind"] for row in rows] == [
        "snapshot", "venue_association_metadata", "snapshot_complete"
    ], "Incomplete discovery capture")
    require(rows[0]["read_only"] == "on" and rows[0]["in_recovery"] is True,
            "Discovery was not a read-only replica capture")
    require(rows[1]["requested_venue_count"] == rows[1]["authenticated_venue_count"] == 67,
            "Discovery venue authentication is incomplete")
    require(len(rows[1]["associations"]) == summary["association_rows"] <= 100000,
            "Discovery row count differs or exceeds the bound")
    venues = json.loads(inputs.read(preparation / "venue-allowlist.json"))
    require(bytes_fingerprint(inputs.read(preparation / "venue-allowlist.json"))
            == prepared["files"]["venue-allowlist.json"], "Prepared venue allowlist changed")
    return rows[1]["associations"], venues


def project(repo, preparation, discovery, output):
    repo = Path(repo).resolve()
    preparation, discovery, output = (Path(path).resolve() for path in (preparation, discovery, output))
    require(output.is_relative_to(repo / "local-dev/normalization-data"), "Output must remain ignored")
    require(not output.exists(), "Output already exists")
    inputs = PreparationInputs()
    prepared = json.loads(inputs.read(preparation / "receipt.json"))
    require(prepared["schema_version"] == "venue-context-preparation-v1"
            and prepared["state"] == "completed", "Incomplete venue preparation")
    for item in prepared["inputs"]:
        path = Path(item["path"])
        require(path.is_relative_to(repo), "Preparation references another checkout")
        expected = {key: item[key] for key in ("bytes", "sha256")}
        inputs.stamp(path)
        require(inputs.stamps[path] == expected, "Changed prepared input: " + path.name)
    for name, expected in prepared["files"].items():
        require(Path(name).name == name, "Unsafe preparation artifact name")
        path = preparation / name
        inputs.stamp(path)
        require(inputs.stamps[path] == expected, "Changed preparation artifact")

    bundle = repo / RESEARCH / "training/venues-development-v1"
    frozen = json.loads(inputs.read(bundle / "manifest.json"))
    annotation = repo / ANNOTATION
    annotation_bytes = inputs.read(annotation / "manifest.json")
    require(hashlib.sha256(annotation_bytes).hexdigest() == frozen["source_annotation_manifest_sha256"],
            "Wrong frozen annotation manifest")
    frame_manifest = json.loads(annotation_bytes)
    require(frame_manifest["builder_version"] == "blind-annotation-v1"
            and frame_manifest["state"] == "complete_unlabeled", "Unsupported annotation envelope")
    expected_frame = {key: frame_manifest["files"]["evidence.jsonl"][key]
                      for key in ("bytes", "sha256")}
    require(expected_frame["sha256"] == frozen["source_files"]["evidence.jsonl"]["sha256"],
            "Annotation frame authority differs")
    reservation = json.loads(inputs.read(preparation / "reservation-index.json"))
    require(reservation["annotation_evidence_sha256"] == expected_frame["sha256"],
            "Reservation frame differs")
    associations, venues = load_discovery(inputs, discovery, preparation, prepared)
    candidates = source_candidates(associations, venues)
    inputs.stamp(Path(__file__).resolve())
    inputs.stamp(Path(__file__).with_name("prepare_venue_context.py").resolve())
    frame = annotation / "evidence.jsonl"
    inputs.stamp(frame)
    require(inputs.stamps[frame] == expected_frame, "Changed frozen annotation frame")
    headers = project_frame(frame, expected_frame)
    classifications = classify_sources(candidates, headers, reservation)
    files = {
        "annotation-metadata.jsonl": b"".join(
            (json.dumps(item, sort_keys=True) + "\n").encode() for item in headers
        ),
        "source-classifications.jsonl": b"".join(
            (json.dumps(item, sort_keys=True) + "\n").encode() for item in classifications
        ),
    }
    receipt = {
        "schema_version": "venue-annotation-metadata-projection-v1", "state": "completed",
        "usage": "development_only", "raw_payloads_decoded": 0, "new_partitions_assigned": 0,
        "expanded_raw_capture_authorized": False,
        "counts": {
            "annotation_headers": len(headers), "source_headers": FRAME_KINDS["source_sample"],
            "discovered_sources": len(candidates),
            "source_classifications": dict(Counter(item["classification"] for item in classifications)),
            "show_only_associations_not_independently_classified": sum(
                row["source_uuid"] is None for row in associations
            ),
        },
        "limits": [
            "The adapter stops before every raw_record value; metadata projection is not source annotation or original numeric-ID verification.",
            "Original artist/family partitions and transitive duplicate groups are preserved. No current-resource hash creates development eligibility.",
            "Absence from the 6000-source annotation frame does not establish absence from the 32593-source snapshot or nested source_context records.",
            "Pending development candidates need a separately authorized selected-row original-ID check before any expanded raw query.",
            "No venue labels, baseline, current query, canonical identity or acceptance policy was changed.",
        ],
    }
    return publish_preparation(output, files, inputs, receipt)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    parser.add_argument("--preparation", type=Path, required=True)
    parser.add_argument("--discovery", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    receipt = project(args.repo_root, args.preparation, args.discovery, args.output)
    print(json.dumps({"state": receipt["state"], "counts": receipt["counts"]}, sort_keys=True))


if __name__ == "__main__":
    main()
