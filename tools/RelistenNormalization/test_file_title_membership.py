"""Public reader journeys over typed replay results and unchanged original files."""

from contextlib import closing
import gzip
import json
from pathlib import Path
import sqlite3
import unittest
import uuid
from urllib.parse import urlencode
from urllib.request import urlopen

import test_music_consolidation as helpers
from file_title_replay import FileTitleReplayExporter, FileTitleReplayProjection, digest, pin
from music_catalogue import MusicCatalogue
from music_discovery import MusicDiscovery, handler_for
from shadow_reader import ShadowIndex, handler_for as source_handler


class TestFileTitleMembership(unittest.TestCase):
    def setUp(self):
        self.fixture = helpers.TestMusicConsolidationExport()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        self.artist = helpers.ARTIST
        self.show = str(uuid.uuid4())
        self.late = str(uuid.uuid4())
        self.source = str(uuid.uuid4())
        self.song = self.fixture.song("repertoire:drums", "Drums")
        self.fixture.chunk("repertoire", self.song, kind="song_sample")
        self.ids = []

    def legacy(self, number, title, keys=(), home=None):
        file_uuid = str(uuid.UUID(int=1000 + number))
        self.ids.append(file_uuid)
        proposal = dict(home=dict(fileUuid=file_uuid, sourceUuid=self.source, showUuid=home or self.show,
                                   literalDate="1970-01-01"),
                        playback=dict(fileUuid=file_uuid, sourceUuid=self.source, showUuid=home or self.show,
                                      mp3Url="https://audio.test/" + file_uuid + ".mp3"),
                        titleInterpretation=None if title is None else dict(fileUuid=file_uuid, sourceUuid=self.source,
                            originalTitle=title, segments=[dict(outcome="unresolved", song=None)]))
        path = self.fixture.chunk("file_contents", proposal, name=str(200 + number),
                                  entities=["file:" + file_uuid, "source:" + self.source, "show:" + (home or self.show), *keys])
        with gzip.open(path, "rt") as stream:
            record = json.load(stream)
        record["inputUuid"] = file_uuid
        with gzip.open(path, "wt") as stream:
            stream.write(json.dumps(record) + "\n")
        return file_uuid

    def build(self):
        index = ShadowIndex(self.fixture.run)
        index.scan_once()
        self.source_url = self.fixture.serve(source_handler(index))
        self.fixture.build(reader_url=self.source_url)
        self.fixture.export()
        descriptors = self.fixture.rows("descriptors.jsonl.gz")
        rows = [self.fixture.correspondence(row, self.song) for row in descriptors]
        self.fixture.project(self.fixture.outputs(rows))
        input_path = self.fixture.bundle / "manifest.json"
        export_dir = self.root / "replay-input"
        request = FileTitleReplayExporter(input_path, export_dir, digest(input_path), minimum_free_bytes=0).export()
        output_dir = self.root / "replay-output"
        output_dir.mkdir()
        results = output_dir / "titles.jsonl.gz"
        self.title_ids = {}
        with gzip.open(request["titles"]["path"], "rt") as stream, gzip.open(results, "wt") as output:
            for title in map(json.loads, stream):
                self.title_ids[title["originalTitle"]] = title["titleId"]
                targets = ["Drums", "Space", "Drums"] if ">" in title["originalTitle"] else [title["originalTitle"]]
                correspondence = dict(scope=title["scope"], artistUuid=title["artistUuid"],
                    originalKey="repertoire:original-" + str(title["titleId"]), originalTitle=title["originalTitle"],
                    sourceWitness=title["witness"], disposition="proposed", reason="shared_title_correspondence",
                    sequenceBasis="source_title_order", targets=[dict(song=self.fixture.song("repertoire:" + name.lower(), name),
                    relation="compound_member", disposition="proposed", reason="shared_title_correspondence",
                    qualifiers=[], evidence=dict(quote=name)) for name in targets])
                output.write(json.dumps(dict(titleId=title["titleId"], correspondence=correspondence)) + "\n")
        with closing(sqlite3.connect(self.root / "normalization.sqlite3")) as db:
            metadata = {key: json.loads(value) for key, value in db.execute("SELECT * FROM metadata")}
        output_manifest = output_dir / "manifest.json"
        output_manifest.write_text(json.dumps(dict(version="retained-file-title-replay-output-v1", state="completed",
            inputManifestSha256=digest(export_dir / "manifest.json"), inputManifest=pin(export_dir / "manifest.json", 1),
            musicInputManifest=request["musicInputManifest"], fileMap=request["fileMap"],
            titles=pin(results, request["titles"]["rows"]), extractorVersion=metadata["extractorVersion"],
            resolverVersion=metadata["resolverVersion"], fileReplayVersion="retained-file-title-replay-v2", knowledgeSha256=None)))
        projection = self.root / "replay-projection"
        FileTitleReplayProjection(output_manifest, projection, digest(output_manifest)).project()
        self.discovery = MusicDiscovery(MusicCatalogue(self.fixture.catalogue), normalization=self.root / "normalization.sqlite3",
                                        file_title_replay=projection / "manifest.json")
        self.base = self.fixture.serve(handler_for(self.discovery))

    def get(self, path, **parameters):
        with urlopen(self.base + path + "?" + urlencode(parameters)) as response:
            return json.load(response)

    def coordinates(self, key="repertoire:drums"):
        return dict(scope="catalogue", artist=self.artist, key=key, generation=self.discovery.stats()["generation"])

    def test_replayed_title_search_returns_all_files_and_preserves_repeated_mentions_and_audio(self):
        for number in range(205):
            self.legacy(number, "Drums > Space > Drums")
        # A known source membership without title remains useful. A stale v1
        # Drums membership on a Space file is replaced by its replayed title.
        missing = self.legacy(205, None, keys=["repertoire:drums"], home=self.late)
        wrong = self.legacy(206, "Space", keys=["repertoire:drums"])
        self.build()
        membership = self.discovery.file_title_replay
        proposed = list(membership.iter_memberships("catalogue", self.artist))
        covered = list(membership.covered_files("catalogue", self.artist))
        self.assertEqual(len(proposed), 616)
        self.assertEqual(len(covered), 206)
        self.assertEqual(sum(row["targetKey"] == "repertoire:drums" for row in proposed), 410)
        self.assertEqual({row["targetTitle"] for row in proposed}, {"Drums", "Space"})
        self.assertEqual({row["fileUuid"] for row in covered}, set(self.ids) - {missing})
        self.assertEqual(len(list(membership.iter_memberships("catalogue", self.artist, self.fixture.run))), 616)
        found = self.get("/api/normalized-songs", q="Space")
        self.assertIn("repertoire:space", [row["entity_key"] for row in found["items"]])
        coordinates = self.coordinates()
        homes = self.get("/api/song-performances", **coordinates)
        self.assertEqual(homes["performancesTotal"], 2)
        all_files = []
        for offset in (0, 100, 200):
            page = self.get("/api/performance-files", **coordinates, homeKey="show:" + self.show, limit=100, offset=offset)
            self.assertEqual(page["total"], 205)
            all_files.extend(page["items"])
        self.assertEqual(len({row["fileKey"] for row in all_files}), 205)
        self.assertNotIn("file:" + wrong, {row["fileKey"] for row in all_files})
        self.assertTrue(all([relation["targetOrdinal"] for relation in row["normalizationRelations"]] == [0, 2] for row in all_files))
        first = all_files[0]
        detail = self.get("/api/record", source=first["source"], id=first["recordId"], generation=coordinates["generation"])
        self.assertEqual(detail["record"]["proposal"]["titleInterpretation"]["segments"][0]["outcome"], "unresolved")
        self.assertEqual(detail["playback"], ["https://audio.test/" + first["fileKey"][5:] + ".mp3"])
        late = self.get("/api/performance-files", **coordinates, homeKey="show:" + self.late)
        self.assertEqual([row["fileKey"] for row in late["items"]], ["file:" + missing])
        self.assertEqual(self.get("/api/performance-files", **coordinates, homeKey="show:" + str(uuid.uuid4()))["total"], 0)

    def native(self, number, titles):
        parent_uuid = str(uuid.uuid4())
        children = [dict(fileOrdinal=ordinal, outcome="proposed", entityKeys=[], entityKey="native_file:fixture-" + str(number) + "-" + str(ordinal),
                        recordingOrigin=dict(item=dict(provider="fixture", providerKey="same-recording")),
                        titleInterpretation=dict(artistUuid=self.artist, originalTitle=title),
                        file=dict(title=dict(value=title)), catalogTrackUuid=None, catalogSourceUuid=None,
                        catalogShowUuid=None, catalogPlayback=None)
                    for ordinal, title in enumerate(titles)]
        path = self.fixture.chunk("file_contents", dict(files=children), name=str(number), kind="provider_input",
                                  entities=[child["entityKey"] for child in children])
        with gzip.open(path, "rt") as stream:
            parent = json.load(stream)
        parent["inputUuid"] = parent_uuid
        with gzip.open(path, "wt") as stream:
            stream.write(json.dumps(parent) + "\n")
        return children

    def test_replayed_native_children_keep_one_recording_account_and_no_invented_audio(self):
        children = self.native(500, ["Drums", "Drums"])
        self.build()
        coordinates = self.coordinates()
        homes = self.get("/api/song-performances", **coordinates)
        self.assertEqual((homes["total"], homes["performancesTotal"], homes["unboundAccountsTotal"]), (1, 0, 1))
        self.assertIsNone(homes["items"][0]["showUuid"])
        files = self.get("/api/performance-files", **coordinates, homeKey=homes["items"][0]["homeKey"])
        self.assertEqual(files["total"], 2)
        self.assertEqual({row["nativeFileKey"] for row in files["items"]}, {row["entityKey"] for row in children})
        for file in files["items"]:
            detail = self.get("/api/file", source=file["source"], generation=coordinates["generation"],
                              parentId=file["recordId"], nativeFileKey=file["nativeFileKey"])
            self.assertEqual(detail["file"]["entityKey"], file["nativeFileKey"])
            self.assertEqual(detail["playback"], [])

    def test_saved_review_replaces_replay_membership_and_withdrawal_restores_it(self):
        self.legacy(0, "Drums > Space > Drums")
        self.build()
        replay = self.discovery.file_title_replay
        class Knowledge:
            changes = []
            sha = "before"
            def snapshot(self):
                return dict(sha256=self.sha, rules=[dict(definition=dict(domain="songs"))])
            def music_changes(self, metadata, snapshot):
                return []
            def file_title_changes(self, metadata, snapshot):
                return self.changes
        knowledge = Knowledge()
        self.discovery.review_client = knowledge
        self.discovery.groups.review_client = knowledge
        replay.review_client = knowledge
        before = self.coordinates()
        self.assertEqual(self.get("/api/song-performances", **before)["total"], 1)
        knowledge.sha = "accepted"
        knowledge.changes = [dict(titleId=self.title_ids["Drums > Space > Drums"], correspondence=dict(
            scope="catalogue", artistUuid=self.artist, originalKey="repertoire:original-1", disposition="proposed",
            reason="reviewed_artist_song_correspondence", sequenceBasis="single", targets=[dict(
                song=self.fixture.song("repertoire:space", "Space"), relation="identity_alias", disposition="proposed",
                reason="reviewed_artist_song_correspondence", qualifiers=[])]))]
        from urllib.error import HTTPError
        with self.assertRaises(HTTPError) as stale:
            self.get("/api/song-performances", **before)
        self.assertEqual(stale.exception.code, 409)
        stale.exception.close()
        current = self.coordinates()
        self.assertEqual(self.get("/api/song-performances", **current)["total"], 0)
        self.assertEqual(self.get("/api/song-performances", **self.coordinates("repertoire:space"))["total"], 1)
        original = self.get("/api/replay-song", scope="catalogue", artist=self.artist, key="repertoire:original-1")
        self.assertEqual(original["title"], "Drums > Space > Drums")
        source = self.get("/api/review-source", domain="songs", artist=self.artist,
                          key=json.dumps(["catalogue", "repertoire:original-1"], separators=(",", ":")))["items"][0]
        self.assertTrue(source["evidenceUrl"].startswith("/api/replay-song?"))
        observations = self.get("/api/review-observations", domain="songs", artist=self.artist,
                                literal="Drums > Space > Drums")
        self.assertEqual([item["stableKey"] for item in observations["items"]], [source["stableKey"]])
        self.assertTrue(observations["complete"])
        candidates = self.get("/api/review-candidates", domain="songs", scope="catalogue", artist=self.artist, q="Space")
        self.assertIn(json.dumps(["catalogue", "repertoire:space"], separators=(",", ":")),
                      [item["stableKey"] for item in candidates["items"]])
        knowledge.sha = "moved-again"
        knowledge.changes[0]["correspondence"]["targets"][0]["song"] = self.fixture.song("repertoire:drums", "Drums")
        baseline_target = self.get("/api/review-source", domain="songs", artist=self.artist,
                                   key=json.dumps(["catalogue", "repertoire:space"]))
        self.assertEqual(baseline_target["total"], 1)
        self.assertEqual(baseline_target["items"][0]["displayName"], "Space")
        with urlopen(self.base + baseline_target["items"][0]["evidenceUrl"]) as response:
            evidence = json.load(response)
        self.assertEqual(evidence["members_total"], 1)
        self.assertTrue(evidence["replay_group"])
        knowledge.sha = "withdrawn"
        knowledge.changes = []
        self.assertEqual(self.get("/api/song-performances", **self.coordinates())["total"], 1)

    def test_changed_replay_fails_explicitly_while_original_playback_stays_available(self):
        self.legacy(0, "Drums")
        self.build()
        coordinates = self.coordinates()
        file = self.get("/api/performance-files", **coordinates, homeKey="show:" + self.show)["items"][0]
        replay = self.discovery.file_title_replay
        Path(replay.metadata["outputManifest"]["path"]).write_text("changed replay manifest")
        from urllib.error import HTTPError
        with self.assertRaises(HTTPError) as changed:
            self.get("/api/stats")
        self.assertEqual(changed.exception.code, 409)
        changed.exception.close()
        with urlopen(self.source_url + "/api/record?id=" + str(file["recordId"])) as response:
            original = json.load(response)
        self.assertEqual(original["playback"], ["https://audio.test/" + self.ids[0] + ".mp3"])

    def test_reader_appends_remain_usable_but_retargeted_saved_coordinates_fail(self):
        self.legacy(0, "Drums")
        self.legacy(1, "Space")
        self.build()
        coordinates = self.coordinates()
        selected = self.get("/api/performance-files", **coordinates, homeKey="show:" + self.show)["items"][0]
        # A later index append is unrelated to the retained selected file.
        self.legacy(2, "Later import")
        index = ShadowIndex(self.fixture.run)
        index.scan_once()
        self.assertEqual(self.get("/api/performance-files", **coordinates, homeKey="show:" + self.show)["total"], 1)
        with closing(index.connect()) as db:
            other = db.execute("SELECT id,header_id FROM records WHERE id<>? AND header_id IS NOT NULL ORDER BY id LIMIT 1",
                               (selected["recordId"],)).fetchone()
            db.execute("UPDATE records SET header_id=? WHERE id=?", (other["header_id"], selected["recordId"]))
            db.commit()
        from urllib.error import HTTPError
        for route, parameters in (("/api/performance-files", dict(**coordinates, homeKey="show:" + self.show)),
                ("/api/record", dict(source=selected["source"], generation=coordinates["generation"],
                                     id=selected["recordId"], fileKey=selected["fileKey"]))):
            with self.assertRaises(HTTPError) as changed:
                self.get(route, **parameters)
            self.assertEqual(changed.exception.code, 409)
            changed.exception.close()
        # Another original source result is still independently available.
        with urlopen(self.source_url + "/api/record?id=" + str(other["id"])) as response:
            self.assertEqual(response.status, 200)

    def test_completed_authority_refresh_has_a_separate_database_work_budget(self):
        for number in range(205):
            self.legacy(number, "Drums")
        self.build()
        clock = dict(now=0)
        class Knowledge:
            def snapshot(self):
                return dict(sha256="saved", rules=[dict(definition=dict(domain="songs"))])
            def file_title_changes(self, metadata, snapshot):
                clock["now"] = 20
                return []
        replay = self.discovery.file_title_replay
        replay.review_client = Knowledge()
        from unittest.mock import patch
        with patch("file_title_membership.time.monotonic", side_effect=lambda: clock["now"]):
            files = replay.files("catalogue", self.artist, "repertoire:drums")
        self.assertEqual(len(files), 205)
