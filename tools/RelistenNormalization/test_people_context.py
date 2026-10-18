"""Context proposals preserve observed source ownership and exact witness evidence."""

import hashlib
import gzip
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import threading
from http.server import ThreadingHTTPServer
from urllib.request import urlopen
from urllib.error import HTTPError

import test_people_directory as directory_fixtures

from people_context import PeopleContextPreparation, PeopleContextProjection
from people_directory import CreditOverlay, PeopleDirectory, encoded, handler_for


class TestPeopleContext(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_context_preserves_unicode_span_and_competing_role_prose(self):
        preparation = PeopleContextPreparation(self.root / "db", self.root / "out")
        raw = "🎵 Not taped by Alex. Transferred by José (jose@example.test)."
        start = raw.index("José")
        anchor = {
            "field": "description",
            "quote": "José",
            "startCodePoint": start,
            "endCodePoint": start + 4,
        }
        row = {
            "id": 1,
            "name": "José",
            "role": "transferrer",
            "evidence": encoded(anchor),
            "interpretation_basis": None,
            "group_id": "jose",
            "credit_key": None,
            "source_uuid": "source-1",
            "artist_uuid": "artist-1",
            "artist_name": "Artist",
            "literal_date": "1970-00-00",
        }
        actor = preparation._actor(
            row, {"evidence": {"originalInputs": {"description": raw}}}
        )
        self.assertEqual(actor["evidence"][0], anchor)
        self.assertTrue(
            any("Not taped by Alex" in e["quote"] for e in actor["evidence"])
        )
        self.assertEqual(actor["roles"], ["transferrer"])
        self.assertEqual(actor["literalDate"], "1970-00-00")
        row["evidence"] = encoded({**anchor, "quote": "Jose"})
        self.assertIsNone(
            preparation._actor(
                row, {"evidence": {"originalInputs": {"description": raw}}}
            )
        )
        self.assertEqual(preparation.issues["unsupported_credit_span"], 1)

    def test_same_date_different_location_is_question_without_invented_negative(self):
        preparation = PeopleContextPreparation(self.root / "db", self.root / "out")
        preparation.candidates = {
            (1, 2): {
                "left": {"id": 1},
                "right": {"id": 2},
                "basis": "same_date_candidate",
            }
        }
        preparation.actors = {
            i: {
                "sourceUuid": f"source-{i}",
                "artistUuid": f"artist-{i}",
                "evidence": [{"field": "catalog_venue_location", "quote": city}],
                "actorFacts": None,
                "roles": ["transferrer"],
            }
            for i, city in [(1, "Paris"), (2, "New York")]
        }
        question = preparation._select_questions()[0]
        self.assertEqual(
            question["retrievalBasis"], "same_date_different_captured_location"
        )
        self.assertNotIn("relation", question)
        self.assertEqual(question["left"]["roles"], ["transferrer"])

    def contextual_run(self):
        database = self.root / "directory.sqlite3"
        with closing(sqlite3.connect(database)) as db, db:
            db.executescript(
                """CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT);
                CREATE TABLE groups(id TEXT PRIMARY KEY,display_name TEXT);
                CREATE TABLE credits(id INTEGER PRIMARY KEY,group_id TEXT,source_uuid TEXT,artist_uuid TEXT,name TEXT,role TEXT,credit_key TEXT,evidence TEXT);
                INSERT INTO groups VALUES('a','Alex'),('b','Alex Smith');
                INSERT INTO credits VALUES(1,'a','source-1','artist-a','Alex','transferrer',NULL,'{"field":"description","quote":"Transferred by Alex"}'),(2,'b','source-2','artist-b','Alex','transferrer',NULL,'{"field":"description","quote":"Transferred by Alex"}');"""
            )
        self.run = self.root / "run"
        self.run.mkdir()

        def actor(group, credit, source, artist):
            return {
                "groupId": group,
                "creditId": credit,
                "sourceUuid": source,
                "artistUuid": artist,
                "name": "Alex",
                "roles": ["transferrer"],
                "creditKey": None,
                "evidence": [{"field": "description", "quote": "Transferred by Alex"}],
            }

        question = {
            "questionId": "pair-1",
            "retrievalBasis": "explicit_alias_overlap",
            "left": actor("a", 1, "source-1", "artist-a"),
            "right": actor("b", 2, "source-2", "artist-b"),
        }
        row = {
            "input": {"questions": [question]},
            "result": {
                "state": "proposed",
                "cacheKey": "cache-1",
                "decisions": [
                    {
                        "questionId": "pair-1",
                        "relation": "same_person",
                        "reason": "Explicit alias in both credits",
                        "evidence": [
                            {
                                "side": side,
                                "field": "description",
                                "quote": "Transferred by Alex",
                            }
                            for side in ("left", "right")
                        ],
                    }
                ],
            },
        }
        results = self.run / "results.jsonl"
        results.write_text(encoded(row) + "\n")
        (self.run / "manifest.json").write_text(
            encoded(
                {
                    "state": "completed",
                    "jobUuid": "job-1",
                    "resultsSha256": hashlib.sha256(results.read_bytes()).hexdigest(),
                    "accountedUsd": 0.01,
                    "knownActualUsd": 0.01,
                }
            )
        )
        return database

    def test_relationship_projection_retains_witnesses_pagination_and_source_playback(
        self,
    ):
        database = self.contextual_run()
        projection = PeopleContextProjection(database, self.run)
        self.assertEqual(projection.apply()["relations"], {"same_person": 1})
        projection.apply()  # Explicit repeat replaces the same proposal, not source credits.
        directory = PeopleDirectory(database, "http://files.test/", "http://core.test/")
        self.assertEqual(directory.relationships("a", {})["total"], 1)
        self.assertEqual(
            directory.relationships("b", {"offset": "1"})["relationships"], []
        )
        detail = directory.relationship("pair-1")
        self.assertEqual(
            detail["decision"]["evidence"][0]["quote"], "Transferred by Alex"
        )
        self.assertEqual(detail["question"]["left"]["sourceUuid"], "source-1")
        self.assertIn(
            "entityKey=source%3Asource-1",
            detail["question"]["left"]["playableFilesUrl"],
        )
        self.assertEqual(
            detail["question"]["right"]["sourceEvidenceUrl"], "/api/credit?id=2"
        )
        with closing(sqlite3.connect(database)) as db, db:
            self.assertEqual(db.execute("SELECT count(*) FROM groups").fetchone()[0], 2)
            self.assertEqual(
                db.execute("SELECT count(*) FROM credits").fetchone()[0], 2
            )

    def test_http_comparison_exposes_exact_witnesses_and_unknown_id(self):
        database = self.contextual_run()
        PeopleContextProjection(database, self.run).apply()
        directory = PeopleDirectory(database, "http://files.test/", "http://core.test/")
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base = "http://127.0.0.1:" + str(server.server_port)
            with urlopen(base + "/api/relationships?limit=1") as response:
                listing = json.load(response)
            self.assertEqual(listing["total"], 1)
            with urlopen(
                base + "/api/relationship?id=" + listing["relationships"][0]["id"]
            ) as response:
                detail = json.load(response)
            self.assertEqual(detail["question"]["right"]["sourceUuid"], "source-2")
            self.assertEqual(detail["decision"]["relation"], "same_person")
            with self.assertRaises(HTTPError) as error:
                urlopen(base + "/api/relationship?id=absent")
            self.assertEqual(error.exception.code, 404)
            error.exception.close()
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_recovered_attachment_preserves_total_coverage_and_original_spend(self):
        database = self.contextual_run()
        manifest_path = self.run / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest.update(questions=10, plannedQuestions=20)
        manifest_path.write_text(encoded(manifest))
        PeopleContextProjection(database, self.run).apply()
        original_run = self.run
        recovered = self.root / "recovered"
        recovered.mkdir()
        row = json.loads((original_run / "results.jsonl").read_text())
        row["input"]["questions"][0]["questionId"] = "pair-2"
        row["result"]["decisions"][0]["questionId"] = "pair-2"
        row["recovery"] = {
            "basis": "recovered_from_failed_batch",
            "rejectedQuestions": [
                {"questionId": "bad-quote", "reason": "invalid_response"}
            ],
        }
        results = recovered / "results.jsonl"
        results.write_text(encoded(row) + "\n")
        (recovered / "manifest.json").write_text(
            encoded(
                {
                    "state": "completed",
                    "jobUuid": "job-1",
                    "originalRun": str(original_run),
                    "resultsSha256": hashlib.sha256(results.read_bytes()).hexdigest(),
                    "accountedUsd": 0,
                    "knownActualUsd": 0,
                    "originalJobAccountedUsdAfter": 0.01,
                    "recoveredDecisions": 1,
                    "rejectedQuestions": 1,
                }
            )
        )
        summary = PeopleContextProjection(database, recovered).apply()
        self.assertEqual(summary["decisions"], 2)
        self.assertEqual(summary["processedQuestions"], 10)
        self.assertEqual(summary["plannedQuestions"], 20)
        self.assertEqual(summary["knownTokenCostUsd"], 0.01)
        self.assertEqual(summary["accountedUsd"], 0.01)
        directory = PeopleDirectory(database, "http://files.test/", "http://core.test/")
        self.assertEqual(
            directory.relationship("pair-2")["recovery"]["basis"],
            "recovered_from_failed_batch",
        )
        self.assertEqual(directory.relationships("a", {})["total"], 2)

    def test_credit_replacement_invalidates_only_relationships_using_replaced_source(
        self,
    ):
        fixture = directory_fixtures.TestPeopleDirectory()
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        directory = fixture.build(
            [
                (source, artist, [fixture.credit(source, artist, name, "taper")])
                for source, artist, name in [
                    ("one", "artist-a", "Alex"),
                    ("two", "artist-b", "Alex Smith"),
                    ("three", "artist-c", "A Smith"),
                ]
            ]
        )
        with closing(directory.connect()) as db:
            credits = {
                row["source_uuid"]: dict(row)
                for row in db.execute("SELECT * FROM credits")
            }

        def actor(source):
            credit = credits[source]
            return {
                "groupId": credit["group_id"],
                "creditId": credit["id"],
                "sourceUuid": source,
                "artistUuid": credit["artist_uuid"],
                "name": credit["name"],
                "roles": [credit["role"]],
                "creditKey": credit["credit_key"],
                "evidence": [json.loads(credit["evidence"])],
            }

        questions = [
            {
                "questionId": identifier,
                "retrievalBasis": "name_variant_candidate",
                "left": actor(left),
                "right": actor(right),
            }
            for identifier, left, right in [
                ("affected", "one", "two"),
                ("retained", "two", "three"),
            ]
        ]
        run = self.root / "context-run"
        run.mkdir()
        results = run / "results.jsonl"
        results.write_text(
            encoded(
                {
                    "input": {"questions": questions},
                    "result": {
                        "state": "proposed",
                        "cacheKey": "cache",
                        "decisions": [
                            {
                                "questionId": q["questionId"],
                                "relation": "insufficient_evidence",
                                "reason": "Names alone do not identify people",
                                "evidence": [],
                            }
                            for q in questions
                        ],
                    },
                }
            )
            + "\n"
        )
        (run / "manifest.json").write_text(
            encoded(
                {
                    "state": "completed",
                    "jobUuid": "job",
                    "questions": 2,
                    "accountedUsd": 0,
                    "knownActualUsd": 0,
                    "resultsSha256": hashlib.sha256(results.read_bytes()).hexdigest(),
                }
            )
        )
        PeopleContextProjection(fixture.database, run).apply()
        overlay = self.root / "credit-run"
        (overlay / "credits").mkdir(parents=True)
        (overlay / "manifest.json").write_text(encoded({"state": "completed"}))
        selection = overlay / "selection.json"
        selection.write_text(encoded({"sourceUuids": ["one"]}))
        credit, facts = fixture.credit("one", "artist-a", "Corrected Name", "taper")
        with gzip.open(overlay / "credits/1.jsonl.gz", "wt") as stream:
            stream.write(
                encoded(
                    {
                        "inputKind": "source_sample",
                        "inputUuid": "one",
                        "artistUuid": "artist-a",
                        "artistName": "Artist A",
                        "proposal": {"credits": [credit]},
                        "evidence": {"creditExtraction": {"credits": [facts]}},
                    }
                )
                + "\n"
            )
        CreditOverlay(overlay, selection, fixture.database, minimum_free=0).apply()
        self.assertIsNone(directory.relationship("affected"))
        self.assertEqual(
            directory.relationship("retained")["question"]["left"]["sourceUuid"], "two"
        )
        self.assertEqual(directory.stats()["peopleContext"]["decisions"], 1)
        self.assertEqual(
            directory.stats()["peopleContext"]["invalidatedByCreditReplacement"], 1
        )
        self.assertEqual(directory.stats()["creditedRecordings"], 3)

    def test_reused_credit_id_with_changed_quote_rejects_old_context(self):
        database = self.contextual_run()
        with closing(sqlite3.connect(database)) as db, db:
            db.execute(
                "UPDATE credits SET evidence=? WHERE id=1",
                (encoded({"field": "description", "quote": "New quotation for Alex"}),),
            )
        with self.assertRaisesRegex(ValueError, "credited source"):
            PeopleContextProjection(database, self.run).apply()

    def test_changed_source_owner_rejects_whole_projection(self):
        database = self.contextual_run()
        with closing(sqlite3.connect(database)) as db, db:
            db.execute("UPDATE credits SET source_uuid='other-source' WHERE id=2")
        with self.assertRaisesRegex(ValueError, "credited source"):
            PeopleContextProjection(database, self.run).apply()
        with closing(sqlite3.connect(database)) as db, db:
            self.assertEqual(
                db.execute("SELECT count(*) FROM context_relationships").fetchone()[0],
                0,
            )


if __name__ == "__main__":
    unittest.main()
