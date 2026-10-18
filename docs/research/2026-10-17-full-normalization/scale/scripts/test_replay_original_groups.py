"""Replay only synthetic captures with the authenticated original algorithm."""

import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

import replay_original_groups as REPLAY


BUILDER_PATH = Path(__file__).resolve().parents[1] / "annotation/build_tasks.py"


def identity(number):
    return str(uuid.UUID(int=number))


def source_metadata(number=1, **changes):
    return {
        "evidence_id": "source_sample:" + identity(number), "resource_kind": "source_sample",
        "resource_uuid": identity(number), "original_id": number,
        "artist_id": 10, "artist_uuid": identity(10), "show_id": 20,
        "original_show_uuid": None, "parent_source_id": None,
        "known_recording_family": "artist:10:show:20", "input_duplicate_family": "a" * 64,
        "family_partition": "development", "artist_partition": "development", **changes,
    }


def candidate(number=1, **changes):
    return {
        "source_uuid": identity(number), "source_id": number, "artist_id": 10,
        "artist_uuid": identity(10), "show_id": 20, "show_uuid": identity(20),
        "associations": [], **changes,
    }


def reservation():
    return {
        "reserved_references": [], "known_exposed_track_references": [],
        "excluded_annotation_metadata": [], "reserved_known_families": [],
        "metadata_matches": [], "metadata_linked_parent_sources": [],
        "reserved_annotation_family_groups": [], "exposure_note": "Preserve prior computational exposure.",
    }


class TestOriginalGroupingReplay(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.path = self.root / "snapshot.jsonl"
        source = BUILDER_PATH.read_bytes()
        self.manifest = {
            "builder_sha256": hashlib.sha256(source).hexdigest(),
            "builder_version": "blind-annotation-v1", "state": "complete_unlabeled",
        }
        self.builder = REPLAY.load_builder(BUILDER_PATH, source, self.manifest)
        self.rows = self.capture()
        self.save()

    def capture(self):
        rows = [
            {"kind": "snapshot", "read_only": "on", "in_recovery": True},
            {"kind": "totals", "rows": {"artists": 2, "sources": 4, "tracks": 2,
             "venues": 2, "songs": 2, "setlist_shows": 2, "setlist_memberships": 2}},
        ]
        for artist in (1, 2):
            rows.append({"kind": "artist", "row": {"id": artist, "uuid": identity(artist), "name": "Fixture artist"}})
            for kind, (profile, field, _, _) in self.builder.PROFILES.items():
                count = 2 if kind == "source_sample" else 1
                profile_row = {"artist_id": artist, field: count}
                if kind == "track_sample":
                    profile_row["rows"] = count
                if kind == "setlist_sample":
                    profile_row["memberships"] = 1
                rows.append({"kind": profile, "row": profile_row})
                for offset in range(count):
                    number = artist * 100 + list(self.builder.PROFILES).index(kind) * 10 + offset
                    row = {"id": number, "uuid": identity(number), "artist_id": artist,
                           "show_id": artist * 1000, "taper": "Fixture credited actor",
                           "description": f"Private fixture prose {artist}/{offset}\u2028end",
                           "name": "Fixture title", "title": "Fixture file",
                           "source_id": artist * 100, "source_set_id": artist * 10}
                    record = {"kind": kind, "row": row}
                    if kind in ("source_sample", "track_sample"):
                        record["family"] = f"artist:{artist}:show:{artist * 1000}"
                    rows.append(record)
        rows.append({"kind": "snapshot_complete", "completed_at": "synthetic"})
        return rows

    def save(self):
        raw = b"".join((json.dumps(row, ensure_ascii=False) + "\n").encode() for row in self.rows)
        self.path.write_bytes(raw)
        self.manifest.update({
            "input_sha256": hashlib.sha256(raw).hexdigest(), "input_bytes": len(raw),
            "snapshot": self.rows[0], "snapshot_complete": {"kind": "snapshot_complete", "completed_at": "synthetic"},
        })
        self.counts = resource_counts(self.rows)

    def replay(self, **overrides):
        options = {"expected_bytes": self.manifest["input_bytes"],
                   "expected_rows": len(self.rows), "expected_counts": self.counts, **overrides}
        return REPLAY.replay_snapshot(self.builder, self.path, self.manifest, **options)

    def test_original_cross_artist_duplicate_bridge_keeps_complete_known_families_together(self):
        sources = [row for row in self.rows if row["kind"] == "source_sample"]
        sources[1]["row"]["description"] = sources[2]["row"]["description"]
        self.save()
        metadata, _ = self.replay()
        source_groups = {item["input_duplicate_family"] for item in metadata if item["resource_kind"] == "source_sample"}
        track_groups = {item["input_duplicate_family"] for item in metadata if item["resource_kind"] == "track_sample"}
        self.assertEqual(1, len(source_groups))
        self.assertEqual(source_groups, track_groups)
        self.assertEqual(4, sum(item["resource_kind"] == "source_sample" for item in metadata))
        self.assertNotIn("Private fixture prose", json.dumps(metadata))

    def test_record_order_cannot_change_original_groups_or_partitions(self):
        first, _ = self.replay()
        self.rows = [self.rows[0], *reversed(self.rows[1:-1]), self.rows[-1]]
        self.save()
        second, _ = self.replay()
        self.assertEqual(first, second)

    def test_changed_builder_is_rejected_before_its_code_executes(self):
        with self.assertRaisesRegex(ValueError, "builder source differs"):
            REPLAY.load_builder(BUILDER_PATH, b"raise RuntimeError('must not execute')", self.manifest)

    def test_changed_raw_bytes_are_rejected_before_original_loader(self):
        self.path.write_bytes(self.path.read_bytes().replace(b"Private", b"Changed", 1))
        with patch.object(self.builder, "load_snapshot") as loader:
            with self.assertRaisesRegex(ValueError, "snapshot hash differs"):
                self.replay()
            loader.assert_not_called()

    def test_partial_capture_type_or_literal_family_mismatch_cannot_succeed(self):
        for change in ("partial", "type", "family"):
            with self.subTest(change=change):
                self.rows = self.capture()
                if change == "partial":
                    self.rows.pop()
                else:
                    record = next(row for row in self.rows if row["kind"] == "source_sample")
                    if change == "type":
                        record["row"]["id"] = True
                    else:
                        record["family"] = "artist:1:show:999"
                self.save()
                with self.assertRaises(ValueError):
                    self.replay()

    def test_wrong_resource_cohort_or_row_byte_bounds_are_explicit_failures(self):
        for change in ({"expected_counts": {"source_sample": 4}},
                       {"expected_rows": len(self.rows) - 1},
                       {"expected_bytes": self.manifest["input_bytes"] - 1}):
            with self.subTest(change=change):
                with self.assertRaises(ValueError):
                    self.replay(**change)

    def test_growth_after_authentication_is_rejected_before_original_loader_can_decode_it(self):
        original_loader = self.builder.load_snapshot
        def grow_then_load(bounded):
            with self.path.open("ab") as stream:
                stream.write(b"not-json-extra-record\n")
            return original_loader(bounded)
        with patch.object(self.builder, "load_snapshot", side_effect=grow_then_load):
            with self.assertRaisesRegex(ValueError, "frozen bounds during original loading"):
                self.replay()

    def test_every_saved_header_must_match_before_classification(self):
        metadata, _ = self.replay()
        REPLAY.authenticate_headers(metadata, copy.deepcopy(metadata), self.counts)
        for field, value in (("artist_uuid", identity(999)), ("input_duplicate_family", "b" * 64),
                             ("family_partition", "different"), ("artist_partition", "different"),
                             ("known_recording_family", "artist:999:show:999")):
            with self.subTest(field=field):
                headers = copy.deepcopy(metadata)
                headers[0][field] = value
                with self.assertRaisesRegex(ValueError, "differs from a saved"):
                    REPLAY.authenticate_headers(metadata, headers, self.counts)
        with self.assertRaisesRegex(ValueError, "cohort differs"):
            REPLAY.authenticate_headers(metadata, metadata[:-1], self.counts)

    def test_all_duplicate_partners_of_a_reserved_source_remain_excluded(self):
        sources = [row for row in self.rows if row["kind"] == "source_sample"]
        sources[1]["row"]["description"] = sources[2]["row"]["description"]
        self.save()
        metadata, _ = self.replay()
        blocked = reservation()
        blocked["reserved_references"] = ["source:100"]
        closure = REPLAY.reservation_closure(metadata, blocked)
        self.assertEqual(1, len(closure["blocked_original_groups"]))
        self.assertTrue(all(item["input_duplicate_family"] in closure["blocked_original_groups"]
                            for item in metadata if item["resource_kind"] == "source_sample"))
        self.assertEqual(blocked["exposure_note"], closure["exposure_note"])
        self.assertEqual([], blocked["reserved_annotation_family_groups"])


def resource_counts(rows):
    result = {}
    for row in rows:
        if row["kind"] in REPLAY.RESOURCE_COUNTS:
            result[row["kind"]] = result.get(row["kind"], 0) + 1
    return result


class TestRecoveredSourceClassification(unittest.TestCase):
    def classify(self, original=None, current=None, records=None, blocked=None):
        metadata = [source_metadata()] if original is None else original
        reserved = blocked or reservation()
        closure = REPLAY.reservation_closure(metadata, reserved)
        return REPLAY.classify_sources([current or candidate()], metadata, records or {}, reserved, closure)[0]

    def test_only_exact_original_source_artist_and_show_tuple_is_verified(self):
        self.assertEqual("original_development_tuple_verified", self.classify()["classification"])
        for field, changed in (("source_id", 999), ("artist_id", 999),
                               ("artist_uuid", identity(999)), ("show_id", 999)):
            with self.subTest(field=field):
                result = self.classify(current=candidate(**{field: changed}))
                self.assertEqual("original_current_tuple_drift", result["classification"])
        absent = self.classify(current=candidate(source_uuid=identity(999)))
        self.assertEqual("absent_original_captured_source_frame", absent["classification"])
        self.assertFalse(absent["original_numeric_source_id_verified"])

    def test_context_only_source_does_not_acquire_a_standalone_group(self):
        result = self.classify(original=[], records={"track_sample": [{"source_context": {
            "id": 1, "uuid": identity(1), "artist_id": 10, "show_id": 20,
        }}]})
        self.assertEqual("context_only_not_original_source_member", result["classification"])
        self.assertIsNone(result["original_metadata"])
        self.assertFalse(result["new_partition_assigned"])

    def test_heldout_and_unknown_show_context_are_preserved(self):
        result = self.classify(original=[source_metadata(family_partition="test")])
        self.assertEqual("original_heldout_partition", result["classification"])
        self.assertEqual("test", result["original_metadata"]["family_partition"])
        for original, current, expected in (
            ([source_metadata(show_id=None)], candidate(), "missing_original_show_context"),
            ([source_metadata()], candidate(show_id=None, show_uuid=None), "missing_current_show_context"),
        ):
            self.assertEqual(expected, self.classify(original, current)["classification"])

    def test_reserved_track_parent_and_linked_uuid_expand_original_component_exclusions(self):
        track = source_metadata(2, resource_kind="track_sample", original_id=22,
                                evidence_id="track_sample:" + identity(2), parent_source_id=1,
                                input_duplicate_family="b" * 64)
        blocked = reservation()
        blocked["reserved_references"] = ["track:22"]
        closure = REPLAY.reservation_closure([source_metadata(), track], blocked)
        self.assertEqual({"a" * 64, "b" * 64}, set(closure["blocked_original_groups"]))
        blocked = reservation()
        blocked["metadata_linked_parent_sources"] = [{"kind": "source", "id": 999, "uuid": identity(1)}]
        self.assertEqual("reserved_or_excluded", self.classify(blocked=blocked)["classification"])
        self.assertEqual("reserved_or_excluded", self.classify(original=[], blocked=blocked)["classification"])

    def test_all_tracks_in_blocked_groups_extend_foreign_parent_closure_without_excluding_unrelated_group(self):
        sources = [source_metadata(number, artist_id=number * 10, artist_uuid=identity(number * 10),
                                   known_recording_family=f"artist:{number * 10}:show:20",
                                   input_duplicate_family=letter * 64)
                   for number, letter in enumerate("abcd", 1)]
        tracks = [source_metadata(100 + index, resource_kind="track_sample", original_id=100 + index,
                                  evidence_id="track_sample:" + identity(100 + index),
                                  artist_id=artist, artist_uuid=identity(artist),
                                  known_recording_family=f"artist:{artist}:show:20",
                                  parent_source_id=parent_id, input_duplicate_family=group * 64)
                  for index, (artist, parent_id, group) in enumerate(
                      ((10, 2, "a"), (20, 3, "b"), (30, 1, "c")), 1)]
        metadata = sources + tracks
        original = copy.deepcopy(metadata)
        blocked = reservation()
        blocked["reserved_references"] = ["source:1"]
        closure = REPLAY.reservation_closure(metadata, blocked)
        self.assertEqual({letter * 64 for letter in "abc"}, set(closure["blocked_original_groups"]))
        self.assertEqual([sources[0]["evidence_id"]], closure["original_seed_evidence_ids"])
        self.assertEqual([sources[1]["evidence_id"], sources[2]["evidence_id"]],
                         closure["derived_parent_source_evidence_ids"])
        results = REPLAY.classify_sources(
            [candidate(index, artist_id=index * 10, artist_uuid=identity(index * 10)) for index in range(1, 5)],
            metadata, {}, blocked, closure)
        self.assertEqual(["reserved_or_excluded"] * 3 + ["original_development_tuple_verified"],
                         [item["classification"] for item in results])
        self.assertEqual(original, metadata)

    def test_failed_publication_retains_partial_files_without_success_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "input"
            source.write_bytes(b"first")
            inputs = REPLAY.PreparationInputs()
            inputs.stamp(source)
            source.write_bytes(b"changed")
            output = root / "output"
            with self.assertRaises(ValueError):
                REPLAY.publish_preparation(output, {"metadata.jsonl": b"{}\n"}, inputs, {"state": "completed"})
            self.assertTrue((output / "metadata.jsonl").exists())
            self.assertTrue((output / "failure.json").exists())
            self.assertFalse((output / "receipt.json").exists())
            with self.assertRaises(FileExistsError):
                REPLAY.publish_preparation(output, {"metadata.jsonl": b"replacement"}, inputs, {})
            self.assertEqual(b"{}\n", (output / "metadata.jsonl").read_bytes())


if __name__ == "__main__":
    unittest.main()
