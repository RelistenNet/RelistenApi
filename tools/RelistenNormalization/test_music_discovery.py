"""Music HTTP behavior over real retained chunks and the existing source reader."""

import gzip
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import urlopen
from unittest.mock import patch

from music_catalogue import MusicCatalogue, MusicCatalogueBuilder, Source
from music_discovery import MusicDiscovery, handler_for
from shadow_reader import ShadowIndex, handler_for as reader_handler


class TestMusicDiscovery(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.key = "repertoire:song-one"
        self.song = {"entityKey": self.key, "artistUuid": "artist-one", "legacySongUuid": "legacy-song", "name": "A Shared Title", "basis": "captured_artist_dictionary"}
        self.catalogue_source = self.make_source("catalogue", "archive-file", "https://archive.org/audio.mp3")
        self.local_source = self.make_source("local", "local-file", "https://example.test/local.mp3")
        self.database = self.root / "music.sqlite3"
        MusicCatalogueBuilder([self.catalogue_source, self.local_source], self.database, minimum_free_bytes=0).build()
        self.discovery = MusicDiscovery(MusicCatalogue(self.database))
        self.url = self.serve(handler_for(self.discovery))

    def serve(self, handler):
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}"

    def make_source(self, scope, file_uuid, playback):
        run = self.root / scope
        run.mkdir()
        (run / "manifest.json").write_text(json.dumps({"state": "completed"}))
        (run / "repertoire").mkdir()
        dictionary = self.record("repertoire", "song:one", self.song)
        with gzip.open(run / "repertoire" / "0001.jsonl.gz", "wt") as stream:
            stream.write(json.dumps(dictionary) + "\n")
        (run / "file_contents").mkdir()
        content = self.record("file_contents", "track:" + file_uuid, {
            "home": {"fileUuid": file_uuid, "sourceUuid": "source-one", "showUuid": "original-show", "literalDate": "1978-03-04"},
            "playback": {"mp3Url": playback},
            "titleInterpretation": {"originalTitle": "A Shared Title > Jam", "segments": [{"title": "A Shared Title", "song": self.song}, {"title": "Jam", "song": None, "candidateSongs": [], "outcome": "unresolved"}]},
        })
        with gzip.open(run / "file_contents" / "0001.jsonl.gz", "wt") as stream:
            stream.write(json.dumps(content) + "\n")
        index = ShadowIndex(run)
        index.scan_once()
        origin = self.serve(reader_handler(index))
        return Source(scope, run, origin)

    def record(self, domain, key, proposal):
        return {"domain": domain, "inputKey": key, "inputKind": "track", "inputUuid": key, "artistUuid": "artist-one", "artistName": "A Real Artist", "provider": "archive.org", "outcome": "proposed", "reason": None, "method": "fixture", "entityKeys": [self.key, "show:original-show"], "proposal": proposal}

    def get(self, path):
        if path.startswith(("/api/related?", "/api/record?", "/api/files?", "/api/file?")) and "generation=" not in path:
            path += "&" + urlencode({"generation": self.discovery.stats()["generation"]})
        with urlopen(self.url + path) as response:
            return json.load(response)

    def source_id(self, scope):
        return next(s["id"] for s in self.get("/api/stats")["sources"] if s["scope"] == scope)

    def make_native_source(self, name, scope, files, legacy=False):
        run = self.root / name
        run.mkdir()
        (run / "file_contents").mkdir()
        parent = self.record("file_contents", "provider_input:native", {"files": files})
        parent["inputKind"] = "provider_input"
        parent["entityKeys"] = [key for file in files for key in file["entityKeys"]]
        rows = [parent]
        if legacy:
            rows.append(self.record("file_contents", "track:legacy-mixed", {
                "titleInterpretation": {"originalTitle": self.song["name"], "segments": [{"song": self.song}]}}))
        with gzip.open(run / "file_contents" / "0001.jsonl.gz", "wt", encoding="utf-8") as stream:
            for row in rows:
                stream.write(json.dumps(row, ensure_ascii=False) + "\n")
        index = ShadowIndex(run)
        index.scan_once()
        return Source(scope, run, self.serve(reader_handler(index)))

    def native_file(self, ordinal, song=None, provider_url=None, audio=None):
        key = "native_file:same-scoped-child-" + str(ordinal)
        title = song["name"] if song else "Unknown native passage"
        return {"fileOrdinal": ordinal, "entityKey": key, "inputRevisionUuid": "native-revision",
                "file": {"title": {"value": title}, "filename": {"value": str(ordinal) + ".flac"},
                         "playbackLocator": {"value": provider_url or str(ordinal) + ".flac"}},
                "titleInterpretation": {"originalTitle": title},
                "mentions": [{"interpretation": {"song": song, "candidateSongs": [], "outcome": "proposed"}}] if song else [],
                "entityKeys": [key, *([song["entityKey"]] if song else [])],
                "outcome": "proposed", "catalogPlayback": audio}

    def install_native_sources(self, *sources):
        MusicCatalogueBuilder(sources, self.database, minimum_free_bytes=0).build()
        return self.get("/api/stats")

    def test_native_file_lookup_is_exact_paged_and_excludes_siblings_without_the_song_key(self):
        target = self.native_file(0, self.song)
        sibling_song = {**self.song, "entityKey": "repertoire:sibling", "name": "Sibling title"}
        sibling = self.native_file(1, sibling_song)
        last = self.native_file(2, self.song)
        source = self.make_native_source("native-only", "catalogue", [target, sibling, last])
        stats = self.install_native_sources(source)
        self.assertIs(stats["native_file_queries"], True)
        metadata = stats["sources"][0]
        self.assertEqual((metadata["native_files"], metadata["native_file_parents"], metadata["file_query_mode"]),
                         (3, 1, "native"))
        page = self.get("/api/files?" + urlencode({"source": metadata["id"], "key": self.key, "limit": 1, "offset": 1}))
        self.assertEqual((page["total"], page["limit"], page["offset"]), (2, 1, 1))
        self.assertEqual([item["fileOrdinal"] for item in page["items"]], [2])
        child = page["items"][0]
        detail = self.get("/api/file?" + urlencode({"source": metadata["id"], "parentId": child["parentId"],
                                                   "nativeFileKey": child["nativeFileKey"]}))
        self.assertEqual(detail["file"], last)
        self.assertNotEqual(detail["file"]["entityKey"], sibling["entityKey"])
        self.assertNotIn("home", detail["file"])
        self.assertNotIn("home", detail["parent"])
        self.assertEqual(detail["playback"], [])
        self.assertEqual(self.get("/api/files?" + urlencode({"source": metadata["id"], "key": "repertoire:absent"}))["total"], 0)
        self.assertEqual(self.get("/api/files?" + urlencode({"source": metadata["id"], "key": self.key, "offset": 2}))["items"], [])

    def test_native_scope_collision_keeps_provider_links_separate_from_catalog_audio(self):
        catalogue_child = self.native_file(0, self.song, provider_url="https://provider.example.test/native.mp3")
        local_child = self.native_file(0, self.song, audio={"fileUuid": "bound-file", "sourceUuid": "bound-source",
                                                         "mp3Url": "https://audio.example.test/catalog.mp3"})
        catalogue = self.make_native_source("native-catalogue", "catalogue", [catalogue_child])
        local = self.make_native_source("native-local", "local", [local_child])
        metadata = self.install_native_sources(catalogue, local)
        self.assertEqual(metadata["counts"]["songs"], 2)
        details = {}
        for scope in ("catalogue", "local"):
            source = next(item for item in metadata["sources"] if item["scope"] == scope)
            files = self.get("/api/files?" + urlencode({"source": source["id"], "key": self.key}))
            child = files["items"][0]
            details[scope] = self.get("/api/file?" + urlencode({"source": source["id"], "parentId": child["parentId"],
                                                              "nativeFileKey": child["nativeFileKey"]}))
        self.assertEqual(details["catalogue"]["file"], catalogue_child)
        self.assertEqual(details["catalogue"]["providerFileUrl"], "https://provider.example.test/native.mp3")
        self.assertEqual(details["catalogue"]["playback"], [])
        self.assertEqual(details["local"]["file"], local_child)
        self.assertEqual(details["local"]["playback"], ["https://audio.example.test/catalog.mp3"])
        self.assertIsNone(details["local"]["providerFileUrl"])
        self.assertNotIn("home", details["catalogue"]["parent"])

    def test_mixed_source_keeps_native_children_and_legacy_record_routes_separate(self):
        source = self.make_native_source("mixed-native", "catalogue", [self.native_file(0, self.song)], legacy=True)
        metadata = self.install_native_sources(source)["sources"][0]
        self.assertEqual(metadata["file_query_mode"], "mixed")
        native = self.get("/api/files?" + urlencode({"source": metadata["id"], "key": self.key}))
        parents = self.get("/api/related?" + urlencode({"source": metadata["id"], "key": self.key}))
        self.assertEqual(native["total"], 1)
        self.assertEqual(parents["total"], 2)
        legacy = next(row for row in parents["items"] if row["input_key"] == "track:legacy-mixed")
        detail = self.get("/api/record?" + urlencode({"source": metadata["id"], "id": legacy["id"]}))
        self.assertEqual(detail["record"]["inputKey"], "track:legacy-mixed")

    def test_native_coordinates_refuse_stale_generation_missing_generation_and_invalid_children(self):
        source = self.make_native_source("native-coordinates", "catalogue", [self.native_file(0, self.song)])
        before = self.install_native_sources(source)
        identifier = before["sources"][0]["id"]
        generation = before["generation"]
        MusicCatalogueBuilder([self.local_source, source], self.database, minimum_free_bytes=0).build()
        stale = {"source": identifier, "generation": generation, "key": self.key}
        for route, query in (("files", stale),
                             ("file", {**stale, "parentId": 1, "nativeFileKey": "native_file:same-scoped-child-0"})):
            with self.subTest(route=route), self.assertRaises(HTTPError) as error:
                self.get("/api/" + route + "?" + urlencode(query))
            self.assertEqual(error.exception.code, 409)
            error.exception.close()
        identifier = self.source_id("catalogue")
        for query in ({"source": identifier, "parentId": 0, "nativeFileKey": "native_file:valid"},
                      {"source": identifier, "parentId": 1, "nativeFileKey": "file:legacy"},
                      {"source": identifier, "parentId": 1, "nativeFileKey": "native_file:" + "x" * 300}):
            with self.subTest(query=query), self.assertRaises(HTTPError) as error:
                self.get("/api/file?" + urlencode(query))
            self.assertEqual(error.exception.code, 400)
            error.exception.close()
        with self.assertRaises(HTTPError) as absent_child:
            self.get("/api/file?" + urlencode({"source": identifier, "parentId": 1,
                                               "nativeFileKey": "native_file:absent-child"}))
        self.assertEqual(absent_child.exception.code, 404)
        absent_child.exception.close()
        with self.assertRaises(HTTPError) as missing:
            urlopen(self.url + "/api/files?" + urlencode({"source": identifier, "key": self.key}))
        self.assertEqual(missing.exception.code, 409)
        missing.exception.close()

    def test_artist_song_reaches_original_playable_file_without_crossing_local_scope(self):
        artists = self.get("/api/artists?q=Real")
        self.assertTrue(any(row["uuid"] == "artist-one" for row in artists["items"]))
        songs = self.get("/api/songs?" + urlencode({"artist": "artist-one", "scope": "catalogue"}))
        self.assertEqual([row["entity_key"] for row in songs["items"]], [self.key])
        song = self.get("/api/song?" + urlencode({"key": self.key, "scope": "catalogue"}))
        self.assertEqual(song["basis"], "captured_artist_dictionary")
        files = self.get("/api/related?" + urlencode({"source": self.source_id("catalogue"), "key": self.key, "domain": "file_contents"}))
        self.assertEqual(files["total"], 1)
        detail = self.get("/api/record?" + urlencode({"source": self.source_id("catalogue"), "id": files["items"][0]["id"]}))
        self.assertEqual(detail["playback"], ["https://archive.org/audio.mp3"])
        self.assertEqual(detail["record"]["proposal"]["home"]["showUuid"], "original-show")
        self.assertIsNone(detail["record"]["proposal"]["titleInterpretation"]["segments"][1]["song"])

    def test_related_results_use_exact_key_not_shared_title_or_global_text_query(self):
        result = self.get("/api/related?" + urlencode({"source": self.source_id("catalogue"), "key": "repertoire:other-song", "domain": "file_contents", "q": "A Shared Title"}))
        self.assertEqual(result["total"], 0)

    def test_local_recording_remains_in_its_explicit_scope(self):
        files = self.get("/api/related?" + urlencode({"source": self.source_id("local"), "key": self.key, "domain": "file_contents"}))
        detail = self.get("/api/record?" + urlencode({"source": self.source_id("local"), "id": files["items"][0]["id"]}))
        self.assertEqual(detail["playback"], ["https://example.test/local.mp3"])
        self.assertEqual(detail["record"]["proposal"]["home"]["fileUuid"], "local-file")

    def test_unavailable_source_reader_does_not_hide_catalogue(self):
        # A source can become unavailable after discovery was built. Artist/song
        # search is independent of its live file reader and remains usable.
        with patch("music_discovery.urlopen", side_effect=URLError("offline")):
            with self.assertRaises(HTTPError) as error:
                self.get("/api/related?" + urlencode({"source": self.source_id("catalogue"), "key": self.key}))
        self.assertEqual(error.exception.code, 503)
        error.exception.close()
        self.assertEqual(self.get("/api/songs?scope=catalogue")["total"], 1)

    def test_relationship_only_reader_adds_playable_files_without_claiming_title_coverage(self):
        full = self.make_source("unprojected-full", "another-file", "https://example.test/another.mp3")
        full = Source("catalogue", full.run, full.reader_url)
        self.discovery = MusicDiscovery(MusicCatalogue(self.database), [full])
        self.url = self.serve(handler_for(self.discovery))
        metadata = self.get("/api/stats")
        self.assertEqual(metadata["counts"]["songs"], 2)
        source = next(row for row in metadata["sources"] if row.get("title_discovery_projected") is False)
        self.assertEqual(source["visibility"], "relationship_queries_only")
        files = self.get("/api/related?" + urlencode({"source": source["id"], "key": self.key}))
        detail = self.get("/api/record?" + urlencode({"source": source["id"], "id": files["items"][0]["id"]}))
        self.assertEqual(detail["playback"], ["https://example.test/another.mp3"])
        self.assertEqual(detail["record"]["proposal"]["home"]["fileUuid"], "another-file")

    def test_unknown_source_cannot_turn_proxy_into_an_arbitrary_url_fetch(self):
        with self.assertRaises(HTTPError) as error:
            self.get("/api/related?" + urlencode({"source": "https://example.test", "key": self.key}))
        self.assertEqual(error.exception.code, 400)
        error.exception.close()

    def test_invalid_relationship_domain_is_rejected(self):
        with self.assertRaises(HTTPError) as error:
            self.get("/api/related?" + urlencode({"source": self.source_id("catalogue"), "key": self.key, "domain": "people"}))
        self.assertEqual(error.exception.code, 400)
        error.exception.close()

    def test_unknown_identity_is_a_404_not_an_empty_invented_song(self):
        with self.assertRaises(HTTPError) as error:
            self.get("/api/song?scope=catalogue&key=repertoire:missing")
        self.assertEqual(error.exception.code, 404)
        error.exception.close()

    def test_old_source_coordinates_refuse_after_atomic_source_reordering(self):
        generation = self.discovery.stats()["generation"]
        source_id = self.source_id("catalogue")
        MusicCatalogueBuilder([self.local_source, self.catalogue_source], self.database, minimum_free_bytes=0).build()
        with self.assertRaises(HTTPError) as error:
            self.get("/api/related?" + urlencode({"source": source_id, "key": self.key, "generation": generation}))
        self.assertEqual(error.exception.code, 409)
        error.exception.close()
        result = self.get("/api/related?" + urlencode({"source": self.source_id("catalogue"), "key": self.key}))
        self.assertEqual(result["total"], 1)

    def test_page_is_available_and_limits_remain_bounded(self):
        with urlopen(self.url) as response:
            self.assertEqual(response.status, 200)
            self.assertIn(b"Find the song.", response.read())
            self.assertEqual(response.headers["X-Content-Type-Options"], "nosniff")
        self.assertEqual(self.get("/api/songs?limit=100000")["limit"], 100)


if __name__ == "__main__":
    unittest.main()
