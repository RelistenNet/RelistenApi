"""Public music queries preserve exact file homes and unknown native accounts."""

from contextlib import closing
import gzip
from http.server import ThreadingHTTPServer
import itertools
import json
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from shadow_music import ShadowMusicQueries
from shadow_reader import ShadowIndex, handler_for


ARTIST = "00000000-0000-0000-0000-000000000001"
SHOW = "00000000-0000-0000-0000-000000000002"
LATE_SHOW = "00000000-0000-0000-0000-000000000003"
SOURCE = "00000000-0000-0000-0000-000000000004"
SONGS = ["repertoire:plain", "repertoire:annotated"]


class TestShadowMusic(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.run = Path(temporary.name)
        self.index = ShadowIndex(self.run)
        self.music = ShadowMusicQueries(self.index)

    def chunk(self, name, *records):
        directory = self.run / "file_contents"
        directory.mkdir(exist_ok=True)
        path = directory / (name + ".jsonl.gz")
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for record in records:
                stream.write(json.dumps(record) + "\n")
        return path

    @staticmethod
    def record(key, proposal, entities, kind="track_sample", artist=ARTIST):
        return {"domain": "file_contents", "inputKey": key, "inputKind": kind, "inputUuid": key.split(":", 1)[1],
                "artistUuid": artist, "artistName": "Artist", "provider": "fixture", "outcome": "proposed",
                "reason": None, "method": "original-fixture-v1", "proposal": proposal, "entityKeys": entities}

    def legacy(self, ordinal, songs=SONGS, show=SHOW, artist=ARTIST, source=SOURCE, key=None):
        file_uuid = f"00000000-0000-0000-0001-{ordinal:012d}"
        home = {"fileUuid": file_uuid, "sourceUuid": source, "showUuid": show,
                "literalDate": "1970-01-02", "performanceLabel": "Late" if show == LATE_SHOW else None}
        entities = [*songs, "file:" + file_uuid]
        if show:
            entities.append("show:" + show)
        if source:
            entities.append("source:" + source)
        return self.record(key or "track_sample:" + file_uuid,
                           {"home": home, "playback": {"mp3Url": "https://audio.test/original.mp3"},
                            "titleInterpretation": {"originalTitle": "Original " + str(ordinal)}}, entities, artist=artist)

    @staticmethod
    def child(ordinal, origin="recording-a", show=None):
        key = "native_file:original-" + str(ordinal)
        return {"fileOrdinal": ordinal, "entityKey": key,
                "recordingOrigin": {"scope": "recording", "evidence": {"jsonPointer": "/" + origin}},
                "file": {"title": {"value": "Native " + str(ordinal)}},
                "outcome": "proposed", "entityKeys": [key, *SONGS, *(["show:" + show] if show else [])]}

    def native(self, *children):
        return self.record("provider_input:revision", {"files": list(children)},
                           [key for child in children for key in child["entityKeys"]], kind="provider_input")

    @staticmethod
    def query(**changes):
        return {"artistUuid": ARTIST, "entityKeys": SONGS, **changes}

    def serve(self):
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.index))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}"

    def post(self, base, route, query):
        request = Request(base + route, data=json.dumps(query).encode(), headers={"Content-Type": "application/json"})
        with urlopen(request) as response:
            return json.load(response)

    def test_alias_union_deduplicates_files_and_keeps_same_day_performances_separate(self):
        self.chunk("100", self.legacy(1), self.legacy(2, songs=SONGS[:1]),
                   self.legacy(3, show=LATE_SHOW), self.legacy(4, artist="00000000-0000-0000-0000-000000000099"),
                   self.legacy(1, key="track_alias:duplicate"))
        self.index.scan_once()
        base = self.serve()

        result = self.post(base, "/api/music/homes", self.query())

        self.assertEqual(result["total"], 2)
        self.assertEqual({row["showUuid"]: row["fileCount"] for row in result["items"]}, {SHOW: 2, LATE_SHOW: 1})
        files = self.post(base, "/api/music/files", self.query(homeKey="show:" + SHOW, limit=1, offset=1))
        self.assertEqual((files["total"], len(files["items"])), (2, 1))
        item = files["items"][0]
        with urlopen(base + "/api/record?id=" + str(item["recordId"])) as response:
            original = json.load(response)
        self.assertEqual(original["record"]["proposal"]["home"]["showUuid"], SHOW)
        self.assertEqual(original["playback"], ["https://audio.test/original.mp3"])

    def test_unbound_native_recordings_remain_distinct_inside_one_input_and_migrate_exactly(self):
        parent = self.native(self.child(0), self.child(1), self.child(2, origin="recording-b"), self.child(3, show=SHOW))
        self.chunk("100", parent, self.record("provider_input:no-files", {"files": []}, [], kind="provider_input"))
        self.index.scan_once()

        result = self.music.homes(self.query())

        self.assertEqual(result["total"], 3)
        self.assertEqual(sorted(row["fileCount"] for row in result["items"]), [1, 1, 2])
        account = next(row for row in result["items"] if row["fileCount"] == 2)
        files = self.music.files(self.query(homeKey=account["homeKey"]))
        self.assertEqual([row["fileOrdinal"] for row in files["items"]], [0, 1])
        self.assertEqual(self.index.file_detail(files["items"][1]["recordId"], files["items"][1]["nativeFileKey"])["file"], parent["proposal"]["files"][1])
        with closing(self.index.connect()) as db:
            db.execute("ALTER TABLE native_files DROP COLUMN recording_key")
            db.commit()
        original_detail = ShadowIndex.detail

        def existing_child_detail(index, record_id):
            value = original_detail(index, record_id)
            self.assertNotEqual(value["record"]["inputKey"], "provider_input:no-files")
            return value

        with patch.object(ShadowIndex, "detail", new=existing_child_detail):
            restarted = ShadowMusicQueries(ShadowIndex(self.run))
        self.assertEqual(restarted.homes(self.query())["items"], result["items"])

    def test_popular_song_pages_every_performance_and_keeps_playback_at_its_original_home(self):
        shows = [f"00000000-0000-0000-0002-{ordinal:012d}" for ordinal in range(115)]
        records = [self.legacy(ordinal * 2 + file_index + 1, show=show)
                   for ordinal, show in enumerate(shows) for file_index in range(2)]
        self.chunk("100", *records)
        self.index.scan_once()
        base = self.serve()

        homes = []
        for offset in (0, 100):
            result = self.post(base, "/api/music/homes", self.query(limit=100, offset=offset))
            self.assertEqual(result["total"], len(shows))
            homes.extend(result["items"])

        self.assertEqual({home["showUuid"] for home in homes}, set(shows))
        self.assertEqual(len(homes), len(shows))
        self.assertTrue(all(home["fileCount"] == 2 for home in homes))
        last_home = homes[-1]
        files = []
        for offset in (0, 1):
            result = self.post(base, "/api/music/files", self.query(
                homeKey=last_home["homeKey"], limit=1, offset=offset))
            self.assertEqual(result["total"], 2)
            files.extend(result["items"])

        self.assertEqual(len({file["fileKey"] for file in files}), 2)
        for file in files:
            with urlopen(base + "/api/record?id=" + str(file["recordId"])) as response:
                original = json.load(response)
            self.assertEqual(file["showKey"], last_home["homeKey"])
            self.assertEqual(original["record"]["proposal"]["home"]["showUuid"], last_home["showUuid"])
            self.assertEqual(original["playback"], ["https://audio.test/original.mp3"])

    def test_replay_and_missing_chunks_remove_memberships_without_date_fallback(self):
        self.chunk("100", self.legacy(1), self.legacy(2, show=None))
        self.index.scan_once()
        self.assertEqual(self.music.homes(self.query())["total"], 2)
        replacement = self.legacy(1, songs=["repertoire:other"])
        path = self.chunk("200", replacement)
        self.index.scan_once()
        remaining = self.music.homes(self.query())
        self.assertEqual(remaining["total"], 1)
        self.assertEqual(remaining["items"][0]["kind"], "recording_account")
        self.assertIsNone(remaining["items"][0]["showUuid"])
        path.unlink()
        self.index.scan_once()
        self.assertEqual(self.music.homes(self.query(entityKeys=["repertoire:other"]))["total"], 0)

    def test_complete_key_union_exceeds_low_sqlite_bind_limit_and_deadline_failure_recovers(self):
        self.chunk("100", self.legacy(1))
        self.index.scan_once()
        original = self.index.connect

        def bounded_connect(**kwargs):
            db = original(**kwargs)
            db.setlimit(sqlite3.SQLITE_LIMIT_VARIABLE_NUMBER, 8)
            return db

        keys = SONGS + ["repertoire:absent-" + str(index) for index in range(100)]
        with patch.object(self.index, "connect", side_effect=bounded_connect):
            self.assertEqual(self.music.homes(self.query(entityKeys=keys))["items"][0]["fileCount"], 1)
        base = self.serve()
        with patch("shadow_reader.time.monotonic", side_effect=itertools.count(step=100)):
            with self.assertRaises(HTTPError) as interrupted:
                self.post(base, "/api/music/homes", self.query(entityKeys=keys))
        self.assertEqual(interrupted.exception.code, 503)
        interrupted.exception.close()
        self.assertEqual(self.post(base, "/api/music/homes", self.query())["total"], 1)
        with self.assertRaises(HTTPError) as invalid:
            self.post(base, "/api/music/homes", self.query(artistUuid="Artist name"))
        self.assertEqual(invalid.exception.code, 400)
        invalid.exception.close()


if __name__ == "__main__":
    unittest.main()
