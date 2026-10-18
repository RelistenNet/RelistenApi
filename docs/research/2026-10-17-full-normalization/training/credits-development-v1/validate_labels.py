"""Check draft labels against retained evidence without creating or changing answers."""

import argparse
import collections
import hashlib
import json
from pathlib import Path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def records(path):
    # Iterating real JSONL lines preserves embedded U+2028/U+2029 characters.
    with path.open() as stream:
        return [json.loads(line) for line in stream]


def canonical(record):
    return json.dumps(
        record, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def check_span(span, text):
    start, end = span["start"], span["end"]
    require(0 <= start < end <= len(text), "Invalid Unicode codepoint span")
    require(text[start:end] == span["quote"], "Invalid Unicode codepoint span")


def validate(output, annotation_pack):
    receipt = json.loads((output / "receipt.json").read_text())
    labels = records(output / "labels.jsonl")
    evidence = {
        row["evidence_id"]: row
        for row in records(annotation_pack / "evidence.jsonl")
    }
    tasks = {
        row["task_id"]: row
        for row in records(annotation_pack / "tasks.jsonl")
    }
    for filename, key in [
        ("evidence.jsonl", "annotation_evidence_sha256"),
        ("tasks.jsonl", "annotation_tasks_sha256"),
    ]:
        require(digest((annotation_pack / filename).read_bytes()) == receipt[key], "Provenance hash mismatch")
    require(digest((output / "labels.jsonl").read_bytes()) == receipt["labels_sha256"], "Provenance hash mismatch")
    require(len(labels) == receipt["fields"] == len({x["label_id"] for x in labels}), "Invalid draft label or review contract")
    raw_path = Path(receipt["raw_fields_path"])
    require(digest(raw_path.read_bytes()) == receipt["raw_fields_sha256"], "Provenance hash mismatch")
    raw_rows = records(raw_path)
    raw_fields = {row["label_id"]: row for row in raw_rows}
    require(len(raw_fields) == len(raw_rows) == len(labels), "Training fields differ from retained evidence")
    require(raw_fields.keys() == {label["label_id"] for label in labels}, "Training fields differ from retained evidence")
    counts = collections.Counter()
    for label in labels:
        source = evidence[label["evidence_id"]]
        task = tasks[label["task_id"]]
        require(task["domain"] == "people", "Invalid draft label or review contract")
        require(task["task_kind"] == "evidence_extraction", "Invalid draft label or review contract")
        require(task["answer"] is None, "Invalid draft label or review contract")  # A field draft does not complete its task.
        require(task["evidence_ids"] == [label["evidence_id"]], "Invalid draft label or review contract")
        require(label["resource"] == source["resource"], "Invalid draft label or review contract")
        require(label["artist"] == source["artist"], "Invalid draft label or review contract")
        require(digest(canonical(source)) == label["evidence_sha256"], "Provenance hash mismatch")
        for key in ["family_partition", "artist_partition"]:
            require(label[key] == source[key] == task[key] == "development", "Held-out or mismatched partition")
        for key in ["known_recording_family", "input_duplicate_family"]:
            require(label[key] == source[key], "Invalid draft label or review contract")
        field = label["field_path"].removeprefix("/raw_record/row/")
        require(label["field_path"] == f"/raw_record/row/{field}", "Invalid draft label or review contract")
        text = source["raw_record"]["row"][field]
        require(digest(text.encode()) == label["field_sha256"], "Provenance hash mismatch")
        require(len(text) == label["field_codepoints"], "Invalid draft label or review contract")
        raw = raw_fields[label["label_id"]]
        require(raw["evidence_id"] == label["evidence_id"], "Training fields differ from retained evidence")
        require(raw["field_path"] == label["field_path"], "Training fields differ from retained evidence")
        require(raw["field_sha256"] == label["field_sha256"], "Training fields differ from retained evidence")
        require(raw["field_text"] == text, "Training fields differ from retained evidence")
        require(label["status"] == "draft_development", "Invalid draft label or review contract")
        require(label["scope"] == "complete_single_field", "Invalid draft label or review contract")
        answer = label["answer"]
        require(answer["exhaustive"] is True, "Invalid draft label or review contract")
        require(answer["unresolved"] == bool(answer["unknowns"]), "Invalid draft label or review contract")
        require(answer["adjudicator"] is None, "Invalid draft label or review contract")
        seen = set()
        for assertion in answer["assertions"]:
            check_span(assertion["name"], text)
            identity = (
                assertion["name"]["start"], assertion["name"]["end"], assertion["role"]
            )
            require(identity not in seen, "Invalid draft label or review contract")
            seen.add(identity)
            basis = assertion["role_basis"]
            if basis["kind"] == "prose":
                check_span(basis["span"], text)
            else:
                require(basis == {"kind": "structured_field", "field": field}, "Invalid draft label or review contract")
                require(field in ["taper", "transferrer"], "Invalid draft label or review contract")
            require(assertion["identity_resolution"] == "not_labeled", "Invalid draft label or review contract")
            counts["assertions"] += 1
        for unknown in answer["unknowns"]:
            check_span(unknown["span"], text)
        counts["fields"] += 1
        counts["unresolved_fields"] += answer["unresolved"]
        counts["exhaustive_empty_assertion_fields"] += not (
            answer["assertions"] or answer["unknowns"]
        )
    for key, value in counts.items():
        require(receipt[key] == value, "Receipt counts differ from labels")
    review_receipt = receipt["blinded_subset_review"]
    for filename, key in [
        (review_receipt["answers_file"], "answers_sha256"),
        (review_receipt["comparison_file"], "comparison_sha256"),
    ]:
        require(digest((output / filename).read_bytes()) == review_receipt[key], "Provenance hash mismatch")
    reviews = records(output / review_receipt["answers_file"])
    require(len(reviews) == review_receipt["fields"], "Invalid draft label or review contract")
    for review in reviews:
        text = raw_fields[review["label_id"]]["field_text"]
        require(review["coverage_complete"] is True, "Invalid draft label or review contract")
        for assertion in review["assertions"]:
            check_span(assertion["mention"], text)
            for cue in assertion["role_cues"]:
                if cue["source"] == "text":
                    check_span(cue, text)
        for unknown in review["unknowns"]:
            check_span(unknown, text)
            if "role_cue" in unknown:
                check_span(unknown["role_cue"], text)
    require(receipt["independent_review_complete"] is False, "Invalid draft label or review contract")
    require(receipt["policy_activation_permitted"] is False, "Invalid draft label or review contract")
    return {"validation": "passed", **counts}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("annotation_pack", type=Path)
    args = parser.parse_args()
    result = validate(Path(__file__).parent, args.annotation_pack)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
