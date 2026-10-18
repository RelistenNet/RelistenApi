"""Immutable reader contracts using disposable caches and loopback HTTP servers."""

import gzip
import itertools
import json
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest
import uuid
from unittest.mock import patch
from contextlib import closing
from pathlib import Path
from http.server import ThreadingHTTPServer
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import urlopen

from shadow_reader import ShadowIndex, handler_for


class TestShadowReader(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.run = Path(self.temp.name)
        self.index = ShadowIndex(self.run)

    @staticmethod
    def record(key="source_sample:one", domain="people", **changes):
        record = {
            "domain": domain, "inputKey": key, "inputKind": "source_sample",
            "inputUuid": "source-one", "artistUuid": "artist-one", "artistName": "Grateful Dead",
            "provider": "archive.org", "outcome": "proposed", "reason": None, "method": "actual-local-v1",
            "entityKeys": ["person:shared"],
            "proposal": {"people": [{"name": "Alice Vale", "aliases": ["Night Owl"]}]},
            "evidence": {"originalInputs": {"taper": "Alice Vale (alice@example.test)"},
                         "citation": {"quote": "Alice Vale", "startCodePoint": 0, "endCodePoint": 10}},
        }
        record.update(changes)
        return record

    def chunk(self, name, *records, domain="people", writing=False):
        directory = self.run / domain
        directory.mkdir(exist_ok=True)
        path = directory / (name + ".jsonl.gz" + (".writing" if writing else ""))
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for record in records:
                stream.write(json.dumps(record, ensure_ascii=False) + "\n")
        return path

    @staticmethod
    def native_file(ordinal, title, **changes):
        key = "native_file:observed-" + str(ordinal)
        file = {
            "fileOrdinal": ordinal, "entityKey": key, "inputRevisionUuid": "revision-one",
            "recordingOrigin": {"scope": "resource_item", "evidence": {"jsonPointer": "/files"}},
            "file": {"title": {"value": title, "evidence": {"jsonPointer": "/files/" + str(ordinal) + "/title"}},
                     "filename": {"value": "track-" + str(ordinal) + ".flac"},
                     "playbackLocator": {"value": "track-" + str(ordinal) + ".flac"}},
            "titleInterpretation": {"originalTitle": title}, "mentions": [],
            "outcome": "unresolved", "reason": "unknown_song", "entityKeys": [key, "source:recording-one"],
            "catalogPlayback": None,
        }
        file.update(changes)
        return file

    def native_record(self, *files, **changes):
        return self.record("provider_input:revision-one", domain="file_contents", inputKind="provider_input",
                           proposal={"files": list(files), "decoderIssues": ["partial input"]}, **changes)

    def test_native_file_search_reaches_late_titles_and_keeps_legacy_files_in_their_original_index(self):
        children = [self.native_file(n, "Earlier " * 1000 if n == 0 else "Ordinary track") for n in range(30)]
        children[-1] = self.native_file(29, "Faraway Nebula Finale", entityKeys=["repertoire:shared-song"])
        parent = self.native_record(*children)
        self.chunk("100", parent, self.record("track:legacy", domain="file_contents", inputKind="track_sample"),
                   domain="file_contents")
        self.index.scan_once()
        self.assertEqual(self.index.search({"domain": "file_contents"})["total"], 2)
        self.assertEqual(self.index.stats()["nativeFileCoverage"], {"indexedFiles": 30, "indexedParents": 1})
        matched = self.index.search_files({"q": "NEBU FINA", "entityKey": "repertoire:shared-song"})
        self.assertEqual(matched["total"], 1)
        item = matched["items"][0]
        self.assertEqual(item["fileOrdinal"], 29)
        detail = self.index.file_detail(item["parentId"], item["nativeFileKey"])
        self.assertEqual(detail["file"], children[-1])
        self.assertEqual(detail["parent"]["inputKey"], parent["inputKey"])
        self.assertEqual(detail["playback"], [])
        self.assertIsNone(detail["providerFileUrl"])
        first = self.index.search_files({"limit": "7"})
        second = self.index.search_files({"limit": "7", "offset": "7"})
        self.assertEqual([x["fileOrdinal"] for x in first["items"] + second["items"]], list(range(14)))
        self.assertEqual(self.index.search_files({"q": '" NEBULA OR ORDINARY *'})["total"], 0)
        self.assertEqual(self.index.search_files({"q": '" *'})["total"], 0)

    def test_native_file_http_separates_provider_links_from_explicit_catalog_audio(self):
        phish = self.native_file(0, "Unbound Phish passage")
        phish["file"]["playbackLocator"]["value"] = "https://media.example.test/native.mp3"
        local = self.native_file(1, "Bound Local passage", catalogPlayback={
            "fileUuid": "catalog-file", "sourceUuid": "catalog-source", "mp3Url": "https://audio.example.test/catalog.mp3"})
        unsafe = self.native_file(2, "Unsafe locator")
        unsafe["file"]["playbackLocator"]["value"] = "javascript:alert(1)"
        malformed = self.native_file(3, "Malformed URL", catalogPlayback={"mp3Url": "https://media.example.test:invalid/file.mp3"})
        malformed["file"]["playbackLocator"]["value"] = "https://media.example.test:invalid/file.mp3"
        self.chunk("100", self.native_record(phish, local, unsafe, malformed), domain="file_contents")
        self.index.scan_once()
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.index))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        base = "http://127.0.0.1:" + str(server.server_port)
        try:
            with urlopen(base + "/api/files?provider=archive.org&artist=artist-one&outcome=unresolved") as response:
                page = json.load(response)
            self.assertEqual(page["total"], 4)
            details = []
            for item in page["items"]:
                with urlopen(base + "/api/file?" + urlencode({"parentId": item["parentId"], "nativeFileKey": item["nativeFileKey"]})) as response:
                    details.append(json.load(response))
            self.assertEqual(details[0]["providerFileUrl"], "https://media.example.test/native.mp3")
            self.assertEqual(details[0]["playback"], [])
            self.assertEqual(details[1]["playback"], ["https://audio.example.test/catalog.mp3"])
            self.assertIsNone(details[1]["providerFileUrl"])
            self.assertIsNone(details[2]["providerFileUrl"])
            self.assertIsNone(details[3]["providerFileUrl"])
            self.assertEqual(details[3]["playback"], [])
            self.assertEqual([d["file"] for d in details], [phish, local, unsafe, malformed])
            with self.assertRaises(HTTPError) as missing:
                urlopen(base + "/api/file?parentId=999999&nativeFileKey=native_file%3Amissing")
            self.assertEqual(missing.exception.code, 404)
            missing.exception.close()
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_native_file_replay_replaces_titles_memberships_and_ordinals_across_restart(self):
        original = self.native_file(0, "Old Song", entityKeys=["repertoire:old"])
        self.chunk("100", self.native_record(original), domain="file_contents")
        self.index.scan_once()
        parent_id = self.index.search_files({})["items"][0]["parentId"]
        replacement = self.native_file(0, "New Song", entityKeys=["repertoire:new"])
        self.chunk("200", self.native_record(replacement, self.native_file(1, "Bonus Song")), domain="file_contents")
        self.index.scan_once()
        restarted = ShadowIndex(self.run)
        self.assertEqual(restarted.search_files({"q": "Old"})["total"], 0)
        self.assertEqual(restarted.search_files({"entityKey": "repertoire:old"})["total"], 0)
        self.assertEqual(restarted.search_files({"entityKey": "repertoire:new"})["total"], 1)
        self.assertEqual(restarted.file_detail(parent_id, replacement["entityKey"])["file"], replacement)
        self.chunk("050", self.native_record(original), domain="file_contents")
        restarted.scan_once()
        self.assertEqual(restarted.search_files({})["total"], 2)
        self.assertEqual(restarted.search_files({"q": "Old"})["total"], 0)
        self.chunk("300", self.native_record(), domain="file_contents")
        restarted.scan_once()
        self.assertEqual(restarted.search_files({})["total"], 0)
        with self.assertRaises(KeyError):
            restarted.file_detail(parent_id, replacement["entityKey"])

    def test_existing_native_parent_cache_upgrade_preserves_originals_and_excludes_missing_chunks(self):
        child = self.native_file(0, "Retained native title")
        path = self.chunk("100", self.native_record(child), domain="file_contents")
        self.index.scan_once()
        parent_id = self.index.search_files({})["items"][0]["parentId"]
        for missing in (False, True):
            with self.subTest(missing=missing):
                if missing:
                    path.unlink()
                with closing(self.index.connect()) as db:
                    db.executescript("""DROP TRIGGER native_files_insert; DROP TRIGGER native_files_delete;
                        DROP TABLE native_file_entities; DROP TABLE native_file_search; DROP TABLE native_files;""")
                upgraded = ShadowIndex(self.run)
                self.assertEqual(upgraded.search_files({})["total"], 0 if missing else 1)
                if not missing:
                    self.assertEqual(upgraded.file_detail(parent_id, child["entityKey"])["file"], child)
                else:
                    self.assertEqual(upgraded.stats()["failedChunks"], 1)
                    self.assertEqual(upgraded.search({})["total"], 0)

    def test_bad_native_child_rolls_back_parent_and_children_and_removed_chunk_hides_them(self):
        original = self.native_file(0, "Original Song")
        self.chunk("100", self.native_record(original), domain="file_contents")
        self.index.scan_once()
        bad = self.native_file(3, "Wrong physical ordinal")
        self.chunk("200", self.native_record(self.native_file(0, "Replacement Song"), bad), domain="file_contents")
        self.index.scan_once()
        self.assertEqual(self.index.search_files({"q": "Original"})["total"], 1)
        self.assertEqual(self.index.search_files({"q": "Replacement"})["total"], 0)
        self.assertEqual(self.index.stats()["nativeFileCoverage"]["indexedFiles"], 1)
        item = self.index.search_files({})["items"][0]
        (self.run / "file_contents" / "100.jsonl.gz").unlink()
        self.index.scan_once()
        self.assertEqual(self.index.search_files({})["total"], 0)
        self.assertEqual(self.index.stats()["nativeFileCoverage"]["indexedFiles"], 0)
        with self.assertRaises(KeyError):
            self.index.file_detail(item["parentId"], item["nativeFileKey"])

    def test_growing_run_becomes_searchable_without_rebuilding_previous_chunks(self):
        first = self.record()
        self.chunk("100", first)
        self.index.scan_once()
        first_id = self.index.search({})["items"][0]["id"]
        self.assertEqual(self.index.detail(first_id)["record"], first)
        self.chunk("200", self.record("source_sample:two", artistName="Phish", artistUuid="artist-two"))
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 2)
        self.assertEqual(self.index.detail(first_id)["record"], first)

    def test_file_reader_configuration_is_operator_owned_and_preserves_exact_entity_keys(self):
        keys = ["source:00274cc4-c9d0-bb87-0dcd-87588219bd9c",
                "show:8a8dca64-c99c-1aa2-a731-d0895e60f37f",
                "file:3984d0c0-d828-3fcf-801b-70b4f7d06f85"]
        record = self.record(entityKeys=keys)
        self.chunk("100", record)
        self.index.scan_once()
        self.assertIsNone(self.index.stats()["fileReaderUrl"])
        configured = ShadowIndex(self.run, file_reader_url="https://files.example.test:9443/")
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(configured))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        base = "http://127.0.0.1:" + str(server.server_port)
        try:
            with urlopen(base + "/api/stats?fileReaderUrl=https://untrusted.example.test") as response:
                self.assertEqual(json.load(response)["fileReaderUrl"], "https://files.example.test:9443")
            identifier = configured.search({})["items"][0]["id"]
            with urlopen(base + "/api/record?id=" + str(identifier)) as response:
                self.assertEqual(json.load(response)["record"]["entityKeys"], keys)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_invalid_file_reader_origin_fails_before_creating_a_cache_or_serving(self):
        invalid = ["", "files.example.test", "javascript:alert(1)", "ftp://files.example.test",
                   "https://user:secret@files.example.test", "https://@files.example.test",
                   "https://files.example.test/files", "https://files.example.test?", "https://files.example.test#",
                   "https://files.example.test:invalid", "https://files.example.test:65536",
                   "https://files.example.test\\evil", "https://files.example.test\n"]
        for origin in invalid:
            with self.subTest(origin=origin), self.assertRaises(ValueError):
                ShadowIndex(self.run, file_reader_url=origin)
        untouched = self.run / "untouched"
        untouched.mkdir()
        result = subprocess.run([sys.executable, str(Path(__file__).with_name("shadow_reader.py")),
                                 "--run", str(untouched), "--port", "0",
                                 "--file-reader-url", "https://files.example.test/recordings"],
                                capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 2)
        self.assertEqual(list(untouched.iterdir()), [])
        self.assertEqual(self.index.stats()["pendingChunks"], 0)

    def test_low_disk_space_defers_new_chunks_without_hiding_existing_results_and_recovers(self):
        first = self.record()
        self.chunk("100", first)
        self.index.scan_once()
        first_id = self.index.search({})["items"][0]["id"]
        second = self.record("source_sample:two")
        self.chunk("200", second)
        self.index.minimum_free_bytes = 1024

        with patch("shadow_reader.shutil.disk_usage") as available:
            available.return_value.free = 512
            self.index.scan_once()
            self.assertEqual(self.index.search({})["total"], 1)
            self.assertEqual(self.index.detail(first_id)["record"], first)
            self.assertEqual(self.index.stats()["pendingChunks"], 1)

            available.return_value.free = 2048
            self.index.scan_once()
            self.index.scan_once()

        self.assertEqual(self.index.search({})["total"], 2)
        self.assertEqual(self.index.detail(first_id)["record"], first)
        second_id = self.index.search({"q": second["inputKey"]})["items"][0]["id"]
        self.assertEqual(self.index.detail(second_id)["record"], second)
        self.assertEqual(self.index.stats()["pendingChunks"], 0)

    def test_operator_pause_keeps_http_results_and_resumes_changed_input_on_restart(self):
        first = self.record()
        self.chunk("100", first)
        self.index.scan_once()
        identifier = self.index.search({})["items"][0]["id"]
        changed = self.record(entityKeys=["person:replacement"],
                              proposal={"people": [{"name": "Corrected Alice"}]})
        self.chunk("200", changed)
        paused = ShadowIndex(self.run, pause_indexing=True)
        paused.scan_once()
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(paused))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = "http://127.0.0.1:" + str(server.server_port)

        try:
            with urlopen(url + "/api/stats", timeout=5) as response:
                stats = json.load(response)
            self.assertEqual(stats["indexedRecords"], 1)
            self.assertEqual(stats["pendingChunks"], 1)
            self.assertTrue(stats["index"]["pauseReason"])
            with urlopen(url + "/api/record?id=" + str(identifier), timeout=5) as response:
                self.assertEqual(json.load(response)["record"], first)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

        resumed = ShadowIndex(self.run)
        resumed.scan_once()
        self.assertEqual(resumed.search({})["total"], 1)
        self.assertEqual(resumed.detail(identifier)["record"], changed)
        self.assertEqual(resumed.search({"entityKey": "person:shared"})["total"], 0)
        self.assertEqual(resumed.search({"entityKey": "person:replacement"})["total"], 1)
        self.assertEqual(resumed.stats()["pendingChunks"], 0)

    def test_interrupted_sqlite_read_returns_503_and_a_later_http_query_recovers(self):
        self.chunk("100", *(self.record("source_sample:" + str(number)) for number in range(1000)))
        self.index.scan_once()
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.index))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = "http://127.0.0.1:" + str(server.server_port) + "/api/records?domain=people&limit=1"

        try:
            # The real SQLite progress callback sees the deadline pass without a
            # wall-clock delay. No database exception or HTTP response is mocked.
            with patch("shadow_reader.time.monotonic", side_effect=itertools.count(step=100)):
                with self.assertRaises(HTTPError) as failed:
                    urlopen(url, timeout=5)
                self.assertEqual(failed.exception.code, 503)
                self.assertTrue(json.loads(failed.exception.read())["error"])
                failed.exception.close()

            with urlopen(url, timeout=5) as response:
                recovered = json.load(response)
                self.assertEqual(response.status, 200)
                self.assertEqual(recovered["total"], 1000)
                self.assertEqual(len(recovered["items"]), 1)
            self.assertEqual(self.index.search({})["total"], 1000)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_latest_logical_record_wins_replays_and_replaces_entity_membership(self):
        self.chunk("200", self.record())
        self.index.scan_once()
        original_id = self.index.search({})["items"][0]["id"]
        latest = self.record(outcome="unresolved", entityKeys=["person:replacement"],
                             proposal={"people": [{"name": "Corrected Alice"}]})
        self.chunk("300", self.record(), latest)
        self.index.scan_once()
        # Late discovery of an older completed chunk must not resurrect old proof.
        self.chunk("100", self.record(outcome="failed"))
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 1)
        self.assertEqual(self.index.detail(original_id)["record"], latest)
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 0)
        self.assertEqual(self.index.search({"entityKey": "person:replacement"})["total"], 1)

    def test_filters_and_entity_lookup_join_artists_and_domains(self):
        self.chunk("100", self.record())
        credit = self.record("source_sample:two", "credits", artistUuid="artist-two", artistName="Phish",
                             provider="taperssection", outcome="unresolved")
        self.chunk("100", credit, domain="credits")
        self.index.scan_once()
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 2)
        filtered = self.index.search({"provider": "taperssection", "artist": "Phish", "domain": "credits",
                                      "outcome": "unresolved", "entityKey": "person:shared"})
        self.assertEqual(filtered["total"], 1)
        self.assertEqual(filtered["items"][0]["artist_uuid"], "artist-two")
        self.assertEqual(self.index.search({"artist": "artist-one"})["total"], 1)
        self.assertEqual(self.index.search({"q": "Night Owl"})["total"], 2)
        self.assertEqual(self.index.search({"provider": "' OR 1=1 --"})["total"], 0)

    def test_writing_chunk_is_invisible_until_completed_atomic_rename(self):
        path = self.chunk("100", self.record(), writing=True)
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 0)
        path.rename(path.with_suffix(""))
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 1)

    def test_invalid_chunk_rolls_back_prefix_and_independent_later_chunk_survives(self):
        directory = self.run / "people"
        directory.mkdir()
        with gzip.open(directory / "100.jsonl.gz", "wt", encoding="utf-8") as stream:
            stream.write(json.dumps(self.record()) + "\n{bad-json}\n")
        self.chunk("200", self.record("source_sample:two"))
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 1)
        self.assertEqual(self.index.search({"q": "source_sample:one"})["total"], 0)
        failures = [chunk for chunk in self.index.stats()["chunks"] if chunk["state"] == "failed"]
        self.assertEqual(len(failures), 1)

    def test_detail_preserves_original_proof_while_sqlite_retains_only_summary_and_pointer(self):
        record = self.record()
        original = "PRIVATE-ORIGINAL-" + "prose " * 12000
        record["evidence"]["originalInputs"]["description"] = original
        with closing(self.index.connect()) as db:
            empty_cache_bytes = db.execute("PRAGMA page_count").fetchone()[0] * db.execute("PRAGMA page_size").fetchone()[0]
        self.chunk("100", record)
        self.index.scan_once()
        item = self.index.search({})["items"][0]
        detail = self.index.detail(item["id"])
        self.assertEqual(detail["record"], record)
        self.assertEqual(detail["record"]["evidence"]["citation"]["quote"], "Alice Vale")
        self.assertEqual(self.index.search({"q": "PRIVATE-ORIGINAL"})["total"], 0)
        with closing(sqlite3.connect(self.index.database)) as db:
            retained = " ".join(str(value) for row in db.execute("SELECT * FROM records") for value in row)
            self.assertNotIn("PRIVATE-ORIGINAL", retained)
        with closing(self.index.connect()) as db:
            populated_cache_bytes = db.execute("PRAGMA page_count").fetchone()[0] * db.execute("PRAGMA page_size").fetchone()[0]
        self.assertLess(populated_cache_bytes - empty_cache_bytes, len(original))

    def test_changed_completed_chunk_cannot_serve_substituted_original_evidence(self):
        path = self.chunk("100", self.record())
        self.index.scan_once()
        item = self.index.search({})["items"][0]
        with path.open("ab") as stream:
            stream.write(b"changed")
        with self.assertRaisesRegex(ValueError, "unchanged"):
            self.index.detail(item["id"])
        self.index.scan_once()
        self.assertEqual(self.index.stats()["chunks"][0]["state"], "failed")
        self.assertEqual(self.index.search({})["total"], 0)
        self.assertEqual(self.index.stats()["indexedRecords"], 0)

    def test_existing_cache_migration_and_replay_keep_coverage_equal_to_visible_results(self):
        first = self.record()
        second = self.record("source_sample:two")
        original = self.chunk("100", first, first, second)
        self.index.scan_once()
        identifier = self.index.search({"q": first["inputKey"]})["items"][0]["id"]
        self.assertEqual(self.index.stats()["indexedRecords"], 2)
        # Historical v3 caches have records and chunk pointers but no aggregate
        # table. Upgrade those existing pointers; do not reread or duplicate chunks.
        with closing(self.index.connect()) as db:
            db.execute("DROP TABLE chunk_facets")
            db.commit()
        migrated = ShadowIndex(self.run)
        self.assertEqual(migrated.stats()["indexedRecords"], 2)
        self.assertEqual(migrated.detail(identifier)["record"], first)

        replacement = self.record(outcome="unresolved", provider="taperssection",
                                  artistUuid="artist-two", artistName="Phish")
        self.chunk("200", replacement, replacement)
        migrated.scan_once()
        stats = migrated.stats()
        self.assertEqual(stats["indexedRecords"], 2)
        self.assertEqual({item["value"]: item["count"] for item in stats["facets"]["outcome"]},
                         {"proposed": 1, "unresolved": 1})
        self.assertEqual({item["value"]: item["count"] for item in stats["facets"]["provider"]},
                         {"archive.org": 1, "taperssection": 1})
        self.assertEqual(migrated.detail(identifier)["record"], replacement)

        original.unlink()
        migrated.scan_once()
        self.assertEqual(migrated.stats()["indexedRecords"], 1)
        self.assertEqual(migrated.search({})["total"], 1)
        self.assertEqual(migrated.detail(identifier)["record"], replacement)

    def test_checkpoint_progress_is_separate_from_visible_unique_results_and_lag(self):
        self.chunk("100", self.record(), self.record("source_sample:two"))
        self.index.scan_once()
        progress = {"state": "running", "counts": [{"provider": "archive.org", "domain": "people", "processed": 3}],
                    "population": {"expectedOutcomes": {"archive.org|people": 10, "taperssection|credits": 4}}}
        (self.run / "progress.json").write_text(json.dumps(progress))
        stats = self.index.stats()
        row = next(row for row in stats["coverage"] if row["provider"] == "archive.org")
        self.assertEqual((row["expected"], row["processed"], row["remaining"], row["indexed"], row["indexLag"]),
                         (10, 3, 7, 2, 1))
        pending = next(row for row in stats["coverage"] if row["provider"] == "taperssection")
        self.assertEqual((pending["expected"], pending["processed"], pending["remaining"]), (4, 0, 4))

    def test_search_treats_percent_and_underscore_as_literal_text(self):
        self.chunk("100", self.record(proposal={"title": "100%_Jam"}),
                   self.record("source_sample:two", proposal={"title": "Plain Jam"}))
        self.index.scan_once()
        self.assertEqual(self.index.search({"q": "%_"})["total"], 1)
        self.assertEqual(self.index.search({"q": "plain JAM"})["total"], 1)

    def test_same_date_files_have_distinct_literal_titles_and_keep_date_search_context(self):
        expected = {}
        for domain in ("repertoire", "file_contents"):
            records = []
            for ordinal, title in enumerate(("Dark Star >", "St. Stephen (reprise)")):
                interpretation = {"originalTitle": title, "segments": [{"title": "Song mention"}]}
                proposal = interpretation if domain == "repertoire" else {
                    "home": {"literalDate": "1987-11-27", "performanceLabel": "late"},
                    "titleInterpretation": interpretation,
                }
                records.append(self.record("track_sample:" + str(ordinal), domain,
                                           inputKind="track_sample", inputUuid="file-" + str(ordinal),
                                           proposal=proposal))
                expected[domain, "file-" + str(ordinal)] = records[-1]
            self.chunk("100", *records, domain=domain)
        self.index.scan_once()

        for domain in ("repertoire", "file_contents"):
            with self.subTest(domain=domain):
                items = self.index.search({"domain": domain})["items"]
                self.assertEqual({item["label"] for item in items},
                                 {"Dark Star >", "St. Stephen (reprise)"})
                result = self.index.search({"domain": domain, "q": "St. Stephen"})
                self.assertEqual(result["total"], 1)
                detail = self.index.detail(result["items"][0]["id"])["record"]
                self.assertEqual(detail, expected[domain, "file-1"])

        self.assertEqual(self.index.search({"domain": "file_contents", "q": "1987-11-27"})["total"], 2)
        self.assertEqual(self.index.search({"domain": "file_contents", "q": "late"})["total"], 2)
        self.assertEqual(self.index.search({"domain": "file_contents", "q": "Song mention"})["total"], 2)

    def test_missing_file_title_keeps_recording_label_and_original_detail(self):
        record = self.record("track_sample:one", "file_contents", inputKind="track_sample",
                             proposal={"home": {"literalDate": "1987-11-27"},
                                       "titleInterpretation": None})
        self.chunk("100", record, domain="file_contents")
        self.index.scan_once()
        item = self.index.search({"domain": "file_contents"})["items"][0]
        self.assertEqual(item["label"], "1987-11-27")
        self.assertEqual(self.index.detail(item["id"])["record"], record)

    def test_playback_returns_only_explicit_http_audio_urls_without_fetching_them(self):
        record = self.record(proposal={"playback": {"mp3Url": "https://archive.example.test/file.mp3"},
                                       "alternative": {"mp3Url": "javascript:alert(1)"},
                                       "unrelated": {"url": "https://example.test/not-audio"},
                                       "malformed": {"mp3_url": "https://[broken/audio.mp3"}})
        self.chunk("100", record)
        self.index.scan_once()
        item = self.index.search({})["items"][0]
        self.assertEqual(self.index.detail(item["id"])["playback"], ["https://archive.example.test/file.mp3"])

    def test_index_restart_keeps_pointer_identity_without_duplicating_results(self):
        self.chunk("100", self.record())
        self.index.scan_once()
        first = self.index.search({})["items"]
        reopened = ShadowIndex(self.run)
        reopened.scan_once()
        self.assertEqual(reopened.search({})["items"], first)
        self.assertEqual(reopened.detail(first[0]["id"])["record"], self.record())

    def test_removed_chunk_leaves_visible_index_and_keeps_other_chunks_available(self):
        removed = self.chunk("100", self.record())
        self.chunk("200", self.record("source_sample:two"))
        self.index.scan_once()
        self.assertEqual(self.index.stats()["indexedRecords"], 2)
        removed.unlink()
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 1)
        self.assertEqual(self.index.stats()["indexedRecords"], 1)
        self.assertEqual(self.index.stats()["failedChunks"], 1)

    def test_symlink_cannot_add_an_arbitrary_external_chunk(self):
        with tempfile.TemporaryDirectory() as elsewhere:
            outside = Path(elsewhere) / "external.jsonl.gz"
            with gzip.open(outside, "wt") as stream:
                stream.write(json.dumps(self.record()) + "\n")
            (self.run / "people").mkdir()
            (self.run / "people" / "100.jsonl.gz").symlink_to(outside)
            self.index.scan_once()
            self.assertEqual(self.index.search({})["total"], 0)

    def test_shared_input_keeps_domain_headers_and_entity_membership_independent_on_replay(self):
        source = self.record(entityKeys=["person:shared", "person:only-first"])
        credit = self.record(domain="credits", provider="local", artistName="Earlier name",
                             artistUuid="earlier-artist", method="credit-v1", inputUuid="other-format",
                             entityKeys=["person:shared", "person:only-credit"], proposal={"title": "Credit label"})
        self.chunk("100", source)
        self.chunk("100", credit, domain="credits")
        self.index.scan_once()
        ids = {item["domain"]: item["id"] for item in self.index.search({})["items"]}
        updated = self.record(entityKeys=["person:replacement"], provider="archive-updated",
                              artistName="Later name", proposal={"title": "Changed label"})
        self.chunk("200", updated)
        self.index.scan_once()
        self.assertEqual(self.index.search({})["total"], 2)
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 1)
        self.assertEqual(self.index.search({"entityKey": "person:shared", "domain": "people"})["total"], 0)
        self.assertEqual(self.index.search({"entityKey": "person:only-first"})["total"], 0)
        self.assertEqual(self.index.search({"entityKey": "person:only-credit", "provider": "local"})["total"], 1)
        self.assertEqual(self.index.detail(ids["credits"])["record"], credit)
        self.assertEqual(self.index.detail(ids["people"])["record"], updated)
        self.assertEqual(self.index.search({"q": "Credit label"})["items"][0]["input_uuid"], "other-format")
        self.assertEqual(self.index.stats()["indexedRecords"], 2)

    def test_failed_replay_rolls_back_header_and_entity_changes_across_domains(self):
        first = self.record(entityKeys=["person:old", "person:shared"])
        other = self.record(domain="credits", entityKeys=["person:shared"])
        self.chunk("100", first)
        self.chunk("100", other, domain="credits")
        self.index.scan_once()
        first_id = self.index.search({"domain": "people"})["items"][0]["id"]
        replay = self.record(provider="replacement", entityKeys=["person:new"], proposal={"title": "Changed"})
        with gzip.open(self.run / "people/200.jsonl.gz", "wt", encoding="utf-8") as stream:
            stream.write(json.dumps(replay) + "\n{bad-json}\n")
        self.index.scan_once()
        self.assertEqual(self.index.detail(first_id)["record"], first)
        self.assertEqual(self.index.search({"entityKey": "person:new"})["total"], 0)
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 2)
        self.assertEqual(self.index.search({"provider": "replacement"})["total"], 0)
        self.assertEqual(self.index.stats()["indexedRecords"], 2)
        self.chunk("300", replay)
        self.index.scan_once()
        self.assertEqual(self.index.detail(first_id)["record"], replay)
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 1)
        self.assertEqual(self.index.search({"entityKey": "person:new"})["total"], 1)

    def test_packed_keys_and_unicode_search_preserve_literal_public_values(self):
        known = str(uuid.UUID(int=123))
        entities = ["file:" + known, "UPPER:" + known.upper(), "opaque:🎹\x00" + "phrase" * 50]
        key = "track_sample:" + known
        record = self.record(key, inputKind="track_sample", inputUuid=known, entityKeys=entities,
                             artistName="Straße 🎹", proposal={"title": "CAFÉ é ﬃ 100%_"})
        self.chunk("100", record)
        self.index.scan_once()
        for entity in entities:
            item = self.index.search({"entityKey": entity})["items"][0]
            self.assertEqual(item["input_key"], key)
            self.assertEqual(item["input_uuid"], known)
            self.assertEqual(item["entityKeys"], sorted(entities))
            self.assertEqual(self.index.detail(item["id"])["record"], record)
        for query in ("STRASSE", "café", "ffi", "100%_"):
            self.assertEqual(self.index.search({"q": query})["total"], 1)

    def test_digest_collisions_do_not_join_inputs_entities_or_labels(self):
        class Collision:
            def digest(self):
                return b"collision-key!!!"
        first = self.record("input:first", entityKeys=["person:first"], proposal={"title": "First only"})
        second = self.record("input:second", entityKeys=["person:second"], proposal={"title": "Second only"})
        with patch("shadow_reader.hashlib.blake2b", return_value=Collision()):
            self.chunk("100", first, second)
            self.index.scan_once()
            self.assertEqual(self.index.search({})["total"], 2)
            self.assertEqual(self.index.search({"entityKey": "person:first"})["total"], 1)
            self.assertEqual(self.index.search({"q": "Second only"})["total"], 1)
            for record in (first, second):
                item = self.index.search({"q": record["inputKey"]})["items"][0]
                self.assertEqual(self.index.detail(item["id"])["record"], record)

    def test_entity_domain_bits_extend_and_clear_without_an_arbitrary_domain_limit(self):
        for ordinal in range(70):
            domain = "domain-" + str(ordinal).zfill(2)
            self.chunk("100", self.record(domain=domain), domain=domain)
        self.index.scan_once()
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 70)
        self.chunk("200", self.record(domain="domain-00", entityKeys=[]), domain="domain-00")
        self.index.scan_once()
        self.assertEqual(self.index.search({"entityKey": "person:shared"})["total"], 69)
        self.assertEqual(self.index.search({"entityKey": "person:shared", "domain": "domain-69"})["total"], 1)
        self.assertEqual(self.index.stats()["indexedRecords"], 70)

    def test_new_cache_does_not_migrate_or_mutate_live_legacy_index(self):
        legacy_paths = [self.run / name for name in (".shadow-reader.sqlite3", ".shadow-reader-v2.sqlite3")]
        for legacy in legacy_paths:
            legacy.write_bytes(b"preserved original live cache")
        record = self.record("track_sample:one", "file_contents", inputKind="track_sample",
                             proposal={"home": {"literalDate": "1987-11-27"},
                                       "titleInterpretation": {"originalTitle": "Morning Dew"}})
        self.chunk("100", record, domain="file_contents")
        reopened = ShadowIndex(self.run)
        reopened.scan_once()
        result = reopened.search({"q": "Morning Dew"})
        self.assertEqual(result["total"], 1)
        self.assertEqual(result["items"][0]["label"], "Morning Dew")
        self.assertEqual(reopened.detail(result["items"][0]["id"])["record"], record)
        for legacy in legacy_paths:
            self.assertEqual(legacy.read_bytes(), b"preserved original live cache")

    def test_representative_multi_domain_population_has_measured_compact_growth_and_exact_lookup(self):
        count = 5000
        domains = ("performances", "file_contents", "songs", "compositions")
        sample = None
        for domain in domains:
            directory = self.run / domain
            directory.mkdir()
            with gzip.open(directory / "100.jsonl.gz", "wt", encoding="utf-8") as stream:
                for ordinal in range(count):
                    file_uuid = str(uuid.UUID(int=ordinal + 1))
                    artist_uuid = str(uuid.UUID(int=100000 + ordinal % 100))
                    source_uuid = str(uuid.UUID(int=200000 + ordinal // 10))
                    show_uuid = str(uuid.UUID(int=300000 + ordinal // 10))
                    song_uuid = str(uuid.UUID(int=400000 + ordinal))
                    title = "Recorded song " + str(ordinal) + " > Another song"
                    proposal = ({"literalDate": "1970-02-03", "performanceLabel": "late"}
                                if domain == "performances" else {"originalTitle": title})
                    entities = ["file:" + file_uuid, "artist-song:" + artist_uuid + ":" + song_uuid]
                    if domain in ("performances", "file_contents"):
                        entities.extend(("source:" + source_uuid, "show:" + show_uuid))
                    record = self.record("track_sample:" + file_uuid, domain, inputUuid=file_uuid,
                                         inputKind="track_sample", artistUuid=artist_uuid,
                                         artistName="Artist " + str(ordinal % 100), entityKeys=entities,
                                         proposal=proposal, method="shadow-" + domain + "-v2")
                    stream.write(json.dumps(record) + "\n")
                    if domain == "file_contents" and ordinal == 123:
                        sample = record
        self.index.scan_once()
        self.assertEqual(self.index.stats()["indexedRecords"], count * len(domains))
        item = self.index.search({"entityKey": sample["entityKeys"][0], "domain": "file_contents"})["items"][0]
        self.assertEqual(self.index.detail(item["id"])["record"], sample)
        self.assertEqual(self.index.search({"entityKey": sample["entityKeys"][0]})["total"], 4)
        self.assertEqual(self.index.search({"q": "Recorded song 123 >"})["total"], 3)
        with closing(self.index.connect()) as db:
            db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            size = db.execute("PRAGMA page_count").fetchone()[0] * db.execute("PRAGMA page_size").fetchone()[0]
            objects = {row[0]: row[1] for row in db.execute(
                "SELECT name,sum(pgsize) FROM dbstat GROUP BY name")}
            # This measures the whole real cache including dictionaries, both lookup
            # directions and schema pages. It is a regression ceiling, not a catalog forecast.
            self.assertLess(size / count, 1200)
        print(json.dumps({"measurement": "synthetic-reader-index-v3", "inputs": count,
                          "domainOutcomes": count * len(domains), "sqliteBytes": size,
                          "bytesPerInput": size / count, "bytesPerOutcome": size / (count * len(domains)),
                          "sqliteObjectBytes": objects}))


if __name__ == "__main__":
    unittest.main()
