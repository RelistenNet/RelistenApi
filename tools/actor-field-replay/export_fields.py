#!/usr/bin/env python3
"""Export the frozen actor-fact development fields for label-free runtime replay.

The primary bundle's validator owns eligibility and annotation authentication.
This tool then reads only its authenticated 207-source copy. Completion is an
atomic receipt publication after the source read set has been checked again;
failed or interrupted output is retained and must be retried in a new directory.
"""

import argparse
from collections import Counter
import hashlib
import io
import json
import os
from pathlib import Path
from types import ModuleType


TASK_COUNT = 543
SOURCE_COUNT = 207
SCHEMA_VERSION = "actor-field-replay-input-v1"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fingerprint(path):
    hasher = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
            size += len(block)
    return {"bytes": size, "sha256": hasher.hexdigest()}


def load_validator(path, source):
    # Execute the frozen local validator without writing bytecode beside it.
    module = ModuleType("actor_field_frozen_validator")
    module.__file__ = str(path)
    exec(compile(source, str(path), "exec"), module.__dict__)
    return module


class FrozenActorFields:
    """The validated source copy and fingerprints consumed by one export."""

    def __init__(self, bundle, repo_root, manifest, read_set):
        self.bundle = bundle
        self.repo_root = repo_root
        self.manifest = manifest
        self.read_set = read_set

    @classmethod
    def validate(cls, bundle, repo_root):
        manifest_path = bundle / "manifest.json"
        manifest_bytes = manifest_path.read_bytes()
        manifest = json.loads(manifest_bytes)
        validator_path = bundle / "validate_bundle.py"
        validator_bytes = validator_path.read_bytes()
        validator = load_validator(validator_path, validator_bytes)

        def bytes_receipt(data):
            return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}

        read_set = {
            manifest_path: bytes_receipt(manifest_bytes),
            validator_path: bytes_receipt(validator_bytes),
        }
        for name, receipt in manifest["files"].items():
            read_set[bundle / name] = {key: receipt[key] for key in ("bytes", "sha256")}
        external = [
            manifest[key]
            for key in (
                "original_sources", "private_values", "annotation_aid",
                "frozen_discovery_inventory", "frozen_discovery_receipt", "reservation_index",
            )
        ]
        external += manifest["contract_snapshots"]
        external += [
            manifest["independent_review"][key]
            for key in ("selection_receipt", "first_answers", "first_answer_receipt", "comparison_script")
        ]
        for receipt in external:
            read_set[repo_root / receipt["path"]] = {
                key: receipt[key] for key in ("bytes", "sha256")
            }
        selection_path = repo_root / manifest["independent_review"]["selection_receipt"]["path"]
        selection_bytes = selection_path.read_bytes()
        require(bytes_receipt(selection_bytes) == read_set[selection_path], "Blind selection receipt changed")
        packet = json.loads(selection_bytes)["input"]
        read_set[repo_root / packet["path"]] = {
            key: packet[key] for key in ("bytes", "sha256")
        }
        annotation = repo_root / validator.ANNOTATION
        annotation_receipt = fingerprint(annotation)
        require(
            annotation_receipt["sha256"] == manifest["source_annotation_evidence_sha256"],
            "Original annotation evidence changed",
        )
        read_set[annotation] = annotation_receipt

        frozen = cls(bundle, repo_root, manifest, read_set)
        frozen.verify_unchanged()
        counts = validator.validate_bundle(bundle, repo_root)
        require(
            counts["tasks"] == TASK_COUNT and counts["source_records"] == SOURCE_COUNT,
            "This exporter requires the frozen 543-field, 207-source development bundle",
        )
        frozen.verify_unchanged()
        return frozen

    def verify_unchanged(self):
        for path, expected in self.read_set.items():
            require(fingerprint(path) == expected, f"Frozen input changed: {path.name}")

    def read_rows(self, path):
        data = path.read_bytes()
        require(
            {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()} == self.read_set[path],
            f"Frozen input changed before decoding: {path.name}",
        )
        # BytesIO iterates physical LF records. str.splitlines() would also split
        # valid JSON strings containing literal U+2028 or U+2029 characters.
        return [json.loads(line) for line in io.BytesIO(data)]

    def field_rows(self):
        source_path = self.repo_root / self.manifest["original_sources"]["path"]
        originals = {}
        for wrapper in self.read_rows(source_path):
            record = wrapper["original_record"]
            key = record["evidence_id"]
            require(key not in originals, "Duplicate copied original source")
            originals[key] = record
        require(len(originals) == SOURCE_COUNT, "Copied original source denominator changed")

        fields = self.read_rows(self.bundle / "field-index.jsonl")
        require(len(fields) == TASK_COUNT, "Whole-field denominator changed")
        seen = set()
        seen_fields = set()
        for field in fields:
            key = field["task_id"]
            source_field = (field["evidence_id"], field["field"])
            require(key not in seen and source_field not in seen_fields, "Duplicate whole-field input")
            seen.add(key)
            seen_fields.add(source_field)
            require(field["evidence_id"] in originals, "Missing copied original for selected field")
            value = originals[field["evidence_id"]]["raw_record"]["row"][field["field"]]
            require(
                isinstance(value, str)
                and hashlib.sha256(value.encode("utf-8")).hexdigest() == field["field_sha256"]
                and len(value) == field["code_points"],
                f"Original field hash or code-point count changed: {key}",
            )
            yield {
                "task_id": key,
                "evidence_id": field["evidence_id"],
                "field": field["field"],
                "field_path": field["field_path"],
                "field_sha256": field["field_sha256"],
                "field_text": value,
            }

    def write_fields(self, path):
        kinds = Counter()
        with path.open("xb") as stream:
            for row in self.field_rows():
                stream.write((json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
                kinds[row["field"]] += 1
        require(sum(kinds.values()) == TASK_COUNT, "Exported field denominator changed")
        return dict(sorted(kinds.items()))

    def receipt(self, fields_path, kinds):
        manifest = self.manifest
        return {
            "schema_version": SCHEMA_VERSION,
            "usage": "development_only",
            "state": "completed",
            "task_count": TASK_COUNT,
            "field_kinds": kinds,
            "fields_file": {"name": fields_path.name, **fingerprint(fields_path)},
            "frozen_bundle": {
                "manifest": self.read_set[self.bundle / "manifest.json"],
                "field_index": self.read_set[self.bundle / "field-index.jsonl"],
            },
            "source_provenance": {
                "annotation_evidence_sha256": manifest["source_annotation_evidence_sha256"],
                "original_sources_sha256": manifest["original_sources"]["sha256"],
                "reservation_index_sha256": manifest["reservation_index"]["sha256"],
                "labels_sha256": manifest["files"]["labels.jsonl"]["sha256"],
                "citations_sha256": manifest["files"]["citations.jsonl"]["sha256"],
            },
        }


def publish_receipt(output, receipt):
    staging = output / "receipt.pending.json"
    with staging.open("xb") as stream:
        stream.write((json.dumps(receipt, indent=2) + "\n").encode("utf-8"))
    # A hard link publishes the complete receipt atomically and refuses to
    # replace any existing destination. Publication is the completion point.
    os.link(staging, output / "receipt.json")


def receipt_is_published(output):
    try:
        return (output / "receipt.json").samefile(output / "receipt.pending.json")
    except OSError:
        return False


def record_failure(output, error):
    try:
        with (output / "failure.json").open("x", encoding="utf-8") as stream:
            json.dump({"schema_version": SCHEMA_VERSION, "state": "failed", "error_type": type(error).__name__}, stream)
            stream.write("\n")
    except OSError:
        # Preserve the original failure, including cancellation or disk errors.
        pass


def export_fields(bundle, repo_root, output):
    """Validate the frozen primary bundle and export all fields to a new directory.

    bundle and repo_root must be absolute. A completed receipt authorizes only
    development replay. No label, contact interpretation or accepted fact is
    included in fields.jsonl. Existing or interrupted output is never reused.
    """
    bundle, repo_root, output = Path(bundle), Path(repo_root), Path(output).resolve()
    require(bundle.is_absolute() and repo_root.is_absolute(), "Bundle and primary repo root must be absolute")
    require(not output.exists(), "Output directory already exists; use a new directory")
    require(not output.is_relative_to(bundle.resolve()), "Output must be outside the frozen bundle")
    frozen = FrozenActorFields.validate(bundle, repo_root)
    output.mkdir(parents=True, exist_ok=False)
    receipt = None
    try:
        fields_path = output / "fields.jsonl"
        kinds = frozen.write_fields(fields_path)
        receipt = frozen.receipt(fields_path, kinds)
        frozen.verify_unchanged()
        publish_receipt(output, receipt)
    except BaseException as error:
        # The complete hard-linked receipt wins an interruption immediately
        # after publication. Do not describe the same run as completed/failed.
        if receipt is not None and receipt_is_published(output):
            return receipt
        record_failure(output, error)
        raise
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        receipt = export_fields(args.bundle, args.repo_root, args.output)
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(1, f"Export failed: {error}\n")
    print(json.dumps(receipt, sort_keys=True))


if __name__ == "__main__":
    main()
