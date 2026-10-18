#!/usr/bin/env python3
"""Prepare bounded production read-only queries from frozen development eligibility.

This command does not connect to a database. It authenticates the existing venue
bundle and reservation frame, then writes new allowlists and executable SQL.
"""

import argparse
from contextlib import redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import re
import types
import uuid


ROOT = Path(__file__).resolve().parents[5]
RESEARCH = Path("docs/research/2026-10-17-full-normalization")
ANNOTATION = Path("local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1")
METADATA_FIELDS = (
    "name", "location", "past_names", "latitude", "longitude", "upstream_identifier", "slug",
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fingerprint(path):
    hasher, size = hashlib.sha256(), 0
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
            size += len(block)
    return {"bytes": size, "sha256": hasher.hexdigest()}


def bytes_fingerprint(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


class PreparationInputs:
    """Pin the bytes actually parsed, plus whole-file stamps for excluded raw data."""

    def __init__(self):
        self.stamps = {}
        self.contents = {}

    def read(self, path):
        if path not in self.contents:
            data = path.read_bytes()
            observed = bytes_fingerprint(data)
            require(path not in self.stamps or self.stamps[path] == observed,
                    "Input changed before parsing")
            self.contents[path] = data
            self.stamps[path] = observed
        return self.contents[path]

    def stamp(self, path):
        if path not in self.stamps:
            self.stamps[path] = fingerprint(path)

    def require_unchanged(self):
        require(all(fingerprint(path) == saved for path, saved in self.stamps.items()),
                "Input changed during preparation")


def canonical_uuid(value):
    require(str(uuid.UUID(value)) == value, "Noncanonical UUID in frozen metadata")
    return value


def eligible_metadata(evidence, reservation):
    """Check frozen eligibility before any original annotation payload is read."""
    require(len(evidence) == 67, "Expected the unchanged 67-resource venue bundle")
    require(len({item["evidence_id"] for item in evidence}) == 67, "Duplicate venue evidence")
    require(len({item["record_uuid"] for item in evidence}) == 67, "Duplicate venue UUID")
    artists = {item["artist"]["uuid"] for item in evidence}
    require(len(artists) == 66, "Expected 66 frozen venue artists")
    reserved_venue_uuids = {
        item["uuid"] for item in reservation["metadata_matches"] if item["kind"] == "venue"
    }
    for item in evidence:
        require(item["artist_partition"] == item["family_partition"] == "development",
                "Venue is not strict development evidence")
        require(item["record_uuid"] not in reserved_venue_uuids, "Reserved venue UUID")
        require(item["resource"] == {
            "provider": "relisten_catalog_snapshot", "resource_type": "venue",
            "resource_uuid": item["record_uuid"],
        }, "Unexpected venue resource")
        canonical_uuid(item["record_uuid"])
        canonical_uuid(item["artist"]["uuid"])

    excluded = {item["evidence_id"] for item in reservation["excluded_annotation_metadata"]}
    reserved_refs = set(reservation["reserved_references"])
    reserved_families = set(reservation["reserved_known_families"])
    reserved_groups = set(reservation["reserved_annotation_family_groups"])
    selected = []
    seen = set()
    for item in reservation["eligible_source_metadata"]:
        require(item["evidence_id"] not in seen, "Duplicate source eligibility metadata")
        seen.add(item["evidence_id"])
        require(item["artist_partition"] == item["family_partition"] == "development",
                "Source eligibility contains a held-out partition")
        require(item["evidence_id"] not in excluded and item["reference"] not in reserved_refs
                and item["family"] not in reserved_families
                and item["family_group"] not in reserved_groups, "Reserved source is eligible")
        if item["artist_uuid"] in artists:
            selected.append(item)
    require(len(selected) == 51, "Strict source intersection changed; review before expanding")
    require(len({item["artist_uuid"] for item in selected}) == 44, "Source artist intersection changed")
    return sorted(selected, key=lambda item: item["evidence_id"])


def build_allowlists(evidence, original_rows, source_metadata, reservation):
    originals = {item["evidence_id"]: item for item in original_rows}
    require(len(originals) == len(original_rows) == 67, "Missing or duplicate original venue rows")
    require(originals.keys() == {item["evidence_id"] for item in evidence}, "Original venue selection differs")
    venues, artist_ids = [], {}
    for item in sorted(evidence, key=lambda row: row["evidence_id"]):
        original = originals[item["evidence_id"]]
        require(original["artist_partition"] == original["family_partition"] == "development",
                "Selected original venue is held out")
        row = original["raw_record"]["row"]
        require(original["raw_record"]["kind"] == "venue_sample", "Not a venue original")
        require(row["uuid"] == item["record_uuid"] and original["artist"] == item["artist"],
                "Original venue identity differs")
        require("venue:" + str(row["id"]) not in reservation["reserved_references"],
                "Reserved venue ID")
        artist_uuid = item["artist"]["uuid"]
        require(artist_ids.setdefault(artist_uuid, row["artist_id"]) == row["artist_id"],
                "Frozen artist UUID maps to different IDs")
        metadata = {field: row[field] for field in METADATA_FIELDS}
        require(len(json.dumps(metadata, ensure_ascii=False).encode()) <= 65536,
                "Original venue metadata exceeds the query bound")
        venues.append({
            "evidence_id": item["evidence_id"], "venue_id": row["id"], "venue_uuid": row["uuid"],
            "artist_id": row["artist_id"], "artist_uuid": artist_uuid, "expected_metadata": metadata,
        })
    require(len({item["venue_id"] for item in venues}) == 67, "Duplicate original venue ID")

    sources = []
    for item in source_metadata:
        family = re.fullmatch(r"artist:(\d+):show:(\d+)", item["family"])
        reference = re.fullmatch(r"source:(\d+)", item["reference"])
        require(family is not None and reference is not None, "Unsupported frozen source keys")
        artist_id, show_id = map(int, family.groups())
        require(artist_ids[item["artist_uuid"]] == artist_id, "Source artist ID/UUID mismatch")
        require(item["evidence_id"].startswith("source_sample:"), "Unexpected source evidence key")
        source_uuid = canonical_uuid(item["evidence_id"].removeprefix("source_sample:"))
        sources.append({
            "evidence_id": item["evidence_id"], "source_id": int(reference.group(1)),
            "source_uuid": source_uuid, "artist_id": artist_id, "artist_uuid": item["artist_uuid"],
            "expected_show_id": show_id, "family_group": item["family_group"],
        })
    require(len({item["source_id"] for item in sources}) == len(sources) == 51,
            "Missing or duplicate allowed source IDs")
    require(len({item["source_uuid"] for item in sources}) == 51, "Duplicate allowed source UUID")
    return venues, sources


def sql_literal(value):
    return "'" + json.dumps(value, ensure_ascii=False, sort_keys=True).replace("'", "''") + "'"


def render_queries(strict_template, discovery_template, venues, sources):
    strict = strict_template.replace("__VENUE_ALLOWLIST__", sql_literal(venues))
    strict = strict.replace("__SOURCE_ALLOWLIST__", sql_literal(sources))
    # The discovery query's parameters contain identity keys only, too. Do not
    # copy expected names, locations or coordinates into this metadata stage.
    venue_keys = [{key: value for key, value in item.items() if key != "expected_metadata"}
                  for item in venues]
    discovery = discovery_template.replace("__VENUE_ALLOWLIST__", sql_literal(venue_keys))
    require("__VENUE_ALLOWLIST__" not in strict + discovery and "__SOURCE_ALLOWLIST__" not in strict,
            "Unexpanded SQL template parameter")
    return strict, discovery


def require_schema(schema):
    required = {
        "artists": {"id", "uuid"},
        "venues": set(METADATA_FIELDS) | {"id", "uuid", "artist_id", "created_at", "updated_at"},
        "shows": {"id", "uuid", "artist_id", "venue_id", "date", "display_date"},
        "sources": {"id", "uuid", "artist_id", "show_id", "venue_id", "display_date"},
    }
    for table, columns in required.items():
        actual = {row["column_name"] for row in schema
                  if row["table_schema"] == "public" and row["table_name"] == table}
        require(columns <= actual, "Retained production schema lacks required " + table + " fields")


def load_validator(path, source):
    module = types.ModuleType("venue_context_frozen_validator")
    module.__file__ = str(path)
    exec(compile(source, str(path), "exec"), module.__dict__)
    return module


def prepare(repo, output):
    repo, output = Path(repo).resolve(), Path(output).resolve()
    require(output.is_relative_to(repo / "local-dev/normalization-data"),
            "Output must remain under the repository's ignored normalization-data directory")
    require(not output.exists(), "Output already exists")
    bundle = repo / RESEARCH / "training/venues-development-v1"
    people = repo / RESEARCH / "training/people-development-v1"
    annotation = repo / ANNOTATION
    inputs = PreparationInputs()
    manifest_path = bundle / "manifest.json"
    manifest = json.loads(inputs.read(manifest_path))
    people_manifest = json.loads(inputs.read(people / "manifest.json"))
    reservation_path = repo / people_manifest["reservation_index"]["path"]
    reservation_bytes = inputs.read(reservation_path)
    require(bytes_fingerprint(reservation_bytes) == {
        key: people_manifest["reservation_index"][key] for key in ("bytes", "sha256")
    }, "Changed frozen reservation index")
    reservation = json.loads(reservation_bytes)
    require(reservation["annotation_evidence_sha256"] == manifest["source_files"]["evidence.jsonl"]["sha256"],
            "Reservation and venue annotation frames differ")

    input_paths = [manifest_path, people / "manifest.json", reservation_path,
                   annotation / "manifest.json", annotation / "evidence.jsonl", annotation / "tasks.jsonl",
                   bundle / "validate_labels.py", Path(__file__).resolve()]
    input_paths.extend(bundle / name for name in manifest["files"])
    supplement = repo / manifest["local_supplement"]
    input_paths.extend(supplement / name for name in ("blind-review-input.jsonl", "root-blind-review.jsonl"))
    sql_directory = repo / RESEARCH / "scale/sql"
    input_paths.extend(sql_directory / name for name in ("venue-context.sql", "venue-context-discovery.sql"))
    schema_path = repo / RESEARCH / "evidence/catalog-schema.json"
    input_paths.append(schema_path)
    for path in input_paths:
        inputs.stamp(path)
    require_schema(json.loads(inputs.read(schema_path)))

    evidence_bytes = inputs.read(bundle / "evidence.jsonl")
    require(bytes_fingerprint(evidence_bytes) == {
        key: manifest["files"]["evidence.jsonl"][key] for key in ("bytes", "sha256")
    }, "Changed frozen venue evidence")
    evidence = [json.loads(line) for line in evidence_bytes.split(b"\n") if line]
    selected_sources = eligible_metadata(evidence, reservation)
    validator = load_validator(bundle / "validate_labels.py", inputs.read(bundle / "validate_labels.py"))
    validation_output = io.StringIO()
    with redirect_stdout(validation_output):
        validator.validate_bundle(bundle, annotation, supplement)
    validation_receipt = json.loads(validation_output.getvalue())
    selected_rows = validator.read_selected_rows(
        annotation / "evidence.jsonl", evidence, "source_line_number", "source_line_sha256"
    )
    originals = [item for _, _, item in selected_rows]
    venues, sources = build_allowlists(evidence, originals, selected_sources, reservation)
    strict, discovery = render_queries(
        inputs.read(sql_directory / "venue-context.sql").decode(),
        inputs.read(sql_directory / "venue-context-discovery.sql").decode(), venues, sources,
    )

    files = {
        "venue-allowlist.json": (json.dumps(venues, ensure_ascii=False, indent=2) + "\n").encode(),
        "source-allowlist.json": (json.dumps(sources, indent=2) + "\n").encode(),
        "selected-venue-originals.jsonl": b"".join(line + b"\n" for _, line, _ in selected_rows),
        "selected-source-metadata.jsonl": b"".join(
            (json.dumps(item, sort_keys=True) + "\n").encode() for item in selected_sources
        ),
        "reservation-index.json": reservation_bytes,
        "strict-context.sql": strict.encode(),
        "metadata-discovery.sql": discovery.encode(),
    }
    receipt = {
        "schema_version": "venue-context-preparation-v1", "state": "completed",
        "usage": "development_only", "query_executed": False,
        "bundle_validation": validation_receipt,
        "counts": {
            "venue_resources": 67, "venue_artists": 66, "source_resources": 51,
            "source_artists": 44, "artists_without_eligible_source_context": 22,
        },
        "limits": [
            "Existing labels, reviews and original fields remain unchanged.",
            "Current associations and field changes require new captured evidence; preparation proves no venue identity.",
            "Source and show venue references remain separate. Catalog sort dates do not establish date precision or venue lifetime.",
            "Discovery captures IDs only. New resources outside the frozen frame have no development assignment.",
            "No database, provider, API, training or paid processing occurred.",
        ],
    }
    return publish_preparation(output, files, inputs, receipt)


def publish_preparation(output, files, inputs, receipt):
    """Own a fresh directory and publish only after the original read set still matches."""
    output.mkdir(parents=True, exist_ok=False)
    try:
        for name, data in files.items():
            with (output / name).open("xb") as stream:
                stream.write(data)
        result = {
            **receipt,
            "inputs": [{"path": str(path), **saved} for path, saved in inputs.stamps.items()],
            "files": {name: fingerprint(output / name) for name in files},
        }
        pending = output / "receipt.pending.json"
        with pending.open("x") as stream:
            json.dump(result, stream, indent=2, sort_keys=True)
            stream.write("\n")
        inputs.require_unchanged()
        os.link(pending, output / "receipt.json")
        return result
    except BaseException as failure:
        # A signal immediately after the atomic publication cannot turn an
        # already committed preparation into a contradictory failure receipt.
        published = output / "receipt.json"
        pending = output / "receipt.pending.json"
        if published.exists() and pending.exists() and published.samefile(pending):
            return result
        with (output / "failure.json").open("x") as stream:
            json.dump({"state": "failed", "error_type": type(failure).__name__}, stream)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare(args.repo_root, args.output)
    print(json.dumps({"state": result["state"], "counts": result["counts"]}, sort_keys=True))


if __name__ == "__main__":
    main()
