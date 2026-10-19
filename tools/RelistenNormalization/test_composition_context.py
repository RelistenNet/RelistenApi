"""Artist inference context stays scoped, literal and tied to its frozen image."""

from contextlib import closing
import gzip
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from composition_context import CompositionArtistContextExporter
from music_catalogue import CatalogueBuildPaused
from music_consolidation import sha256_file


ARTIST = "00000000-0000-0000-0000-000000000001"


class TestCompositionArtistContext(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.catalogue = self.root / "catalogue.sqlite3"
        self.input = self.root / "music-input.json"
        self.output = self.root / "music-output.json"
        self.destination = self.root / "context"
        self.sources = [{"id": 1, "scope": "catalogue", "run": "/retained/catalogue",
                         "index": "/retained/catalogue/index.sqlite3", "snapshot_at": 42},
                        {"id": 2, "scope": "native", "run": "/retained/native",
                         "index": "/retained/native/index.sqlite3", "snapshot_at": 43}]
        self.names = [("catalogue", ARTIST, "Grateful Dead"),
                      ("local", "00000000-0000-0000-0000-000000000000", "Unknown artist"),
                      ("native", ARTIST, "Zoë’s Artist (source spelling)"),
                      ("native", "00000000-0000-0000-0000-000000000002", None)]
        with closing(sqlite3.connect(self.catalogue)) as db:
            db.executescript("CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT); "
                             "CREATE TABLE artists(scope TEXT,uuid TEXT,name TEXT,PRIMARY KEY(scope,uuid));")
            db.executemany("INSERT INTO artists VALUES(?,?,?)", self.names)
            db.executemany("INSERT INTO metadata VALUES(?,?)", [(key, json.dumps(value)) for key, value in
                {"status": "completed_projection", "built_at": 44, "sources": self.sources}.items()])
            db.commit()
        self.input.write_text(json.dumps({"version": "music-consolidation-input-v1", "state": "completed",
            "catalogue": {"path": str(self.catalogue), "bytes": self.catalogue.stat().st_size,
                          "sha256": sha256_file(self.catalogue), "builtAt": 44},
            "descriptors": {"rows": 27}, "sources": self.sources}))
        self.output.write_text(json.dumps({"version": "music-consolidation-output-v1", "complete": True,
            "inputManifestSha256": sha256_file(self.input), "logicalRows": 27}))

    def exporter(self, **options):
        return CompositionArtistContextExporter(self.input, self.output, self.destination,
            input_manifest_sha256=sha256_file(self.input), output_manifest_sha256=sha256_file(self.output),
            minimum_free_bytes=0, **options)

    def test_export_preserves_all_scoped_names_unknowns_and_exact_cell_provenance(self):
        original = sha256_file(self.catalogue)

        manifest = self.exporter().export()

        with gzip.open(self.destination / manifest["artists"]["path"], "rt", encoding="utf-8") as stream:
            rows = [json.loads(line) for line in stream]
        self.assertEqual([(row["scope"], row["artistUuid"], row["name"]) for row in rows], self.names)
        self.assertEqual(manifest["artists"]["rows"], len(self.names))
        self.assertEqual(manifest["artists"]["sha256"], sha256_file(self.destination / "artists.jsonl.gz"))
        self.assertEqual(manifest["musicInputManifest"]["sha256"], sha256_file(self.input))
        self.assertEqual(manifest["musicOutputManifest"]["sha256"], sha256_file(self.output))
        self.assertEqual(manifest["sources"], self.sources)
        self.assertFalse(manifest["namesAreAuthorshipEvidence"])
        with closing(sqlite3.connect(self.catalogue)) as db:
            for row in rows:
                origin = row["origin"]
                key = origin["key"]
                literal = db.execute("SELECT name FROM artists WHERE scope=? AND uuid=?", (key["scope"], key["uuid"])).fetchone()[0]
                self.assertEqual(row["name"], literal)
                self.assertEqual(origin["catalogueSha256"], original)
                self.assertIsNone(row["musicBrainzId"])
        self.assertEqual(sha256_file(self.catalogue), original)

    def test_wrong_pins_incomplete_output_and_other_catalogue_are_not_published(self):
        exporter = self.exporter()
        exporter.output_sha = "0" * 64
        with self.assertRaisesRegex(ValueError, "SHA256"):
            exporter.export()
        output = json.loads(self.output.read_text())
        output["complete"] = False
        self.output.write_text(json.dumps(output))
        with self.assertRaisesRegex(ValueError, "completed"):
            self.exporter().export()
        output["complete"] = True
        self.output.write_text(json.dumps(output))
        with closing(sqlite3.connect(self.catalogue)) as db:
            db.execute("UPDATE artists SET name='Changed name'")
            db.commit()
        with self.assertRaisesRegex(ValueError, "catalogue differs"):
            self.exporter().export()
        self.assertFalse((self.destination / "manifest.json").exists())

    def test_replacement_after_context_read_cannot_relabel_the_export(self):
        write = CompositionArtistContextExporter._write_artists

        def replace_after_read(exporter, db, catalogue_sha):
            result = write(exporter, db, catalogue_sha)
            self.output.write_text(self.output.read_text() + "\n")
            return result

        with patch.object(CompositionArtistContextExporter, "_write_artists", new=replace_after_read):
            with self.assertRaisesRegex(ValueError, "manifest changed"):
                self.exporter().export()

        self.assertTrue((self.destination / "artists.jsonl.gz").is_file())
        self.assertFalse((self.destination / "manifest.json").exists())

    def test_byte_cap_leaves_incomplete_context_and_existing_output_is_preserved(self):
        with self.assertRaises(CatalogueBuildPaused):
            self.exporter(maximum_bytes=1).export()
        self.assertFalse((self.destination / "manifest.json").exists())
        self.destination = self.root / "complete"
        self.exporter().export()
        retained = sha256_file(self.destination / "manifest.json")
        with self.assertRaises(FileExistsError):
            self.exporter().export()
        self.assertEqual(sha256_file(self.destination / "manifest.json"), retained)


if __name__ == "__main__":
    unittest.main()
