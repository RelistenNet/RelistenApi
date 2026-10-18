"""Synthetic file/span/coverage contracts; no real reviews or data are read."""

from copy import deepcopy
import importlib.util
import json
import os
from pathlib import Path
import py_compile
import sys
import tempfile
import unittest
from unittest.mock import patch


SOURCE = Path(__file__).with_name("review_people_identity_packet.py")
SPEC = importlib.util.spec_from_file_location("people_review_validation", SOURCE)
VALIDATOR = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = VALIDATOR
SPEC.loader.exec_module(VALIDATOR)
ROOT = Path(os.environ["RELISTEN_NORMALIZATION_ROOT"]).resolve() if "RELISTEN_NORMALIZATION_ROOT" in os.environ else next(
    path for path in [Path.cwd(), *SOURCE.resolve().parents] if (path / "RelistenApi.sln").is_file())
PACKET_SOURCE = ROOT / "tools/RelistenNormalization/people-identity-packet/people_identity_packet.py"
TOOL = VALIDATOR.load_packet_tool(PACKET_SOURCE, VALIDATOR.digest(PACKET_SOURCE.read_bytes()))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n")


def write_rows(path, rows, ending=b"\n"):
    data = b"".join(json.dumps(row, ensure_ascii=False, sort_keys=True).encode() + ending for row in rows)
    path.write_bytes(data)
    return VALIDATOR.digest(data)


def citation(eid, quote="Élodie", start=2, end=8):
    text = "🎸 Élodie > name\u2028context"
    return {"evidence_id": eid, "field_path": "/raw_record/row/taper",
            "field_sha256": VALIDATOR.digest(text.encode()),
            "span": {"start": start, "end": end, "quote": quote, "unit": "unicode_code_points"}}


def answer(key, reviewer, decision="same_entity"):
    return {"question_id": key, "decision": decision, "reason": "Supported local source context is retained.",
            "evidence": [citation("left"), citation("right")],
            "contradiction_basis": "Both source-local credits distinguish the expressed actors." if decision == "different_entities" else None,
            "reviewer": {"id": reviewer, "kind": "agent", "answer_blind": True, "prior_exposure": []}}


def adjudicate(key, decision, a, b, row_a_hash, row_b_hash):
    row = answer(key, "unused", decision)
    del row["reviewer"]
    row.update(adjudicator={"id": "separate-adjudicator", "kind": "agent", "prior_exposure": ["Source-only development packet"]},
               first_answer_row_sha256={"reader_a": row_a_hash, "reader_b": row_b_hash},
               reviewer_disagreement=a["decision"] != b["decision"])
    return row


class PacketFixture:
    def __init__(self, root, questions=2):
        self.root = root.resolve()
        self.packet = self.root / "packet"
        self.packet.mkdir()
        self.questions = [{"question_id": f"q-{i}", "answer": None, "adjudication": None,
                           "left": {"evidence_id": "left"}, "right": {"evidence_id": "right"}}
                          for i in range(questions)]
        evidence = [{"original_record": {"evidence_id": key,
                                          "raw_record": {"row": {"taper": "🎸 Élodie > name\u2028context"}}}}
                    for key in ("left", "right", "extra-context")]
        write_rows(self.packet / "questions.jsonl", self.questions)
        write_rows(self.packet / "evidence.jsonl", evidence)
        (self.packet / "REVIEW.md").write_text("Synthetic source review contract.\n")
        manifest = {"schema_version": TOOL.VERSION, "state": "frozen_unlabeled_development_packet",
                    "exposure": {"partition": "development", "eligible_for_release_evaluation": False,
                                 "policy_activation_permitted": False},
                    "counts": {"pair_questions": questions, "recordings": 3},
                    "artifacts": [TOOL.file_record(self.packet / name, self.packet)
                                  for name in ("questions.jsonl", "evidence.jsonl", "REVIEW.md")]}
        write_json(self.packet / "manifest.json", manifest)
        self.packet_hash = VALIDATOR.digest((self.packet / "manifest.json").read_bytes())
        self.a_rows = [answer(row["question_id"], "reader-a") for row in self.questions]
        self.b_rows = [answer(row["question_id"], "reader-b", "insufficient_evidence" if i else "same_entity")
                       for i, row in enumerate(self.questions)]
        self.a, self.b = self.root / "reader-a.jsonl", self.root / "reader-b.jsonl"
        self.refresh()

    def refresh(self):
        self.a_hash = write_rows(self.a, self.a_rows)
        self.b_hash = write_rows(self.b, self.b_rows)

    def validate(self, adjudication=None, adjudication_hash=None):
        return VALIDATOR.validate_files(TOOL, self.packet, self.packet_hash, self.a, self.a_hash,
                                         self.b, self.b_hash, adjudication, adjudication_hash)

    def adjudication_file(self):
        original_a = VALIDATOR.physical_rows(self.a.read_bytes())
        original_b = VALIDATOR.physical_rows(self.b.read_bytes())
        rows = [adjudicate(a[0]["question_id"], "same_entity", a[0], b[0], a[1], b[1])
                for a, b in zip(original_a, original_b)]
        path = self.root / "adjudication.jsonl"
        return path, rows, write_rows(path, rows)

    def add_primary(self):
        text = "🎸 Hi, my name is J.Free.\n"
        quote = "Hi, my name is J.Free."
        primary = self.packet / "primary"
        primary.mkdir()
        (self.packet / VALIDATOR.PRIMARY_TEXT).write_bytes(text.encode("utf-8"))
        write_json(primary / "excerpts.json", {"source_id": VALIDATOR.PRIMARY_ID,
                   "excerpts": [{"quote": quote, "sha256": VALIDATOR.digest(quote.encode("utf-8"))}]})
        self.refresh_primary_manifest()
        return {"evidence_id": VALIDATOR.PRIMARY_ID, "field_path": VALIDATOR.PRIMARY_TEXT,
                "field_sha256": VALIDATOR.digest(text.encode("utf-8")),
                "span": {"start": 2, "end": len(text) - 1, "quote": quote, "unit": "unicode_code_points"}}

    def refresh_primary_manifest(self):
        path = self.packet / "manifest.json"
        manifest = json.loads(path.read_text())
        manifest["artifacts"] = [record for record in manifest["artifacts"]
                                 if not record["path"].startswith("primary/")]
        manifest["artifacts"].extend(TOOL.file_record(self.packet / name, self.packet)
                                     for name in (VALIDATOR.PRIMARY_TEXT, "primary/excerpts.json"))
        write_json(path, manifest)
        self.packet_hash = VALIDATOR.digest(path.read_bytes())

    def make_controls(self):
        if len(self.questions) != 6:
            raise ValueError("Synthetic control fixture needs six questions")
        evidence_path = self.packet / "evidence.jsonl"
        records = [row for row, _ in VALIDATOR.physical_rows(evidence_path.read_bytes())]
        extra = deepcopy(records[0])
        extra["original_record"]["evidence_id"] = "fourth-context"
        records.append(extra)
        write_rows(evidence_path, records)
        for index, question in enumerate(self.questions):
            kind = ("local_co_credit_distinctness", "cross_source_linkage", "cross_source_contrast")[index % 3]
            left, right = "left", "left" if index % 3 == 0 else "right"
            question.update(question_kind=kind)
            for side, eid, is_right in (("left", left, False), ("right", right, True)):
                anchor = citation(eid, "name", 11, 15) if is_right else citation(eid)
                question[side] = {"evidence_id": eid, "actor_kind": None, "actor_identity": None,
                                  "reported_actor_anchor": anchor}
            decision = "same_entity" if index % 3 == 1 else "different_entities"
            for rows, reviewer in ((self.a_rows, "reader-a"), (self.b_rows, "reader-b")):
                rows[index] = answer(question["question_id"], reviewer, decision)
                rows[index]["evidence"] = ([citation(left, "Élodie > name", 2, 15)] if left == right
                                            else [citation(left), citation(right)])
        write_rows(self.packet / "questions.jsonl", self.questions)
        path = self.packet / "manifest.json"
        manifest = json.loads(path.read_text())
        manifest.update(schema_version=VALIDATOR.CONTROLS_VERSION, state="frozen_unlabeled_development_controls",
                        counts={"questions": 6, "complete_source_contexts": 4})
        manifest["artifacts"] = [TOOL.file_record(self.packet / name, self.packet)
                                 for name in ("questions.jsonl", "evidence.jsonl", "REVIEW.md")]
        write_json(path, manifest)
        self.packet_hash = VALIDATOR.digest(path.read_bytes())
        self.refresh()


class TestPeopleReviewValidation(unittest.TestCase):
    def test_known_controls_schema_reuses_full_review_and_adjudication_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=6)
            fixture.make_controls()
            path, rows, _ = fixture.adjudication_file()
            for row, first in zip(rows, fixture.a_rows):
                row.update(decision=first["decision"], evidence=deepcopy(first["evidence"]),
                           contradiction_basis=first["contradiction_basis"])
            summary, pins = fixture.validate(path, write_rows(path, rows))
            self.assertEqual(summary["packet_schema_version"], VALIDATOR.CONTROLS_VERSION)
            self.assertEqual(summary["questions"], 6)
            self.assertEqual(summary["first_reviews"]["reader_a"]["class_counts"],
                             {"same_entity": 2, "different_entities": 4, "insufficient_evidence": 0})
            receipt = VALIDATOR.publish_receipt(TOOL, Path(directory) / "validated-controls", summary, pins)
            self.assertEqual(receipt["packet_schema_version"], VALIDATOR.CONTROLS_VERSION)
            self.assertEqual(receipt["packet_format_adapter_version"], VALIDATOR.PACKET_ADAPTER_VERSION)

    def test_controls_still_require_every_answer_and_original_packet_bytes(self):
        for alteration in ("missing-answer", "source-tamper"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory), questions=6)
                fixture.make_controls()
                if alteration == "missing-answer":
                    fixture.a_rows.pop()
                    fixture.refresh()
                else:
                    path = fixture.packet / "evidence.jsonl"
                    path.write_bytes(path.read_bytes() + b"changed")
                with self.assertRaises(ValueError):
                    fixture.validate()

    def test_unknown_packet_schema_or_wrong_known_schema_state_never_falls_through(self):
        for alteration in ("schema", "state"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory), questions=6)
                fixture.make_controls()
                path = fixture.packet / "manifest.json"
                manifest = json.loads(path.read_text())
                manifest["schema_version" if alteration == "schema" else "state"] = "unsupported-future-format"
                write_json(path, manifest)
                fixture.packet_hash = VALIDATOR.digest(path.read_bytes())
                with self.assertRaisesRegex(ValueError, "Unsupported"):
                    fixture.validate()

    def test_resolved_local_control_needs_citation_coverage_of_both_actor_anchors(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=6)
            fixture.make_controls()
            fixture.a_rows[0]["evidence"] = [citation("left")]
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "both actor anchors"):
                fixture.validate()

    def test_control_question_kind_and_anchor_coordinates_are_checked(self):
        for alteration in ("kind", "same-source", "anchor-source", "same-anchor", "anchor-quote"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory), questions=6)
                fixture.make_controls()
                question = fixture.questions[0]
                if alteration == "kind":
                    question["question_kind"] = "unrecognized-control"
                elif alteration == "same-source":
                    question["question_kind"] = "cross_source_contrast"
                elif alteration == "anchor-source":
                    question["left"]["reported_actor_anchor"]["evidence_id"] = "right"
                elif alteration == "same-anchor":
                    question["right"]["reported_actor_anchor"] = deepcopy(question["left"]["reported_actor_anchor"])
                else:
                    question["left"]["reported_actor_anchor"]["span"]["quote"] = "wrong"
                write_rows(fixture.packet / "questions.jsonl", fixture.questions)
                path = fixture.packet / "manifest.json"
                manifest = json.loads(path.read_text())
                manifest["artifacts"] = [TOOL.file_record(fixture.packet / name, fixture.packet)
                                         for name in ("questions.jsonl", "evidence.jsonl", "REVIEW.md")]
                write_json(path, manifest)
                fixture.packet_hash = VALIDATOR.digest(path.read_bytes())
                with self.assertRaises(ValueError):
                    fixture.validate()

    def test_wrapper_dot_locators_verify_without_changing_frozen_row_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=1)
            for value in fixture.a_rows[0]["evidence"]:
                value["field_path"] = "original_record.raw_record.row.taper"
            fixture.refresh()
            original = fixture.a.read_bytes()
            summary, pins = fixture.validate()
            self.assertEqual(fixture.a.read_bytes(), original)
            self.assertEqual(summary["first_reviews"]["reader_a"]["citation_formats"]["wrapper_dot_raw_field"], 2)
            receipt = VALIDATOR.publish_receipt(TOOL, Path(directory) / "result", summary, pins)
            self.assertEqual(receipt["citation_locator_adapter"]["version"], VALIDATOR.LOCATOR_ADAPTER_VERSION)
            self.assertEqual(receipt["citation_locator_adapter"]["counts"],
                             {"canonical_raw_field": 2, "wrapper_dot_raw_field": 2, "primary_visible_text": 0})

    def test_only_exact_wrapper_prefix_and_simple_known_raw_field_are_allowed(self):
        locators = ("raw_record.row.taper", "original_record.raw_record.row.taper.extra",
                    "original_record.raw_record.row./taper", "original_record.raw_record.row.unknown",
                    "/raw_record/row/taper/more", "primary/sonicarchives-visible-text.txt")
        for locator in locators:
            with self.subTest(locator=locator), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory), questions=1)
                fixture.a_rows[0]["evidence"][0]["field_path"] = locator
                fixture.refresh()
                with self.assertRaisesRegex(ValueError, "locator"):
                    fixture.validate()

    def test_authenticated_primary_uses_full_file_hash_including_final_newline(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=1)
            primary = fixture.add_primary()
            fixture.a_rows[0]["evidence"].append(primary)
            fixture.refresh()
            summary, pins = fixture.validate()
            self.assertEqual(summary["first_reviews"]["reader_a"]["citation_formats"]["primary_visible_text"], 1)
            self.assertTrue(any(Path(record["path"]).name == "excerpts.json" for record in pins))
            primary["field_sha256"] = VALIDATOR.digest((fixture.packet / VALIDATOR.PRIMARY_TEXT).read_bytes().rstrip(b"\n"))
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "complete retained UTF-8 file bytes"):
                fixture.validate()

    def test_primary_cannot_replace_required_raw_evidence_from_both_recordings(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=1)
            fixture.a_rows[0]["evidence"] = [fixture.add_primary()]
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "both question sources"):
                fixture.validate()

    def test_primary_identity_path_hash_and_codepoint_spans_are_strict(self):
        changes = (lambda c: c.update(evidence_id="primary:another-source"),
                   lambda c: c.update(field_path="primary/arbitrary.txt"),
                   lambda c: c.update(field_sha256="0" * 64),
                   lambda c: c["span"].update(start=5),
                   lambda c: c["span"].update(end=10000),
                   lambda c: c["span"].update(start=True),
                   lambda c: c["span"].update(quote="Different wording"))
        for change in changes:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory), questions=1)
                primary = fixture.add_primary()
                change(primary)
                fixture.a_rows[0]["evidence"].append(primary)
                fixture.refresh()
                with self.assertRaises(ValueError):
                    fixture.validate()

    def test_primary_bytes_and_excerpt_metadata_are_authenticated(self):
        for alteration in ("bytes", "excerpt", "source-id", "missing-primary"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory), questions=1)
                primary = fixture.add_primary()
                fixture.a_rows[0]["evidence"].append(primary)
                fixture.refresh()
                if alteration == "bytes":
                    target = fixture.packet / VALIDATOR.PRIMARY_TEXT
                    target.write_bytes(target.read_bytes() + b"changed")
                elif alteration == "missing-primary":
                    path = fixture.packet / "manifest.json"
                    manifest = json.loads(path.read_text())
                    manifest["artifacts"] = [r for r in manifest["artifacts"] if r["path"] != "primary/excerpts.json"]
                    write_json(path, manifest)
                    fixture.packet_hash = VALIDATOR.digest(path.read_bytes())
                else:
                    path = fixture.packet / "primary/excerpts.json"
                    metadata = json.loads(path.read_text())
                    if alteration == "source-id":
                        metadata["source_id"] = "primary:unapproved-source"
                    else:
                        quote = "Not in the primary text"
                        metadata["excerpts"] = [{"quote": quote, "sha256": VALIDATOR.digest(quote.encode())}]
                    write_json(path, metadata)
                    fixture.refresh_primary_manifest()
                with self.assertRaises(ValueError):
                    fixture.validate()

    def test_primary_citation_requires_primary_artifacts_in_the_pinned_packet(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=1)
            value = citation(VALIDATOR.PRIMARY_ID)
            value["field_path"] = VALIDATOR.PRIMARY_TEXT
            fixture.a_rows[0]["evidence"].append(value)
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "Unknown source identity"):
                fixture.validate()

    def test_import_executes_authenticated_bytes_instead_of_timestamp_valid_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cached_packet_tool.py"
            template = ("import hashlib\nVERSION = 'people-identity-development-packet:1'\n"
                        "EXECUTED = 'MARKER'\n"
                        "def file_record(path):\n"
                        "    return {'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}\n")
            path.write_text(template.replace("MARKER", "cached"))
            timestamp = path.stat().st_mtime_ns
            py_compile.compile(str(path), doraise=True)
            data = template.replace("MARKER", "source").encode()
            path.write_bytes(data)
            os.utime(path, ns=(timestamp, timestamp))
            loaded = VALIDATOR.load_packet_tool(path, VALIDATOR.digest(data))
            self.assertEqual(loaded.EXECUTED, "source")

    def test_complete_first_files_validate_without_certifying_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            summary, pins = fixture.validate()
            self.assertEqual(summary["comparison"], {"agreements": 1, "disagreements": 1})
            self.assertIsNone(summary["adjudication"])
            output = Path(directory) / "validated"
            receipt = VALIDATOR.publish_receipt(TOOL, output, summary, pins)
            self.assertEqual(receipt["state"], "validated_first_answer_files")
            self.assertFalse(receipt["semantic_truth_certified"])
            self.assertFalse(receipt["policy_activation_permitted"])
            for record in receipt["inputs"]:
                retained = TOOL.file_record(output / record["retained_path"])
                self.assertEqual((retained["bytes"], retained["sha256"]), (record["bytes"], record["sha256"]))

    def test_complete_adjudication_links_original_rows_and_counts_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            path, rows, _ = fixture.adjudication_file()
            rows[0]["decision"] = "insufficient_evidence"
            rows[1]["decision"] = "different_entities"
            rows[1]["contradiction_basis"] = "Supported distinct actors, with exact evidence from both sources."
            summary, _ = fixture.validate(path, write_rows(path, rows))
            adjudication = summary["adjudication"]
            self.assertEqual(adjudication["agreements_changed"], 1)
            self.assertEqual(adjudication["disagreement_outcomes"]["neither_first_decision"], 1)
            self.assertEqual(adjudication["changes_from_reader_a_on_disagreements"], 1)
            self.assertEqual(adjudication["changes_from_reader_b_on_disagreements"], 1)

    def test_missing_duplicate_or_foreign_question_is_rejected(self):
        for alteration in ("missing", "duplicate", "foreign"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory))
                if alteration == "missing":
                    fixture.a_rows.pop()
                elif alteration == "duplicate":
                    fixture.a_rows[1] = deepcopy(fixture.a_rows[0])
                else:
                    fixture.a_rows[0]["question_id"] = "another-packet-question"
                fixture.refresh()
                with self.assertRaisesRegex(ValueError, "question"):
                    fixture.validate()

    def test_resolved_answer_requires_each_question_source_not_only_other_context(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            fixture.a_rows[0]["evidence"] = [citation("left"), citation("extra-context")]
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "both question sources"):
                fixture.validate()

    def test_unknown_can_keep_empty_evidence_without_becoming_a_negative(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=1)
            fixture.a_rows[0].update(decision="insufficient_evidence", evidence=[])
            fixture.refresh()
            summary, _ = fixture.validate()
            self.assertEqual(summary["first_reviews"]["reader_a"]["class_counts"]["insufficient_evidence"], 1)
            self.assertEqual(summary["first_reviews"]["reader_a"]["class_counts"]["different_entities"], 0)

    def test_negative_requires_cited_distinctness_basis_and_other_decisions_require_null(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory), questions=1)
            fixture.a_rows[0]["decision"] = "different_entities"
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "contradiction/distinctness"):
                fixture.validate()
            fixture.a_rows[0].update(decision="insufficient_evidence", contradiction_basis="No evidence is not distinctness")
            fixture.refresh()
            with self.assertRaisesRegex(ValueError, "Only a different-entity"):
                fixture.validate()

    def test_span_checks_unicode_field_hash_quote_and_source_membership(self):
        changes = (lambda c: c["span"].update(start=5),
                   lambda c: c["span"].update(end=100000),
                   lambda c: c["span"].update(start=True),
                   lambda c: c["span"].update(quote="Elodie"),
                   lambda c: c.update(field_sha256="0" * 64),
                   lambda c: c.update(evidence_id="outside-packet"))
        for change in changes:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory))
                change(fixture.a_rows[0]["evidence"][0])
                fixture.refresh()
                with self.assertRaises(ValueError):
                    fixture.validate()

    def test_original_row_hashes_exclude_only_lf_or_crlf_and_retain_spaces(self):
        row = answer("q", "reader")
        data = json.dumps(row, ensure_ascii=False).encode()
        self.assertEqual(VALIDATOR.physical_rows(data + b"\n")[0][1], VALIDATOR.digest(data))
        self.assertEqual(VALIDATOR.physical_rows(data + b"\r\n")[0][1], VALIDATOR.digest(data))
        self.assertEqual(VALIDATOR.physical_rows(data + b"  \n")[0][1], VALIDATOR.digest(data + b"  "))
        self.assertEqual(VALIDATOR.physical_rows(data)[0][1], VALIDATOR.digest(data))
        self.assertIn("\u2028", VALIDATOR.physical_rows((json.dumps({"text": "x\u2028y"}, ensure_ascii=False) + "\n").encode())[0][0]["text"])

    def test_blank_rows_or_duplicate_json_fields_do_not_disappear(self):
        with self.assertRaisesRegex(ValueError, "Blank"):
            VALIDATOR.physical_rows(b'{}\n\n')
        with self.assertRaisesRegex(ValueError, "Duplicate JSON"):
            VALIDATOR.physical_rows(b'{"decision":"same_entity","decision":"different_entities"}\n')

    def test_wrong_first_row_hash_or_disagreement_flag_is_rejected(self):
        for alteration in ("hash", "flag"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory))
                path, rows, _ = fixture.adjudication_file()
                if alteration == "hash":
                    rows[0]["first_answer_row_sha256"]["reader_a"] = "0" * 64
                else:
                    rows[0]["reviewer_disagreement"] = True
                with self.assertRaisesRegex(ValueError, "original|disagreement"):
                    fixture.validate(path, write_rows(path, rows))

    def test_first_answer_whitespace_change_invalidates_adjudication_links(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            path, _, hash_value = fixture.adjudication_file()
            fixture.a.write_bytes(fixture.a.read_bytes().replace(b"\n", b"  \n", 1))
            fixture.a_hash = VALIDATOR.digest(fixture.a.read_bytes())
            with self.assertRaisesRegex(ValueError, "original first-answer row"):
                fixture.validate(path, hash_value)

    def test_missing_adjudication_rows_and_paired_hash_requirement(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            path, rows, _ = fixture.adjudication_file()
            with self.assertRaisesRegex(ValueError, "supplied together"):
                fixture.validate(path, None)
            with self.assertRaisesRegex(ValueError, "every packet question"):
                fixture.validate(path, write_rows(path, rows[:1]))

    def test_exposure_and_distinct_roles_are_required(self):
        for alteration in ("exposure", "not-blind", "same-reviewer", "adjudicator-is-reader"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory))
                if alteration == "exposure":
                    del fixture.a_rows[0]["reviewer"]["prior_exposure"]
                elif alteration == "not-blind":
                    fixture.a_rows[0]["reviewer"]["answer_blind"] = False
                elif alteration == "same-reviewer":
                    for row in fixture.b_rows:
                        row["reviewer"]["id"] = "reader-a"
                fixture.refresh()
                if alteration == "adjudicator-is-reader":
                    path, rows, _ = fixture.adjudication_file()
                    for row in rows:
                        row["adjudicator"]["id"] = "reader-a"
                    with self.assertRaisesRegex(ValueError, "separate reviewer"):
                        fixture.validate(path, write_rows(path, rows))
                else:
                    with self.assertRaises(ValueError):
                        fixture.validate()

    def test_changed_packet_tool_manifest_or_review_bytes_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "source hash"):
            VALIDATOR.load_packet_tool(PACKET_SOURCE, "0" * 64)
        for alteration in ("manifest", "source", "review"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory))
                target = {"manifest": fixture.packet / "manifest.json", "source": fixture.packet / "evidence.jsonl", "review": fixture.a}[alteration]
                target.write_bytes(target.read_bytes() + b" ")
                with self.assertRaises(ValueError):
                    fixture.validate()

    def test_review_inside_frozen_packet_is_rejected_before_reading(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            fixture.a = fixture.packet / "new-review.jsonl"
            with self.assertRaisesRegex(ValueError, "outside the frozen packet"):
                fixture.validate()

    def test_cli_rejects_packet_output_before_loading_the_packet_tool(self):
        with tempfile.TemporaryDirectory() as directory:
            packet = Path(directory) / "packet"
            arguments = ["review-validator", "--packet", str(packet), "--packet-manifest-sha256", "unused",
                         "--packet-tool", str(Path(directory) / "absent.py"), "--packet-tool-sha256", "unused",
                         "--reader-a", "a", "--reader-a-sha256", "unused", "--reader-b", "b",
                         "--reader-b-sha256", "unused", "--output", str(packet / "new-receipt")]
            with patch.object(sys, "argv", arguments):
                with self.assertRaisesRegex(ValueError, "outside the frozen packet"):
                    VALIDATOR.main()
            self.assertFalse(packet.exists())

    def test_no_overwrite_or_completed_receipt_on_write_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = PacketFixture(Path(directory))
            summary, pins = fixture.validate()
            existing = Path(directory) / "existing"
            existing.mkdir()
            with self.assertRaises(FileExistsError):
                VALIDATOR.publish_receipt(TOOL, existing, summary, pins)
            output = Path(directory) / "failed"
            with patch.object(VALIDATOR, "retain_inputs", side_effect=OSError("disk failure")):
                with self.assertRaisesRegex(OSError, "disk failure"):
                    VALIDATOR.publish_receipt(TOOL, output, summary, pins)
            self.assertFalse((output / "receipt.json").exists())

    def test_post_validation_mutation_of_original_or_copy_prevents_completion(self):
        for alteration in ("original", "copy"):
            with self.subTest(alteration=alteration), tempfile.TemporaryDirectory() as directory:
                fixture = PacketFixture(Path(directory))
                summary, pins = fixture.validate()
                original = VALIDATOR.retain_inputs
                def mutate(tool, destination, records):
                    retained = original(tool, destination, records)
                    target = Path(records[0]["path"]) if alteration == "original" else destination / retained[0]["retained_path"]
                    target.write_text("changed")
                    return retained
                output = Path(directory) / "partial"
                with patch.object(VALIDATOR, "retain_inputs", side_effect=mutate):
                    with self.assertRaisesRegex(ValueError, "changed"):
                        VALIDATOR.publish_receipt(TOOL, output, summary, pins)
                self.assertFalse((output / "receipt.json").exists())


if __name__ == "__main__":
    unittest.main()
