"""Exercise metadata-only parsing and eligibility with synthetic physical rows."""

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

import project_annotation_metadata as PROJECT


def identity(number):
    return str(uuid.UUID(int=number))


def row(number=1, kind="source_sample", **changes):
    metadata = {
        "artist": {"name": "Artist", "uuid": identity(100)},
        "artist_partition": "development",
        "evidence_id": kind + ":" + identity(number),
        "family_partition": "development",
        "input_duplicate_family": "a" * 64,
        "known_recording_family": "artist:10:show:20",
    }
    metadata.update(changes)
    prefix = json.dumps(metadata, ensure_ascii=False, separators=(",", ":"))[:-1].encode()
    return prefix + b',"raw_record":'


def venue(number=1):
    return {
        "evidence_id": "venue_sample:" + identity(1000 + number),
        "venue_uuid": identity(1000 + number), "venue_id": 1000 + number,
        "artist_uuid": identity(100), "artist_id": 10,
    }


def association(number=1, selected=None, **changes):
    selected = venue() if selected is None else selected
    value = {
        "venue_evidence_id": selected["evidence_id"],
        **{key: selected[key] for key in ("venue_id", "venue_uuid", "artist_id", "artist_uuid")},
        "association_kind": "source_venue", "source_id": 100 + number,
        "source_uuid": identity(number), "show_id": 20, "show_uuid": identity(200),
    }
    value.update(changes)
    return value


def reservation():
    return {
        "excluded_annotation_metadata": [], "reserved_references": [],
        "reserved_known_families": [], "reserved_annotation_family_groups": [],
        "metadata_matches": [], "metadata_linked_parent_sources": [],
    }


class TestMetadataPrefix(unittest.TestCase):
    def test_payload_can_be_invalid_utf8_or_json_without_being_decoded(self):
        prefix = row()
        for payload in (b"\xff\xfe NOT JSON", b"", b" " * 20000 + b"["):
            with self.subTest(payload_bytes=len(payload)):
                actual = PROJECT.read_metadata_prefix(prefix + payload)
                self.assertEqual(len(prefix), actual["raw_record_value_offset"])
                self.assertEqual(identity(1), actual["resource_uuid"])
                self.assertNotIn("name", actual)
                self.assertNotIn("raw_record", actual)

    def test_escaped_markers_and_unicode_in_artist_metadata_do_not_move_boundary(self):
        name = 'Artist \\ "},"raw_record":\u2028\u2029 🎸'
        prefix = row(artist={"name": name, "uuid": identity(100)})
        actual = PROJECT.read_metadata_prefix(prefix + b"\xff")
        self.assertEqual(len(prefix), actual["raw_record_value_offset"])
        self.assertEqual(identity(100), actual["artist_uuid"])

    def test_order_missing_duplicate_and_unknown_metadata_keys_fail_closed(self):
        valid = row()
        invalid = [
            valid.replace(b'"artist_partition"', b'"family_partition"', 1),
            valid.replace(b'"evidence_id"', b'"unknown_key"', 1),
            valid.replace(b',"family_partition":"development"', b"", 1),
            valid.replace(b'"raw_record"', b'"resource"', 1),
            valid.replace(b'"name":"Artist","uuid"', b'"uuid":"Artist","name"', 1),
        ]
        for value in invalid:
            with self.subTest(value=value[:100]):
                with self.assertRaises(ValueError):
                    PROJECT.read_metadata_prefix(value + b"\xff")

    def test_invalid_metadata_is_rejected_before_payload(self):
        invalid = [
            row()[:25], row().replace(b"Artist", b"\xff"),
            row(artist_partition="new-partition"),
            row(input_duplicate_family="newly-assigned"),
            row(known_recording_family="show:20"),
            row(evidence_id="source_sample:" + identity(1).upper().replace("00000001", "0000000A")),
        ]
        for value in invalid:
            with self.subTest(value=value[:100]):
                with self.assertRaises(ValueError):
                    PROJECT.read_metadata_prefix(value + b"\xff")

    def test_oversized_or_nested_metadata_is_not_searched_for_a_later_raw_key(self):
        nested = row().replace(b'"name":"Artist"', b'"name":' + b"[" * 9 + b'"Artist"' + b"]" * 9)
        oversized = row(artist={"name": "x" * PROJECT.MAX_PREFIX_BYTES, "uuid": identity(100)})
        for value in (nested, oversized):
            with self.subTest(size=len(value)):
                with self.assertRaises(ValueError):
                    PROJECT.read_metadata_prefix(value + b"\xff")

    def test_missing_known_family_stays_unknown(self):
        actual = PROJECT.read_metadata_prefix(row(known_recording_family=None))
        self.assertIsNone(actual["known_recording_family"])


class TestFrameProjection(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "frame.jsonl"

    def save(self, lines):
        self.path.write_bytes(b"".join(lines))
        return PROJECT.fingerprint(self.path)

    def test_physical_lines_preserve_unicode_and_authenticate_complete_unread_payloads(self):
        first = row(artist={"name": "A\u2028B\u2029C", "uuid": identity(100)}) + b"\xff\r\n"
        second = row(2, "venue_sample") + b"not-json\n"
        expected = self.save([first, second])
        result = PROJECT.project_frame(self.path, expected, 2, {"source_sample": 1, "venue_sample": 1})
        self.assertEqual([1, 2], [item["physical_line_number"] for item in result])
        self.assertEqual(hashlib.sha256(first.rstrip(b"\r\n")).hexdigest(), result[0]["physical_line_sha256"])
        self.assertEqual("source_sample:" + identity(1), result[0]["evidence_id"])

    def test_missing_malformed_duplicate_or_wrong_kind_row_never_becomes_partial_success(self):
        cases = [
            ([row() + b"\n"], 2, {"source_sample": 2}),
            ([row() + b"\n", b"\xffbroken\n"], 2, {"source_sample": 2}),
            ([row() + b"\n", row() + b"\n"], 2, {"source_sample": 2}),
            ([row() + b"\n"], 1, {"venue_sample": 1}),
        ]
        for lines, count, kinds in cases:
            with self.subTest(lines=len(lines), count=count, kinds=kinds):
                expected = self.save(lines)
                with self.assertRaises(ValueError):
                    PROJECT.project_frame(self.path, expected, count, kinds)

    def test_changed_payload_bytes_fail_even_when_all_metadata_stays_identical(self):
        expected = self.save([row() + b"original\n"])
        self.path.write_bytes(row() + b"different\n")
        with self.assertRaisesRegex(ValueError, "Changed frozen"):
            PROJECT.project_frame(self.path, expected, 1, {"source_sample": 1})

    def test_change_after_initial_hash_is_detected_by_streaming_authentication(self):
        expected = self.save([row() + b"original\n"])
        def changed_after_hash(path):
            path.write_bytes(row() + b"different\n")
            return expected
        with patch.object(PROJECT, "fingerprint", side_effect=changed_after_hash):
            with self.assertRaisesRegex(ValueError, "changed during"):
                PROJECT.project_frame(self.path, expected, 1, {"source_sample": 1})


class TestSourceClassification(unittest.TestCase):
    def candidates(self, rows):
        return PROJECT.source_candidates(rows, [venue(), venue(2)], len({
            item["source_uuid"] for item in rows if item["source_uuid"] is not None
        }))

    def test_multiple_venue_associations_keep_one_complete_current_source(self):
        result = self.candidates([association(), association(selected=venue(2), association_kind="show_source"),
                                  association(source_id=None, source_uuid=None, association_kind="show_venue")])
        self.assertEqual(1, len(result))
        self.assertEqual(2, len(result[0]["associations"]))
        self.assertEqual(identity(1), result[0]["source_uuid"])

    def test_source_denominator_and_contradictory_current_tuples_fail(self):
        with self.assertRaisesRegex(ValueError, "denominator"):
            PROJECT.source_candidates([association()], [venue()])
        with self.assertRaisesRegex(ValueError, "contradictory"):
            self.candidates([association(), association(show_id=21)])
        for changes in ({"artist_id": 11}, {"show_uuid": None}, {"source_id": 0},
                        {"association_kind": "show_venue"}):
            with self.subTest(changes=changes):
                with self.assertRaises(ValueError):
                    self.candidates([association(**changes)])

    def classify(self, changes=None, blocked=None, candidate=None):
        header = PROJECT.read_metadata_prefix(row(**(changes or {})))
        return PROJECT.classify_sources(
            self.candidates([association(**(candidate or {}))]), [header], blocked or reservation()
        )[0]

    def test_original_development_group_is_preserved_but_never_authorizes_raw_capture(self):
        actual = self.classify()
        self.assertEqual("frozen_development_pending_raw_id_verification", actual["classification"])
        self.assertEqual("a" * 64, actual["original_metadata"]["input_duplicate_family"])
        self.assertFalse(actual["original_numeric_source_id_in_prefix"])
        self.assertFalse(actual["original_numeric_source_id_verified"])
        self.assertFalse(actual["raw_payload_decoded"])
        self.assertFalse(actual["expanded_raw_capture_authorized"])

    def test_heldout_partitions_are_not_reassigned_from_current_ids(self):
        for field in ("artist_partition", "family_partition"):
            with self.subTest(field=field):
                actual = self.classify({field: "test"})
                self.assertEqual("original_heldout_partition", actual["classification"])
                self.assertEqual("test", actual["original_metadata"][field])

    def test_all_reservation_paths_override_development_or_absent_frame(self):
        blocks = [
            ("reserved_references", ["source:101"]),
            ("metadata_matches", [{"kind": "source", "uuid": identity(1)}]),
            ("metadata_linked_parent_sources", [{"kind": "source", "uuid": identity(1)}]),
            ("excluded_annotation_metadata", [{"evidence_id": "source_sample:" + identity(1)}]),
            ("reserved_known_families", ["artist:10:show:20"]),
            ("reserved_annotation_family_groups", ["a" * 64]),
        ]
        for key, value in blocks:
            with self.subTest(key=key):
                blocked = reservation()
                blocked[key] = value
                result = self.classify(blocked=blocked)
                self.assertEqual("reserved_or_excluded", result["classification"])
                self.assertTrue(result["reservation_reasons"])
        blocked = reservation()
        blocked["reserved_references"] = ["source:101"]
        result = PROJECT.classify_sources(self.candidates([association()]), [], blocked)[0]
        self.assertEqual("reserved_or_excluded", result["classification"])

    def test_absence_drift_and_missing_context_remain_distinct(self):
        missing = PROJECT.classify_sources(self.candidates([association()]), [], reservation())[0]
        self.assertEqual("absent_annotation_source_frame", missing["classification"])
        self.assertIsNone(missing["original_metadata"])
        for changes in ({"artist": {"name": "Other", "uuid": identity(999)}},
                        {"known_recording_family": "artist:10:show:21"}):
            self.assertEqual("known_tuple_drift", self.classify(changes)["classification"])
        self.assertEqual("missing_original_family_context", self.classify({"known_recording_family": None})["classification"])
        self.assertEqual("missing_current_show_context", self.classify(candidate={"show_id": None, "show_uuid": None})["classification"])


class TestDiscoveryProvenance(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.base = Path(self.directory.name)
        self.preparation = self.base / "prepared"
        self.discovery = self.base / "discovery"
        self.preparation.mkdir()
        self.discovery.mkdir()
        self.sql = b"synthetic read-only metadata query"
        (self.preparation / "metadata-discovery.sql").write_bytes(self.sql)
        (self.discovery / "executed.sql").write_bytes(self.sql)
        allowlist = json.dumps([venue()]).encode()
        (self.preparation / "venue-allowlist.json").write_bytes(allowlist)
        self.prepared = {"files": {"venue-allowlist.json": PROJECT.bytes_fingerprint(allowlist)}}
        self.query = {
            "sql_sha256": hashlib.sha256(self.sql).hexdigest(), "read_only": True,
            "database": "app", "endpoint": "relisten-db-ro.tail09dbf.ts.net:5432",
        }
        self.rows = [
            {"kind": "snapshot", "read_only": "on", "in_recovery": True},
            {"kind": "venue_association_metadata", "requested_venue_count": 67,
             "authenticated_venue_count": 67, "associations": [association()]},
            {"kind": "snapshot_complete"},
        ]
        self.save()

    def save(self):
        raw = b"".join(json.dumps(item).encode() + b"\n" for item in self.rows)
        (self.discovery / "snapshot.jsonl").write_bytes(raw)
        (self.discovery / "summary.json").write_text(json.dumps({
            "snapshot_sha256": hashlib.sha256(raw).hexdigest(), "association_rows": 1,
        }))
        (self.discovery / "query.json").write_text(json.dumps(self.query))

    def load(self):
        return PROJECT.load_discovery(
            PROJECT.PreparationInputs(), self.discovery, self.preparation, self.prepared
        )

    def test_complete_metadata_capture_returns_exact_original_associations(self):
        actual, venues = self.load()
        self.assertEqual([association()], actual)
        self.assertEqual([venue()], venues)

    def test_different_sql_is_rejected_even_with_refreshed_query_hash(self):
        changed = b"different query"
        (self.discovery / "executed.sql").write_bytes(changed)
        self.query["sql_sha256"] = hashlib.sha256(changed).hexdigest()
        self.save()
        with self.assertRaisesRegex(ValueError, "different query"):
            self.load()

    def test_partial_capture_or_unauthenticated_venue_cannot_classify_sources(self):
        self.rows.pop()
        self.save()
        with self.assertRaisesRegex(ValueError, "Incomplete discovery"):
            self.load()
        self.rows.append({"kind": "snapshot_complete"})
        self.rows[1]["authenticated_venue_count"] = 66
        self.save()
        with self.assertRaisesRegex(ValueError, "authentication is incomplete"):
            self.load()

    def test_changed_capture_or_allowlist_cannot_be_accepted_with_original_receipts(self):
        original = (self.discovery / "snapshot.jsonl").read_bytes()
        (self.discovery / "snapshot.jsonl").write_bytes(original + b"\n")
        with self.assertRaisesRegex(ValueError, "snapshot changed"):
            self.load()
        (self.discovery / "snapshot.jsonl").write_bytes(original)
        (self.preparation / "venue-allowlist.json").write_text(json.dumps([venue(2)]))
        with self.assertRaisesRegex(ValueError, "allowlist changed"):
            self.load()


if __name__ == "__main__":
    unittest.main()
