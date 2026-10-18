"""Behavior contracts for immutable output indexing; no server or network is needed."""

import gzip
import itertools
import json
import sqlite3
import tempfile
import threading
import unittest
import uuid
from unittest.mock import patch
from contextlib import closing
from pathlib import Path
from http.server import ThreadingHTTPServer
from urllib.error import HTTPError
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
