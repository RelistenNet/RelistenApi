#!/usr/bin/env python3
"""Check normalization corpus integrity and report coverage, without scoring a model.

Evidence preserves imported values. Labels are curator-authored expectations, not
facts certified by this validator. Exact quotes establish traceability only.
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys


DOMAINS = {"venues", "songs", "compositions", "people", "lineage"}
TASKS = {"extract", "match", "relationship"}
SPLITS = {"development", "evaluation"}
STATUSES = {"supported", "different", "unresolved"}
ANNOTATIONS = {"development_labeled", "independently_reviewed"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def text(value):
    return isinstance(value, str) and bool(value.strip())


def read_rows(path):
    rows = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError as error:
            raise ValueError(f"{path.name}:{number}: invalid JSON: {error.msg}") from error
        require(isinstance(row, dict), f"{path.name}:{number}: expected an object")
        rows.append(row)
    require(rows, f"{path.name}: empty corpus")
    ids = [row.get("id") for row in rows]
    require(all(text(identity) for identity in ids), f"{path.name}: missing or invalid id")
    require(len(set(ids)) == len(ids), f"{path.name}: duplicate id")
    return rows


def validate_manifest(path, evidence_path, cases_path, evidence_count, case_count):
    manifest = json.loads(path.read_text(encoding="utf-8"))
    require(isinstance(manifest, dict), "manifest: expected an object")
    entries = manifest.get("files")
    require(isinstance(entries, list) and entries, "manifest: files must be a nonempty list")
    verified = set()
    for entry in entries:
        require(isinstance(entry, dict), "manifest: file entry must be an object")
        relative = entry.get("path")
        digest = entry.get("sha256")
        require(text(relative), "manifest: file path is missing")
        require(
            isinstance(digest, str) and len(digest) == 64
            and all(character in "0123456789abcdef" for character in digest),
            f"manifest: invalid SHA256 for {relative}",
        )
        target = (path.parent / relative).resolve()
        require(target not in verified, f"manifest: duplicate file {relative}")
        require(target.is_file(), f"manifest: missing file {relative}")
        require(
            hashlib.sha256(target.read_bytes()).hexdigest() == digest,
            f"manifest: SHA256 mismatch for {relative}",
        )
        verified.add(target)
    require(evidence_path.resolve() in verified, "manifest: evidence file is not covered")
    counts = manifest.get("record_counts", {})
    require(isinstance(counts, dict), "manifest: record_counts must be an object")
    for name, actual in (("evidence", evidence_count), ("cases", case_count)):
        if name in counts:
            require(
                type(counts[name]) is int and counts[name] == actual,
                f"manifest: {name} record count mismatch",
            )
    return cases_path.resolve() in verified


def validate_evidence(rows):
    for row in rows:
        label = f"evidence {row['id']}"
        for field in ("kind", "artist_name", "provider"):
            require(text(row.get(field)), f"{label}: missing {field}")
        for field in ("artist_id", "entity_id"):
            require(row.get(field) is not None, f"{label}: missing {field}")
        fields = row.get("fields")
        require(isinstance(fields, dict) and fields, f"{label}: fields must be a nonempty object")
    return {row["id"]: row for row in rows}


def input_reference(reference, evidence, label):
    require(isinstance(reference, dict), f"{label}: reference must be an object")
    evidence_id, field = reference.get("evidence_id"), reference.get("field")
    require(text(evidence_id) and text(field), f"{label}: invalid evidence_id or field")
    require(evidence_id in evidence, f"{label}: unknown evidence {evidence_id}")
    require(field in evidence[evidence_id]["fields"], f"{label}: absent field {evidence_id}.{field}")
    return evidence_id, field


def validate_cases(rows, evidence):
    group_splits, evidence_splits = {}, {}
    domains = set()
    used_evidence = set()
    for row in rows:
        label = f"case {row['id']}"
        for field, options in (
            ("domain", DOMAINS), ("task", TASKS), ("split", SPLITS),
            ("annotation_status", ANNOTATIONS),
        ):
            require(text(row.get(field)) and row[field] in options, f"{label}: invalid {field}")
        domains.add(row["domain"])
        require(text(row.get("group")), f"{label}: missing group")
        require(text(row.get("rationale")), f"{label}: missing rationale")
        expected = row.get("expected")
        require(isinstance(expected, dict), f"{label}: expected must be an object")
        require(
            text(expected.get("status")) and expected["status"] in STATUSES,
            f"{label}: invalid expected status",
        )
        if expected["status"] == "supported":
            assertions = expected.get("assertions")
            require(
                isinstance(assertions, (dict, list)) and bool(assertions),
                f"{label}: supported labels require nonempty assertions",
            )
        inputs, basis = row.get("inputs"), row.get("basis")
        require(isinstance(inputs, list) and inputs, f"{label}: inputs must be nonempty")
        require(isinstance(basis, list) and basis, f"{label}: basis must be nonempty")
        references = [input_reference(item, evidence, label) for item in inputs]
        require(len(set(references)) == len(references), f"{label}: duplicate input")
        for span in basis:
            reference = input_reference(span, evidence, label)
            require(reference in references, f"{label}: basis is not among inputs")
            value = evidence[reference[0]]["fields"][reference[1]]
            require(
                ("quote" in span) != ("value" in span),
                f"{label}: basis must contain exactly one of quote or value",
            )
            if "quote" in span:
                quote = span["quote"]
                require(text(quote), f"{label}: quote must be nonempty")
                require(
                    isinstance(value, str) and quote in value,
                    f"{label}: quote is not an exact substring of {reference[0]}.{reference[1]}",
                )
            else:
                # JSON comparison preserves false versus zero and null versus an
                # empty string, unlike Python's ordinary equality for booleans.
                require(
                    json.dumps(span["value"], sort_keys=True) == json.dumps(value, sort_keys=True),
                    f"{label}: value does not exactly match {reference[0]}.{reference[1]}",
                )
        # Keep related examples and every reuse of raw evidence in one split.
        prior = group_splits.setdefault(row["group"], row["split"])
        require(prior == row["split"], f"{label}: group crosses splits: {row['group']}")
        for evidence_id, _ in references:
            prior = evidence_splits.setdefault(evidence_id, row["split"])
            require(prior == row["split"], f"{label}: evidence crosses splits: {evidence_id}")
            used_evidence.add(evidence_id)
    require(domains == DOMAINS, f"missing domains: {', '.join(sorted(DOMAINS - domains))}")
    return used_evidence


def validate_corpus(evidence_path, cases_path, manifest_path=None):
    """Return integrity and coverage observations; never assess label correctness."""
    evidence_rows, cases = read_rows(evidence_path), read_rows(cases_path)
    evidence = validate_evidence(evidence_rows)
    used = validate_cases(cases, evidence)
    case_digest_verified = False
    if manifest_path:
        case_digest_verified = validate_manifest(
            manifest_path, evidence_path, cases_path, len(evidence_rows), len(cases)
        )
    artists = {(str(evidence[key]["artist_id"]), evidence[key]["artist_name"]) for key in used}
    report = {
        "validation": "corpus integrity only",
        "limitations": [
            "Quotes establish traceability, not the correctness of labels or identity links.",
            "Coverage is descriptive; no extraction or matching accuracy has been measured.",
            "Evaluation labels need independent review before they can support acceptance decisions.",
        ],
        "manifest_verified": manifest_path is not None,
        "case_digest_verified": case_digest_verified,
        "evidence_rows": len(evidence_rows),
        "used_evidence_rows": len(used),
        "cases": len(cases),
        "artists": [{"id": identity, "name": name} for identity, name in sorted(artists)],
        "providers": sorted({evidence[key]["provider"] for key in used}),
    }
    for field in ("domain", "task", "split", "annotation_status"):
        report[f"cases_by_{field}"] = dict(sorted(Counter(row[field] for row in cases).items()))
    report["cases_by_expected_status"] = dict(
        sorted(Counter(row["expected"]["status"] for row in cases).items())
    )
    if not case_digest_verified:
        report["limitations"].append("Case-file completeness is not verified by a manifest digest.")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--cases", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    try:
        report = validate_corpus(args.evidence, args.cases, args.manifest)
    except (ValueError, OSError) as error:
        print(f"Corpus validation failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
