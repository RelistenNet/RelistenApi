"""Music browse projection contracts using real compact ShadowIndex fixtures."""

from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from music_catalogue import CatalogueBuildPaused, MusicCatalogue, MusicCatalogueBuilder, Source
from shadow_reader import ShadowIndex


class TestMusicCatalogue(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "music.sqlite3"
        self.run, self.index = self.source_run("core")

    def source_run(self, name):
        run = self.root / name
        run.mkdir()
        return run, ShadowIndex(run)

    def chunk(self, domain, name, *records, run=None):
        directory = (run or self.run) / domain
        directory.mkdir(exist_ok=True)
        path = directory / (name + ".jsonl.gz")
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for record in records:
                stream.write(json.dumps(record, ensure_ascii=False) + "\n")
        return path

    @staticmethod
    def record(domain, key, proposal, entities, artist="artist-one", artist_name="Artist One"):
        return {"domain": domain, "inputKey": key, "inputKind": "song_sample" if key.startswith("song:") else "track_sample",
                "inputUuid": key, "artistUuid": artist, "artistName": artist_name, "provider": "fixture",
                "outcome": "proposed", "reason": None, "method": "actual-fixture-v1", "entityKeys": entities,
                "proposal": proposal, "evidence": {"originalInputs": {"description": "PRIVATE PROSE NEVER COPY"}}}

    @staticmethod
    def song(key, title, basis="captured_artist_dictionary", legacy=None):
        return {"entityKey": key, "artistUuid": "artist-one", "legacySongUuid": legacy,
                "name": title, "basis": basis}

    @staticmethod
    def work(key, title="Shared work"):
        return {"entityKey": key, "musicBrainzWorkMbid": key.rsplit(":", 1)[-1], "title": title,
                "correspondenceBasis": "retained_supported_correspondence",
                "primaryEvidence": {"private": "PRIVATE WORK EVIDENCE NEVER COPY"}}

    def build(self, sources=None, **options):
        self.index.scan_once()
        sources = sources or [Source("catalogue", self.run, "http://127.0.0.1:18765")]
        return MusicCatalogueBuilder(sources, self.output, minimum_free_bytes=0, **options).build()

    def catalogue(self):
        return MusicCatalogue(self.output)

    @staticmethod
    def native_file(ordinal, title, interpretations):
        key = "native_file:fixture-" + str(ordinal)
        songs = [song for interpretation in interpretations
                 for song in ([interpretation["song"]] if interpretation.get("song") else [])
                 + interpretation.get("candidateSongs", [])]
        return {"fileOrdinal": ordinal, "entityKey": key, "inputRevisionUuid": "native-revision",
                "file": {"title": {"value": title}, "filename": {"value": str(ordinal) + ".flac"}},
                "titleInterpretation": {"originalTitle": title},
                "mentions": [{"interpretation": interpretation} for interpretation in interpretations],
                "entityKeys": [key, *(song["entityKey"] for song in songs)],
                "outcome": "proposed", "catalogPlayback": None}

    def native_record(self, *files, key="provider_input:one", artist_name="Artist One"):
        record = self.record("file_contents", key, {"files": list(files)},
                             [entity for file in files for entity in file["entityKeys"]], artist_name=artist_name)
        record["inputKind"] = "provider_input"
        return record

    def test_native_only_interpretations_discover_unicode_derived_and_candidate_songs(self):
        derived = self.song("repertoire:native-derived", "Zoë’s 🌟 Jam", "title_derived")
        rivals = [self.song("repertoire:native-rival-a", "Uncertain α"),
                  self.song("repertoire:native-rival-b", "Uncertain β")]
        child = self.native_file(0, "Zoë’s 🌟 Jam > Unknown", [
            {"song": derived, "candidateSongs": [], "outcome": "proposed"},
            {"song": None, "candidateSongs": rivals, "outcome": "unresolved"},
            {"song": None, "candidateSongs": [], "outcome": "unresolved", "reason": "unknown_song"}])
        self.chunk("file_contents", "100", self.native_record(child))

        stats = self.build()

        self.assertEqual(stats["version"], "music-catalogue-v2")
        self.assertEqual(stats["counts"]["songs"], 3)
        self.assertEqual(self.catalogue().song("catalogue", derived["entityKey"])["title"], "Zoë’s 🌟 Jam")
        self.assertEqual(self.catalogue().song("catalogue", derived["entityKey"])["basis"], "title_derived")
        self.assertEqual(self.catalogue().song("catalogue", rivals[0]["entityKey"])["status"], "candidate")
        self.assertEqual(self.catalogue().songs(q="unknown")["total"], 0)
        source = stats["sources"][0]
        self.assertEqual((source["native_files"], source["native_file_parents"], source["file_query_mode"]),
                         (1, 1, "native"))

    def test_native_song_discovery_does_not_stop_at_a_thousand_mentions(self):
        interpretations = [{"song": self.song("repertoire:native-" + str(index), "Native song " + str(index)),
                            "candidateSongs": [], "outcome": "proposed"} for index in range(1005)]
        self.chunk("file_contents", "100", self.native_record(self.native_file(0, "Long native recording", interpretations)))

        stats = self.build()

        self.assertEqual(stats["counts"]["songs"], 1005)
        self.assertEqual(self.catalogue().song("catalogue", "repertoire:native-1004")["title"], "Native song 1004")

    def test_native_replay_withdrawal_and_scope_collision_preserve_current_membership(self):
        key = "repertoire:native-same"
        first = self.native_file(0, "Catalogue title", [{"song": self.song(key, "Catalogue title")}])
        self.chunk("file_contents", "100", self.native_record(first))
        self.index.scan_once()
        local_run, local_index = self.source_run("native-local")
        local = self.native_file(0, "Local title", [{"song": self.song(key, "Local title")}])
        self.chunk("file_contents", "100", self.native_record(local, artist_name="Local artist"), run=local_run)
        local_index.scan_once()
        sources = [Source("catalogue", self.run, "http://127.0.0.1:1"),
                   Source("local", local_run, "http://127.0.0.1:2")]
        self.build(sources)
        self.assertEqual(self.catalogue().songs()["total"], 2)
        self.assertEqual(self.catalogue().song("local", key)["title"], "Local title")
        replacement = self.native_file(0, "Replacement", [{"song": self.song("repertoire:replacement", "Replacement")}])
        self.chunk("file_contents", "200", self.native_record(replacement))
        self.build(sources)
        self.assertIsNone(self.catalogue().song("catalogue", key))
        self.assertIsNotNone(self.catalogue().song("local", key))
        self.assertIsNotNone(self.catalogue().song("catalogue", "repertoire:replacement"))
        self.chunk("file_contents", "300", self.native_record())
        self.chunk("file_contents", "050", self.native_record(first))
        stats = self.build(sources)
        self.assertIsNone(self.catalogue().song("catalogue", "repertoire:replacement"))
        self.assertIsNone(self.catalogue().song("catalogue", key))
        self.assertIsNotNone(self.catalogue().song("local", key))
        self.assertEqual((stats["sources"][0]["native_files"], stats["sources"][0]["native_file_parents"],
                          stats["sources"][0]["file_query_mode"]), (0, 1, "records"))

    def test_mixed_reader_metadata_counts_empty_native_accounts_and_legacy_rows_separately(self):
        child = self.native_file(0, "Native", [{"song": self.song("repertoire:native", "Native")}])
        legacy = self.record("file_contents", "track:legacy", {"titleInterpretation": {"segments": []}}, [])
        self.chunk("file_contents", "100", self.native_record(child),
                   self.native_record(key="provider_input:empty"), legacy)

        stats = self.build()

        source = stats["sources"][0]
        self.assertEqual((source["native_files"], source["native_file_parents"], source["file_query_mode"]),
                         (1, 2, "mixed"))

    def test_dictionary_title_derived_and_candidate_only_keys_keep_exact_scoped_facts(self):
        known = self.song("repertoire:known", "Dictionary title", legacy="legacy-song")
        derived = self.song("repertoire:derived", "New title", "title_derived")
        candidates = [self.song("repertoire:rival-a", "Ambiguous title"),
                      self.song("repertoire:rival-b", "Ambiguous title")]
        track = {"originalTitle": "New title > Ambiguous title", "segments": [
            {"song": derived, "candidateSongs": [], "outcome": "proposed"},
            {"song": None, "candidateSongs": candidates, "outcome": "unresolved"}],
            "wholeFileDictionaryCandidates": [{"song": known}]}
        self.chunk("file_contents", "100", self.record("file_contents", "file:1", {"titleInterpretation": track},
                   ["repertoire:known", "repertoire:derived", "repertoire:rival-a", "repertoire:rival-b"]))
        self.chunk("repertoire", "100", self.record("repertoire", "song:1", known, [known["entityKey"]]),
                   self.record("repertoire", "file:metadata", None, [], artist="artist-aaa", artist_name="Zebra artist"))

        stats = self.build()

        self.assertEqual(stats["counts"]["songs"], 4)
        catalogue = self.catalogue()
        self.assertEqual(catalogue.song("catalogue", known["entityKey"])["status"], "dictionary")
        self.assertEqual(catalogue.song("catalogue", known["entityKey"])["legacy_uuid"], "legacy-song")
        self.assertEqual(catalogue.song("catalogue", derived["entityKey"])["basis"], "title_derived")
        self.assertEqual(catalogue.song("catalogue", "repertoire:rival-a")["status"], "candidate")
        self.assertEqual(catalogue.songs(q="artist one")["total"], 4)
        self.assertEqual(catalogue.songs(q="AMBIGUOUS")["total"], 2)
        self.assertEqual([song["title"] for song in catalogue.songs()["items"]],
                         ["Ambiguous title", "Ambiguous title", "Dictionary title", "New title"])
        self.assertEqual([artist["name"] for artist in catalogue.artists()["items"]],
                         ["Artist One", "Zebra artist"])
        self.assertEqual(catalogue.songs(limit=1, offset=1)["limit"], 1)
        self.assertEqual(catalogue.songs()["items"][0]["artist_name"], "Artist One")
        self.assertEqual(stats["visibility"], "independent_index_snapshots_only")
        with closing(sqlite3.connect(self.output)) as db:
            self.assertIsNone(db.execute("SELECT name FROM sqlite_master WHERE name='pending'").fetchone())
        self.assertNotIn(b"PRIVATE PROSE", self.output.read_bytes())

    def test_latest_logical_replay_and_withdrawal_do_not_resurrect_older_chunks(self):
        key = "repertoire:retired"
        self.chunk("repertoire", "100", self.record("repertoire", "song:1", self.song(key, "Old title"), [key]))
        self.build()
        self.assertIsNotNone(self.catalogue().song("catalogue", key))
        self.chunk("repertoire", "200", self.record("repertoire", "song:1", None, []))
        stats = self.build()
        self.assertEqual(stats["counts"]["songs"], 0)
        self.assertIsNone(self.catalogue().song("catalogue", key))
        self.chunk("repertoire", "050", self.record("repertoire", "song:1", self.song(key, "Older title"), [key]))
        self.build()
        self.assertEqual(self.catalogue().songs()["total"], 0)

    def test_artist_labels_include_only_current_headers_and_latest_visible_variant(self):
        self.chunk("repertoire", "100", self.record("repertoire", "file:1", None, [],
                   artist="old-artist", artist_name="Withdrawn artist"),
                   self.record("repertoire", "file:2", None, [], artist_name="Earlier label"))
        self.build()
        self.assertEqual(self.catalogue().artists()["total"], 2)
        self.chunk("repertoire", "200", self.record("repertoire", "file:1", None, [],
                   artist_name="Latest visible label"))

        self.build()

        self.assertEqual(self.catalogue().artists()["items"],
                         [{"scope": "catalogue", "uuid": "artist-one", "name": "Latest visible label"}])

    def test_actual_work_links_ignore_rivals_and_keep_all_unambiguous_song_associations(self):
        work = "composition:musicbrainz:work-one"
        rival = "composition:musicbrainz:rival-only"
        first = {"title": "First song", "repertoireEntityKeys": ["repertoire:first"], "links": [],
                 "retainedCandidates": [{"musicBrainzWorkMbid": "work-one", "title": "Rival"}]}
        second = {"title": "First song", "repertoireEntityKeys": ["repertoire:first"], "links": [self.work(work)]}
        third = {"title": "Second song", "repertoireEntityKeys": ["repertoire:second"], "links": [self.work(work)]}
        ambiguous = {"title": "Ambiguous", "repertoireEntityKeys": ["repertoire:third", "repertoire:fourth"],
                     "links": [self.work(work)], "competingWorkKeys": [rival]}
        self.chunk("compositions", "100", self.record("compositions", "file:0", {"mentions": [first]},
                   [work, rival, "repertoire:first"]),
                   self.record("compositions", "file:1", {"mentions": [second]}, [work, "repertoire:first"]),
                   self.record("compositions", "file:2", {"mentions": [third, ambiguous]},
                               [work, "repertoire:second", "repertoire:third", "repertoire:fourth"]))

        stats = self.build()

        self.assertEqual(stats["counts"]["works"], 1)
        self.assertEqual(stats["counts"]["work_songs"], 2)
        actual = self.catalogue().work(work)
        self.assertEqual({song["entity_key"] for song in actual["songs"]}, {"repertoire:first", "repertoire:second"})
        self.assertNotEqual(actual["witness_record"], 1)
        self.assertIsNone(self.catalogue().work(rival))
        self.assertEqual(len(self.catalogue().song("catalogue", "repertoire:first")["works"]), 1)
        self.assertNotIn(b"PRIVATE WORK EVIDENCE", self.output.read_bytes())

    def test_setlist_accounts_preserve_order_qualification_and_scoped_artist_labels(self):
        unordered = {"entityKey": "setlist:legacy", "literalDate": "2026", "ordered": False,
                     "memberships": [{"songEntityKey": "repertoire:member", "title": "Member title", "legacySongUuid": "member-uuid"}],
                     "occurrences": []}
        ordered = {"entityKey": "setlist_account:native", "literalDate": "2026-10-18", "ordered": True,
                   "memberships": [], "occurrences": []}
        self.chunk("setlists", "100", self.record("setlists", "setlist:1", unordered,
                   [unordered["entityKey"], "repertoire:member"]),
                   self.record("setlists", "provider:1", [ordered], [ordered["entityKey"]]))
        self.build()
        catalogue = self.catalogue()
        self.assertFalse(catalogue.setlist("catalogue", "setlist:legacy")["ordered"])
        self.assertTrue(catalogue.setlist("catalogue", "setlist_account:native")["ordered"])
        self.assertEqual(catalogue.setlists(q="2026-10-18")["total"], 1)
        self.assertEqual(catalogue.setlists(q="Artist One")["total"], 2)
        self.assertEqual(catalogue.song("catalogue", "repertoire:member")["legacy_uuid"], "member-uuid")

    def test_relationship_details_have_explicit_totals_and_bounded_pages(self):
        works = [self.work("composition:musicbrainz:" + str(index)) for index in range(3)]
        records = []
        for index in range(3):
            key = "repertoire:" + str(index)
            mention = {"title": "Song " + str(index), "repertoireEntityKeys": [key], "links": works}
            records.append(self.record("compositions", "file:" + str(index), {"mentions": [mention]},
                                       [key, *(work["entityKey"] for work in works)]))
        self.chunk("compositions", "100", *records)
        self.build()
        catalogue = self.catalogue()
        song = catalogue.song("catalogue", "repertoire:0", limit=1, offset=1)
        self.assertEqual((song["works_total"], song["works_limit"], song["works_offset"]), (3, 1, 1))
        self.assertEqual([work["entity_key"] for work in song["works"]], [works[1]["entityKey"]])
        work = catalogue.work(works[0]["entityKey"], limit=1, offset=2)
        self.assertEqual((work["songs_total"], work["songs_limit"], work["songs_offset"]), (3, 1, 2))
        self.assertEqual([song["entity_key"] for song in work["songs"]], ["repertoire:2"])
        self.assertEqual(catalogue.work(works[0]["entityKey"], limit=1, offset=3)["songs"], [])

    def test_later_source_witness_precedence_does_not_merge_local_artist_or_song_namespace(self):
        key = "repertoire:same-key"
        self.chunk("repertoire", "100", self.record("repertoire", "song:1", self.song(key, "Core"), [key]))
        self.index.scan_once()
        full_run, full = self.source_run("full")
        local_run, local = self.source_run("local")
        for run, index, title, name in [(full_run, full, "Repaired", "Catalogue artist"),
                                       (local_run, local, "Independent local title", "Local artist")]:
            self.chunk("repertoire", "100", self.record("repertoire", "song:1", self.song(key, title), [key],
                       artist_name=name), run=run)
            index.scan_once()
        sources = [Source("catalogue", self.run, "http://127.0.0.1:1"),
                   Source("catalogue", full_run, "http://127.0.0.1:2"),
                   Source("local", local_run, "http://127.0.0.1:3")]
        self.build(sources)
        catalogue = self.catalogue()
        self.assertEqual(catalogue.songs()["total"], 2)
        self.assertEqual(catalogue.song("catalogue", key)["title"], "Repaired")
        self.assertEqual(catalogue.song("catalogue", key)["witness_reader_url"], "http://127.0.0.1:2")
        self.assertEqual(catalogue.song("local", key)["artist_name"], "Local artist")
        self.assertEqual(catalogue.artists()["total"], 2)

    def test_byte_cap_and_space_floor_preserve_previous_completed_projection(self):
        key = "repertoire:one"
        self.chunk("repertoire", "100", self.record("repertoire", "song:1", self.song(key, "Stable"), [key]))
        self.build()
        before = self.output.read_bytes()
        with self.assertRaises(CatalogueBuildPaused):
            self.build(maximum_bytes=4096)
        self.assertEqual(self.output.read_bytes(), before)
        with patch("music_catalogue.shutil.disk_usage") as usage:
            usage.return_value.free = 1
            with self.assertRaises(CatalogueBuildPaused):
                MusicCatalogueBuilder([Source("catalogue", self.run, "http://127.0.0.1:1")],
                                      self.output, minimum_free_bytes=2).build()
        self.assertEqual(self.output.read_bytes(), before)
        self.assertFalse(list(self.root.glob("*.building")))

    def test_multiple_chunks_reuse_private_staging_pages_under_a_small_cap(self):
        for chunk in range(55):
            records = []
            for item in range(20):
                number = chunk * 20 + item
                key = "repertoire:" + format(number, "032x")
                records.append(self.record("repertoire", "song:" + str(number),
                               self.song(key, "Song " + str(number) + " " + "x" * 500), [key]))
            self.chunk("repertoire", str(chunk).zfill(3), *records)

        stats = self.build(maximum_bytes=1024**2)

        self.assertEqual(stats["counts"]["songs"], 1100)
        self.assertLessEqual(stats["bytes"], 1024**2)
        self.assertIsNotNone(self.catalogue().song("catalogue", "repertoire:" + format(1099, "032x")))

    def test_existing_v2_fallback_is_read_only_and_changed_immutable_witness_refuses_rebuild(self):
        key = "repertoire:one"
        path = self.chunk("repertoire", "100", self.record("repertoire", "song:1", self.song(key, "Stable"), [key]))
        self.index.scan_once()
        # A genuinely older compact cache predates native child lookup tables.
        with closing(sqlite3.connect(self.index.database)) as db:
            db.executescript("""DROP TRIGGER native_files_insert; DROP TRIGGER native_files_delete;
                DROP TABLE native_file_entities; DROP TABLE native_file_search; DROP TABLE native_files;""")
        old_index = self.run / ".shadow-reader-v2.sqlite3"
        self.index.database.rename(old_index)
        before = hashlib.sha256(old_index.read_bytes()).hexdigest()
        sources = [Source("catalogue", self.run, "http://127.0.0.1:1")]
        stats = MusicCatalogueBuilder(sources, self.output, minimum_free_bytes=0).build()
        self.assertEqual(stats["sources"][0]["indexVersion"], 2)
        self.assertEqual((stats["sources"][0]["native_files"], stats["sources"][0]["native_file_parents"],
                          stats["sources"][0]["file_query_mode"]), (0, 0, "records"))
        self.assertEqual(hashlib.sha256(old_index.read_bytes()).hexdigest(), before)
        completed = self.output.read_bytes()
        with gzip.open(path, "wt") as stream:
            stream.write("{}\n")
        with self.assertRaisesRegex(ValueError, "changed"):
            MusicCatalogueBuilder(sources, self.output, minimum_free_bytes=0).build()
        self.assertEqual(self.output.read_bytes(), completed)


if __name__ == "__main__":
    unittest.main()
