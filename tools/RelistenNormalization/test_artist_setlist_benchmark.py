"""Public CLI comparisons over synthetic retained observations, never truth labels."""

from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

from shadow_reader import _pack_text


PHISH = "ca53d281-0041-ae33-a050-c87702d93b0c"
DEAD = "77a58ff9-2e01-c59c-b8eb-cff106049b72"
SCRIPT = Path(__file__).with_name("artist_setlist_benchmark.py")


class TestArtistSetlistBenchmark(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.metadata = self.root / "metadata.sqlite3"
        self.normalization = self.root / "normalization.sqlite3"
        self.raw = self.root / "setlists.jsonl.gz"
        self.references = self.root / "references"
        self.files = self.root / "files"
        self.output = self.root / "output"
        self.raw_rows = []
        self.reference_rows = []
        self.file_rows = []
        with closing(sqlite3.connect(self.metadata)) as db, db:
            db.executescript("""
                CREATE TABLE show(uuid TEXT PRIMARY KEY,artist_uuid TEXT);
                CREATE TABLE source(id INTEGER PRIMARY KEY,uuid TEXT,show_uuid TEXT);
                CREATE TABLE source_track(uuid TEXT PRIMARY KEY,artist_uuid TEXT,source_id INTEGER,is_orphaned INTEGER);
            """)
        with closing(sqlite3.connect(self.normalization)) as db, db:
            db.executescript("""
                CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT);
                CREATE TABLE groups(scope TEXT,artist_uuid TEXT,entity_key TEXT,legacy_uuid TEXT,title TEXT);
                CREATE TABLE originals(scope TEXT,original_key TEXT,artist_uuid TEXT);
                CREATE TABLE members(scope TEXT,original_key TEXT,target_key TEXT,disposition TEXT,relation TEXT);
            """)
            db.execute("INSERT INTO metadata VALUES (?,?)", ("state", json.dumps("completed")))
            db.execute("INSERT INTO metadata VALUES (?,?)", ("generation", json.dumps("synthetic-current")))

    def population(self, shows, files=()):
        """Each file tuple preserves its captured artist, source and show ownership."""
        with closing(sqlite3.connect(self.metadata)) as db, db:
            db.executemany("INSERT INTO show VALUES (?,?)", shows)
            sources = {}
            for uuid, artist, source, show in files:
                if source not in sources:
                    sources[source] = len(sources) + 1
                    db.execute("INSERT INTO source VALUES (?,?,?)", (sources[source], source, show))
                db.execute("INSERT INTO source_track VALUES (?,?,?,0)", (uuid, artist, sources[source]))

    def mapping(self, original, target, disposition="proposed"):
        with closing(sqlite3.connect(self.normalization)) as db, db:
            db.execute("INSERT INTO groups VALUES ('catalogue',?,?,?,?)", (PHISH, target, "legacy-" + target, target))
            db.execute("INSERT INTO originals VALUES ('catalogue',?,?)", (original, PHISH))
            db.execute("INSERT INTO members VALUES ('catalogue',?,?,?,'IdentityAlias')", (original, target, disposition))

    def reference(self, uuid, show, songs, basis="existing_provider_performance_key", candidates=None, reason=None):
        # The separate raw membership account and its C# projection deliberately
        # have different shapes; the benchmark must authenticate their agreement.
        self.raw_rows.append({"kind": "setlist_sample", "row": {"uuid": uuid, "artist_id": 4},
                              "unordered_memberships": [{"song": {"uuid": legacy}} for legacy, _ in songs]})
        self.reference_rows.append({"domain": "setlists", "inputUuid": uuid, "artistUuid": PHISH,
            "proposal": {"legacySetlistUuid": uuid, "showUuid": show,
                "association": {"showUuid": show, "basis": basis, "reason": reason,
                                "candidates": candidates or []},
                "memberships": [{"legacySongUuid": legacy, "songEntityKey": key} for legacy, key in songs]}})

    def prediction(self, uuid, show, source, segments, artist=PHISH):
        self.file_rows.append({"domain": "file_contents", "inputUuid": uuid, "artistUuid": artist,
            "proposal": {"home": {"showUuid": show, "sourceUuid": source},
                         "titleInterpretation": {"originalTitle": "Été 🌙 > Another song", "segments": segments}}})

    @staticmethod
    def proposed(key):
        return {"outcome": "proposed", "song": {"entityKey": key}, "candidateSongs": []}

    def freeze(self):
        with gzip.open(self.raw, "wt", encoding="utf-8") as stream:
            for row in self.raw_rows:
                stream.write(json.dumps(row, ensure_ascii=False) + "\n")
        self.reader(self.references, "setlists", self.reference_rows)
        self.reader(self.files, "file_contents", self.file_rows)

    @staticmethod
    def reader(run, domain, rows):
        """Only the existing compact reader coordinates consumed by the CLI."""
        run.mkdir()
        chunk = run / domain / "000001.jsonl.gz"
        chunk.parent.mkdir()
        with gzip.open(chunk, "wt", encoding="utf-8") as stream:
            for row in rows:
                stream.write(json.dumps(row, ensure_ascii=False) + "\n")
        with closing(sqlite3.connect(run / ".shadow-reader-v3.sqlite3")) as db, db:
            db.executescript("""
                CREATE TABLE artists(id INTEGER PRIMARY KEY,uuid TEXT);
                CREATE TABLE headers(id INTEGER PRIMARY KEY,input_id INTEGER,input_uuid TEXT,artist_id INTEGER);
                CREATE TABLE input_keys(id INTEGER PRIMARY KEY,data BLOB);
                CREATE TABLE domains(id INTEGER PRIMARY KEY,name TEXT);
                CREATE TABLE chunks(id INTEGER PRIMARY KEY,path TEXT,bytes INTEGER,state TEXT);
                CREATE TABLE records(header_id INTEGER,domain_id INTEGER,chunk_id INTEGER,line_no INTEGER);
            """)
            db.executemany("INSERT INTO artists VALUES (?,?)", [(1, PHISH), (2, DEAD)])
            db.execute("INSERT INTO domains VALUES (1,?)", (domain,))
            db.execute("INSERT INTO chunks VALUES (1,?,?,'indexed')", (str(chunk.relative_to(run)), chunk.stat().st_size))
            for line, row in enumerate(rows, 1):
                packed = _pack_text(row.get("inputKey", domain + ":" + row["inputUuid"]))
                db.execute("INSERT INTO input_keys VALUES (?,?)", (line, packed))
                input_uuid = None if packed[:1] == b"u" else row["inputUuid"]
                db.execute("INSERT INTO headers VALUES (?,?,?,?)", (line, line, input_uuid, 1 if row["artistUuid"] == PHISH else 2))
                db.execute("INSERT INTO records VALUES (?,1,1,?)", (line, line))

    def cli(self, **limits):
        request = {"metadata": str(self.metadata), "normalization": str(self.normalization),
                   "rawSetlists": str(self.raw), "referenceRun": str(self.references),
                   "fileRun": str(self.files), **limits}
        path = self.root / "request.json"
        path.write_text(json.dumps(request), encoding="utf-8")
        return subprocess.run([sys.executable, str(SCRIPT), "--request", str(path), "--output", str(self.output)],
                              capture_output=True, text=True, timeout=15)

    def completed(self):
        result = self.cli()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["state"], "completed")
        manifest = json.loads((self.output / "manifest.json").read_text())
        for pin in manifest["outputs"]:
            payload = Path(pin["path"]).read_bytes()
            self.assertEqual((len(payload), hashlib.sha256(payload).hexdigest()), (pin["bytes"], pin["sha256"]))
        return manifest

    def rows(self, name):
        return [json.loads(line) for line in (self.output / name).read_text().splitlines()]

    def test_multisong_presence_reports_missing_extra_and_excludes_candidate_only_or_review_targets(self):
        self.population([("show", PHISH)], [("file-one", PHISH, "source-one", "show"), ("file-two", PHISH, "source-two", "show")])
        for original, target, disposition in [("original-a", "song-a", "proposed"), ("original-b", "song-b", "proposed"),
                                               ("original-extra", "song-extra", "proposed"), ("original-review", "song-review", "review")]:
            self.mapping(original, target, disposition)
        self.reference("setlist", "show", [("legacy-a", "song-a"), ("legacy-b", "song-b"), ("legacy-missing", "song-missing")])
        self.prediction("file-one", "show", "source-one", [self.proposed("original-a"), self.proposed("original-b"),
            {"outcome": "unresolved", "song": None, "candidateSongs": [{"entityKey": "song-missing"}]}, self.proposed("original-review")])
        self.prediction("file-two", "show", "source-two", [self.proposed("original-extra")])
        self.freeze()

        manifest = self.completed()

        show = self.rows("shows.jsonl")[0]
        self.assertEqual((show["status"], show["predicted"], show["missing"], show["extra"]),
                         ("compared", ["song-a", "song-b", "song-extra"], ["song-missing"], ["song-extra"]))
        self.assertEqual((show["fileCount"], show["sourceCount"], show["unresolvedSegments"]), (2, 2, 2))
        self.assertEqual([file["fileUuid"] for file in self.rows("errors.jsonl")[0]["filesForReview"]], ["file-two"])
        artist = next(row for row in manifest["artists"] if row["artistUuid"] == PHISH)
        self.assertEqual((artist["expected"], artist["predicted"], artist["intersection"]), (3, 3, 2))
        self.assertAlmostEqual(artist["songPresencePrecision"], 2 / 3)
        self.assertAlmostEqual(artist["songPresenceRecall"], 2 / 3)
        self.assertEqual(manifest["predictionGeneration"]["generation"], "synthetic-current")

    def test_all_captured_shows_remain_visible_without_reference_or_prediction(self):
        self.population([("missing-files", PHISH), ("missing-reference", PHISH), ("neither", PHISH), ("dead-neither", DEAD)],
                        [("unprocessed", PHISH, "source-missing", "missing-files"), ("processed", PHISH, "source", "missing-reference")])
        self.mapping("original-a", "song-a")
        self.reference("setlist", "missing-files", [("legacy-a", "song-a")])
        self.prediction("processed", "missing-reference", "source", [self.proposed("original-a")])
        self.freeze()

        manifest = self.completed()

        shows = {row["showUuid"]: row for row in self.rows("shows.jsonl")}
        self.assertEqual(set(shows), {"missing-files", "missing-reference", "neither", "dead-neither"})
        self.assertEqual(shows["missing-files"]["status"], "missing_predictions")
        for key in ["missing-reference", "neither", "dead-neither"]:
            self.assertEqual(shows[key]["status"], "missing_reference")
        artist = next(row for row in manifest["artists"] if row["artistUuid"] == PHISH)
        self.assertEqual((artist["shows"], artist["activeFiles"], artist["files_without_prediction"]), (3, 2, 1))
        self.assertIsNone(artist["songPresencePrecision"])
        self.assertIsNone(artist["songPresenceRecall"])

    def test_date_alignment_keeps_its_proposed_basis_and_tied_accounts_stay_unaligned(self):
        self.population([("date-aligned", PHISH), ("tied", PHISH)], [("file", PHISH, "source", "date-aligned")])
        self.mapping("original-a", "song-a")
        basis = "unique_exact_artist_date"
        self.reference("date-reference", "date-aligned", [("legacy-a", "song-a")], basis, [{"showUuid": "date-aligned"}])
        self.reference("tied-reference", None, [("legacy-a", "song-a")], basis,
                       [{"showUuid": "date-aligned"}, {"showUuid": "tied"}], "ambiguous_artist_date")
        self.prediction("file", "date-aligned", "source", [self.proposed("original-a")])
        self.freeze()

        manifest = self.completed()

        aligned = next(row for row in self.rows("shows.jsonl") if row["showUuid"] == "date-aligned")
        self.assertEqual(aligned["status"], "compared")
        self.assertEqual(aligned["referenceAccounts"][0]["bindingBasis"], basis)
        self.assertEqual(manifest["unalignedReferenceAccounts"], 1)
        unaligned = next(row for row in self.rows("errors.jsonl") if row["status"] == "unaligned_reference")
        self.assertEqual((unaligned["reference"]["setlistUuid"], unaligned["reference"]["showUuid"]), ("tied-reference", None))
        self.assertEqual(unaligned["reference"]["bindingBasis"], basis)

    def test_repeated_memberships_do_not_make_equal_reference_presence_accounts_conflict(self):
        self.population([("show", PHISH)], [("file", PHISH, "source", "show")])
        self.mapping("original-a", "song-a")
        self.reference("repeated-account", "show", [("legacy-a", "song-a"), ("legacy-a", "song-a")])
        self.reference("single-account", "show", [("legacy-a", "song-a")])
        self.reference_rows.append({"domain": "setlists", "inputKind": "provider_input", "inputUuid": "provider-array",
                                    "artistUuid": PHISH, "proposal": [{"nativeAccount": "separate-provider-account"}]})
        self.prediction("file", "show", "source", [self.proposed("original-a")])
        self.freeze()

        manifest = self.completed()

        show = self.rows("shows.jsonl")[0]
        self.assertEqual((show["status"], show["expected"], show["predicted"]), ("compared", ["song-a"], ["song-a"]))
        self.assertEqual(len(show["referenceAccounts"]), 2)
        self.assertEqual(manifest["referenceAccounts"], 2)
        self.assertEqual(self.rows("errors.jsonl"), [])
        artist = next(row for row in manifest["artists"] if row["artistUuid"] == PHISH)
        self.assertEqual((artist["expected"], artist["predicted"], artist["exact_presence_agreement"]), (1, 1, 1))

    def test_compact_input_key_uuid_suffix_recovers_deliberately_null_header_uuids(self):
        file_uuid = "00000000-0000-4000-8000-000000000091"
        setlist_uuid = "00000000-0000-4000-8000-000000000092"
        self.population([("show", PHISH)], [(file_uuid, PHISH, "source", "show")])
        self.mapping("original-a", "song-a")
        self.reference(setlist_uuid, "show", [("legacy-a", "song-a")])
        self.prediction(file_uuid, "show", "source", [self.proposed("original-a")])
        self.freeze()
        for run in [self.references, self.files]:
            with closing(sqlite3.connect(run / ".shadow-reader-v3.sqlite3")) as db:
                header, packed = db.execute("SELECT h.input_uuid,i.data FROM headers h JOIN input_keys i ON i.id=h.input_id").fetchone()
                self.assertIsNone(header)
                self.assertEqual(packed[:1], b"u")

        manifest = self.completed()

        show = self.rows("shows.jsonl")[0]
        self.assertEqual((show["status"], show["predicted"], show["missing"], show["extra"]), ("compared", ["song-a"], [], []))
        self.assertEqual(show["referenceAccounts"][0]["setlistUuid"], setlist_uuid)
        self.assertEqual(show["fileCount"], 1)
        artist = next(row for row in manifest["artists"] if row["artistUuid"] == PHISH)
        self.assertEqual((artist["activeFiles"], artist["files_with_prediction"]), (1, 1))

    def test_counterfactual_reference_labels_change_scores_without_changing_file_predictions(self):
        self.population([("show", PHISH)], [("file", PHISH, "source", "show")])
        self.mapping("original-a", "song-a")
        self.mapping("original-b", "song-b")
        self.reference("setlist", "show", [("legacy-a", "song-a")])
        self.prediction("file", "show", "source", [self.proposed("original-a")])
        self.freeze()
        first_manifest = self.completed()
        first_show = self.rows("shows.jsonl")[0]
        self.assertEqual(self.rows("errors.jsonl"), [])
        frozen_files = {path.relative_to(self.files): path.read_bytes() for path in self.files.rglob("*") if path.is_file()}

        # Change the separate account and its retained C# projection together;
        # the existing file interpretation and normalization image stay fixed.
        self.raw_rows[0]["unordered_memberships"] = [{"song": {"uuid": "legacy-b"}}]
        self.reference_rows[0]["proposal"]["memberships"] = [{"legacySongUuid": "legacy-b", "songEntityKey": "song-b"}]
        self.raw = self.root / "counterfactual-setlists.jsonl.gz"
        with gzip.open(self.raw, "wt", encoding="utf-8") as stream:
            for row in self.raw_rows:
                stream.write(json.dumps(row, ensure_ascii=False) + "\n")
        self.references = self.root / "counterfactual-references"
        self.reader(self.references, "setlists", self.reference_rows)
        self.output = self.root / "counterfactual-output"

        second_manifest = self.completed()

        second_show = self.rows("shows.jsonl")[0]
        self.assertEqual(first_show["predicted"], ["song-a"])
        self.assertEqual(second_show["predicted"], first_show["predicted"])
        self.assertEqual((second_show["expected"], second_show["missing"], second_show["extra"]), (["song-b"], ["song-b"], ["song-a"]))
        self.assertEqual(self.rows("errors.jsonl")[0]["filesForReview"][0]["predicted"], ["song-a"])
        first_artist = next(row for row in first_manifest["artists"] if row["artistUuid"] == PHISH)
        second_artist = next(row for row in second_manifest["artists"] if row["artistUuid"] == PHISH)
        self.assertEqual((first_artist["songPresencePrecision"], second_artist["songPresencePrecision"]), (1, 0))
        self.assertEqual(first_manifest["inputs"]["normalization"], second_manifest["inputs"]["normalization"])
        self.assertEqual({path.relative_to(self.files): path.read_bytes() for path in self.files.rglob("*") if path.is_file()}, frozen_files)

    def test_prediction_source_ownership_mismatch_refuses_completion(self):
        self.population([("show", PHISH)], [("file", PHISH, "captured-source", "show")])
        self.prediction("file", "show", "foreign-source", [])
        self.freeze()

        result = self.cli()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ownership", result.stderr)
        self.assertFalse((self.output / "manifest.json").exists())
        self.assertFalse(json.loads((self.output / "failure.json").read_text())["completionPublished"])

    def test_captured_file_with_foreign_show_owner_refuses_completion(self):
        self.population([("foreign-show", DEAD)], [("file", PHISH, "source", "foreign-show")])
        self.freeze()

        result = self.cli()

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("foreign show owner", result.stderr)
        self.assertFalse((self.output / "manifest.json").exists())

    def test_existing_output_directory_is_refused_without_rewriting_the_completed_result(self):
        self.population([("show", PHISH)])
        self.freeze()
        self.completed()
        before = {path.name: path.read_bytes() for path in self.output.iterdir()}

        result = self.cli()

        self.assertNotEqual(result.returncode, 0)
        self.assertEqual({path.name: path.read_bytes() for path in self.output.iterdir()}, before)
        self.assertNotIn("failure.json", before)

    def test_output_bound_failure_never_publishes_a_completion_manifest(self):
        self.population([("show", PHISH)])
        self.freeze()

        result = self.cli(maximumOutputBytes=1)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("output bound exceeded", result.stderr)
        self.assertFalse((self.output / "manifest.json").exists())
        self.assertEqual(json.loads((self.output / "failure.json").read_text())["state"], "failed")


if __name__ == "__main__":
    unittest.main()
