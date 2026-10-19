"""Context export preserves actual rendition scope without inventing attribution."""

import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from composition_target_context import CompositionTargetContextExporter
from music_catalogue import CatalogueBuildPaused
from shadow_reader import ShadowIndex


def uid(number):
    return "00000000-0000-0000-0000-" + str(number).zfill(12)


class TestCompositionTargetContext(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.catalog = self.root / "catalog"
        self.native = self.root / "native"
        self.catalog.mkdir()
        self.native.mkdir()
        self.original_keys = ["repertoire:old" + str(i) for i in range(3)]
        self.targets = [dict(scope="native" if i == 2 else "catalogue", artistUuid=uid(2) if i == 2 else uid(1),
                             songEntityKey="repertoire:target" + str(i)) for i in range(3)]
        files = []
        for i in (0, 1):
            files.append(self.record("file_contents", "track_sample", uid(100+i), uid(1),
                dict(home=dict(fileUuid=uid(100+i), sourceUuid=uid(10+i), showUuid=uid(20+i)),
                     titleInterpretation=dict(originalTitle="Such A Night")),
                [self.original_keys[0], "source:"+uid(10+i), "show:"+uid(20+i), "file:"+uid(100+i)]))
        # A shared spelling/key under another artist cannot supply this target's context.
        files.append(self.record("file_contents", "track_sample", uid(199), uid(2),
            dict(home=dict(fileUuid=uid(199)), titleInterpretation=dict(originalTitle="Foreign artist")), [self.original_keys[0]]))
        self.chunk(self.catalog, "file_contents", files)
        self.description = "The Last Waltz\nVarious Artists\nHouse Band: Example Band\n☃"
        people = [self.record("people", "source_sample", uid(10+i), uid(1), dict(people=[]), [],
            evidence=dict(originalInputs=dict(description=self.description, taper=None, transferrer=None,
                source=None, lineage=None, taper_notes=None))) for i in (0, 1)]
        self.people_chunk = self.chunk(self.catalog, "people", people)
        member = dict(songEntityKey=self.original_keys[1], legacySongUuid=uid(301), title="Memory Lane")
        self.chunk(self.catalog, "setlists", [self.record("setlists", "setlist_sample", uid(30), uid(1),
            dict(entityKey="setlist:"+uid(30), legacySetlistUuid=uid(30), showUuid=None, literalDate="2015-02-11",
                 ordered=False, memberships=[member], occurrences=[], providerAccount=None), [self.original_keys[1]])])
        evidence = dict(inputRevisionUuid=uid(40), jsonPointer="/recordings/0", quote="Program A")
        self.origin = dict(item=dict(provider="example.org", resourceType="recording", providerKey="recording-a"),
                           evidence=evidence, scope="resource_item")
        observed = lambda value: dict(value=value, evidence=evidence)
        child = dict(fileOrdinal=0, entityKey="native_file:one", outcome="proposed", recordingOrigin=self.origin,
            file=dict(title=observed("Native Song"), text=[dict(field="notes", text=observed(self.description))]),
            catalogSourceUuid=None, catalogShowUuid=None, catalogTrackUuid=None, entityKeys=[self.original_keys[2]])
        self.chunk(self.native, "file_contents", [self.record("file_contents", "provider_input", uid(40), uid(2),
            dict(files=[child]), [self.original_keys[2]])])
        accounts = [dict(origin=self.origin, recording=dict(text=[dict(field="title", text=observed("Program A"))],
            performers=[dict(name=observed("House Band A"), identities=[])])),
            dict(origin={**self.origin, "scope":"page_member"}, recording=dict(text=[dict(field="notes", text=observed("Must not leak"))]))]
        self.chunk(self.native, "performances", [self.record("performances", "provider_input", uid(40), uid(2), accounts, [])])
        observation = dict(footnoteNumbers=[observed(1)], annotations=[dict(field="notes", text=observed("A tribute performance"))],
                           performerAnnotations=[dict(kind="cover_artist", name=observed("Another Performer"), identities=[])])
        self.chunk(self.native, "setlists", [self.record("setlists", "provider_input", uid(40), uid(2),
            [dict(entityKey="native_setlist:one", ordered=True, showUuid=None,
                occurrences=[dict(songEntityKey=self.original_keys[2], title="Native Song", observation=observation)],
                providerAccount=dict(text=[], performers=[], footnotes=[dict(number=observed(1),text=dict(field="footnote",text=observed("Cover performer, not composer")))]))], [self.original_keys[2]])])
        self.raw = self.root / "raw"
        self.raw.mkdir()
        raw_file = self.raw / "sources.jsonl.gz"
        self.write(raw_file, [dict(kind="snapshot"),
            dict(kind="artist", row=dict(uuid=uid(1), name="Example Band", musicbrainz_id=uid(500))),
            dict(kind="artist", row=dict(uuid=uid(2), name="Native Artist", musicbrainz_id=None)),
            dict(kind="source_sample", row=dict(uuid=uid(10)))])
        self.raw_manifest = self.raw / "manifest.json"
        self.raw_manifest.write_text(json.dumps(dict(plan=[dict(id="one", domain="sources")],
            completedShards=[dict(shardId="one", gzipPath=raw_file.name, compressed=dict(sha256=self.sha(raw_file)))])))
        sources = []
        for scope, run in (("catalogue", self.catalog), ("native", self.native)):
            (run / "manifest.json").write_text(json.dumps(dict(state="completed", population=dict(snapshotPaths=[str(raw_file)]))))
            index = ShadowIndex(run)
            index.scan_once()
            sources.append(dict(scope=scope, run=str(run), index=str(index.database)))
        self.catalogue = self.root / "catalogue.sqlite3"
        with sqlite3.connect(self.catalogue) as db:
            self.addCleanup(db.close)
            db.execute("CREATE TABLE artists(scope TEXT,uuid TEXT,name TEXT)")
            db.executemany("INSERT INTO artists VALUES(?,?,?)", [("catalogue",uid(1),"Example Band"),("native",uid(2),"Native Artist")])
        self.music_input = self.root / "music-input.json"
        self.music_input.write_text(json.dumps(dict(catalogue=self.artifact(self.catalogue), sources=sources)))
        self.music_output = self.root / "music-output.json"
        self.music_output.write_text(json.dumps(dict(inputManifestSha256=self.sha(self.music_input))))
        self.hypothesis = self.root / "hypothesis.json"
        self.hypothesis.write_text(json.dumps(dict(complete=True, inputs=dict(musicInputManifest=self.artifact(self.music_input),
            musicOutputManifest=self.artifact(self.music_output)))))
        self.plan = self.root / "plan.json"
        self.plan.write_text(json.dumps(dict(version="composition-selection-plan-v1", songs=self.targets,
                                            hypothesisManifest=self.artifact(self.hypothesis))))
        self.normalization = self.root / "normalization.sqlite3"
        with sqlite3.connect(self.normalization) as db:
            self.addCleanup(db.close)
            db.executescript("CREATE TABLE metadata(key TEXT,value TEXT); CREATE TABLE groups(scope TEXT,artist_uuid TEXT,entity_key TEXT);"
                "CREATE TABLE originals(scope TEXT,artist_uuid TEXT,original_key TEXT);"
                "CREATE TABLE members(scope TEXT,original_key TEXT,target_key TEXT,disposition TEXT);")
            metadata = dict(state="completed", inputManifestSha256=self.sha(self.music_input),
                outputManifestSha256=self.sha(self.music_output), catalogueSha256=self.sha(self.catalogue))
            db.executemany("INSERT INTO metadata VALUES(?,?)", [(k,json.dumps(v)) for k,v in metadata.items()])
            for i, target in enumerate(self.targets):
                db.execute("INSERT INTO groups VALUES(?,?,?)", tuple(target.values()))
                db.execute("INSERT INTO originals VALUES(?,?,?)", (target["scope"],target["artistUuid"],self.original_keys[i]))
                db.execute("INSERT INTO members VALUES(?,?,?,'proposed')", (target["scope"],self.original_keys[i],target["songEntityKey"]))

    @staticmethod
    def sha(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    @classmethod
    def artifact(cls, path):
        return dict(path=str(path),bytes=path.stat().st_size,sha256=cls.sha(path),rows=1)

    @staticmethod
    def write(path, rows):
        with gzip.open(path,"wt",encoding="utf-8") as f:
            for row in rows:f.write(json.dumps(row)+"\n")

    def chunk(self, run, domain, rows):
        folder = run / domain
        folder.mkdir(exist_ok=True)
        path = folder / "100.jsonl.gz"
        self.write(path, rows)
        return path

    @staticmethod
    def record(domain, kind, identifier, artist, proposal, entities, evidence=None):
        return dict(domain=domain,inputKind=kind,inputKey=kind+":"+identifier,inputUuid=identifier,
                    artistUuid=artist,artistName="Artist",provider="fixture",outcome="proposed",method="fixture",
                    proposal=proposal,entityKeys=entities,evidence=evidence or {})

    def export(self, name="context", **options):
        destination=self.root/name
        exporter=CompositionTargetContextExporter(self.plan,self.normalization,self.raw_manifest,destination,
            plan_sha256=self.sha(self.plan),raw_manifest_sha256=self.sha(self.raw_manifest),minimum_free_bytes=0,**options)
        return destination,exporter.export()

    @staticmethod
    def rows(path):
        with gzip.open(path,"rt") as f:return [json.loads(line) for line in f]

    def test_export_retains_all_renditions_setlist_only_and_native_scopes_with_text_deduplication(self):
        destination,manifest=self.export()
        targets=self.rows(destination/manifest["targets"]["path"])
        texts=self.rows(destination/manifest["texts"]["path"])
        first,second,native=targets
        self.assertEqual(first["coverage"]["fileWitnesses"],2)
        self.assertEqual(first["coverage"]["recordingContexts"],2)
        self.assertEqual({w["sourceUuid"] for w in first["witnesses"] if w["kind"]=="catalog_recording"},{uid(10),uid(11)})
        self.assertEqual(sum(t["value"]==self.description for t in texts),1)
        self.assertEqual(first["artist"]["musicBrainzId"],uid(500))
        self.assertEqual(first["artist"]["observations"][1]["origin"]["jsonPointer"],"/row/musicbrainz_id")
        self.assertEqual((second["coverage"]["fileWitnesses"],second["coverage"]["setlistWitnesses"]),(0,1))
        membership=second["witnesses"][0]
        self.assertFalse(membership["ordered"])
        self.assertEqual(membership["claims"][0]["kind"],"legacy_membership")
        self.assertIn("provider_annotations_not_retained",membership["missingFields"])
        self.assertEqual((native["coverage"]["fileWitnesses"],native["coverage"]["setlistWitnesses"],native["coverage"]["recordingContexts"]),(1,1,1))
        self.assertTrue(native["coverage"]["missingArtistMbid"])
        self.assertNotIn("Must not leak",{t["value"] for t in texts})
        claim=next(c for w in native["witnesses"] if w["kind"]=="native_passage" for c in w["claims"] if c["kind"]=="performance_annotation")
        self.assertEqual(claim["observation"]["kind"],"cover_artist")
        self.assertFalse(any(c.get("role")=="composer" for w in native["witnesses"] for c in w["claims"]))
        self.assertIn("Cover performer, not composer",{t["value"] for t in texts})
        self.assertTrue(any(c["kind"]=="footnote_reference" for w in native["witnesses"] for c in w["claims"]))
        self.assertTrue(all(t["coverage"]["complete"] and t["coverage"]["omittedWitnesses"]==0 for t in targets))
        self.assertEqual(len({c["path"] for c in manifest["witnessChunks"]}),len(manifest["witnessChunks"]))

    def test_bound_failure_publishes_no_completion_and_retry_preserves_original_sources(self):
        before=self.people_chunk.read_bytes()
        with self.assertRaises(CatalogueBuildPaused):self.export("too-small",maximum_bytes=1)
        self.assertFalse((self.root/"too-small/manifest.json").exists())
        self.assertEqual(self.people_chunk.read_bytes(),before)
        destination,manifest=self.export("retry")
        self.assertEqual(manifest["targetCount"],3)
        self.assertTrue((destination/"manifest.json").exists())

    def test_legacy_artist_mbid_cells_keep_raw_evidence_but_only_publish_valid_identifiers(self):
        raw = json.loads(self.raw_manifest.read_text())
        path = self.raw / raw["completedShards"][0]["gzipPath"]
        rows = self.rows(path)
        canonical = "84eac621-1c5a-49a1-9500-555099c6e184"
        for number, (literal, expected) in enumerate((("", None), ("not-a-guid", None),
                                                    (None, None), (uid(0), None), (canonical.replace("-", ""), None),
                                                    (canonical.upper(), canonical))):
            with self.subTest(literal=literal):
                rows[1]["row"]["musicbrainz_id"] = literal
                self.write(path, rows)
                raw["completedShards"][0]["compressed"]["sha256"] = self.sha(path)
                self.raw_manifest.write_text(json.dumps(raw))
                destination, manifest = self.export("mbid-" + str(number))
                target = self.rows(destination / manifest["targets"]["path"])[0]
                self.assertEqual(target["artist"]["musicBrainzId"], expected)
                self.assertEqual(target["coverage"]["missingArtistMbid"], expected is None)
                observation = next(item for item in target["artist"]["observations"]
                                   if item["field"] == "musicbrainz_id")
                self.assertEqual(observation["value"], literal)
                self.assertEqual(observation["origin"]["jsonPointer"], "/row/musicbrainz_id")
                self.assertEqual(observation["origin"]["sha256"], self.sha(path))

    def test_oversized_nonselected_artist_shard_row_cannot_publish_complete_context(self):
        raw = json.loads(self.raw_manifest.read_text())
        path = self.raw / raw["completedShards"][0]["gzipPath"]
        rows = self.rows(path)
        rows.append(dict(kind="source_sample", row=dict(description="x" * 4096)))
        self.write(path, rows)
        raw["completedShards"][0]["compressed"]["sha256"] = self.sha(path)
        self.raw_manifest.write_text(json.dumps(raw))
        with patch("composition_target_context.MAXIMUM_LINE", 2048):
            with self.assertRaisesRegex(ValueError, "Artist headers"):
                self.export("oversized")
        self.assertFalse((self.root / "oversized/manifest.json").exists())

    def test_wrong_target_artist_or_changed_source_fails_without_completed_output(self):
        plan = json.loads(self.plan.read_text())
        plan["songs"][0]["artistUuid"] = uid(2)
        self.plan.write_text(json.dumps(plan))
        with self.assertRaises(ValueError):self.export("foreign")
        self.assertFalse((self.root/"foreign/manifest.json").exists())
        plan["songs"][0]["artistUuid"] = uid(1)
        self.plan.write_text(json.dumps(plan))
        self.people_chunk.write_bytes(b"changed source")
        with self.assertRaises((ValueError,gzip.BadGzipFile)):self.export("changed")
        self.assertFalse((self.root/"changed/manifest.json").exists())


if __name__=="__main__":unittest.main()
