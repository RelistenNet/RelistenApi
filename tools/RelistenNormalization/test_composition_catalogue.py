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

from composition_catalogue import CompositionCatalogueBuilder, CompositionSelectionBuilder
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

    def write_selections(self, states=("selected",), *, target_context=False):
        output = self.root / "selections"
        output.mkdir(exist_ok=True)
        self.selection_database = self.root / "selections.sqlite3"
        parents = [row for row in self.results if row["domain"] == "composition_hypotheses"]
        prepared, records = [], []
        artifact = json.loads(self.manifest.read_text())["chunks"][0]
        method = "luna-composition-hypothesis-v3" if target_context else "luna-composition-hypothesis-v2"
        for index, state in enumerate(states):
            parent = parents[index]
            key = {"scope": "catalogue", "artistUuid": self.songs[index]["artistUuid"], "songEntityKey": self.songs[index]["entityKey"]}
            request = {"method": method, "model": "gpt-6-luna", "cacheKey": str(index) * 64}
            row = {"key": key, "inputSha256": "f" * 64,
                   "hypothesisOrigin": {"chunk": artifact["path"], "sha256": artifact["sha256"], "line": self.results.index(parent) + 1},
                   "request": request, "skipReason": "request_bound" if state == "skipped" else None}
            if index == 2:
                row.update(inputSha256=None, hypothesisOrigin=None, request=None, skipReason="missing_candidate_bearing_hypothesis")
            prepared.append(row)
            inference_state = "proposed" if state in {"selected", "insufficient_evidence"} else state
            choice = MBIDS[0] if state == "selected" else None
            selection = None if state == "skipped" else {"selectedWorkMbid": choice, "alternativeWorkMbids": [], "outcome": state,
                "method": request["method"], "rationale": "A model hypothesis, with literal <untrusted> prose.",
                "inference": {"selectedWorkMbid": choice, "alternativeWorkMbids": [], "outcome": state if inference_state == "proposed" else "insufficient_evidence",
                    "state": inference_state, "method": request["method"], "rationale": "A model hypothesis, with literal <untrusted> prose.",
                    "cacheKey": request["cacheKey"], "reason": None if inference_state == "proposed" else "job_budget_exhausted",
                    "publicationPermitted": False, "externalUniverseComplete": False}}
            records.append(self.result("composition_selections", json.dumps(list(key.values())), {"input": row, "selection": selection}))
        plan = {"version": "composition-selection-plan-v1", "jobUuid": SHOWS[0], "songs": [row["key"] for row in prepared],
                "hypothesisManifest": {"path": str(self.manifest), **self.artifact(self.manifest, 1)}}
        plan["hypothesisManifest"]["path"] = str(self.manifest)
        if target_context:
            context = self.root / "target-context.json"
            context.write_text(json.dumps({"version": "composition-target-context-v1", "state": "completed", "complete": True}))
            plan.update(version="composition-selection-plan-v2", targetContextManifest={
                "path": str(context), **self.artifact(context, 1)})
            plan["targetContextManifest"]["path"] = str(context)
        plan_path = output / "approved-plan.json"
        plan_path.write_text(json.dumps(plan))
        manifest = {"version": "composition-selection-batch-v2" if target_context else "composition-selection-batch-v1",
            "state": "completed", "planSha256": sha256_file(plan_path),
            "jobUuid": plan["jobUuid"], "method": method, "processedInputs": len(records),
            "preflight": {"planSha256": sha256_file(plan_path), "selectedInputs": len(records), "rows": prepared},
            "chunks": [self.chunk(output, "composition_selections", records)],
            "outcomes": {state: states.count(state) for state in set(states)}, "publicationPermitted": False, "externalUniverseComplete": False}
        path = output / "manifest.json"
        path.write_text(json.dumps(manifest))
        return path

    def build_selections(self, manifest):
        return CompositionSelectionBuilder(self.database, manifest, self.selection_database,
            manifest_sha256=sha256_file(manifest), minimum_free_bytes=0).build()

    def test_context_enriched_selection_reaches_original_audio_and_rejects_changed_context(self):
        self.build()
        manifest = self.write_selections(target_context=True)
        metadata = self.build_selections(manifest)
        base = self.serve(handler_for(self.discovery(composition_selections=self.selection_database)))
        coordinates = {"scope": "catalogue", "artist": ARTISTS[0], "key": self.songs[0]["entityKey"],
                       "generation": self.get(base, "/api/stats")["generation"]}
        song = self.get(base, "/api/song-composition-hypotheses", **coordinates)
        self.assertEqual(song["modelSelection"]["selectedWorkKey"], self.work_keys[0])
        self.assertEqual(song["modelSelection"]["method"], "luna-composition-hypothesis-v3")
        self.assertFalse(song["modelSelection"]["acceptedIdentity"])
        performances = self.get(base, "/api/song-performances", **coordinates)
        files = self.get(base, "/api/performance-files", **coordinates, homeKey=performances["items"][0]["homeKey"])
        self.assertEqual(files["total"], 1)
        self.assertEqual(metadata["targetContextManifest"]["sha256"], sha256_file(self.root / "target-context.json"))
        previous = self.selection_database.read_bytes()
        plan_path = manifest.parent / "approved-plan.json"
        original_plan, original_manifest = plan_path.read_bytes(), manifest.read_bytes()
        malformed = json.loads(original_plan)
        malformed["targetContextManifest"] = {}
        plan_path.write_text(json.dumps(malformed))
        report = json.loads(original_manifest)
        report["planSha256"] = report["preflight"]["planSha256"] = sha256_file(plan_path)
        manifest.write_text(json.dumps(report))
        with self.assertRaisesRegex(ValueError, "complete artifact pin"):
            self.build_selections(manifest)
        self.assertEqual(self.selection_database.read_bytes(), previous)

        plan_path.write_bytes(original_plan)
        manifest.write_bytes(original_manifest)
        (self.root / "target-context.json").write_text('{"changed":true}')
        with self.assertRaisesRegex(MusicNormalizationChanged, "target context"):
            self.build_selections(manifest)
        self.assertEqual(self.selection_database.read_bytes(), previous)

    def test_partial_model_plan_keeps_alternatives_supported_links_and_audio(self):
        self.build()
        manifest = self.write_selections()
        report = json.loads(manifest.read_text())
        chunk = manifest.parent / report["chunks"][0]["path"]
        with gzip.open(chunk, "rt") as stream:
            repeated = json.loads(next(stream))
        resumed = json.loads(json.dumps(repeated))
        resumed["proposal"]["selection"]["inference"]["fromCache"] = True
        report["chunks"] = [self.chunk(manifest.parent, "composition_selections", [repeated, resumed])]
        manifest.write_text(json.dumps(report))
        metadata = self.build_selections(manifest)
        base = self.serve(handler_for(self.discovery(composition_selections=self.selection_database)))
        stats = self.get(base, "/api/stats")
        self.assertEqual((metadata["coveredSongs"], metadata["candidateSongs"]), (1, 2))
        coordinates = {"scope": "catalogue", "artist": ARTISTS[0], "key": self.songs[0]["entityKey"], "generation": stats["generation"]}
        song = self.get(base, "/api/song-composition-hypotheses", **coordinates)
        self.assertEqual(len(song["candidates"]), 2)
        self.assertEqual(song["modelSelection"]["selectedWorkKey"], self.work_keys[0])
        self.assertFalse(song["modelSelection"]["acceptedIdentity"])
        work = self.get(base, "/api/composition-work", key=self.work_keys[1], generation=stats["generation"])
        self.assertEqual(work["songs"][0]["modelSelection"]["selectedWorkKey"], self.work_keys[0])
        missing = self.get(base, "/api/song-composition-hypotheses", **{**coordinates, "artist": ARTISTS[1], "key": self.songs[1]["entityKey"]})
        self.assertIsNone(missing["modelSelection"])
        proof = self.get(base, "/api/composition-selection-evidence", **coordinates)
        self.assertEqual(proof["proposal"]["selection"]["selectedWorkMbid"], MBIDS[0])
        self.assertTrue(proof["proposal"]["selection"]["inference"]["fromCache"])
        performances = self.get(base, "/api/song-performances", **coordinates)
        files = self.get(base, "/api/performance-files", **coordinates, homeKey=performances["items"][0]["homeKey"])
        self.assertEqual(files["total"], 1)
        self.assertEqual(self.get(base, "/api/works")["total"], 1)
        old = self.serve(handler_for(self.discovery()))
        self.assertNotEqual(self.get(old, "/api/stats")["generation"], stats["generation"])
        with self.assertRaises(HTTPError) as error:
            self.get(base, "/api/composition-selection-evidence", **{**coordinates, "generation": self.get(old, "/api/stats")["generation"]})
        self.assertEqual(error.exception.code, 409)
        error.exception.close()
        chunk.write_bytes(b"changed original model evidence")
        with self.assertRaises(HTTPError) as error:
            self.get(base, "/api/composition-selection-evidence", **coordinates)
        self.assertEqual(error.exception.code, 409)
        error.exception.close()

    def test_model_abstention_is_distinct_from_failed_and_skipped_requests(self):
        self.build()
        for states in [("insufficient_evidence", "unresolved"), ("skipped",), ("selected", "insufficient_evidence", "skipped")]:
            with self.subTest(states=states):
                self.build_selections(self.write_selections(states))
                discovery = self.discovery(composition_selections=self.selection_database)
                for index, state in enumerate(states):
                    result = discovery.compositions.song("catalogue", self.songs[index]["artistUuid"], self.songs[index]["entityKey"])["modelSelection"]
                    self.assertEqual(result["outcome"], state)
                    self.assertEqual(result["executionState"], "proposed" if state in {"selected", "insufficient_evidence"} else state)
                    self.assertEqual(result["selectedWorkKey"], self.work_keys[0] if state == "selected" else None)

    def test_model_foreign_work_or_request_mismatch_preserves_prior_projection(self):
        self.build()
        manifest = self.write_selections()
        self.build_selections(manifest)
        before = self.selection_database.read_bytes()
        for mutation in ("candidate", "cache", "origin", "skip", "unfinished"):
            with self.subTest(mutation=mutation):
                manifest = self.write_selections()
                report = json.loads(manifest.read_text())
                path = manifest.parent / report["chunks"][0]["path"]
                with gzip.open(path, "rt") as stream:
                    record = json.loads(next(stream))
                selection = record["proposal"]["selection"]
                if mutation == "candidate":
                    selection["selectedWorkMbid"] = MBIDS[2]
                    selection["inference"]["selectedWorkMbid"] = MBIDS[2]
                elif mutation == "cache":
                    selection["inference"]["cacheKey"] = "different-request"
                elif mutation == "skip":
                    record["proposal"]["input"]["skipReason"] = "input_too_large"
                    report["preflight"]["rows"][0] = record["proposal"]["input"]
                elif mutation == "origin":
                    record["proposal"]["input"]["hypothesisOrigin"]["line"] += 1
                    report["preflight"]["rows"][0] = record["proposal"]["input"]
                else:
                    report["state"] = "interrupted"
                report["chunks"] = [self.chunk(manifest.parent, "composition_selections", [record])]
                manifest.write_text(json.dumps(report))
                with self.assertRaises(ValueError):
                    self.build_selections(manifest)
                self.assertEqual(self.selection_database.read_bytes(), before)

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
