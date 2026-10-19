"""Work suggestions reach exact song groups and audio without replacing supported links."""

from contextlib import closing
import gzip
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import urlopen

from composition_catalogue import CompositionCatalogueBuilder
from music_catalogue import MusicCatalogue, MusicCatalogueBuilder, Source
from music_consolidation import MusicConsolidationExporter, MusicConsolidationProjection, sha256_file
from music_discovery import MusicDiscovery, handler_for
from music_groups import MusicNormalizationChanged
from shadow_reader import ShadowIndex, handler_for as reader_handler


ARTISTS = ["00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002"]
SHOWS = ["00000000-0000-0000-0000-000000000011", "00000000-0000-0000-0000-000000000012"]
MBIDS = ["00000000-0000-0000-0000-000000000021", "00000000-0000-0000-0000-000000000022", "00000000-0000-0000-0000-000000000023"]


class TestCompositionCatalogue(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.run = self.root / "run"
        self.run.mkdir()
        self.catalogue = self.root / "catalogue.sqlite3"
        self.normalization = self.root / "normalization.sqlite3"
        self.database = self.root / "compositions.sqlite3"
        self.songs = [{"entityKey": "repertoire:" + str(index), "name": "Shared title" if index < 2 else "No retained version",
                       "artistUuid": ARTISTS[index % 2], "basis": "captured_artist_dictionary", "legacySongUuid": SHOWS[index % 2]}
                      for index in range(3)]
        records = []
        for index, song in enumerate(self.songs):
            records.append(self.record("repertoire", "song_sample:" + str(index), song, [song["entityKey"]], song["artistUuid"]))
        self.chunk(self.run, "repertoire", records)
        for index, song in enumerate(self.songs[:2]):
            file_uuid = "00000000-0000-0000-0001-" + str(index).zfill(12)
            self.chunk(self.run, "file_contents", [self.record("file_contents", "track_sample:" + file_uuid,
                {"home": {"fileUuid": file_uuid, "showUuid": SHOWS[index], "literalDate": "1970-01-02"},
                 "playback": {"mp3Url": "https://audio.test/" + file_uuid + ".mp3"},
                 "titleInterpretation": {"originalTitle": song["name"], "segments": [{"song": song}]}},
                [song["entityKey"], "file:" + file_uuid, "show:" + SHOWS[index]], song["artistUuid"])], str(index))
        supported = {"entityKey": "composition:musicbrainz:" + MBIDS[0], "title": "Original supported work",
                     "musicBrainzWorkMbid": MBIDS[0], "correspondenceBasis": "retained_supported_correspondence"}
        self.chunk(self.run, "compositions", [self.record("compositions", "track_sample:supported", {
            "mentions": [{"repertoireEntityKeys": [self.songs[0]["entityKey"]], "links": [supported]}]},
            [supported["entityKey"], self.songs[0]["entityKey"]], ARTISTS[0])])
        index = ShadowIndex(self.run)
        index.scan_once()
        reader = self.serve(reader_handler(index))
        MusicCatalogueBuilder([Source("catalogue", self.run, reader)], self.catalogue, minimum_free_bytes=0).build()
        bundle = self.root / "music-input"
        MusicConsolidationExporter(self.catalogue, bundle, minimum_free_bytes=0).export()
        with gzip.open(bundle / "descriptors.jsonl.gz", "rt") as stream:
            descriptors = [json.loads(line) for line in stream]
        output = self.root / "music-output"
        output.mkdir()
        rows = []
        for descriptor in descriptors:
            song = next(song for song in self.songs if song["entityKey"] == descriptor["originalKey"])
            rows.append({"domain": "repertoire_correspondences", "inputKind": "repertoire_descriptor", "artistUuid": song["artistUuid"],
                "proposal": {"scope": "catalogue", "artistUuid": song["artistUuid"], "originalKey": song["entityKey"],
                    "originalTitle": song["name"], "sourceWitness": descriptor["witness"], "disposition": "proposed",
                    "reason": "fixture", "sequenceBasis": "title_text_only", "targets": [{"song": song, "relation": "identity_alias",
                        "disposition": "proposed", "reason": "fixture", "qualifiers": []}]}})
        artifact = self.chunk(output, "repertoire_correspondences", rows)
        progress = output / "progress.json"
        progress.write_text('{"state":"completed"}')
        music_output = {"complete": True, "inputManifestSha256": sha256_file(bundle / "manifest.json"), "logicalRows": len(rows),
                        "extractorVersion": "fixture", "resolverVersion": "fixture", "chunks": [artifact], "progress": self.artifact(progress, 0)}
        (output / "manifest.json").write_text(json.dumps(music_output))
        MusicConsolidationProjection(self.catalogue, bundle / "manifest.json", output / "manifest.json", self.normalization,
                                    minimum_free_bytes=0).build()
        self.hypotheses = self.root / "hypotheses"
        self.hypotheses.mkdir()
        self.work_keys = ["composition:musicbrainz:" + mbid for mbid in MBIDS]
        works = [self.work(index) for index in range(3)]
        results = [self.result("retained_work_candidates", work["entityKey"], work) for work in works]
        for index, song in enumerate(self.songs):
            candidates = [self.candidate(0), self.candidate(1)] if index == 0 else [self.candidate(0)] if index == 1 else []
            proposal = {"song": {"scope": "catalogue", "artistUuid": song["artistUuid"], "artistName": "Artist " + song["artistUuid"][-1],
                                "song": song, "originalMemberships": 1},
                        "outcome": "hypotheses" if candidates else "missing_retained_candidates", "reason": "retained literal evidence",
                        "method": "composition-hypotheses-v1", "candidates": candidates,
                        "externalUniverseComplete": False, "publicationPermitted": False}
            results.append(self.result("composition_hypotheses", json.dumps(["catalogue", song["artistUuid"], song["entityKey"]]), proposal))
        self.results = results
        self.manifest = self.write_hypotheses(results)

    @staticmethod
    def artifact(path, rows):
        return {"path": path.name, "bytes": path.stat().st_size, "sha256": sha256_file(path), "rows": rows}

    @staticmethod
    def record(domain, key, proposal, entities, artist):
        return {"domain": domain, "inputKey": key, "inputUuid": key, "inputKind": key.split(":")[0],
                "artistUuid": artist, "artistName": "Artist " + artist[-1], "provider": "fixture", "method": "fixture",
                "outcome": "proposed", "entityKeys": entities, "proposal": proposal}

    def chunk(self, root, domain, records, name="100"):
        folder = root / domain
        folder.mkdir(exist_ok=True)
        path = folder / (name + ".jsonl.gz")
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for record in records:
                stream.write(json.dumps(record) + "\n")
        value = self.artifact(path, len(records))
        value["path"] = str(path.relative_to(root))
        return value

    def work(self, index):
        evidence = {"inputRevisionUuid": SHOWS[0], "jsonPointer": "/works/" + str(index)}
        observed = lambda value: {"value": value, "evidence": evidence}
        return {"entityKey": self.work_keys[index], "identity": {"identity": {"provider": "musicbrainz.org", "resourceType": "work", "providerKey": MBIDS[index]}, "evidence": evidence},
                "titles": [observed("Shared work" if index < 2 else "Unassociated work")],
                "aliases": [{"name": observed("An explicit alias")}], "iswcs": [{"original": observed("T-123.456.789-0")}],
                "writerRoles": [{"role": observed("composer"), "artist": {"name": observed("An external writer")}}],
                "lookupAssertions": [], "searchAssertions": [], "recordingAssertions": [], "recordingWorkRelationships": []}

    def candidate(self, index):
        work = self.work(index)
        return {"workEntityKey": work["entityKey"], "workIdentity": work["identity"], "matchingTitles": work["titles"],
                "retrievalBasis": "retained_literal_work_title", "comparison": {"disposition": "review", "features": []}}

    @staticmethod
    def result(domain, key, proposal):
        return {"domain": domain, "inputKey": key, "proposal": proposal}

    def write_hypotheses(self, rows):
        artifact = self.chunk(self.hypotheses, "composition_hypotheses", rows)
        progress = self.hypotheses / "progress.json"
        progress.write_text('{"state":"completed"}')
        manifest = {"version": "composition-hypothesis-output-v1", "complete": True, "songs": 3, "works": 3,
                    "outcomes": {"hypotheses": 2, "missing_retained_candidates": 1}, "projectorVersion": "composition-hypotheses-v1",
                    "inputs": {"musicInputManifest": {"sha256": sha256_file(self.root / "music-input/manifest.json")},
                               "musicOutputManifest": {"sha256": sha256_file(self.root / "music-output/manifest.json")}},
                    "chunks": [artifact], "progress": self.artifact(progress, 1)}
        path = self.hypotheses / "manifest.json"
        path.write_text(json.dumps(manifest))
        return path

    def build(self, **options):
        return CompositionCatalogueBuilder(self.normalization, self.manifest, self.database,
            manifest_sha256=sha256_file(self.manifest), minimum_free_bytes=0, **options).build()

    def discovery(self, **options):
        return MusicDiscovery(MusicCatalogue(self.catalogue), normalization=self.normalization, compositions=self.database, **options)

    def serve(self, handler):
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}"

    def get(self, base, route, **query):
        with urlopen(base + route + "?" + urlencode(query)) as response:
            return json.load(response)

    def test_work_to_other_artists_song_groups_and_exact_audio_preserves_supported_work_routes(self):
        self.build()
        base = self.serve(handler_for(self.discovery(people_url="https://people.test", venue_url="https://venues.test")))
        stats = self.get(base, "/api/stats")
        generation = stats["generation"]
        self.assertEqual(stats["navigation"]["tapers"], "https://people.test/")
        self.assertEqual(self.get(base, "/api/composition-works", q="external writer")["total"], 3)
        work = self.get(base, "/api/composition-work", key=self.work_keys[0], generation=generation)
        self.assertEqual({row["artist_uuid"] for row in work["songs"]}, set(ARTISTS))
        self.assertFalse(work["acceptedIdentity"])
        self.assertIsNone(work["inference"])
        song = work["songs"][0]
        coordinates = {"scope": song["scope"], "artist": song["artist_uuid"], "key": song["entity_key"], "generation": generation}
        group = self.get(base, "/api/song-group", **coordinates)
        self.assertEqual(group["entity_key"], song["entity_key"])
        performances = self.get(base, "/api/song-performances", **coordinates)
        self.assertEqual(performances["performancesTotal"], 1)
        files = self.get(base, "/api/performance-files", **coordinates, homeKey=performances["items"][0]["homeKey"])
        selected = files["items"][0]
        original = self.get(base, "/api/record", source=selected["source"], id=selected["recordId"], generation=generation)
        self.assertTrue(original["playback"][0].startswith("https://audio.test/"))
        self.assertEqual(self.get(base, "/api/works")["total"], 1)
        self.assertEqual(self.get(base, "/api/work", key=self.work_keys[0])["title"], "Original supported work")

    def test_alternatives_missing_and_all_work_ids_keep_original_evidence(self):
        self.build()
        discovery = self.discovery()
        self.assertEqual(discovery.compositions.works()["total"], 3)
        self.assertEqual(discovery.compositions.works(q=MBIDS[0])["total"], 1)
        self.assertEqual(discovery.compositions.works(q=self.work_keys[0])["total"], 1)
        self.assertEqual(discovery.compositions.work(self.work_keys[2])["songs_total"], 0)
        first = discovery.compositions.song("catalogue", ARTISTS[0], self.songs[0]["entityKey"])
        self.assertEqual([item["work_key"] for item in first["candidates"]], self.work_keys[:2])
        missing = discovery.compositions.song("catalogue", ARTISTS[0], self.songs[2]["entityKey"])
        self.assertEqual((missing["outcome"], missing["candidates"]), ("missing_retained_candidates", []))
        proof = discovery.compositions.evidence("song", self.songs[0]["entityKey"], "catalogue", ARTISTS[0])
        self.assertEqual(proof, self.results[3])
        with self.assertRaises(KeyError):
            discovery.compositions.song("native", ARTISTS[0], self.songs[0]["entityKey"])
        with self.assertRaises(ValueError):
            self.discovery(people_url="https://people.test/?destination=elsewhere")

    def test_exact_replay_is_allowed_but_conflicting_or_incomplete_output_keeps_previous_image(self):
        self.write_hypotheses([*self.results, self.results[0]])
        self.build()
        retained = sha256_file(self.database)
        changed = {**self.results[0], "proposal": {**self.results[0]["proposal"], "titles": []}}
        self.write_hypotheses([*self.results, changed])
        with self.assertRaisesRegex(ValueError, "replay changes"):
            self.build()
        self.write_hypotheses(self.results[:-1])
        with self.assertRaisesRegex(ValueError, "complete population"):
            self.build()
        self.assertEqual(sha256_file(self.database), retained)

    def test_new_suggestion_generation_rejects_open_page_and_changed_evidence_requires_reload(self):
        self.build()
        generation = self.discovery().stats()["generation"]
        self.results[3]["proposal"]["reason"] = "Later interpretation of the same source"
        self.write_hypotheses(self.results)
        self.build()
        base = self.serve(handler_for(self.discovery()))
        with self.assertRaises(HTTPError) as stale:
            self.get(base, "/api/composition-work", key=self.work_keys[0], generation=generation)
        self.assertEqual(stale.exception.code, 409)
        stale.exception.close()
        current = self.discovery()
        with gzip.open(self.hypotheses / "composition_hypotheses/100.jsonl.gz", "at") as stream:
            stream.write("{}\n")
        with self.assertRaises(MusicNormalizationChanged):
            current.compositions.evidence("work", self.work_keys[0])


if __name__ == "__main__":
    unittest.main()
