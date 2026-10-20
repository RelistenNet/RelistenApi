"""Public export behavior: unresolved titles, full membership and unchanged ownership."""

import gzip
import json
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

from file_title_replay import FileTitleReplayExporter, FileTitleReplayProjection, digest, pin
from shadow_reader import _pack_text


class FileTitleReplayExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.artist = str(uuid.uuid4())
        self.source, self.show = str(uuid.uuid4()), str(uuid.uuid4())
        self.file_ids = [str(uuid.uuid4()) for _ in range(3)]

    def tearDown(self):
        self.temp.cleanup()

    def legacy(self, file_uuid, title):
        return {"domain": "file_contents", "inputKind": "track_sample", "inputUuid": file_uuid,
                "artistUuid": self.artist, "proposal": {
                    "home": {"fileUuid": file_uuid, "sourceUuid": self.source, "showUuid": self.show},
                    "playback": {"fileUuid": file_uuid, "sourceUuid": self.source, "showUuid": self.show,
                                 "mp3Url": "https://example.test/original.mp3"},
                    "titleInterpretation": None if title is None else {
                        "fileUuid": file_uuid, "sourceUuid": self.source, "originalTitle": title,
                        "extractorVersion": "track-title-v1", "segments": [{"outcome": "unresolved",
                         "reason": "multiple_artist_dictionary_entries", "song": None}]}}}

    def fixture(self, records):
        run = self.root / "reader"
        (run / "file_contents").mkdir(parents=True)
        chunk = run / "file_contents/retained.jsonl.gz"
        with gzip.open(chunk, "wt", encoding="utf-8") as stream:
            for record in records:
                stream.write(json.dumps(record) + "\n")
        index = run / "index.sqlite3"
        with closing(sqlite3.connect(index)) as db:
            db.executescript("""
                CREATE TABLE records(id INTEGER,header_id INTEGER,chunk_id INTEGER,domain_id INTEGER,line_no INTEGER);
                CREATE TABLE headers(id INTEGER,artist_id INTEGER,kind_id INTEGER,input_uuid TEXT,input_id INTEGER);
                CREATE TABLE input_keys(id INTEGER,data BLOB);
                CREATE TABLE chunks(id INTEGER,path TEXT,bytes INTEGER,state TEXT);
                CREATE TABLE artists(id INTEGER,uuid TEXT);
                CREATE TABLE domains(id INTEGER,name TEXT);
                CREATE TABLE terms(id INTEGER,value TEXT);
            """)
            db.execute("INSERT INTO chunks VALUES(1,?,?,?)", ("file_contents/retained.jsonl.gz", chunk.stat().st_size, "indexed"))
            db.execute("INSERT INTO artists VALUES(1,?)", (self.artist,))
            db.execute("INSERT INTO domains VALUES(1,'file_contents')")
            db.execute("INSERT INTO terms VALUES(1,'track_sample')")
            db.execute("INSERT INTO terms VALUES(2,'provider_input')")
            for number, record in enumerate(records, 1):
                kind = 2 if record["inputKind"] == "provider_input" else 1
                db.execute("INSERT INTO headers VALUES(?,?,?,?,?)", (number, 1, kind, None, number))
                db.execute("INSERT INTO input_keys VALUES(?,?)", (number, _pack_text(record["inputKind"] + ":" + record["inputUuid"])))
                db.execute("INSERT INTO records VALUES(?,?,1,1,?)", (number, number, number))
            db.commit()
        manifest = self.root / "music.json"
        manifest.write_text(json.dumps({"version": "music-consolidation-input-v1", "state": "completed", "sources": [
            {"id": 1, "scope": "catalogue", "run": str(run), "index": str(index), "reader_url": "http://reader.test"}]}))
        return manifest, chunk, index

    def export(self, records):
        manifest, chunk, index = self.fixture(records)
        before = digest(chunk), digest(index)
        result = FileTitleReplayExporter(manifest, self.root / "export", digest(manifest), minimum_free_bytes=0).export()
        self.assertEqual(before, (digest(chunk), digest(index)))
        return result

    def test_unresolved_files_share_literal_work_without_losing_files_or_playback_homes(self):
        result = self.export([self.legacy(self.file_ids[0], "Drums"), self.legacy(self.file_ids[1], "Drums"),
                              self.legacy(self.file_ids[2], None)])
        self.assertEqual(3, result["fileMap"]["rows"])
        self.assertEqual(1, result["titles"]["rows"])
        self.assertEqual(1, result["filesWithoutTitle"])
        with closing(sqlite3.connect(result["fileMap"]["path"])) as db:
            files = db.execute("SELECT file_uuid,source_uuid,show_uuid,title_id FROM files ORDER BY record_id").fetchall()
            self.assertEqual(self.file_ids, [row[0] for row in files])
            self.assertTrue(all(row[1:3] == (self.source, self.show) for row in files))
            self.assertEqual(files[0][3], files[1][3])
            self.assertIsNone(files[2][3])
        with gzip.open(result["titles"]["path"], "rt") as stream:
            title = json.loads(next(stream))
        self.assertEqual("Drums", title["originalTitle"])
        self.assertEqual("/proposal/titleInterpretation/originalTitle", title["witness"]["valuePointer"])
        self.assertEqual(1, title["witness"]["line"])

    def test_native_children_keep_their_existing_keys_and_missing_audio_does_not_become_playback(self):
        native_uuid = str(uuid.uuid4())
        file = {"fileOrdinal": 0, "entityKey": "native_file:retained-audio", "catalogSourceUuid": self.source,
                "catalogShowUuid": self.show, "catalogTrackUuid": None, "catalogPlayback": None,
                "titleInterpretation": {"artistUuid": self.artist, "originalTitle": "2001"}, "file": {"title": {"value": "2001"}}}
        parent = {"domain": "file_contents", "inputKind": "provider_input", "inputUuid": native_uuid,
                  "artistUuid": self.artist, "proposal": {"files": [file, {
                      "fileOrdinal": 1, "entityKey": "native_file:retained-image", "file": {"title": None},
                      "titleInterpretation": None}]}}
        result = self.export([parent])
        self.assertEqual(2, result["fileMap"]["rows"])
        self.assertEqual(2, result["readers"][0]["nativeFiles"])
        with closing(sqlite3.connect(result["fileMap"]["path"])) as db:
            rows = db.execute("SELECT file_key,file_uuid,file_ordinal,kind FROM files ORDER BY file_ordinal").fetchall()
            self.assertEqual([("native_file:retained-audio", None, 0, "native"),
                              ("native_file:retained-image", None, 1, "native")], rows)
        with gzip.open(result["titles"]["path"], "rt") as stream:
            self.assertEqual("2001", json.loads(next(stream))["originalTitle"])

    def test_wrong_playback_home_leaves_no_completed_export(self):
        record = self.legacy(self.file_ids[0], "Drums")
        record["proposal"]["playback"]["showUuid"] = str(uuid.uuid4())
        manifest, _, _ = self.fixture([record])
        with self.assertRaisesRegex(ValueError, "playback disagrees"):
            FileTitleReplayExporter(manifest, self.root / "export", digest(manifest), minimum_free_bytes=0).export()
        self.assertFalse((self.root / "export/manifest.json").exists())

    def completed_replay(self, export, names):
        with gzip.open(export["titles"]["path"], "rt") as stream:
            original = json.loads(next(stream))
        members = []
        for ordinal, name in enumerate(names):
            members.append({"song": {"entityKey": "repertoire:" + name.lower(), "artistUuid": self.artist,
                                      "name": name, "basis": "title_derived", "legacySongUuid": None},
                            "relation": "compound_member", "disposition": "proposed", "reason": "shared_title_correspondence",
                            "qualifiers": [], "evidence": {"quote": name, "startCodePoint": ordinal * 8}})
        row = {"titleId": original["titleId"], "correspondence": {
            "scope": original["scope"], "artistUuid": original["artistUuid"], "originalKey": "repertoire:original",
            "originalTitle": original["originalTitle"], "sourceWitness": original["witness"],
            "disposition": "proposed", "reason": "shared_title_correspondence", "sequenceBasis": "source_title_order",
            "targets": members}}
        output = self.root / "replay"
        output.mkdir()
        titles = output / "titles.jsonl.gz"
        with gzip.open(titles, "wt") as stream:
            stream.write(json.dumps(row) + "\n")
        manifest = {"version": "retained-file-title-replay-output-v1", "state": "completed",
                    "inputManifestSha256": digest(self.root / "export/manifest.json"), "inputManifest": pin(self.root / "export/manifest.json", 1),
                    "musicInputManifest": export["musicInputManifest"],
                    "fileMap": export["fileMap"], "titles": pin(titles, 1), "extractorVersion": "fixture-extractor",
                    "resolverVersion": "fixture-resolver", "knowledgeSha256": None}
        manifest["fileReplayVersion"] = "retained-file-title-replay-v2"
        path = output / "manifest.json"
        path.write_text(json.dumps(manifest))
        return path, row

    def test_projection_preserves_repeated_mentions_and_links_them_to_original_files(self):
        export = self.export([self.legacy(self.file_ids[0], "Drums > Space > Drums")])
        manifest, _ = self.completed_replay(export, ["Drums", "Space", "Drums"])
        result = FileTitleReplayProjection(manifest, self.root / "projection", digest(manifest)).project()
        with closing(sqlite3.connect(result["projection"]["path"])) as db:
            db.execute("ATTACH DATABASE ? AS filemap", (export["fileMap"]["path"],))
            rows = db.execute("SELECT f.file_uuid,f.source_uuid,f.show_uuid,t.target_key,t.target_ordinal "
                              "FROM targets t JOIN filemap.files f ON f.title_id=t.title_id ORDER BY t.target_ordinal").fetchall()
            self.assertEqual(["repertoire:drums", "repertoire:space", "repertoire:drums"], [row[3] for row in rows])
            self.assertEqual([0, 1, 2], [row[4] for row in rows])
            self.assertTrue(all(row[:3] == (self.file_ids[0], self.source, self.show) for row in rows))

    def test_projection_refuses_results_for_another_artist(self):
        export = self.export([self.legacy(self.file_ids[0], "Drums")])
        manifest, row = self.completed_replay(export, ["Drums"])
        row["correspondence"]["artistUuid"] = str(uuid.uuid4())
        data = json.loads(manifest.read_bytes())
        with gzip.open(data["titles"]["path"], "wt") as stream:
            stream.write(json.dumps(row) + "\n")
        data["titles"] = pin(data["titles"]["path"], 1)
        manifest.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "lost its original title, scope, artist or witness"):
            FileTitleReplayProjection(manifest, self.root / "projection", digest(manifest)).project()
        self.assertFalse((self.root / "projection/manifest.json").exists())

    def test_unbound_native_files_keep_one_original_recording_account(self):
        children = [{"fileOrdinal": ordinal, "entityKey": "native_file:" + str(ordinal),
                     "recordingOrigin": {"item": {"provider": "fixture", "providerKey": "recording-a"}},
                     "titleInterpretation": {"artistUuid": self.artist, "originalTitle": "Drums"},
                     "catalogTrackUuid": None, "catalogShowUuid": None, "catalogSourceUuid": None}
                    for ordinal in range(2)]
        export = self.export([{"domain": "file_contents", "inputKind": "provider_input", "inputUuid": str(uuid.uuid4()),
                               "artistUuid": self.artist, "proposal": {"files": children}}])
        manifest, _ = self.completed_replay(export, ["Drums"])
        result = FileTitleReplayProjection(manifest, self.root / "projection", digest(manifest)).project()
        with closing(sqlite3.connect(result["projection"]["path"])) as db:
            rows = db.execute("SELECT file_key,account_key,home_key,home_kind FROM native_homes ORDER BY file_key").fetchall()
            self.assertEqual(2, len(rows))
            self.assertEqual(rows[0][1], rows[1][1])
            self.assertEqual(rows[0][1], rows[0][2])
            self.assertEqual("recording_account", rows[0][3])
            self.assertNotEqual(rows[0][0], rows[0][2])

    def test_projection_detects_a_replaced_original_reader_file_identity(self):
        export = self.export([self.legacy(self.file_ids[0], "Drums")])
        manifest, _ = self.completed_replay(export, ["Drums"])
        with closing(sqlite3.connect(self.root / "reader/index.sqlite3")) as index:
            index.execute("UPDATE input_keys SET data=?", (_pack_text("track_sample:" + str(uuid.uuid4())),))
            index.commit()
        with self.assertRaisesRegex(ValueError, "differs from its original reader header"):
            FileTitleReplayProjection(manifest, self.root / "projection", digest(manifest)).project()
        self.assertFalse((self.root / "projection/manifest.json").exists())


if __name__ == "__main__":
    unittest.main()
