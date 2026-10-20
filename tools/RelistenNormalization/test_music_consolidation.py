"""Literal export contracts over real retained chunks and compact reader indexes."""

import gzip
from contextlib import closing
from http.server import ThreadingHTTPServer
import json
import sqlite3
import shutil
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import urlencode
from urllib.request import urlopen
from urllib.error import HTTPError

from music_catalogue import MusicCatalogue, MusicCatalogueBuilder, Source
from music_consolidation import MusicConsolidationExporter, MusicConsolidationProjection, sha256_file
from music_discovery import MusicDiscovery, handler_for
from music_groups import MusicGroups, MusicNormalizationChanged
from shadow_reader import ShadowIndex, handler_for as reader_handler


ARTIST = "00000000-0000-0000-0000-000000000001"


class TestMusicConsolidationExport(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.run = self.root / "run"
        self.run.mkdir()
        self.catalogue = self.root / "catalogue.sqlite3"
        self.bundle = self.root / "bundle"

    @staticmethod
    def song(key, title, legacy=None):
        return {"entityKey": key, "name": title, "legacySongUuid": legacy,
                "artistUuid": ARTIST, "basis": "captured_artist_dictionary" if legacy else "title_derived"}

    def chunk(self, domain, *proposals, name="100", entities=None, kind="track_sample"):
        folder = self.run / domain
        folder.mkdir(exist_ok=True)
        path = folder / (name + ".jsonl.gz")
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for index, proposal in enumerate(proposals):
                record = {"domain": domain, "inputKey": f"{kind}:{name}:{index}", "inputKind": kind,
                          "inputUuid": f"{name}:{index}", "artistUuid": ARTIST, "artistName": "Artist A",
                          "provider": "fixture", "method": "literal-fixture-v1", "reason": None,
                          "outcome": "proposed", "proposal": proposal,
                          "entityKeys": entities if entities is not None else [proposal["entityKey"]],
                          "evidence": {"original": "Unrelated private evidence stays in this chunk"}}
                stream.write(json.dumps(record, ensure_ascii=False) + "\n")
        return path

    def build(self, scopes=("catalogue",), reader_url="http://reader.test"):
        ShadowIndex(self.run).scan_once()
        MusicCatalogueBuilder([Source(scope, self.run, reader_url) for scope in scopes],
                              self.catalogue, minimum_free_bytes=0).build()

    def export(self, **options):
        return MusicConsolidationExporter(self.catalogue, self.bundle, minimum_free_bytes=0, **options).export()

    def rows(self, filename):
        with gzip.open(self.bundle / filename, "rt", encoding="utf-8") as stream:
            return [json.loads(line) for line in stream]

    def test_exact_titles_scopes_dictionary_variants_and_namesake_rivals_survive(self):
        first = self.song("repertoire:known-a", "Zoë’s Song", "legacy-a")
        rival = self.song("repertoire:known-b", "Zoë’s Song", "legacy-b")
        self.chunk("repertoire", first, rival, kind="song_sample")
        membership = {"entityKey": "setlist:one", "memberships": [
            {"songEntityKey": first["entityKey"], "title": "Zoe's song", "legacySongUuid": "legacy-a"},
            {"songEntityKey": first["entityKey"], "title": "Zoe's song", "legacySongUuid": "legacy-a"}],
            "occurrences": []}
        self.chunk("setlists", [membership], entities=["setlist:one", first["entityKey"]], kind="setlist_sample")
        self.build(("catalogue", "local"))

        manifest = self.export()

        descriptors = self.rows("descriptors.jsonl.gz")
        dictionaries = self.rows("dictionary-names.jsonl.gz")
        self.assertEqual(manifest["state"], "completed")
        self.assertEqual(manifest["descriptors"]["rows"], 4)
        self.assertEqual(manifest["dictionaryNames"]["rows"], 6)
        self.assertEqual({(row["scope"], row["legacySongUuid"], row["title"]) for row in dictionaries},
                         {(scope, legacy, title) for scope in ("catalogue", "local")
                          for legacy, title in [("legacy-a", "Zoë’s Song"), ("legacy-b", "Zoë’s Song"),
                                                ("legacy-a", "Zoe's song")]})
        for row in descriptors + dictionaries:
            witness = row["witness"]
            chunk = Path(witness["run"]) / witness["chunk"]
            self.assertEqual(witness["chunkSha256"], sha256_file(chunk))
            with gzip.open(chunk, "rt", encoding="utf-8") as stream:
                original = json.loads(next(line for index, line in enumerate(stream, 1) if index == witness["line"]))
            value = original
            for part in witness["valuePointer"].split("/")[1:]:
                value = value[int(part)] if isinstance(value, list) else value[part.replace("~1", "/").replace("~0", "~")]
            self.assertEqual(value, row["title"])
        self.assertEqual(manifest["catalogue"]["sha256"], sha256_file(self.catalogue))
        self.assertNotIn("Unrelated private evidence", (self.bundle / "manifest.json").read_text())

    def test_reference_only_title_remains_unknown_without_fabricated_pointer(self):
        self.chunk("file_contents", None, entities=["repertoire:reference"])
        self.build()

        self.export()

        row, = self.rows("descriptors.jsonl.gz")
        self.assertEqual(row["originalKey"], "repertoire:reference")
        self.assertIsNone(row["title"])
        self.assertIsNone(row["witness"]["valuePointer"])
        self.assertEqual(self.rows("dictionary-names.jsonl.gz"), [])

    def test_provider_interpretations_do_not_expand_the_dictionary_or_replace_direct_witnesses(self):
        song = self.song("repertoire:known", "Original dictionary name", "legacy")
        self.chunk("repertoire", song, kind="song_sample", name="900")
        account = {"entityKey": "setlist:catalogue", "memberships": [
            {"songEntityKey": song["entityKey"], "title": song["name"], "legacySongUuid": "legacy"}]}
        self.chunk("setlists", [account], kind="setlist_sample", entities=[account["entityKey"], song["entityKey"]], name="100")
        inferred = {**song, "name": "Matched provider spelling is not a dictionary assertion"}
        provider = {"entityKey": "setlist_account:provider", "memberships": [],
                    "occurrences": [{"songInterpretation": {"song": inferred}}]}
        self.chunk("setlists", [provider], kind="provider_input", entities=[provider["entityKey"], song["entityKey"]], name="200")
        self.build()

        self.export()

        names = self.rows("dictionary-names.jsonl.gz")
        self.assertEqual(len(names), 1)
        self.assertEqual(names[0]["title"], song["name"])
        self.assertTrue(names[0]["inputKey"].startswith("song_sample:"))

    def test_changed_original_title_refuses_completion_and_preserves_catalogue(self):
        path = self.chunk("repertoire", self.song("repertoire:known", "Original", "legacy"), kind="song_sample")
        self.build()
        original_sha = sha256_file(self.catalogue)
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            stream.write(json.dumps({"artistUuid": ARTIST, "entityKeys": ["repertoire:known"],
                                     "proposal": self.song("repertoire:known", "Changed", "legacy")}) + "\n")

        with self.assertRaisesRegex(ValueError, "title differs"):
            self.export()

        self.assertFalse((self.bundle / "manifest.json").exists())
        self.assertEqual(sha256_file(self.catalogue), original_sha)

    def test_existing_bundle_is_never_replaced(self):
        self.chunk("repertoire", self.song("repertoire:one", "One", "legacy"), kind="song_sample")
        self.build()
        self.export()
        original_sha = sha256_file(self.bundle / "manifest.json")

        with self.assertRaises(FileExistsError):
            self.export()

        self.assertEqual(sha256_file(self.bundle / "manifest.json"), original_sha)

    def outputs(self, correspondences, logical_rows=None):
        output = self.root / "output"
        output.mkdir(exist_ok=True)
        chunk = output / "repertoire_correspondences.jsonl.gz"
        with gzip.open(chunk, "wt", encoding="utf-8") as stream:
            for row in correspondences:
                stream.write(json.dumps({"domain": "repertoire_correspondences", "inputKind": "repertoire_descriptor",
                                         "artistUuid": row["artistUuid"], "proposal": row}) + "\n")
        progress = output / "progress.json"
        progress.write_text(json.dumps({"state": "completed"}))
        manifest = {"version": "music-consolidation-output-v1", "complete": True,
                    "inputManifestSha256": sha256_file(self.bundle / "manifest.json"),
                    "extractorVersion": "fixture-extractor", "resolverVersion": "fixture-resolver",
                    "logicalRows": logical_rows if logical_rows is not None else len(correspondences),
                    "chunks": [{"path": chunk.name, "bytes": chunk.stat().st_size,
                                "sha256": sha256_file(chunk), "rows": len(correspondences)}],
                    "progress": {"path": progress.name, "bytes": progress.stat().st_size,
                                 "sha256": sha256_file(progress), "rows": 0}}
        (output / "manifest.json").write_text(json.dumps(manifest))
        return output / "manifest.json"

    def correspondence(self, descriptor, song, relation="identity_alias"):
        return {"scope": descriptor["scope"], "artistUuid": descriptor["artistUuid"],
                "originalKey": descriptor["originalKey"], "originalTitle": descriptor["title"],
                "disposition": "proposed", "reason": "fixture-supported", "sequenceBasis": "title_text_only",
                "sourceWitness": descriptor["witness"], "targets": [{"song": song, "relation": relation,
                    "disposition": "proposed", "reason": "fixture-supported-target",
                    "qualifiers": [{"kind": "jam", "evidence": {"quote": "Original full proof stays in this chunk"}}],
                    "evidence": {"quote": descriptor["title"]}, "comparisonDiagnostics": []}]}

    def project(self, manifest):
        return MusicConsolidationProjection(self.catalogue, self.bundle / "manifest.json", manifest,
                                            self.root / "normalization.sqlite3", minimum_free_bytes=0).build()

    def test_projection_preserves_distinct_namesakes_variants_and_exact_replay(self):
        first = self.song("repertoire:a", "Same title", "legacy-a")
        rival = self.song("repertoire:b", "Same title", "legacy-b")
        variant = self.song("repertoire:jam", "Same title jam")
        self.chunk("repertoire", first, rival, kind="song_sample")
        self.chunk("file_contents", {"titleInterpretation": {"segments": [{"song": variant}]}}, entities=[variant["entityKey"]])
        self.build()
        self.export()
        descriptors = self.rows("descriptors.jsonl.gz")
        rows = [self.correspondence(row, first if row["originalKey"] != rival["entityKey"] else rival,
                                   "jam" if row["originalKey"] == variant["entityKey"] else "identity_alias") for row in descriptors]
        manifest = self.outputs([*rows, rows[0]], logical_rows=3)

        metadata = self.project(manifest)

        self.assertEqual((metadata["originals"], metadata["groups"], metadata["memberships"]), (3, 2, 3))
        with closing(sqlite3.connect(self.root / "normalization.sqlite3")) as db:
            self.assertEqual(db.execute("SELECT target_key,relation FROM members WHERE original_key=?", (variant["entityKey"],)).fetchone(),
                             (first["entityKey"], "jam"))
            self.assertEqual(db.execute("SELECT count(*) FROM groups WHERE title='Same title'").fetchone()[0], 2)
            self.assertNotIn("Original full proof", str(db.execute("SELECT * FROM members").fetchall()))

    def test_incomplete_and_conflicting_replays_preserve_completed_projection(self):
        song = self.song("repertoire:one", "One", "legacy")
        self.chunk("repertoire", song, kind="song_sample")
        self.build()
        self.export()
        row = self.correspondence(self.rows("descriptors.jsonl.gz")[0], song)
        self.project(self.outputs([row]))
        retained = sha256_file(self.root / "normalization.sqlite3")

        with self.assertRaisesRegex(ValueError, "every original"):
            self.project(self.outputs([], logical_rows=1))
        with self.assertRaisesRegex(ValueError, "Conflicting"):
            self.project(self.outputs([row, {**row, "reason": "contradictory"}], logical_rows=1))

        self.assertEqual(sha256_file(self.root / "normalization.sqlite3"), retained)

    def test_projection_rejects_cross_artist_target_and_changed_witness(self):
        song = self.song("repertoire:one", "One", "legacy")
        self.chunk("repertoire", song, kind="song_sample")
        self.build()
        self.export()
        row = self.correspondence(self.rows("descriptors.jsonl.gz")[0], song)

        with self.assertRaisesRegex(ValueError, "artist or relation"):
            self.project(self.outputs([self.correspondence(self.rows("descriptors.jsonl.gz")[0], {**song, "artistUuid": "other-artist"})]))
        row["sourceWitness"] = {**row["sourceWitness"], "line": 99}
        with self.assertRaisesRegex(ValueError, "original descriptor"):
            self.project(self.outputs([row]))

    def serve(self, handler):
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}"

    def test_public_song_journey_unions_aliases_groups_real_shows_and_returns_exact_files(self):
        song = self.song("repertoire:known", "Song", "legacy")
        variant = self.song("repertoire:variant", "Song (12:03)")
        partial = self.song("repertoire:partial", "Song > Unknown")
        rival = self.song("repertoire:rival", "Possibly Song")
        show = "00000000-0000-0000-0000-000000000010"
        late = "00000000-0000-0000-0000-000000000011"
        self.chunk("repertoire", song, kind="song_sample")
        for ordinal, (interpretation, home) in enumerate([(variant, show), (partial, late), (rival, show)]):
            file_uuid = f"00000000-0000-0000-0001-{ordinal:012d}"
            self.chunk("file_contents", {"home": {"fileUuid": file_uuid, "showUuid": home, "literalDate": "1970-01-02"},
                "playback": {"mp3Url": "https://audio.test/" + file_uuid + ".mp3"},
                "titleInterpretation": {"originalTitle": interpretation["name"], "segments": [{"song": interpretation}]}},
                entities=[interpretation["entityKey"], "show:" + home, "file:" + file_uuid], name=str(200 + ordinal))
        index = ShadowIndex(self.run)
        index.scan_once()
        reader_url = self.serve(reader_handler(index))
        # Overlapping captures are legal. The discovery union must not duplicate
        # their known show UUIDs or exact catalog file UUIDs.
        proof_run = self.root / "proof-only"
        proof_run.mkdir()
        proof_index = ShadowIndex(proof_run)
        proof_index.scan_once()
        MusicCatalogueBuilder([Source("catalogue", proof_run, "http://127.0.0.1:1"),
                               Source("catalogue", self.run, reader_url), Source("catalogue", self.run, reader_url)],
                              self.catalogue, minimum_free_bytes=0).build()
        self.export()
        rows = [self.correspondence(descriptor, song) for descriptor in self.rows("descriptors.jsonl.gz")]
        for row in rows:
            if row["originalKey"] == partial["entityKey"]:
                row["disposition"] = "review"
            elif row["originalKey"] == rival["entityKey"]:
                row["disposition"] = "review"
                row["targets"][0]["disposition"] = "review"
        self.project(self.outputs(rows))
        discovery = MusicDiscovery(MusicCatalogue(self.catalogue), normalization=self.root / "normalization.sqlite3")
        url = self.serve(handler_for(discovery))

        def get(route, **query):
            with urlopen(url + route + "?" + urlencode(query)) as response:
                return json.load(response)

        stats = get("/api/stats")
        self.assertTrue(stats["normalized_song_queries"])
        self.assertTrue(any(source["reader_url"] == "http://127.0.0.1:1" for source in stats["sources"]))
        found = get("/api/normalized-songs", q="12:03")
        self.assertEqual([(row["title"], row["entity_key"]) for row in found["items"]], [("Song", song["entityKey"])])
        coordinates = {"scope": "catalogue", "artist": ARTIST, "key": song["entityKey"], "generation": stats["generation"]}
        group = get("/api/song-group", **coordinates)
        self.assertEqual(group["members_total"], 3)
        partial_member = next(member for member in group["members"] if member["original_key"] == partial["entityKey"])
        self.assertEqual((partial_member["disposition"], partial_member["target_disposition"]), ("review", "proposed"))
        performances = get("/api/song-performances", **coordinates)
        self.assertEqual((performances["total"], performances["performancesTotal"]), (2, 2))
        self.assertEqual({row["showUuid"] for row in performances["items"]}, {show, late})
        files = get("/api/performance-files", **coordinates, homeKey="show:" + show)
        self.assertEqual(files["total"], 1)
        selected = files["items"][0]
        original = get("/api/record", source=selected["source"], id=selected["recordId"], generation=stats["generation"])
        self.assertEqual(original["record"]["proposal"]["titleInterpretation"]["originalTitle"], variant["name"])
        self.assertEqual(original["playback"], ["https://audio.test/00000000-0000-0000-0001-000000000000.mp3"])
        proof = get("/api/correspondence", scope="catalogue", artist=ARTIST, key=partial["entityKey"])
        self.assertEqual(proof["proposal"]["originalTitle"], partial["name"])
        raw = get("/api/song", scope="catalogue", key=variant["entityKey"])
        self.assertEqual(raw["title"], variant["name"])

    def test_group_browsing_ignores_unrelated_memberships_in_the_same_catalog_scope(self):
        song = self.song("repertoire:selected", "Selected song", "legacy-selected")
        self.chunk("repertoire", song, kind="song_sample")
        self.build()
        self.export()
        descriptor, = self.rows("descriptors.jsonl.gz")
        self.project(self.outputs([self.correspondence(descriptor, song)]))
        sidecar = self.root / "normalization.sqlite3"
        with closing(sqlite3.connect(sidecar)) as db:
            for index in range(30000):
                key = "repertoire:unrelated-" + str(index)
                db.execute("""INSERT INTO originals SELECT scope,?,artist_uuid,'Unrelated song',expected_hash,
                    output_hash,disposition,reason,sequence_basis,chunk,line FROM originals
                    WHERE scope='catalogue' AND original_key=?""", (key, song["entityKey"]))
                db.execute("""INSERT INTO members SELECT scope,?,'repertoire:another-song',relation,
                    target_ordinal,qualifiers,disposition,reason FROM members
                    WHERE scope='catalogue' AND original_key=?""", (key, song["entityKey"]))
            db.commit()
        groups = MusicGroups(self.catalogue, sidecar)
        discovery = MusicDiscovery(MusicCatalogue(self.catalogue), normalization=sidecar)
        discovery.groups = groups
        connect = groups._connect
        def bounded_connection(*args, **kwargs):
            db = connect(*args, **kwargs)
            ticks = 0
            def budget():
                nonlocal ticks
                ticks += 1
                return ticks > 30
            db.set_progress_handler(budget, 100)
            return db
        # Bound actual SQLite work instead of wall time or a particular query plan.
        # Looking up one song must not visit every other song's memberships.
        with patch.object(groups, "_connect", side_effect=bounded_connection):
            group = groups.group("catalogue", ARTIST, song["entityKey"])
            self.assertEqual(group["members_total"], 1)
            self.assertEqual([row["original_key"] for row in group["members"]], [song["entityKey"]])
            self.assertEqual(groups.member_keys("catalogue", ARTIST, song["entityKey"]), [song["entityKey"]])
            self.assertEqual(list(groups.member_relations("catalogue", ARTIST, song["entityKey"])), [song["entityKey"]])
            observations = discovery.review_observations(dict(literal="Selected song", artist=ARTIST, domain="songs"))
            self.assertEqual(observations["total"], 1)
            self.assertEqual(observations["items"][0]["stableKey"], json.dumps(["catalogue", song["entityKey"]], separators=(",", ":")))

    def test_saved_song_projection_updates_search_memberships_and_withdrawal_without_rebuilding(self):
        first = self.song("repertoire:one", "Scarlet", "legacy-one")
        second = self.song("repertoire:two", "Fire", "legacy-two")
        variant = self.song("repertoire:variant", "Scarlet fragment")
        self.chunk("repertoire", first, second, kind="song_sample")
        self.chunk("file_contents", {"titleInterpretation": {"segments": [{"song": variant}]}}, entities=[variant["entityKey"]])
        self.build()
        self.export()
        rows = [self.correspondence(row, second if row["originalKey"] == second["entityKey"] else first)
                for row in self.rows("descriptors.jsonl.gz")]
        self.project(self.outputs(rows))
        # Other artists must not enter the cost of applying one artist's saved
        # correspondence. Their full source rows remain in the pinned image.
        with closing(sqlite3.connect(self.root / "normalization.sqlite3")) as db:
            db.execute("""WITH RECURSIVE counter(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM counter WHERE n<30000)
                INSERT INTO originals SELECT scope,'repertoire:other-'||n,'00000000-0000-0000-0000-000000000099',
                    original_title,expected_hash,output_hash,disposition,reason,sequence_basis,chunk,line
                FROM counter CROSS JOIN originals WHERE original_key='repertoire:one'""")
            db.execute("""INSERT INTO groups SELECT scope,original_key,artist_uuid,original_title,'title_derived',NULL
                FROM originals WHERE artist_uuid='00000000-0000-0000-0000-000000000099'""")
            db.commit()
        discovery = MusicDiscovery(MusicCatalogue(self.catalogue), normalization=self.root / "normalization.sqlite3")
        group = discovery.groups
        before = sha256_file(group.normalization)

        class Corrections:
            changes = []
            generation = "before"
            def snapshot(self):
                return {"sha256": self.generation, "rules": [{"definition": {"domain": "songs"}}]}
            def music_changes(self, metadata, snapshot):
                return self.changes

        corrections = Corrections()
        group.review_client = corrections
        initial_generation = discovery.stats()["generation"]
        change = dict(scope="catalogue", artistUuid=ARTIST, originalKey=variant["entityKey"],
                      disposition="proposed", reason="reviewed", sequenceBasis="single", targets=[dict(
                          song=second, relation="identity_alias", disposition="proposed", reason="reviewed", qualifiers=[])])
        corrections.changes = [change]
        corrections.generation = "accepted"
        self.assertNotEqual(initial_generation, discovery.stats()["generation"])
        self.assertEqual(group.member_keys("catalogue", ARTIST, first["entityKey"]), [first["entityKey"]])
        self.assertEqual(group.member_keys("catalogue", ARTIST, second["entityKey"]), [second["entityKey"], variant["entityKey"]])
        result = group.songs(q="fragment")
        self.assertEqual([item["entity_key"] for item in result["items"]], [second["entityKey"]])
        self.assertEqual(group.original_groups("catalogue", ARTIST, variant["entityKey"])["items"][0]["entity_key"], second["entityKey"])
        apply_reviews = group._apply_reviews
        def bounded_artist_projection(db, changes):
            ticks = 0
            def budget():
                nonlocal ticks
                ticks += 1
                return ticks > 30
            db.set_progress_handler(budget, 100)
            apply_reviews(db, changes)
            db.set_progress_handler(None, 0)
        with patch.object(group, "_apply_reviews", side_effect=bounded_artist_projection):
            self.assertEqual(group.group("catalogue", ARTIST, second["entityKey"])["members_total"], 2)
        self.assertEqual(group.correspondence("catalogue", ARTIST, variant["entityKey"])["proposal"]["targets"][0]["song"]["entityKey"], first["entityKey"])
        corrections.changes = []
        corrections.generation = "withdrawn"
        self.assertEqual(group.member_keys("catalogue", ARTIST, first["entityKey"]), [first["entityKey"], variant["entityKey"]])
        self.assertEqual(sha256_file(group.normalization), before)

    def test_new_normalization_generation_rejects_old_group_links_and_compounds_do_not_inherit_works(self):
        song = self.song("repertoire:known", "Scarlet", "legacy")
        compound = self.song("repertoire:compound", "Scarlet > Fire")
        self.chunk("repertoire", song, compound, kind="song_sample")
        self.build()
        # A compound's work links describe the whole source assertion. They do
        # not assign both compositions to each inferred member.
        with closing(sqlite3.connect(self.catalogue)) as db:
            for key in ("work:a", "work:b"):
                db.execute("INSERT INTO works(entity_key,title,witness_run,witness_record,witness_chunk,witness_line) "
                           "SELECT ?,?,witness_run,witness_record,witness_chunk,witness_line FROM songs LIMIT 1", (key, key))
                db.execute("INSERT INTO work_songs VALUES(?,?,?)", (key, "catalogue", compound["entityKey"]))
            db.execute("INSERT INTO work_songs VALUES(?,?,?)", ("work:a", "catalogue", song["entityKey"]))
            db.commit()
        self.export()
        rows = [self.correspondence(row, song, "compound_member" if row["originalKey"] == compound["entityKey"] else "identity_alias")
                for row in self.rows("descriptors.jsonl.gz")]
        self.project(self.outputs(rows))
        first = MusicDiscovery(MusicCatalogue(self.catalogue), normalization=self.root / "normalization.sqlite3")
        generation = first.stats()["generation"]
        self.assertEqual([row["entity_key"] for row in first.groups.group("catalogue", ARTIST, song["entityKey"])["works"]], ["work:a"])
        rows[0]["reason"] = "A later completed interpretation"
        self.project(self.outputs(rows))
        replacement = MusicDiscovery(MusicCatalogue(self.catalogue), normalization=self.root / "normalization.sqlite3")
        self.assertNotEqual(replacement.stats()["generation"], generation)
        url = self.serve(handler_for(replacement))
        query = urlencode({"scope": "catalogue", "artist": ARTIST, "key": song["entityKey"], "generation": generation})
        with self.assertRaises(HTTPError) as stale:
            urlopen(url + "/api/song-group?" + query)
        self.assertEqual(stale.exception.code, 409)
        stale.exception.close()
        with urlopen(url + "/api/song?" + urlencode({"scope": "catalogue", "key": compound["entityKey"]})) as response:
            self.assertEqual(json.load(response)["title"], compound["name"])

    def native_context(self):
        legacy = "00000000-0000-0000-0000-000000000021"
        setlist = "00000000-0000-0000-0000-000000000022"
        song = {"id": 1, "uuid": legacy, "artist_id": 42, "name": "Original literal"}
        frames = [{"kind": "artist", "row": {"id": 42, "uuid": ARTIST}},
                  {"kind": "song_sample", "row": song},
                  {"kind": "setlist_sample", "row": {"uuid": setlist, "artist_id": 42},
                   "unordered_memberships": [{"song": {**song, "name": "Original membership spelling"}}]}]
        context = self.root / "context.jsonl.gz"
        with gzip.open(context, "wt", encoding="utf-8") as stream:
            for frame in frames:
                stream.write(json.dumps(frame) + "\n")
        manifest = {"state": "completed", "inputIdentity": "native-rows-inline/" + sha256_file(context) + "/none",
                    "population": {"processingScope": "provider_inputs_only", "snapshotPath": str(context),
                                   "snapshotPaths": [str(context)], "inputKinds": {"artist": 1, "song_sample": 1, "setlist_sample": 1}}}
        (self.run / "manifest.json").write_text(json.dumps(manifest))
        derived = self.song("repertoire:provider", "An interpreted provider label")
        child = {"fileOrdinal": 0, "entityKey": "native_file:one", "outcome": "proposed",
                 "file": {"title": {"value": derived["name"]}}, "mentions": [{"interpretation": {"song": derived}}],
                 "entityKeys": ["native_file:one", derived["entityKey"]]}
        self.chunk("file_contents", {"files": [child]}, kind="provider_input", entities=child["entityKeys"])
        self.build(("native",))
        return context

    def test_provider_only_run_exports_actual_observed_dictionary_context_with_exact_scope(self):
        context = self.native_context()

        manifest = self.export()

        names = self.rows("dictionary-names.jsonl.gz")
        self.assertEqual({row["title"] for row in names}, {"Original literal", "Original membership spelling"})
        self.assertEqual({row["scope"] for row in names}, {"native"})
        self.assertEqual({row["witness"]["line"] for row in names}, {2, 3})
        self.assertTrue(all(row["songKey"] is None and row["witness"]["recordId"] == 0
                            and row["witness"]["documentKind"] == "catalog_context" for row in names))
        self.assertEqual({row["witness"]["chunk"] for row in names}, {str(context.resolve())})
        self.assertEqual(manifest["contextDocuments"][0]["sha256"], sha256_file(context))

    def test_changed_native_context_refuses_dictionary_authority(self):
        context = self.native_context()
        with gzip.open(context, "at", encoding="utf-8") as stream:
            stream.write(json.dumps({"kind": "artist", "row": {"id": 99, "uuid": ARTIST}}) + "\n")

        with self.assertRaisesRegex(ValueError, "producer input identity"):
            self.export()

        self.assertFalse((self.bundle / "manifest.json").exists())

    def test_manifest_replacement_during_projection_cannot_relabel_completed_evidence(self):
        song = self.song("repertoire:one", "One", "legacy")
        self.chunk("repertoire", song, kind="song_sample")
        self.build()
        self.export()
        row = self.correspondence(self.rows("descriptors.jsonl.gz")[0], song)
        manifest = self.outputs([row])
        self.project(manifest)
        retained = sha256_file(self.root / "normalization.sqlite3")
        project_outputs = MusicConsolidationProjection._outputs

        def replace_after_read(projection, db, outputs):
            project_outputs(projection, db, outputs)
            manifest.write_text(manifest.read_text() + "\n")

        with patch.object(MusicConsolidationProjection, "_outputs", new=replace_after_read):
            with self.assertRaisesRegex(ValueError, "manifest changed"):
                self.project(manifest)

        self.assertEqual(sha256_file(self.root / "normalization.sqlite3"), retained)

    def test_sidecar_replacement_during_open_requires_reload_without_affecting_raw_songs(self):
        song = self.song("repertoire:one", "One", "legacy")
        self.chunk("repertoire", song, kind="song_sample")
        self.build()
        self.export()
        self.project(self.outputs([self.correspondence(self.rows("descriptors.jsonl.gz")[0], song)]))
        sidecar = self.root / "normalization.sqlite3"
        groups = MusicGroups(self.catalogue, sidecar)
        replacement = self.root / "replacement.sqlite3"
        shutil.copyfile(sidecar, replacement)
        with closing(sqlite3.connect(replacement)) as db:
            db.execute("UPDATE metadata SET value=? WHERE key='outputManifestSha256'", (json.dumps("replacement"),))
            db.commit()
        connect = sqlite3.connect

        def replace_before_snapshot(*args, **kwargs):
            replacement.replace(sidecar)
            return connect(*args, **kwargs)

        with patch("music_groups.sqlite3.connect", side_effect=replace_before_snapshot):
            with self.assertRaises(MusicNormalizationChanged):
                groups.group("catalogue", ARTIST, song["entityKey"])

        self.assertEqual(MusicCatalogue(self.catalogue).song("catalogue", song["entityKey"])["title"], "One")


if __name__ == "__main__":
    unittest.main()
