"""Validate frozen people-review files and separate adjudication, without accepting identities.

The packet manifest is the trust anchor. Its version-pinned source tool supplies
file authentication and exact raw-field span checks; this tool does not reopen
the source population or repeat reservation selection.
"""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import types


VERSION = "people-identity-review-validation:3"
LOCATOR_ADAPTER_VERSION = "people-review-citation-locators:1"
PACKET_ADAPTER_VERSION = "people-review-packet-formats:1"
PRIMARY_ID = "primary:sonicarchives-self-introduction"
PRIMARY_TEXT = "primary/sonicarchives-visible-text.txt"
CITATION_FORMATS = ("canonical_raw_field", "wrapper_dot_raw_field", "primary_visible_text")
PACKET_VERSION = "people-identity-development-packet:1"
CONTROLS_VERSION = "people-identity-distinctness-controls:1"
CONTROL_QUESTION_KINDS = {"local_co_credit_distinctness", "cross_source_linkage", "cross_source_contrast"}
MAXIMUM_BYTES = 32 * 1024 * 1024
MAXIMUM_ROWS = 10000
DECISIONS = ("same_entity", "different_entities", "insufficient_evidence")
FIRST_KEYS = {"question_id", "decision", "reason", "evidence", "contradiction_basis", "reviewer"}
ADJUDICATION_KEYS = {"question_id", "decision", "reason", "evidence", "contradiction_basis",
                     "adjudicator", "first_answer_row_sha256", "reviewer_disagreement"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_bounded(path):
    with Path(path).open("rb") as stream:
        data = stream.read(MAXIMUM_BYTES + 1)
    require(len(data) <= MAXIMUM_BYTES, f"File exceeds review bound: {Path(path).name}")
    return data


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "Duplicate JSON object property")
        value[key] = item
    return value


def decode_json(data):
    return json.loads(data, object_pairs_hook=unique_object)


def physical_rows(data):
    """Hash original row bytes, excluding one LF or CRLF terminator only."""
    lines = data.split(b"\n")
    if lines[-1] == b"":
        lines.pop()
    require(0 < len(lines) <= MAXIMUM_ROWS, "Review must contain a bounded nonempty row set")
    result = []
    for index, line in enumerate(lines):
        # A CR is a terminator only when this record was followed by LF.
        followed_by_lf = index < len(lines) - 1 or data.endswith(b"\n")
        payload = line[:-1] if followed_by_lf and line.endswith(b"\r") else line
        require(bool(payload.strip()), "Blank physical review row")
        result.append((decode_json(payload), digest(payload)))
    return result


def load_packet_tool(path, expected_hash):
    path = Path(path).resolve()
    data = read_bounded(path)
    require(digest(data) == expected_hash, "Packet tool source hash changed")
    name = "pinned_people_identity_packet"
    module = types.ModuleType(name)
    module.__file__ = str(path)
    sys.modules[name] = module
    # Execute the authenticated bytes themselves. A timestamp-valid .pyc must not
    # replace the code whose source hash this run records.
    exec(compile(data, str(path), "exec"), module.__dict__)
    require(module.VERSION == PACKET_VERSION, "Unsupported packet tool version")
    require(module.file_record(path)["sha256"] == expected_hash, "Packet tool changed during import")
    return module


def pin(tool, path, expected_hash=None):
    path = Path(path).resolve()
    record = tool.file_record(path)
    if expected_hash is not None:
        require(record["sha256"] == expected_hash, f"Input digest changed: {path.name}")
    return record


def recheck(tool, records):
    for record in records:
        current = tool.file_record(Path(record["path"]))
        require(current == {key: record[key] for key in ("path", "bytes", "sha256")},
                f"Input changed: {Path(record['path']).name}")


def load_packet(tool, packet, expected_manifest_hash):
    packet = Path(packet).resolve()
    manifest_path = packet / "manifest.json"
    pins = [pin(tool, manifest_path, expected_manifest_hash)]
    manifest = decode_json(read_bounded(manifest_path))
    schema = manifest["schema_version"]
    if schema == PACKET_VERSION:
        require(manifest["state"] == "frozen_unlabeled_development_packet", "Unsupported development packet state")
        question_count, source_count = manifest["counts"]["pair_questions"], manifest["counts"]["recordings"]
    elif schema == CONTROLS_VERSION:
        require(manifest["state"] == "frozen_unlabeled_development_controls", "Unsupported distinctness-controls state")
        question_count, source_count = manifest["counts"]["questions"], manifest["counts"]["complete_source_contexts"]
    else:
        raise ValueError("Unsupported review packet schema version")
    require(manifest["exposure"]["partition"] == "development"
            and manifest["exposure"]["eligible_for_release_evaluation"] is False
            and manifest["exposure"]["policy_activation_permitted"] is False,
            "Packet must remain an unlabeled development packet")
    artifacts = {record["path"]: record for record in manifest["artifacts"]}
    require(len(artifacts) == len(manifest["artifacts"])
            and {"questions.jsonl", "evidence.jsonl", "REVIEW.md"} <= artifacts.keys(),
            "Packet lacks unique source/question artifacts")
    # Consume only the source/question contract. Operator provenance and prior
    # discovery material are outside this validator's reading responsibilities.
    consumed = ["questions.jsonl", "evidence.jsonl", "REVIEW.md"]
    primary_names = {PRIMARY_TEXT, "primary/excerpts.json"}
    require(not primary_names.intersection(artifacts) or primary_names <= artifacts.keys(),
            "Primary source needs both retained text and excerpt metadata")
    if primary_names <= artifacts.keys():
        consumed.extend(sorted(primary_names))
    for name in consumed:
        record = artifacts[name]
        target = (packet / record["path"]).resolve()
        require(target.is_relative_to(packet), "Packet artifact escapes its directory")
        tool.authenticate(packet, record)
        pins.append(pin(tool, target, record["sha256"]))
    question_rows = physical_rows(read_bounded(packet / "questions.jsonl"))
    evidence_rows = physical_rows(read_bounded(packet / "evidence.jsonl"))
    questions = {}
    for row, _ in question_rows:
        require(row["question_id"] not in questions and row["answer"] is None
                and row["adjudication"] is None, "Packet question changed or duplicated")
        questions[row["question_id"]] = row
    originals = {}
    for wrapper, _ in evidence_rows:
        key = wrapper["original_record"]["evidence_id"]
        require(key not in originals, "Duplicate packet source")
        originals[key] = wrapper
    require(len(questions) == question_count and len(originals) == source_count,
            "Packet coverage differs from its manifest")
    for question in questions.values():
        require({question["left"]["evidence_id"], question["right"]["evidence_id"]} <= originals.keys(),
                "Question source is absent from packet")
        if schema == CONTROLS_VERSION:
            validate_control_question(tool, question, originals)
    primary = None
    if PRIMARY_TEXT in consumed:
        data = read_bounded(packet / PRIMARY_TEXT)
        text = data.decode("utf-8")
        excerpts = decode_json(read_bounded(packet / "primary/excerpts.json"))
        require(excerpts["source_id"] == PRIMARY_ID and bool(excerpts["excerpts"]),
                "Unsupported primary source identity or missing excerpts")
        for excerpt in excerpts["excerpts"]:
            quote = excerpt["quote"]
            require(type(quote) is str and bool(quote) and quote in text
                    and digest(quote.encode("utf-8")) == excerpt["sha256"],
                    "Primary excerpt differs from the retained text")
        primary = {"text": text, "field_sha256": digest(data)}
    recheck(tool, pins)
    return questions, originals, pins, manifest, primary


def validate_control_question(tool, question, originals):
    """Check control coordinates without treating another answer as a premise."""
    kind = question["question_kind"]
    require(kind in CONTROL_QUESTION_KINDS, "Unsupported distinctness-control question kind")
    left, right = question["left"], question["right"]
    same_source = left["evidence_id"] == right["evidence_id"]
    require(same_source == (kind == "local_co_credit_distinctness"),
            "Local/cross-source control coordinates do not match the question kind")
    for side in (left, right):
        anchor = side["reported_actor_anchor"]
        require(anchor["evidence_id"] == side["evidence_id"]
                and side["actor_kind"] is None and side["actor_identity"] is None,
                "Control anchor must retain its original source and unknown identity")
        tool.verify_span(originals, anchor, "control actor anchor")
    if same_source:
        first, second = left["reported_actor_anchor"], right["reported_actor_anchor"]
        require((first["field_path"], first["span"]["start"], first["span"]["end"])
                != (second["field_path"], second["span"]["start"], second["span"]["end"]),
                "Local control must retain two distinct actor-anchor coordinates")


class CitationVerifier:
    """Interpret documented locators without changing frozen review bytes or IDs."""

    def __init__(self, tool, originals, primary, packet_schema=PACKET_VERSION):
        self.tool = tool
        self.originals = originals
        self.primary = primary
        self.packet_schema = packet_schema

    def verify(self, citation):
        evidence_id, locator = citation["evidence_id"], citation["field_path"]
        require(type(locator) is str, "Citation locator must be text")
        if evidence_id in self.originals:
            return self.verify_raw(citation, locator)
        require(evidence_id == PRIMARY_ID and locator == PRIMARY_TEXT and self.primary is not None,
                "Unknown source identity or unsupported primary locator")
        text, span = self.primary["text"], citation["span"]
        require(citation["field_sha256"] == self.primary["field_sha256"],
                "Primary field hash must cover the complete retained UTF-8 file bytes")
        start, end = span["start"], span["end"]
        require(type(start) is int and type(end) is int and 0 <= start < end <= len(text)
                and span["unit"] == "unicode_code_points" and text[start:end] == span["quote"],
                "Primary citation differs from exact retained text/code-point bounds")
        return "primary_visible_text"

    def verify_raw(self, citation, locator):
        pointer, wrapper = "/raw_record/row/", "original_record.raw_record.row."
        if locator.startswith(pointer):
            field, form = locator[len(pointer):], "canonical_raw_field"
        else:
            require(locator.startswith(wrapper), "Unsupported raw-field locator")
            field, form = locator[len(wrapper):], "wrapper_dot_raw_field"
        row = self.originals[citation["evidence_id"]]["original_record"]["raw_record"]["row"]
        require(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", field) is not None and field in row,
                "Raw locator must name one known source field")
        # Project a canonical locator for the existing exact-field checker only.
        # The original citation and physical first-answer row remain immutable.
        self.tool.verify_span(self.originals, {**citation, "field_path": pointer + field}, "review citation")
        return form


def validate_metadata(value, role):
    required = {"id", "kind", "prior_exposure"} | ({"answer_blind"} if role == "reviewer" else set())
    require(type(value) is dict and set(value) == required, f"Incomplete {role} metadata")
    require(type(value["id"]) is str and bool(value["id"].strip())
            and value["kind"] in ("human", "agent")
            and type(value["prior_exposure"]) is list
            and all(type(item) is str and bool(item.strip()) for item in value["prior_exposure"]),
            f"Invalid {role} identity or exposure disclosure")
    if role == "reviewer":
        require(value["answer_blind"] is True, "First answer must disclose answer-blind review")


def validate_answer(row, question, citations):
    require(row["decision"] in DECISIONS and type(row["reason"]) is str and bool(row["reason"].strip()),
            "Answer needs a permitted decision and source-grounded reason")
    evidence = row["evidence"]
    require(type(evidence) is list, "Answer evidence must be a list")
    cited = set()
    formats = Counter()
    for citation in evidence:
        require(type(citation) is dict
                and set(citation) == {"evidence_id", "field_path", "field_sha256", "span"}
                and type(citation["span"]) is dict
                and set(citation["span"]) == {"start", "end", "quote", "unit"}, "Incomplete raw citation")
        formats[citations.verify(citation)] += 1
        cited.add(citation["evidence_id"])
    if row["decision"] != "insufficient_evidence":
        require({question["left"]["evidence_id"], question["right"]["evidence_id"]} <= cited,
                "Resolved answer needs exact support from both question sources")
        if citations.packet_schema == CONTROLS_VERSION and question["question_kind"] == "local_co_credit_distinctness":
            for side in (question["left"], question["right"]):
                anchor = side["reported_actor_anchor"]
                pointer = anchor["field_path"]
                locators = (pointer, pointer.replace("/raw_record/row/", "original_record.raw_record.row.", 1))
                require(any(value["evidence_id"] == anchor["evidence_id"]
                            and value["field_path"] in locators
                            and value["field_sha256"] == anchor["field_sha256"]
                            and value["span"]["start"] <= anchor["span"]["start"]
                            and value["span"]["end"] >= anchor["span"]["end"] for value in evidence),
                        "Resolved local control needs cited coverage of both actor anchors")
    basis = row["contradiction_basis"]
    if row["decision"] == "different_entities":
        require(type(basis) is str and bool(basis.strip()),
                "Different-entity answer needs a cited contradiction/distinctness reason")
    else:
        require(basis is None, "Only a different-entity answer carries a contradiction basis")
    return formats


def load_review(tool, path, expected_hash, questions, citations, role="reviewer", first_reviews=None):
    record = pin(tool, path, expected_hash)
    rows = physical_rows(read_bounded(path))
    expected_keys = FIRST_KEYS if role == "reviewer" else ADJUDICATION_KEYS
    answers, row_hashes, identities, exposure = {}, {}, set(), set()
    formats = Counter()
    for row, row_hash in rows:
        require(type(row) is dict and set(row) == expected_keys, f"Unexpected {role} row schema")
        key = row["question_id"]
        require(key in questions and key not in answers, "Unexpected or duplicate question answer")
        validate_metadata(row[role], role)
        formats.update(validate_answer(row, questions[key], citations))
        identity = (row[role]["id"], row[role]["kind"])
        identities.add(identity)
        exposure.update(row[role]["prior_exposure"])
        if role == "adjudicator":
            a, b = first_reviews
            links = row["first_answer_row_sha256"]
            require(type(links) is dict and set(links) == {"reader_a", "reader_b"}
                    and links["reader_a"] == a["row_hashes"][key]
                    and links["reader_b"] == b["row_hashes"][key], "Adjudication does not link original first-answer row bytes")
            disagreement = a["answers"][key]["decision"] != b["answers"][key]["decision"]
            require(type(row["reviewer_disagreement"]) is bool
                    and row["reviewer_disagreement"] == disagreement,
                    "Reviewer disagreement must reflect the two original decisions")
        answers[key], row_hashes[key] = row, row_hash
    require(answers.keys() == questions.keys(), "Review does not cover every packet question")
    require(len(identities) == 1, "One review file must identify one reviewer/adjudicator")
    identifier, kind = next(iter(identities))
    recheck(tool, [record])
    return {"record": record, "answers": answers, "row_hashes": row_hashes,
            "citation_formats": {key: formats[key] for key in CITATION_FORMATS},
            "identity": {"id": identifier, "kind": kind, "prior_exposure": sorted(exposure)}}


def class_counts(answers):
    counts = Counter(row["decision"] for row in answers.values())
    return {key: counts[key] for key in DECISIONS}


def summarize(a, b, adjudication=None, packet_schema=PACKET_VERSION):
    agreed = {key for key in a["answers"] if a["answers"][key]["decision"] == b["answers"][key]["decision"]}
    disagreed = a["answers"].keys() - agreed
    summary = {"questions": len(a["answers"]), "packet_schema_version": packet_schema, "first_reviews": {
        "reader_a": {"reviewer": a["identity"], "class_counts": class_counts(a["answers"]),
                     "citation_formats": a["citation_formats"],
                     "exact_citations_checked": sum(len(row["evidence"]) for row in a["answers"].values())},
        "reader_b": {"reviewer": b["identity"], "class_counts": class_counts(b["answers"]),
                     "citation_formats": b["citation_formats"],
                     "exact_citations_checked": sum(len(row["evidence"]) for row in b["answers"].values())},
    }, "comparison": {"agreements": len(agreed), "disagreements": len(disagreed)}, "adjudication": None}
    if adjudication is not None:
        answers = adjudication["answers"]
        changed_agreed = sum(answers[key]["decision"] != a["answers"][key]["decision"] for key in agreed)
        outcomes = Counter("followed_reader_a" if answers[key]["decision"] == a["answers"][key]["decision"]
                           else "followed_reader_b" if answers[key]["decision"] == b["answers"][key]["decision"]
                           else "neither_first_decision" for key in disagreed)
        summary["adjudication"] = {
            "adjudicator": adjudication["identity"], "class_counts": class_counts(answers),
            "citation_formats": adjudication["citation_formats"],
            "exact_citations_checked": sum(len(row["evidence"]) for row in answers.values()),
            "agreements_retained": len(agreed) - changed_agreed, "agreements_changed": changed_agreed,
            "disagreement_outcomes": {key: outcomes[key] for key in
                                      ("followed_reader_a", "followed_reader_b", "neither_first_decision")},
            "changes_from_reader_a_on_disagreements": sum(answers[key]["decision"] != a["answers"][key]["decision"] for key in disagreed),
            "changes_from_reader_b_on_disagreements": sum(answers[key]["decision"] != b["answers"][key]["decision"] for key in disagreed),
        }
    return summary


def retain_inputs(tool, output, records):
    retained = []
    for record in records:
        data = read_bounded(record["path"])
        require(len(data) == record["bytes"] and digest(data) == record["sha256"], "Input changed before retention")
        relative = Path("inputs") / record["sha256"] / Path(record["path"]).name
        target = output / relative
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if not target.exists():
            with target.open("xb") as stream:
                stream.write(data)
            target.chmod(0o600)
        retained.append({**record, "retained_path": str(relative)})
    return retained


def publish_receipt(tool, output, summary, records):
    """Publish a receipt last, after full validation and original/copy rechecks."""
    output = Path(output).resolve()
    output.mkdir(mode=0o700, exist_ok=False)
    recheck(tool, records)
    retained = retain_inputs(tool, output, records)
    receipt = {
        "state": "validated_adjudication_files" if summary["adjudication"] is not None else "validated_first_answer_files",
        "version": VERSION, "summary": summary, "inputs": retained,
        "packet_schema_version": summary["packet_schema_version"],
        "packet_format_adapter_version": PACKET_ADAPTER_VERSION,
        "citation_locator_adapter": {"version": LOCATOR_ADAPTER_VERSION,
            "counts": {key: sum(review["citation_formats"][key] for review in summary["first_reviews"].values())
                       + (summary["adjudication"]["citation_formats"][key] if summary["adjudication"] else 0)
                       for key in CITATION_FORMATS},
            "primary_field_hash": "complete_retained_UTF8_bytes_including_final_newline"},
        "identity_accuracy_established": False, "semantic_truth_certified": False,
        "policy_activation_permitted": False, "accepted_data_writes": 0,
        "limits": [
            "The pinned packet is already exposed development evidence, not an untouched evaluation set.",
            "Citations establish exact source wording and required coverage, not semantic truth or historical identity.",
            "Reviewer role, exposure and answer-blind declarations are recorded self-reports; this tool does not authenticate people.",
            "Agreement and adjudication do not authorize canonical identities, merges, credits or policy activation.",
        ],
    }
    temporary = output / "receipt.partial.json"
    with temporary.open("xb") as stream:
        stream.write((json.dumps(receipt, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode())
    temporary.chmod(0o600)
    recheck(tool, records)
    for record in retained:
        current = tool.file_record(output / record["retained_path"])
        require(current["bytes"] == record["bytes"] and current["sha256"] == record["sha256"],
                "Retained input copy changed")
    os.replace(temporary, output / "receipt.json")
    return receipt


def validate_files(tool, packet, packet_hash, reader_a, reader_a_hash, reader_b, reader_b_hash,
                   adjudication=None, adjudication_hash=None):
    require((adjudication is None) == (adjudication_hash is None), "Adjudication path and SHA256 must be supplied together")
    packet = Path(packet).resolve()
    for path in (reader_a, reader_b, adjudication):
        if path is not None:
            require(not Path(path).resolve().is_relative_to(packet), "Review files must remain outside the frozen packet")
    questions, originals, pins, manifest, primary = load_packet(tool, packet, packet_hash)
    citations = CitationVerifier(tool, originals, primary, manifest["schema_version"])
    a = load_review(tool, reader_a, reader_a_hash, questions, citations)
    b = load_review(tool, reader_b, reader_b_hash, questions, citations)
    require(a["identity"]["id"] != b["identity"]["id"], "Independent first reviewers must have distinct IDs")
    require(a["record"]["path"] != b["record"]["path"], "First-answer files must be distinct")
    pins.extend([a["record"], b["record"]])
    result = None
    if adjudication is not None:
        result = load_review(tool, adjudication, adjudication_hash, questions, citations,
                             role="adjudicator", first_reviews=(a, b))
        require(result["identity"]["id"] not in {a["identity"]["id"], b["identity"]["id"]},
                "Adjudication needs a separate reviewer identity")
        pins.append(result["record"])
    recheck(tool, pins)
    return summarize(a, b, result, manifest["schema_version"]), pins


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packet", type=Path, required=True)
    parser.add_argument("--packet-manifest-sha256", required=True)
    parser.add_argument("--packet-tool", type=Path, required=True)
    parser.add_argument("--packet-tool-sha256", required=True)
    for name in ("reader-a", "reader-b"):
        parser.add_argument(f"--{name}", type=Path, required=True)
        parser.add_argument(f"--{name}-sha256", required=True)
    parser.add_argument("--adjudication", type=Path)
    parser.add_argument("--adjudication-sha256")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    require(not args.output.resolve().is_relative_to(args.packet.resolve()),
            "Output must remain outside the frozen packet")
    require((args.adjudication is None) == (args.adjudication_sha256 is None),
            "Adjudication path and SHA256 must be supplied together")
    tool = load_packet_tool(args.packet_tool, args.packet_tool_sha256)
    tool_pin, own_pin = pin(tool, args.packet_tool, args.packet_tool_sha256), pin(tool, Path(__file__))
    summary, pins = validate_files(tool, args.packet, args.packet_manifest_sha256,
                                   args.reader_a, args.reader_a_sha256, args.reader_b, args.reader_b_sha256,
                                   args.adjudication, args.adjudication_sha256)
    receipt = publish_receipt(tool, args.output, summary, [*pins, tool_pin, own_pin])
    print(json.dumps({"state": receipt["state"], "summary": summary,
                      "receipt": tool.file_record(args.output.resolve() / "receipt.json")}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, OSError) as error:
        raise SystemExit(f"Review validation failed without completion: {error}")
