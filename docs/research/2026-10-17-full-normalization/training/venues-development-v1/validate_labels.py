"""Verify this draft venue bundle against unchanged local annotation inputs.

Read JSONL by physical file iteration. Unicode line separators inside a JSON
string are content and must not split a record. This verifies provenance and
label structure; it does not decide whether a venue identity judgment is true.
"""

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path


BUNDLE = Path(__file__).resolve().parent
REPO = BUNDLE.parents[4]
RAW = REPO / "local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1"


def require(condition, message="Venue bundle contract violated"):
    """Keep evidence checks active when Python optimization is enabled."""
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def rows(path):
    with path.open("rb") as stream:
        for number, line in enumerate(stream, 1):
            yield number, line.rstrip(b"\r\n"), json.loads(line)


def validate_bundle(bundle, raw, local):
    manifest = json.loads((bundle / "manifest.json").read_text())
    for name, expected in manifest["files"].items():
        data = (bundle / name).read_bytes()
        require(digest(data) == expected["sha256"], f"Changed bundle file: {name}")
        require(len(data) == expected["bytes"], name)
        if "rows" in expected:
            require(sum(1 for _ in rows(bundle / name)) == expected["rows"], name)

    require(digest((raw / "manifest.json").read_bytes()) == manifest["source_annotation_manifest_sha256"])
    for name, expected in manifest["source_files"].items():
        require(digest((raw / name).read_bytes()) == expected["sha256"], f"Changed annotation file: {name}")
        require(sum(1 for _ in rows(raw / name)) == expected["physical_lines"], name)

    evidence = {item["evidence_id"]: item for _, _, item in rows(bundle / "evidence.jsonl")}
    labels = [item for _, _, item in rows(bundle / "labels.jsonl")]
    primary = {item["source_id"]: item for _, _, item in rows(bundle / "primary-sources.jsonl")}
    original_evidence = {}
    for number, line, item in rows(raw / "evidence.jsonl"):
        if item.get("artist_partition") != "development" or item.get("family_partition") != "development":
            continue
        if item["evidence_id"] in evidence:
            original_evidence[item["evidence_id"]] = (number, line, item)

    selected_ids = {item["task_id"] for item in labels}
    original_tasks = {}
    for number, line, item in rows(raw / "tasks.jsonl"):
        if item.get("family_partition") != "development":
            continue
        if item["task_id"] in selected_ids:
            original_tasks[item["task_id"]] = (number, line, item)

    for source in primary.values():
        require(source["source_kind"] == "venue_operator_primary")
        require(source["url"].startswith("https://"))
        require(sum(len(x["text"].split()) for x in source["excerpts"]) <= 25)
        for excerpt in source["excerpts"]:
            require(digest(excerpt["text"].encode()) == excerpt["text_sha256"])

    used_evidence = set()
    for label in labels:
        number, line, original = original_tasks[label["task_id"]]
        require(label["task_source_line_number"] == number)
        require(label["task_source_line_sha256"] == digest(line))
        require(original["answer"] is None)
        require(original["domain"] == "venues" and original["task_kind"] == "entity_link")
        require(original["family_partition"] == label["family_partition"] == "development")
        require(original["evidence_ids"] == label["evidence_ids"])
        require(original["candidate_reason"] == label["candidate_reason"])
        require(label["answer"]["decision"] in original["allowed_decisions"])
        require(label["answer"]["adjudicator"]["status"] == "pending")
        require(label["evaluation_eligible"] is False)
        require(set(label["answer"]["primary_source_ids"]) <= primary.keys())
        require({x["evidence_id"] for x in label["answer"]["evidence"]} == set(label["evidence_ids"]))
        if label["answer"]["decision"] != "insufficient_evidence":
            require(label["answer"]["primary_source_ids"])
        used_evidence.update(label["evidence_ids"])

    require(used_evidence == evidence.keys())
    for evidence_id, item in evidence.items():
        number, line, original = original_evidence[evidence_id]
        require(item["source_line_number"] == number)
        require(item["source_line_sha256"] == digest(line))
        require(item["artist_partition"] == item["family_partition"] == "development", f"Held-out evidence: {evidence_id}")
        require(item["artist"] == original["artist"])
        require(item["event_date"] is None)
        for field in item["fields"]:
            key = field["path"].removeprefix("/raw_record/row/")
            text = original["raw_record"]["row"][key]
            require(text == field["text"])
            require(digest(text.encode()) == field["text_sha256"])
            span = field["span"]
            require(span["unit"] == "unicode_code_points")
            require(type(span["start"]) is int and type(span["end"]) is int,
                    f"Non-integer field span: {evidence_id}/{key}")
            require(0 <= span["start"] <= span["end"] <= len(text),
                    f"Field span outside original text: {evidence_id}/{key}")
            require(text[span["start"]:span["end"]] == span["quote"],
                    f"Field span does not match original text: {evidence_id}/{key}")
        require(item["coordinates"] == {k: original["raw_record"]["row"].get(k) for k in ("latitude", "longitude")})

    counts = dict(Counter(item["answer"]["decision"] for item in labels))
    require(counts == manifest["decisions"])
    require(len(labels) == manifest["selection"]["selected_pairs"])
    require(len(evidence) == manifest["selection"]["selected_unique_resources"])
    require(len({item["artist"]["uuid"] for item in evidence.values()}) == manifest["selection"]["selected_unique_artists"])
    review = [item for _, _, item in rows(bundle / "blind-review.jsonl")]
    comparison = json.loads((bundle / "review-comparison.json").read_text())
    by_task = {item["task_id"]: item for item in labels}
    ranked = sorted(by_task, key=lambda task_id: digest(task_id.encode()))[:6]
    require([item["task_id"] for item in review] == ranked)
    require(len(review) == comparison["reviewed_pairs"] == 6)
    require(comparison["draft_labels_sha256"] == digest((bundle / "labels.jsonl").read_bytes()))
    require(comparison["blind_review_sha256"] == digest((bundle / "blind-review.jsonl").read_bytes()))
    require(comparison["blind_input_sha256"] == digest((local / "blind-review-input.jsonl").read_bytes()))
    require((bundle / "blind-review.jsonl").read_bytes() == (local / "root-blind-review.jsonl").read_bytes())
    require(comparison["all_labels_adjudicated"] is False and comparison["accuracy_estimated"] is False)
    require({item["task_id"] for item in comparison["comparisons"]} == set(ranked))
    comparison_by_task = {item["task_id"]: item for item in comparison["comparisons"]}
    agreements = 0
    for item in review:
        require(item["draft_answers_seen"] is False)
        require(item["historical_performance_verified"] is False)
        require(item["blind_input_sha256"] == comparison["blind_input_sha256"])
        saved = comparison_by_task[item["task_id"]]
        draft_decision = by_task[item["task_id"]]["answer"]["decision"]
        require(saved["draft_decision"] == draft_decision)
        require(saved["blind_decision"] == item["decision"])
        require(saved["decision_agreement"] == (draft_decision == item["decision"]))
        require(saved["adjudication_status"] == "pending" and saved["draft_changed"] is False)
        agreements += saved["decision_agreement"]
    require(agreements == comparison["decision_agreements"])
    require(len(review) - agreements == comparison["decision_disagreements"])
    require(dict(Counter(item["decision"] for item in review)) == comparison["review_decisions"])
    print(json.dumps({"validated_pairs": len(labels), "validated_resources": len(evidence),
                      "draft_decisions": counts, "blind_review_agreements": agreements,
                      "blind_review_disagreements": len(review) - agreements}, sort_keys=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, default=BUNDLE)
    parser.add_argument("--annotation", type=Path, default=RAW)
    parser.add_argument("--supplement", type=Path)
    options = parser.parse_args()
    if options.supplement is None:
        manifest = json.loads((options.bundle / "manifest.json").read_text())
        supplement = REPO / manifest["local_supplement"]
    else:
        supplement = options.supplement
    validate_bundle(options.bundle, options.annotation, supplement)


if __name__ == "__main__":
    main()
