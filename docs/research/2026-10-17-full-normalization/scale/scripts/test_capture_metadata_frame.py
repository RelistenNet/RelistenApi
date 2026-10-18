"""Exercise the public fixed-profile capture without credentials or database access."""
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

_SCRIPT_DIRECTORY = Path(__file__).resolve().parent
sys.path.insert(0, str(_SCRIPT_DIRECTORY))
_spec = importlib.util.spec_from_file_location(
    "full_metadata_capture_tests_subject", _SCRIPT_DIRECTORY / "capture_metadata_frame.py"
)
_capture = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = _capture
_spec.loader.exec_module(_capture)

STAMP = "2026-10-18T08:47:31+00:00"
ARTIST = "00000000-0000-0000-0000-000000000001"
ZERO_ARTIST = "00000000-0000-0000-0000-000000000002"
SOURCE = "00000000-0000-0000-0000-000000000010"
SHOW = "00000000-0000-0000-0000-000000000020"
TRACK = "00000000-0000-0000-0000-000000000042"
SET = "00000000-0000-0000-0000-000000000030"
SECRET = b"synthetic-credential-must-not-be-exported"

# Independent copy of root's metadata-census.json schema, restricted to the
# three roster tables. No retained source values or adjudication inputs are used.
SCHEMA = {
    "artists": [
        ("id", "integer", "NO"), ("musicbrainz_id", "text", "YES"),
        ("created_at", "timestamp with time zone", "NO"),
        ("updated_at", "timestamp with time zone", "NO"),
        ("name", "text", "NO"), ("featured", "integer", "NO"),
        ("slug", "text", "NO"), ("sort_name", "text", "YES"), ("uuid", "uuid", "NO"),
    ],
    "sources": [
        ("id", "bigint", "NO"), ("show_id", "bigint", "YES"),
        ("is_soundboard", "boolean", "NO"), ("is_remaster", "boolean", "NO"),
        ("avg_rating", "real", "NO"), ("num_reviews", "integer", "NO"),
        ("upstream_identifier", "text", "NO"), ("has_jamcharts", "boolean", "NO"),
        ("description", "text", "YES"), ("taper_notes", "text", "YES"),
        ("source", "text", "YES"), ("taper", "text", "YES"),
        ("transferrer", "text", "YES"), ("lineage", "text", "YES"),
        ("created_at", "timestamp with time zone", "NO"),
        ("updated_at", "timestamp with time zone", "NO"),
        ("artist_id", "integer", "NO"), ("avg_rating_weighted", "real", "YES"),
        ("duration", "real", "YES"), ("venue_id", "bigint", "YES"),
        ("display_date", "text", "NO"), ("num_ratings", "integer", "YES"),
        ("flac_type", "integer", "NO"), ("uuid", "uuid", "NO"),
    ],
    "shows": [
        ("id", "bigint", "NO"), ("artist_id", "integer", "NO"),
        ("tour_id", "bigint", "YES"), ("year_id", "bigint", "YES"),
        ("era_id", "bigint", "YES"), ("date", "date", "NO"),
        ("avg_rating", "real", "YES"), ("avg_duration", "real", "YES"),
        ("display_date", "text", "NO"),
        ("created_at", "timestamp with time zone", "NO"),
        ("updated_at", "timestamp with time zone", "NO"),
        ("venue_id", "integer", "YES"), ("uuid", "uuid", "NO"),
    ],
}


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


def profile(value):
    return {
        "is_null": value is None,
        "characters": None if value is None else len(value),
        "utf8_bytes": None if value is None else len(value.encode("utf-8")),
        "md5_utf8": None if value is None else hashlib.md5(value.encode("utf-8")).hexdigest(),
    }


def seeds():
    return [
        {"catalog_namespace": "catalog-2026-10-17", "track_id": 42, "track_uuid": TRACK},
        {"catalog_namespace": "catalog-2026-10-17", "track_id": 999, "track_uuid": None},
        {"catalog_namespace": "local-catalog", "track_id": 42, "track_uuid": None},
    ]


def lookup(seed, coordinate, found):
    result = dict.fromkeys([
        "id", "uuid", "artist_id", "source_id", "source_uuid", "source_artist_id",
        "source_show_id", "source_show_uuid", "source_set_id", "source_set_uuid",
        "set_source_id", "set_source_uuid", "set_source_artist_id", "set_source_show_id",
        "set_source_show_uuid", "historical_binding_verified",
    ])
    result.update(kind="track_parent_lookup", requested=seed, coordinate=coordinate,
                  lookup_status=("different_catalog_not_queried" if seed["catalog_namespace"] == "local-catalog"
                                 else "found_current_row" if found else "missing_current_row"))
    if found:
        result.update(id=42, uuid=TRACK, artist_id=1, source_id=10, source_uuid=SOURCE,
                      source_artist_id=1, source_show_id=20, source_show_uuid=SHOW,
                      source_set_id=30, source_set_uuid=SET, set_source_id=10,
                      set_source_uuid=SOURCE, set_source_artist_id=1,
                      set_source_show_id=20, set_source_show_uuid=SHOW)
    return result


def rows(lookup_seeds=None):
    lookup_seeds = seeds() if lookup_seeds is None else lookup_seeds
    result = [{
        "kind": "metadata_snapshot", "version": "full-catalog-metadata-frame-v1",
        "captured_at": STAMP, "snapshot": "123:123:", "read_only": "on",
        "isolation": "repeatable read", "server_version": "16.4",
        "server_encoding": "UTF8", "client_encoding": "UTF8", "in_recovery": True,
        "replay_lsn": "0/123456", "last_replayed_transaction_at": STAMP,
        "provider_attribution": "unavailable_on_sources", "date_precision": "unavailable",
        "knowledge_clock": "unavailable", "text_profile_algorithm": "md5_utf8_not_identity_proof",
        "content_derived_metadata_computed": True,
        "server_source_processing": "nullness_length_md5_only", "semantic_parsing": False,
    }, {"kind": "metadata_counts", "artists": 2, "sources": 1, "shows": 1}]
    for table in sorted(SCHEMA):
        result.extend({"kind": "metadata_columns", "table_name": table, "column_name": name,
                       "data_type": data_type, "is_nullable": nullable}
                      for name, data_type, nullable in SCHEMA[table])
    for native_id, uuid, source_count in [(1, ARTIST, 1), (2, ZERO_ARTIST, 0)]:
        result.append({"kind": "artist", "id": native_id, "uuid": uuid,
                       "created_at": STAMP, "updated_at": STAMP, "featured": 0,
                       "source_count": source_count,
                       "text_profiles": {"name": profile("aé"), "slug": profile(""),
                                         "sort_name": profile(None), "musicbrainz_id": profile(None)}})
    result.append({
        "kind": "source", "id": 10, "uuid": SOURCE, "artist_id": 1, "artist_uuid": ARTIST,
        "show_id": 20, "show_uuid": SHOW, "show_artist_id": 1, "venue_id": None,
        "show_venue_id": None, "created_at": STAMP, "updated_at": STAMP,
        "is_soundboard": False, "is_remaster": False, "has_jamcharts": False,
        "avg_rating": 0.0, "num_reviews": 0, "avg_rating_weighted": None,
        "duration": None, "num_ratings": None, "stored_flac_type": 0,
        "provider": None, "verified_audio_format": None, "knowledge_at": None,
        "input_revision_uuid": None, "input_generation": None,
        "text_profiles": {name: profile("" if name in ("upstream_identifier", "display_date") else None)
                          for name in ("upstream_identifier", "display_date", "description", "taper_notes",
                                       "source", "taper", "transferrer", "lineage")},
    })
    result.append({
        "kind": "show", "id": 20, "uuid": SHOW, "artist_id": 1, "artist_uuid": ARTIST,
        "venue_id": None, "tour_id": None, "year_id": None, "era_id": None,
        "catalog_sort_date": "1970-01-01", "event_date_precision": None,
        "avg_rating": None, "avg_duration": None, "created_at": STAMP, "updated_at": STAMP,
        "text_profiles": {"display_date": profile("")},
    })
    for seed in lookup_seeds:
        for coordinate in ("native_id", "uuid"):
            if seed["track_id" if coordinate == "native_id" else "track_uuid"] is not None:
                result.append(lookup(seed, coordinate,
                                     seed["catalog_namespace"] == "catalog-2026-10-17" and seed["track_id"] == 42))
    result.append({"kind": "snapshot_complete", "completed_at": STAMP})
    return result


class FakeQuery:
    """A real byte stream and process exit; only the external transport is replaced."""
    def __init__(self, content, returncode=0, on_wait=None):
        self.stdout = io.BytesIO(content)
        self.returncode = None
        self.exit_code = returncode
        self.on_wait = on_wait
        self.stopped = False

    def wait(self, timeout=None):
        if self.on_wait is not None:
            callback, self.on_wait = self.on_wait, None
            callback()
        self.returncode = self.exit_code
        return self.returncode

    def poll(self):
        return self.returncode

    def terminate(self):
        self.stopped = True
        self.returncode = -15

    def kill(self):
        self.stopped = True
        self.returncode = -9

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.stdout.close()


class TestCaptureMetadataFrame(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.directory = self.root / "frame"
        self.seed_path = self.root / "lookup-seeds.json"
        self.write_seeds(seeds())

    def write_seeds(self, lookup_seeds, **extra):
        artifact = {"version": "full-metadata-track-lookup-seeds-v1", "seeds": lookup_seeds,
                    "authorities": [{"path": "metadata/original-reservation-receipt.json",
                                     "bytes": 123, "sha256": "a" * 64}], **extra}
        self.seed_path.write_bytes(json_bytes(artifact))
        self.seed_sha = hashlib.sha256(self.seed_path.read_bytes()).hexdigest()

    @contextlib.contextmanager
    def transport(self, content, returncode=0, on_wait=None):
        process = FakeQuery(content, returncode, on_wait)
        credential = subprocess.CompletedProcess(["kubectl"], 0, stdout=base64.b64encode(SECRET))
        with patch.object(_capture.subprocess, "run", return_value=credential) as run:
            with patch.object(_capture.subprocess, "Popen", return_value=process) as popen:
                yield process, run, popen

    def capture(self, directory=None):
        return _capture.capture(directory or self.directory, self.seed_path, self.seed_sha)

    def assert_not_completed(self):
        self.assertFalse((self.directory / "manifest.json").exists())
        self.assertFalse((self.directory / "frame.jsonl").exists())

    def assert_no_leak(self, forbidden):
        for path in self.directory.rglob("*"):
            if path.is_file():
                self.assertNotIn(forbidden, path.read_bytes(), path.name)

    def test_complete_stream_preserves_zero_artists_qualified_nulls_and_exact_bytes(self):
        content = b"".join(map(json_bytes, rows()))
        with self.transport(content) as (_, run, popen):
            report = self.capture()
        manifest = json.loads((self.directory / "manifest.json").read_bytes())
        self.assertEqual(report, manifest)
        self.assertEqual("completed_metadata_only", report["state"])
        artifact = report["metadataFrame"]
        saved = self.directory / artifact["path"]
        self.assertEqual(content, saved.read_bytes())
        self.assertEqual(len(content), artifact["bytes"])
        self.assertEqual(hashlib.sha256(content).hexdigest(), artifact["sha256"])
        self.assertEqual({"artists": 2, "sources": 1, "shows": 1}, report["sameSnapshotCounts"])
        self.assertEqual(self.seed_sha, report["lookupSeeds"]["sha256"])
        self.assertEqual(3, report["lookupSeeds"]["seedCount"])
        self.assertEqual(4, report["lookupSeeds"]["coordinateCount"])
        self.assertEqual("externally_pinned_plan;references_not_reopened",
                         report["lookupSeeds"]["authorityAuthentication"])
        self.assertTrue(report["header"]["content_derived_metadata_computed"])
        self.assertFalse(report["header"]["semantic_parsing"])
        decoded = [json.loads(line) for line in saved.read_bytes().splitlines()]
        self.assertEqual([1, 0], [r["source_count"] for r in decoded if r["kind"] == "artist"])
        source = next(r for r in decoded if r["kind"] == "source")
        self.assertIsNone(source["provider"])
        self.assertIsNone(source["knowledge_at"])
        self.assertIsNone(source["text_profiles"]["description"]["md5_utf8"])
        self.assertEqual(profile(""), source["text_profiles"]["upstream_identifier"])
        lookups = [r for r in decoded if r["kind"] == "track_parent_lookup"]
        self.assertEqual(4, len(lookups))
        local = next(r for r in lookups if r["requested"]["catalog_namespace"] == "local-catalog")
        self.assertEqual("different_catalog_not_queried", local["lookup_status"])
        self.assertIsNone(local["source_id"])
        self.assertTrue(all(r["historical_binding_verified"] is None for r in lookups))
        self.assertEqual(1, popen.call_count)
        self.assertEqual(1, run.call_count)
        self.assert_no_leak(SECRET)
        self.assert_no_leak(base64.b64encode(SECRET))

    def test_catalog_sentinels_and_postgres_date_literals_are_retained_without_new_authority(self):
        cases = [(-1, "infinity", "-infinity"), (0, "-infinity", "infinity"),
                 (-1, "10000-01-02", "10000-01-02T03:04:05+00:00"),
                 (0, "0001-01-01 BC", "0001-01-01T03:04:05+00:00 BC")]
        for index, (native_id, catalog_date, timestamp) in enumerate(cases):
            with self.subTest(native_id=native_id, catalog_date=catalog_date):
                self.directory = self.root / ("catalog-literal-" + str(index))
                data = rows()
                artist_positions = [i for i, row in enumerate(data) if row["kind"] == "artist"]
                normal, zero_source = (data[i] for i in artist_positions)
                zero_source.update(id=native_id, uuid="00000000-0000-0000-0000-000000000000")
                data[artist_positions[0]], data[artist_positions[1]] = zero_source, normal
                source = next(row for row in data if row["kind"] == "source")
                source["created_at"] = timestamp
                show = next(row for row in data if row["kind"] == "show")
                show["catalog_sort_date"] = catalog_date
                with self.transport(b"".join(map(json_bytes, data))):
                    report = self.capture()
                saved = [json.loads(line) for line in (self.directory / "frame.jsonl").read_bytes().splitlines()]
                first_artist = next(row for row in saved if row["kind"] == "artist")
                self.assertEqual(native_id, first_artist["id"])
                self.assertEqual("00000000-0000-0000-0000-000000000000", first_artist["uuid"])
                self.assertEqual(0, first_artist["source_count"])
                self.assertEqual(1, report["joinDiagnostics"]["artists_without_sources"])
                self.assertEqual(timestamp, next(row for row in saved if row["kind"] == "source")["created_at"])
                saved_show = next(row for row in saved if row["kind"] == "show")
                self.assertEqual(catalog_date, saved_show["catalog_sort_date"])
                self.assertIsNone(saved_show["event_date_precision"])

    def test_empty_seed_list_requires_explicit_authority_but_is_a_complete_frame(self):
        self.write_seeds([])
        with self.transport(b"".join(map(json_bytes, rows([])))):
            report = self.capture()
        self.assertEqual("completed_metadata_only", report["state"])

    def test_seed_authentication_and_closed_profile_fail_before_transport_or_output(self):
        valid = self.seed_path.read_bytes()
        malformed = [
            (valid, "0" * 64),
            (b"not-json", hashlib.sha256(b"not-json").hexdigest()),
            (json_bytes({"version": "full-metadata-track-lookup-seeds-v1", "seeds": [], "authorities": []}), None),
            (json_bytes({"version": "full-metadata-track-lookup-seeds-v1", "seeds": seeds(),
                         "authorities": [], "targets": ["forbidden"]}), None),
            (json_bytes({"version": "full-metadata-track-lookup-seeds-v1", "seeds": [seeds()[0], seeds()[0]],
                         "authorities": [{"path": "metadata/receipt.json", "bytes": 1, "sha256": "a" * 64}]}), None),
        ]
        for content, sha in malformed:
            with self.subTest(sha=sha, bytes=len(content)):
                self.seed_path.write_bytes(content)
                self.seed_sha = sha or hashlib.sha256(content).hexdigest()
                with patch.object(_capture.subprocess, "run") as run, patch.object(_capture.subprocess, "Popen") as popen:
                    with self.assertRaises(_capture.FrameCaptureError):
                        self.capture()
                    run.assert_not_called()
                    popen.assert_not_called()
                self.assertFalse(self.directory.exists())

    def test_unsafe_plaintext_and_oversized_lines_never_enter_artifacts_or_diagnostics(self):
        sentinel = b"PRIVATE-SYNTHETIC-SOURCE-NOT-METADATA"
        cases = [(json_bytes({"kind": "metadata_snapshot", "description": sentinel.decode()}), "invalid_fields"),
                 (b'{"raw":"' + sentinel + b"x" * 65536 + b'"}\n', "metadata_line_capacity_exceeded")]
        for index, (content, expected_code) in enumerate(cases):
            self.directory = self.root / ("unsafe-" + str(index))
            output = io.StringIO()
            with self.transport(content), contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                with self.assertRaises(_capture.FrameCaptureError) as failure:
                    self.capture()
            self.assert_not_completed()
            self.assertEqual(expected_code, failure.exception.code)
            self.assertNotIn(sentinel.decode(), str(failure.exception))
            self.assertNotIn(sentinel.decode(), output.getvalue())
            self.assert_no_leak(sentinel)

    def test_footer_does_not_grant_completion_when_commit_process_fails(self):
        content = b"".join(map(json_bytes, rows()))
        with self.transport(content, returncode=1):
            with self.assertRaises(_capture.FrameCaptureError):
                self.capture()
        self.assert_not_completed()
        self.assertEqual(content, (self.directory / "frame.partial.jsonl").read_bytes())
        failure = json.loads((self.directory / "failure.json").read_bytes())
        self.assertEqual("query_process_failed", failure["reasonCode"])
        self.assertEqual(1, failure["processExitCode"])
        self.assert_no_leak(SECRET)

    def test_directory_sync_refusal_cannot_publish_a_completion_marker(self):
        content = b"".join(map(json_bytes, rows()))
        real_fsync = os.fsync
        refusal = "SYNTHETIC_PRIVATE_DIRECTORY_SYNC_ERROR"

        def sync(fd):
            if stat.S_ISDIR(os.fstat(fd).st_mode):
                raise OSError(refusal)
            return real_fsync(fd)

        with self.transport(content), patch.object(_capture.os, "fsync", side_effect=sync):
            with self.assertRaises(_capture.FrameCaptureError) as failure:
                self.capture()
        self.assertEqual("capture_failed", failure.exception.code)
        self.assertFalse((self.directory / "manifest.json").exists())
        # The validated data may already have its final name; only the final marker
        # grants completion. Retain those bytes for diagnosis rather than erase them.
        self.assertEqual(content, (self.directory / "frame.jsonl").read_bytes())
        receipt = json.loads((self.directory / "failure.json").read_bytes())
        self.assertEqual("failed", receipt["state"])
        self.assertEqual("capture_failed", receipt["reasonCode"])
        self.assertEqual(0, receipt["processExitCode"])
        self.assert_no_leak(refusal.encode("utf-8"))

    def test_incomplete_or_contradictory_metadata_does_not_complete(self):
        def missing_source(data):
            return [r for r in data if r["kind"] != "source"]

        def duplicate_artist(data):
            index = next(i for i, r in enumerate(data) if r["kind"] == "artist")
            data.insert(index, dict(data[index]))
            return data

        def wrong_count(data):
            next(r for r in data if r["kind"] == "metadata_counts")["sources"] = 2
            return data

        def omitted_coordinate(data):
            index = next(i for i, r in enumerate(data) if r["kind"] == "track_parent_lookup")
            data.pop(index)
            return data

        def wrong_namespace_claim(data):
            row = next(r for r in data if r["kind"] == "track_parent_lookup" and
                       r["requested"]["catalog_namespace"] == "local-catalog")
            row.update(lookup(row["requested"], row["coordinate"], True))
            row["lookup_status"] = "found_current_row"
            return data

        for name, mutate in [("missing-source", missing_source), ("duplicate", duplicate_artist),
                             ("count", wrong_count), ("seed", omitted_coordinate),
                             ("local-inference", wrong_namespace_claim), ("missing-footer", lambda data: data[:-1])]:
            with self.subTest(name=name):
                self.directory = self.root / name
                with self.transport(b"".join(map(json_bytes, mutate(rows())))):
                    with self.assertRaises(_capture.FrameCaptureError):
                        self.capture()
                self.assert_not_completed()

    def test_seed_mutation_after_transport_prevents_completion(self):
        def mutate():
            self.seed_path.write_bytes(self.seed_path.read_bytes() + b" ")
        with self.transport(b"".join(map(json_bytes, rows())), on_wait=mutate):
            with self.assertRaises(_capture.FrameCaptureError):
                self.capture()
        self.assert_not_completed()

    def test_failed_partial_requires_new_directory_and_completed_output_is_immutable(self):
        with self.transport(b"".join(map(json_bytes, rows()[:-1]))):
            with self.assertRaises(_capture.FrameCaptureError):
                self.capture()
        partial_before = {p.name: p.read_bytes() for p in self.directory.iterdir() if p.is_file()}
        with patch.object(_capture.subprocess, "run") as run, patch.object(_capture.subprocess, "Popen") as popen:
            with self.assertRaises(_capture.FrameCaptureError) as failure:
                self.capture()
            self.assertEqual("output_directory_exists", failure.exception.code)
            run.assert_not_called()
            popen.assert_not_called()
        self.assertEqual(partial_before, {p.name: p.read_bytes() for p in self.directory.iterdir() if p.is_file()})
        retry = self.root / "retry"
        with self.transport(b"".join(map(json_bytes, rows()))):
            self.capture(retry)
        before = {p.name: p.read_bytes() for p in retry.iterdir() if p.is_file()}
        with patch.object(_capture.subprocess, "run") as run:
            with self.assertRaises(_capture.FrameCaptureError) as failure:
                self.capture(retry)
            self.assertEqual("output_directory_exists", failure.exception.code)
            run.assert_not_called()
        self.assertEqual(before, {p.name: p.read_bytes() for p in retry.iterdir() if p.is_file()})


if __name__ == "__main__":
    unittest.main()
