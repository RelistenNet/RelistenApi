"""Exercise expanded eligibility and provenance with metadata-only synthetic inputs."""

import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

import prepare_expanded_venue_context as EXPAND


def identity(number):
    return str(uuid.UUID(int=number))


def reservation():
    return {"reserved_references": [], "known_exposed_track_references": [],
            "excluded_annotation_metadata": [], "reserved_known_families": [],
            "metadata_matches": [], "metadata_linked_parent_sources": [],
            "reserved_annotation_family_groups": [], "exposure_note": "Retain original exposure."}


def fixture():
    metadata, classifications = [], []
    for index in range(29):
        artist_id, source_id, show_id = index % 21 + 1, 100 + index, 200 + index
        source_uuid, artist_uuid = identity(source_id), identity(artist_id)
        original = {
            "evidence_id": "source_sample:" + source_uuid, "resource_kind": "source_sample",
            "resource_uuid": source_uuid, "original_id": source_id,
            "artist_id": artist_id, "artist_uuid": artist_uuid, "show_id": show_id,
            "original_show_uuid": None, "parent_source_id": None,
            "known_recording_family": f"artist:{artist_id}:show:{show_id}",
            "input_duplicate_family": hashlib.sha256(str(source_id).encode()).hexdigest(),
            "artist_partition": "development", "family_partition": "development",
        }
        metadata.append(original)
        classifications.append({
            "source_id": source_id, "source_uuid": source_uuid, "artist_id": artist_id,
            "artist_uuid": artist_uuid, "show_id": show_id, "show_uuid": identity(show_id),
            "associations": [{"venue_evidence_id": "venue_sample:" + identity(1000 + artist_id),
                              "association_kind": "source_venue"}],
            "classification": "original_development_tuple_verified", "original_metadata": copy.deepcopy(original),
            "original_numeric_source_id_verified": True, "new_partition_assigned": False,
            "provider_query_executed": False,
        })
    venues = [{"artist_id": index % 66 + 1, "artist_uuid": identity(index % 66 + 1),
               "venue_id": 1000 + index, "venue_uuid": identity(1000 + index)} for index in range(67)]
    return metadata, classifications, venues


class TestExpandedVenueSelection(unittest.TestCase):
    def setUp(self):
        self.metadata, self.classifications, self.venues = fixture()
        self.reservation = reservation()

    def select(self, discovered=None, closure=None, **options):
        discovered = ([{field: item[field] for field in EXPAND.CURRENT_FIELDS}
                       for item in self.classifications] if discovered is None else discovered)
        closure = (EXPAND.reservation_closure(self.metadata, self.reservation)
                   if closure is None else closure)
        return EXPAND.select_sources(self.classifications, self.metadata, discovered,
                                     self.venues, self.reservation, closure, **options)

    def test_exact_original_tuples_preserve_groups_and_separate_current_show_uuid(self):
        sources = self.select()
        self.assertEqual(29, len(sources))
        self.assertEqual(21, len({row["artist_uuid"] for row in sources}))
        for source, original in zip(sources, self.metadata):
            self.assertEqual(original["original_id"], source["source_id"])
            self.assertEqual(original["resource_uuid"], source["source_uuid"])
            self.assertEqual(original["show_id"], source["expected_show_id"])
            self.assertEqual(original["input_duplicate_family"], source["family_group"])
            self.assertIsNone(source["original_show_uuid"])
            self.assertEqual(identity(original["show_id"]), source["current_discovery_show_uuid"])

    def test_heldout_and_context_only_statuses_cannot_supply_the_full_29_source_cohort(self):
        for status in ("original_heldout_partition", "context_only_not_original_source_member",
                       "reserved_or_excluded", "absent_original_captured_source_frame"):
            with self.subTest(status=status):
                self.metadata, self.classifications, self.venues = fixture()
                self.classifications[0]["classification"] = status
                with self.assertRaisesRegex(ValueError, "source denominator"):
                    self.select()

    def test_verified_status_does_not_override_heldout_original_or_missing_original(self):
        for change in ("artist_partition", "family_partition", "missing"):
            with self.subTest(change=change):
                self.metadata, self.classifications, self.venues = fixture()
                if change == "missing":
                    self.metadata.pop(0)
                    self.classifications[0]["original_metadata"] = None
                else:
                    self.metadata[0][change] = "validation"
                    self.classifications[0]["original_metadata"][change] = "validation"
                with self.assertRaises(ValueError):
                    self.select()

    def test_exact_source_artist_show_and_association_discovery_receipt_is_required(self):
        for field, value in (("source_id", 999), ("artist_id", 999), ("artist_uuid", identity(999)),
                             ("show_id", 999), ("show_uuid", identity(999)), ("associations", [])):
            with self.subTest(field=field):
                discovered = [{key: item[key] for key in EXPAND.CURRENT_FIELDS} for item in self.classifications]
                discovered[0][field] = value
                with self.assertRaisesRegex(ValueError, "exact discovery tuple"):
                    self.select(discovered)

    def test_changed_current_identity_cannot_follow_foreign_catalog_tuple(self):
        for field, value in (("source_id", 999), ("artist_id", 999), ("artist_uuid", identity(999)),
                             ("show_id", 999)):
            with self.subTest(field=field):
                self.metadata, self.classifications, self.venues = fixture()
                self.classifications[0][field] = value
                with self.assertRaises(ValueError):
                    self.select()

    def test_current_discovery_show_uuid_must_be_present_and_canonical(self):
        for value in (None, "not-a-uuid", "00000000-0000-0000-0000-0000000000AB"):
            with self.subTest(value=value):
                self.classifications[0]["show_uuid"] = value
                with self.assertRaises(ValueError):
                    self.select()

    def test_duplicate_status_unknown_status_and_verification_flags_fail_closed(self):
        self.classifications.append(copy.deepcopy(self.classifications[0]))
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.select()
        for field, value in (("classification", "new-status"), ("original_numeric_source_id_verified", False),
                             ("new_partition_assigned", True), ("provider_query_executed", True)):
            with self.subTest(field=field):
                self.metadata, self.classifications, self.venues = fixture()
                self.classifications[0][field] = value
                with self.assertRaises(ValueError):
                    self.select()

    def test_reserved_full_group_and_changed_reservation_closure_cannot_be_ignored(self):
        self.reservation["reserved_references"] = ["source:100"]
        with self.assertRaisesRegex(ValueError, "Reserved original component"):
            self.select()
        closure = EXPAND.reservation_closure(self.metadata, self.reservation)
        closure["blocked_original_groups"] = []
        with self.assertRaisesRegex(ValueError, "reservation closure differs"):
            self.select(closure=closure)

    def test_source_id_type_literal_family_and_original_uuid_claim_must_match(self):
        for field, value in (("original_id", True), ("known_recording_family", "artist:1:show:999"),
                             ("original_show_uuid", identity(200)), ("resource_kind", "track_sample")):
            with self.subTest(field=field):
                self.metadata, self.classifications, self.venues = fixture()
                self.metadata[0][field] = value
                self.classifications[0]["original_metadata"][field] = value
                with self.assertRaises(ValueError):
                    self.select()


class TestExpandedPreparationProvenance(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def test_changed_completed_replay_receipt_rejects_before_prerequisites_or_output(self):
        replay = self.root / "local-dev/normalization-data/replay"
        replay.mkdir(parents=True)
        (replay / "receipt.json").write_bytes(b"changed receipt")
        anchor = self.root / EXPAND.RESEARCH / "scale/evidence/original-group-replay-v1.json"
        anchor.parent.mkdir(parents=True)
        anchor.write_text(json.dumps({
            "schema_version": "original-group-replay-local-v1", "state": "completed_original_group_replay",
            "replay_receipt": {"path": str((replay / "receipt.json").relative_to(self.root)),
                               **EXPAND.bytes_fingerprint(b"original receipt")},
        }))
        output = self.root / "local-dev/normalization-data/output"
        with patch.object(EXPAND, "read_receipt") as prerequisites:
            with self.assertRaisesRegex(ValueError, "Changed completed replay"):
                EXPAND.prepare(self.root, replay, self.root / "prep", self.root / "discovery", output)
            prerequisites.assert_not_called()
        self.assertFalse(output.exists())

    def test_overflow_rows_bytes_and_physical_unicode_are_checked_before_json_decode(self):
        path = self.root / "rows.jsonl"
        value = {"text": "literal\u2028separator\u2029and 🎸"}
        raw = (json.dumps(value, ensure_ascii=False) + "\n").encode()
        path.write_bytes(raw)
        self.assertEqual([value], EXPAND.read_rows(EXPAND.PreparationInputs(), path, 1))
        for raw, count, bound in ((b"\xff\n\xff\n", 1, 100),
                                  (b"\xff" * (EXPAND.MAX_LINE_BYTES + 1) + b"\n", 1, EXPAND.MAX_METADATA_BYTES),
                                  (b"\xff\n", 1, 1)):
            with self.subTest(size=len(raw), count=count, bound=bound):
                path.write_bytes(raw)
                with patch.object(EXPAND.json, "loads") as decoder:
                    with self.assertRaises(ValueError):
                        EXPAND.read_rows(EXPAND.PreparationInputs(), path, count, bound)
                    decoder.assert_not_called()

    def test_changed_prerequisite_artifact_cannot_be_refreshed_by_new_preparation(self):
        data = self.root / "data.json"
        data.write_bytes(b"original")
        (self.root / "receipt.json").write_text(json.dumps({
            "schema_version": "fixture", "state": "completed", "files": {"data.json": EXPAND.bytes_fingerprint(b"original")},
            "inputs": [],
        }))
        data.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "Changed prerequisite output"):
            EXPAND.read_receipt(EXPAND.PreparationInputs(), self.root, "fixture", self.root)

    def test_publication_failure_preserves_partial_output_without_success_or_overwrite(self):
        source = self.root / "source"
        source.write_bytes(b"original")
        inputs = EXPAND.PreparationInputs()
        inputs.stamp(source)
        source.write_bytes(b"changed")
        output = self.root / "output"
        with self.assertRaises(ValueError):
            EXPAND.publish_preparation(output, {"source-allowlist.json": b"[]"}, inputs, {})
        self.assertTrue((output / "failure.json").exists())
        self.assertFalse((output / "receipt.json").exists())
        with self.assertRaises(FileExistsError):
            EXPAND.publish_preparation(output, {"source-allowlist.json": b"replacement"}, inputs, {})
        self.assertEqual(b"[]", (output / "source-allowlist.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
