"""Synthetic public capture/verification contracts; no provider or catalog access."""
import copy
import errno
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from uuid import UUID

import capture_domain_metadata_frame as capture
import domain_metadata_frame_validation as validation
from domain_metadata_frame_validation import DomainMetadataFrameValidator, FrameCaptureError

STAMP = "2026-10-18T00:00:00+00:00"


def stable(number):
    return str(UUID(int=number))


def profile(value):
    if value is None:
        return {"is_null": True, "characters": None, "utf8_bytes": None, "md5_utf8": None}
    raw = value.encode("utf-8")
    return {"is_null": False, "characters": len(value), "utf8_bytes": len(raw),
            "md5_utf8": hashlib.md5(raw).hexdigest()}


def entity(kind, native, **fields):
    row = {"kind": kind, "id": native, "uuid": stable(abs(native)), "created_at": STAMP, "updated_at": STAMP}
    row.update(fields)
    if kind in validation.PROFILE_FIELDS:
        row["text_profiles"] = {field: profile("") for field in validation.PROFILE_FIELDS[kind]}
    return row


def records():
    header = {
        "kind": "metadata_snapshot", "version": validation.VERSION, "captured_at": STAMP,
        "snapshot": "123:124:", "read_only": "on", "isolation": "repeatable read",
        "server_version": "17.6", "server_encoding": "UTF8", "client_encoding": "UTF8",
        "in_recovery": True, "replay_lsn": "0/ABC", "last_replayed_transaction_at": STAMP,
        "provider_attribution": "unavailable", "date_precision": "unavailable",
        "knowledge_clock": "unavailable", "text_profile_algorithm": "md5_utf8_not_identity_proof",
        "content_derived_metadata_computed": True, "server_source_processing": "nullness_length_md5_only",
        "semantic_parsing": False, "performance_interpretation": "unavailable",
        "play_association_interpretation": "unordered_multiset", "exposure_history": "unavailable",
    }
    rows = [
        entity("artist", 1), entity("artist", 2),
        entity("source", 10, artist_id=1, artist_uuid=stable(1), show_id=20, show_uuid=stable(20), venue_id=30, venue_uuid=stable(30)),
        entity("source", 11, artist_id=2, artist_uuid=stable(2), show_id=None, show_uuid=None, venue_id=999, venue_uuid=None),
        entity("show", 20, artist_id=1, artist_uuid=stable(1), venue_id=30, venue_uuid=stable(30), catalog_sort_date="1970-01-01", event_date_precision=None),
        entity("venue", 30, artist_id=1, artist_uuid=stable(1), latitude=None, longitude=None),
        entity("source_set", 40, source_id=11, source_uuid=stable(11), index=0, is_encore=False),
        entity("source_set", 41, source_id=999, source_uuid=None, index=1, is_encore=True),
        entity("source_track", 50, artist_id=1, artist_uuid=stable(1), source_id=10, source_uuid=stable(10), source_set_id=40, source_set_uuid=stable(40), track_position=0, duration=None, is_orphaned=False),
        entity("source_track", 51, artist_id=None, artist_uuid=None, source_id=999, source_uuid=None, source_set_id=999, source_set_uuid=None, track_position=-1, duration=0, is_orphaned=True),
        entity("source_track", 52, artist_id=None, artist_uuid=None, source_id=None, source_uuid=None, source_set_id=41, source_set_uuid=stable(41), track_position=2, duration=30, is_orphaned=True),
        entity("setlist_show", 60, artist_id=1, artist_uuid=stable(1), venue_id=30, venue_uuid=stable(30), tour_id=None, era_id=None, catalog_sort_date="1970-01-01", event_date_precision=None),
        entity("setlist_show", 61, artist_id=2, artist_uuid=stable(2), venue_id=999, venue_uuid=None, tour_id=99, era_id=None, catalog_sort_date="1970-01-01", event_date_precision=None),
        entity("setlist_song", 70, artist_id=2, artist_uuid=stable(2)),
        entity("setlist_song", 71, artist_id=1, artist_uuid=stable(1)),
        {"kind": "setlist_song_play", "played_setlist_song_id": 70, "played_setlist_song_uuid": stable(70), "played_setlist_show_id": 60, "played_setlist_show_uuid": stable(60), "multiplicity": 2},
        {"kind": "setlist_song_play", "played_setlist_song_id": 71, "played_setlist_song_uuid": stable(71), "played_setlist_show_id": 999, "played_setlist_show_uuid": None, "multiplicity": 1},
        {"kind": "setlist_song_play", "played_setlist_song_id": 999, "played_setlist_song_uuid": None, "played_setlist_show_id": 61, "played_setlist_show_uuid": stable(61), "multiplicity": 1},
    ]
    rows[5]["text_profiles"]["name"] = profile(None)
    rows[8]["text_profiles"]["title"] = profile("e\u0301🐢")
    counts = {table: sum(row["kind"] == kind for row in rows) for kind, table in validation.ROSTER_TABLES.items()}
    counts["setlist_song_play_groups"] = counts["setlist_songs_plays"]
    counts["setlist_songs_plays"] = 4
    columns = [{"kind": "metadata_columns", "table_name": table, "column_name": field,
                "data_type": data_type, "is_nullable": nullable}
               for table, field, data_type, nullable in validation.EXPECTED_COLUMNS]
    return [header, {"kind": "metadata_counts", **counts}, *columns, *rows,
            {"kind": "snapshot_complete", "completed_at": STAMP}]


def encoded(rows):
    return b"".join((json.dumps(row, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8") for row in rows)


def storage_capacity(available=None, inodes=1_000_000):
    free = capture.MINIMUM_FREE_FILESYSTEM_BYTES + 1024**3 if available is None else available
    return SimpleNamespace(f_bavail=free, f_frsize=1, f_favail=inodes)


def more_than_one_storage_interval():
    rows = records()
    last_track = max(i for i, row in enumerate(rows) if row["kind"] == "source_track")
    template = rows[last_track]
    added = []
    for native in range(1000, 1000 + capture.STORAGE_CHECK_INTERVAL_ROWS):
        row = copy.deepcopy(template)
        row["id"], row["uuid"] = native, stable(native)
        added.append(row)
    rows[last_track + 1:last_track + 1] = added
    rows[1]["source_tracks"] += len(added)
    return rows


class FakeProcess:
    def __init__(self, raw, code=0):
        self.stdout = io.BytesIO(raw)
        self.code = code
        self.returncode = None

    def wait(self, timeout=None):
        if self.returncode is None:
            self.returncode = self.code
        return self.returncode

    def poll(self):
        return self.returncode

    def terminate(self):
        self.returncode = -15

    def kill(self):
        self.returncode = -9


class TestDomainMetadataFrame(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def reconcile(self, rows):
        validator = DomainMetadataFrameValidator(self.directory / "index.sqlite")
        try:
            for line in encoded(rows).splitlines(keepends=True):
                validator.accept(line)
            return validator.finish()
        finally:
            validator.close()

    def test_complete_current_parent_graph_preserves_orphans_and_disagreements(self):
        summary = self.reconcile(records())
        self.assertEqual(3, summary["sameSnapshotCounts"]["source_tracks"])
        self.assertEqual(4, summary["sameSnapshotCounts"]["setlist_songs_plays"])
        self.assertEqual(3, summary["rowsByKind"]["setlist_song_play"])
        diagnostics = summary["joinDiagnostics"]
        self.assertEqual(1, diagnostics["source_track_source_id_null"])
        self.assertEqual(1, diagnostics["source_track_source_id_dangling"])
        self.assertEqual(1, diagnostics["source_track_source_set_id_dangling"])
        self.assertEqual(1, diagnostics["tracks_direct_and_set_source_disagree"])
        self.assertEqual(1, diagnostics["tracks_artist_and_set_source_disagree"])
        self.assertEqual(1, diagnostics["setlist_song_play_played_setlist_song_id_dangling"])
        self.assertEqual(1, diagnostics["setlist_song_play_played_setlist_show_id_dangling"])
        self.assertEqual(2, diagnostics["play_rows_song_and_show_artist_disagree"])
        self.assertEqual(1, diagnostics["play_groups_song_and_show_artist_disagree"])
        self.assertEqual(1, diagnostics["duplicate_play_groups"])

    def test_empty_rosters_are_complete_without_invented_parent_rows(self):
        rows = [row for row in records() if row["kind"] not in validation.ROSTER_TABLES]
        rows[1] = {"kind": "metadata_counts", **{key: 0 for key in rows[1] if key != "kind"}}
        summary = self.reconcile(rows)
        self.assertTrue(all(value == 0 for value in summary["sameSnapshotCounts"].values()))
        self.assertTrue(all(value == 0 for value in summary["joinDiagnostics"].values()))

    def test_wrong_joined_uuid_is_not_misreported_as_a_catalog_orphan(self):
        rows = records()
        next(row for row in rows if row["kind"] == "source_track")["source_set_uuid"] = stable(900)
        with self.assertRaisesRegex(FrameCaptureError, "parent_tuple_mismatch"):
            self.reconcile(rows)

    def test_duplicate_native_rows_cannot_satisfy_complete_counts(self):
        rows = records()
        position = next(i for i, row in enumerate(rows) if row["kind"] == "source_track")
        rows.insert(position, copy.deepcopy(rows[position]))
        with self.assertRaisesRegex(FrameCaptureError, "duplicate_or_unordered_key"):
            self.reconcile(rows)

    def test_different_native_rows_with_equal_uuid_remain_visible(self):
        rows = records()
        artists = [row for row in rows if row["kind"] == "artist"]
        artists[1]["uuid"] = artists[0]["uuid"]
        for row in rows:
            if row.get("artist_id") == 2:
                row["artist_uuid"] = artists[0]["uuid"]
        summary = self.reconcile(rows)
        self.assertEqual(1, summary["joinDiagnostics"]["artist_duplicate_uuid_groups"])

    def test_multiplicity_is_exact_and_not_replaced_by_group_count(self):
        rows = records()
        next(row for row in rows if row["kind"] == "setlist_song_play")["multiplicity"] = 1
        with self.assertRaisesRegex(FrameCaptureError, "play_multiplicity_mismatch"):
            self.reconcile(rows)

    def test_association_order_identity_and_raw_text_are_forbidden(self):
        for extra in ("id", "position", "set_id", "title"):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as temporary:
                validator = DomainMetadataFrameValidator(Path(temporary) / "index.sqlite")
                try:
                    rows = records()
                    next(row for row in rows if row["kind"] == "setlist_song_play")[extra] = "PRIVATE_SENTINEL"
                    with self.assertRaisesRegex(FrameCaptureError, "invalid_fields"):
                        for line in encoded(rows).splitlines(keepends=True):
                            validator.accept(line)
                finally:
                    validator.close()

    def test_missing_domain_row_or_column_or_footer_refuses_completion(self):
        for remove_kind in ("source_track", "metadata_columns", "snapshot_complete"):
            with self.subTest(remove_kind=remove_kind), tempfile.TemporaryDirectory() as temporary:
                validator = DomainMetadataFrameValidator(Path(temporary) / "index.sqlite")
                try:
                    rows = records()
                    rows.pop(next(i for i, row in enumerate(rows) if row["kind"] == remove_kind))
                    with self.assertRaises(FrameCaptureError):
                        for line in encoded(rows).splitlines(keepends=True):
                            validator.accept(line)
                        validator.finish()
                finally:
                    validator.close()

    def test_profile_cannot_embed_raw_text_or_erase_nonnullable_title(self):
        for replacement in ({"raw": "PRIVATE"}, profile(None)):
            with self.subTest(replacement=replacement), tempfile.TemporaryDirectory() as temporary:
                rows = records()
                next(row for row in rows if row["kind"] == "source_track")["text_profiles"]["title"] = replacement
                validator = DomainMetadataFrameValidator(Path(temporary) / "index.sqlite")
                try:
                    with self.assertRaises(FrameCaptureError):
                        for line in encoded(rows).splitlines(keepends=True):
                            validator.accept(line)
                finally:
                    validator.close()

    def test_signed_zero_coordinates_and_postgres_dates_are_preserved(self):
        rows = records()
        artists = [row for row in rows if row["kind"] == "artist"]
        artists[0]["id"], artists[0]["uuid"] = -2, stable(0)
        for row in rows:
            if row.get("artist_id") == 1:
                row["artist_id"], row["artist_uuid"] = -2, stable(0)
            if row["kind"] == "show":
                row["catalog_sort_date"] = "12000-01-01 BC"
        self.assertEqual(2, self.reconcile(rows)["sameSnapshotCounts"]["artists"])

    def test_nonpositive_raw_parent_pointers_are_dangling_metadata(self):
        cases = (
            ("source", "artist_id", "artist_uuid", -2),
            ("source", "show_id", "show_uuid", 0),
            ("source", "venue_id", "venue_uuid", -2),
            ("source_set", "source_id", "source_uuid", 0),
            ("source_track", "source_id", "source_uuid", 0),
            ("source_track", "source_set_id", "source_set_uuid", -2),
            ("source_track", "artist_id", "artist_uuid", -2),
            ("setlist_show", "venue_id", "venue_uuid", 0),
            ("setlist_song", "artist_id", "artist_uuid", -2),
            ("setlist_song_play", "played_setlist_song_id", "played_setlist_song_uuid", 0),
            ("setlist_song_play", "played_setlist_show_id", "played_setlist_show_uuid", -2),
        )
        for kind, pointer, joined, value in cases:
            with self.subTest(kind=kind, pointer=pointer), tempfile.TemporaryDirectory() as temporary:
                rows = records()
                row = next(row for row in rows if row["kind"] == kind)
                row[pointer], row[joined] = value, None
                validator = DomainMetadataFrameValidator(Path(temporary) / "index.sqlite")
                try:
                    for line in encoded(rows).splitlines(keepends=True):
                        validator.accept(line)
                    summary = validator.finish()
                    self.assertGreaterEqual(summary["joinDiagnostics"][f"{kind}_{pointer}_dangling"], 1)
                    self.assertEqual(3, summary["sameSnapshotCounts"]["source_tracks"])
                finally:
                    validator.close()

    def test_profiles_keep_unicode_bytes_distinct_from_codepoints(self):
        value = next(row for row in records() if row["kind"] == "source_track")["text_profiles"]["title"]
        self.assertEqual((3, 7), (value["characters"], value["utf8_bytes"]))
        self.assertEqual(3, self.reconcile(records())["sameSnapshotCounts"]["source_tracks"])

    def test_replica_and_unknown_interpretation_are_required(self):
        for field, value in (("in_recovery", False), ("semantic_parsing", True), ("exposure_history", "untouched"),
                             ("play_association_interpretation", "ordered_occurrences")):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temporary:
                rows = records()
                rows[0][field] = value
                validator = DomainMetadataFrameValidator(Path(temporary) / "index.sqlite")
                try:
                    with self.assertRaises(FrameCaptureError):
                        validator.accept(encoded(rows[:1]))
                finally:
                    validator.close()

    def test_native_count_overflow_fails_before_roster_retention(self):
        rows = records()
        rows[1]["source_tracks"] = validation.MAXIMUM_ROWS
        with self.assertRaisesRegex(FrameCaptureError, "metadata_row_capacity_exceeded"):
            self.reconcile(rows)

    def test_parent_tuple_forgery_for_missing_parent_is_rejected(self):
        rows = records()
        next(row for row in rows if row["kind"] == "source_track" and row["id"] == 51)["source_uuid"] = stable(999)
        with self.assertRaisesRegex(FrameCaptureError, "parent_tuple_mismatch"):
            self.reconcile(rows)


class TestDomainMetadataCapture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        healthy_storage = patch.object(capture.os, "statvfs", return_value=storage_capacity())
        healthy_storage.start()
        self.addCleanup(healthy_storage.stop)

    def capture(self, rows=None, code=0, name="capture"):
        raw = encoded(records() if rows is None else rows)
        with patch.object(capture, "_start_query", return_value=FakeProcess(raw, code)):
            return capture.capture(self.directory / name)

    def test_capture_publishes_exact_stream_and_offline_verifier_reconciles_it(self):
        report = self.capture()
        directory = self.directory / "capture"
        self.assertEqual(encoded(records()), (directory / "frame.jsonl").read_bytes())
        pin = capture.transport.fingerprint(directory / "manifest.json")["sha256"]
        with patch.object(capture, "_start_query", side_effect=AssertionError("offline verification opened transport")):
            verified = capture.verify(directory, pin, self.directory / "verification.sqlite")
        self.assertEqual("verified_metadata_only", verified["state"])
        self.assertEqual(report["sameSnapshotCounts"], verified["counts"])

    def test_plaintext_line_never_reaches_partial_or_error_receipt(self):
        rows = records()
        next(row for row in rows if row["kind"] == "source_track")["text_profiles"]["title"]["raw"] = "PRIVATE_SENTINEL"
        with self.assertRaisesRegex(FrameCaptureError, "invalid_fields"):
            self.capture(rows)
        directory = self.directory / "capture"
        self.assertFalse((directory / "manifest.json").exists())
        self.assertNotIn(b"PRIVATE_SENTINEL", (directory / "frame.partial.jsonl").read_bytes())
        self.assertNotIn(b"PRIVATE_SENTINEL", (directory / "failure.json").read_bytes())

    def test_footer_with_failed_commit_does_not_complete_and_retry_needs_new_directory(self):
        with self.assertRaisesRegex(FrameCaptureError, "query_process_failed"):
            self.capture(code=1)
        self.assertFalse((self.directory / "capture/manifest.json").exists())
        with patch.object(capture, "_start_query", side_effect=AssertionError("existing directory opened transport")):
            with self.assertRaisesRegex(FrameCaptureError, "output_directory_exists"):
                capture.capture(self.directory / "capture")
        self.assertEqual("completed_metadata_only", self.capture(name="new-snapshot")["state"])

    def test_changed_template_or_executed_sql_refuses_completion(self):
        for filename in ("template.sql", "executed.sql"):
            with self.subTest(filename=filename):
                template = self.directory / "template.sql"
                template.write_bytes(capture.SQL_PATH.read_bytes())
                output = self.directory / filename.removesuffix(".sql")
                process = FakeProcess(encoded(records()))
                def change_after_query(timeout=None):
                    target = template if filename == "template.sql" else output / filename
                    with target.open("ab") as stream:
                        stream.write(b"-- changed while the process ran\n")
                    process.returncode = 0
                    return 0
                process.wait = change_after_query
                with patch.object(capture, "SQL_PATH", template), patch.object(capture, "_start_query", return_value=process):
                    with self.assertRaises(FrameCaptureError):
                        capture.capture(output)
                self.assertFalse((output / "manifest.json").exists())

    def test_changed_retained_frame_refuses_completion(self):
        output = self.directory / "capture"
        process = FakeProcess(encoded(records()))
        def change_after_query(timeout=None):
            with (output / "frame.partial.jsonl").open("ab") as stream:
                stream.write(b"{}\n")
            process.returncode = 0
            return 0
        process.wait = change_after_query
        with patch.object(capture, "_start_query", return_value=process):
            with self.assertRaisesRegex(FrameCaptureError, "retained_frame_changed"):
                capture.capture(output)
        self.assertFalse((output / "manifest.json").exists())

    def test_interruption_retains_no_completion_and_has_safe_reason(self):
        process = FakeProcess(encoded(records()))
        process.stdout.readline = lambda size: (_ for _ in ()).throw(KeyboardInterrupt())
        with patch.object(capture, "_start_query", return_value=process):
            with self.assertRaises(KeyboardInterrupt):
                capture.capture(self.directory / "interrupted")
        output = self.directory / "interrupted"
        self.assertFalse((output / "manifest.json").exists())
        self.assertEqual("interrupted", json.loads((output / "failure.json").read_bytes())["reasonCode"])
        self.assertEqual(-15, process.returncode)

    def test_deadline_failure_cannot_publish_even_a_complete_stream(self):
        class ImmediateDeadline:
            def __init__(self, seconds, callback):
                self.callback = callback
            def start(self):
                self.callback()
            def cancel(self):
                pass
        with patch.object(capture.threading, "Timer", ImmediateDeadline):
            with self.assertRaisesRegex(FrameCaptureError, "query_deadline_exceeded"):
                self.capture()
        self.assertFalse((self.directory / "capture/manifest.json").exists())

    def test_failed_final_publication_leaves_no_authoritative_completion(self):
        original_link = capture.os.link
        def fail_manifest_link(source, target):
            if Path(target).name == "manifest.json":
                raise OSError("synthetic publication failure")
            original_link(source, target)
        with patch.object(capture.os, "link", side_effect=fail_manifest_link):
            with self.assertRaisesRegex(FrameCaptureError, "capture_failed"):
                self.capture()
        output = self.directory / "capture"
        self.assertTrue((output / "frame.jsonl").exists())
        self.assertFalse((output / "manifest.json").exists())
        self.assertTrue((output / "failure.json").exists())

    def test_line_output_and_index_bounds_refuse_whole_capture(self):
        with patch.object(capture, "MAXIMUM_OUTPUT_BYTES", 20):
            with self.assertRaisesRegex(FrameCaptureError, "metadata_byte_capacity_exceeded"):
                self.capture(name="byte-overflow")
        with patch.object(capture, "MAXIMUM_LINE_BYTES", 20):
            with self.assertRaisesRegex(FrameCaptureError, "metadata_line_capacity_exceeded"):
                self.capture(name="line-overflow")
        # A tiny real SQLite page budget proves disk allocation refusal; no in-memory
        # roster or truncation is substituted when the durable index fills.
        with patch.object(validation, "MAXIMUM_INDEX_BYTES", 4096):
            with self.assertRaises(FrameCaptureError):
                self.capture(name="index-overflow")
        for name in ("byte-overflow", "line-overflow", "index-overflow"):
            self.assertFalse((self.directory / name / "manifest.json").exists())

    def test_verifier_rejects_tampered_stream_and_pinned_incomplete_manifest(self):
        self.capture()
        directory = self.directory / "capture"
        pin = capture.transport.fingerprint(directory / "manifest.json")["sha256"]
        with (directory / "frame.jsonl").open("ab") as stream:
            stream.write(b"{}\n")
        with self.assertRaisesRegex(FrameCaptureError, "artifact_changed"):
            capture.verify(directory, pin, self.directory / "verify-tampered.sqlite")
        with self.assertRaisesRegex(FrameCaptureError, "manifest_changed"):
            capture.verify(directory, "0" * 64, self.directory / "verify-wrong-pin.sqlite")

    def test_verifier_never_overwrites_capture_or_existing_index(self):
        self.capture()
        directory = self.directory / "capture"
        pin = capture.transport.fingerprint(directory / "manifest.json")["sha256"]
        existing = self.directory / "existing.sqlite"
        existing.write_bytes(b"prior-index")
        with self.assertRaisesRegex(FrameCaptureError, "index_exists"):
            capture.verify(directory, pin, existing)
        self.assertEqual(b"prior-index", existing.read_bytes())
        with self.assertRaisesRegex(FrameCaptureError, "verification_index_inside_capture"):
            capture.verify(directory, pin, directory / "new-index.sqlite")

    def test_fixed_transport_discards_pg_overrides_and_postgres_stderr(self):
        with patch.dict(os.environ, {"PGHOSTADDR": "attacker", "PGSERVICE": "attacker", "PGPASSFILE": "attacker"}), \
             patch.object(capture.transport, "_credentials", return_value="synthetic-password"), \
             patch.object(capture.subprocess, "Popen") as popen:
            capture._start_query(Path("executed.sql"))
        args, kwargs = popen.call_args
        self.assertIn(capture.ENDPOINT, args[0])
        self.assertNotIn("attacker", kwargs["env"].values())
        self.assertNotIn("PGPASSWORD", kwargs["env"])
        self.assertIs(capture.subprocess.DEVNULL, kwargs["stderr"])


class TestDomainMetadataStorage(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def publish_fixture(self):
        output = self.directory / "completed"
        with patch.object(capture, "_start_query", return_value=FakeProcess(encoded(records()))), \
             patch.object(capture.os, "statvfs", return_value=storage_capacity()):
            capture.capture(output)
        return output, capture.transport.fingerprint(output / "manifest.json")["sha256"]

    def test_capture_refuses_low_space_before_creating_output_or_transport(self):
        output = self.directory / "new" / "capture"
        with patch.object(capture.os, "statvfs", return_value=storage_capacity(available=0)), \
             patch.object(capture, "_start_query", side_effect=AssertionError("transport started")):
            with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                capture.capture(output)
        self.assertFalse(output.parent.exists())

    def test_capture_midstream_floor_refusal_keeps_only_validated_prefix(self):
        output = self.directory / "capture"
        process = FakeProcess(encoded(more_than_one_storage_interval()))
        def capacity_after_prefix(path):
            prefix = output / "frame.partial.jsonl"
            return storage_capacity(available=0) if prefix.exists() and prefix.stat().st_size > 65536 else storage_capacity()
        with patch.object(capture.os, "statvfs", side_effect=capacity_after_prefix), \
             patch.object(capture, "_start_query", return_value=process):
            with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                capture.capture(output)
        prefix = (output / "frame.partial.jsonl").read_bytes().splitlines()
        self.assertEqual(capture.STORAGE_CHECK_INTERVAL_ROWS, len(prefix))
        self.assertTrue(all(json.loads(line)["kind"] != "snapshot_complete" for line in prefix))
        self.assertFalse((output / "manifest.json").exists())
        self.assertEqual(-15, process.returncode)
        self.assertEqual("local_storage_reserve_exhausted", json.loads((output / "failure.json").read_bytes())["reasonCode"])

    def test_capture_rechecks_reserve_after_reconciliation_before_publication(self):
        output = self.directory / "capture"
        original = capture._completion_report
        ready = False
        def finish_report(*args):
            nonlocal ready
            report = original(*args)
            ready = True
            return report
        with patch.object(capture, "_start_query", return_value=FakeProcess(encoded(records()))), \
             patch.object(capture, "_completion_report", side_effect=finish_report), \
             patch.object(capture.os, "statvfs", side_effect=lambda path: storage_capacity(available=0) if ready else storage_capacity()):
            with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                capture.capture(output)
        self.assertFalse((output / "manifest.json").exists())
        self.assertTrue((output / "frame.partial.jsonl").exists())

    def test_verifier_refuses_before_index_creation_and_preserves_capture(self):
        output, pin = self.publish_fixture()
        index = self.directory / "verify.sqlite"
        with patch.object(capture.os, "statvfs", return_value=storage_capacity(available=0)):
            with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                capture.verify(output, pin, index)
        self.assertFalse(index.exists())
        self.assertEqual(pin, capture.transport.fingerprint(output / "manifest.json")["sha256"])

    def test_verifier_midstream_refusal_retains_partial_index_without_success(self):
        output = self.directory / "completed"
        with patch.object(capture, "_start_query", return_value=FakeProcess(encoded(more_than_one_storage_interval()))), \
             patch.object(capture.os, "statvfs", return_value=storage_capacity()):
            capture.capture(output)
        pin = capture.transport.fingerprint(output / "manifest.json")["sha256"]
        index = self.directory / "verify.sqlite"
        original_accept = DomainMetadataFrameValidator.accept
        accepted = 0
        def count_accepted(validator, line):
            nonlocal accepted
            original_accept(validator, line)
            accepted += 1
        with patch.object(DomainMetadataFrameValidator, "accept", count_accepted), \
             patch.object(capture.os, "statvfs", side_effect=lambda path: storage_capacity(available=0) if accepted >= capture.STORAGE_CHECK_INTERVAL_ROWS else storage_capacity()):
            with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                capture.verify(output, pin, index)
        self.assertEqual(capture.STORAGE_CHECK_INTERVAL_ROWS, accepted)
        self.assertTrue(index.exists())
        self.assertEqual(pin, capture.transport.fingerprint(output / "manifest.json")["sha256"])

    def test_verifier_rechecks_reserve_before_returning_success(self):
        output, pin = self.publish_fixture()
        index = self.directory / "verify.sqlite"
        original = DomainMetadataFrameValidator.finish
        reconciled = False
        def finish(validator):
            nonlocal reconciled
            result = original(validator)
            reconciled = True
            return result
        with patch.object(DomainMetadataFrameValidator, "finish", finish), \
             patch.object(capture.os, "statvfs", side_effect=lambda path: storage_capacity(available=0) if reconciled else storage_capacity()):
            with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                capture.verify(output, pin, index)
        self.assertTrue(index.exists())
        self.assertEqual(pin, capture.transport.fingerprint(output / "manifest.json")["sha256"])

    def test_same_filesystem_is_checked_once_and_distinct_filesystem_is_checked(self):
        frame, index = Path("/frame-device"), Path("/index-device")
        with patch.object(capture, "_existing_storage", side_effect=[(frame, 1), (index, 1)]), \
             patch.object(capture.os, "statvfs", return_value=storage_capacity()) as statvfs:
            capture._check_storage((frame, index))
        self.assertEqual([frame], [call.args[0] for call in statvfs.call_args_list])
        for low in (frame, index):
            with self.subTest(low=low), \
                 patch.object(capture, "_existing_storage", side_effect=[(frame, 1), (index, 2)]), \
                 patch.object(capture.os, "statvfs", side_effect=lambda path: storage_capacity(available=0) if path == low else storage_capacity()):
                with self.assertRaisesRegex(FrameCaptureError, "local_storage_reserve_exhausted"):
                    capture._check_storage((frame, index))

    def test_exhausted_inodes_and_stat_errors_fail_without_capture_completion(self):
        for name, outcome, reason in (
            ("inodes", storage_capacity(inodes=0), "local_storage_inodes_exhausted"),
            ("stat-error", OSError(errno.EIO, "synthetic IO failure"), "local_storage_check_failed"),
        ):
            output = self.directory / name
            with self.subTest(name=name), patch.object(capture.os, "statvfs", side_effect=outcome if isinstance(outcome, OSError) else None,
                                                     return_value=outcome):
                with self.assertRaisesRegex(FrameCaptureError, reason):
                    capture.capture(output)
            self.assertFalse(output.exists())

    def test_allocation_errors_keep_capture_incomplete_without_deleting_prefix(self):
        for error in (errno.ENOSPC, errno.EDQUOT):
            output = self.directory / str(error)
            original = capture.transport._new_bytes
            def fail_allocation(path, value):
                if Path(path).name == "executed.sql":
                    raise OSError(error, "synthetic allocation failure")
                original(path, value)
            with self.subTest(error=error), patch.object(capture.os, "statvfs", return_value=storage_capacity()), \
                 patch.object(capture.transport, "_new_bytes", side_effect=fail_allocation), \
                 patch.object(capture, "_start_query", side_effect=AssertionError("transport started")):
                with self.assertRaisesRegex(FrameCaptureError, "capture_failed"):
                    capture.capture(output)
            self.assertFalse((output / "manifest.json").exists())
            self.assertTrue((output / "sql-template.sql").exists())


if __name__ == "__main__":
    unittest.main()
