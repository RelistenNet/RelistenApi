"""Behavior contracts for immutable output indexing; no server or network is needed."""

import gzip
import json
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from shadow_reader import ShadowIndex


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
        self.assertLess(self.index.database.stat().st_size, len(original))

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


if __name__ == "__main__":
    unittest.main()
