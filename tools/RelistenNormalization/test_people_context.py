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
from types import SimpleNamespace
from unittest.mock import patch
from uuid import uuid4
import sys
from http.server import ThreadingHTTPServer
from urllib.request import urlopen
from urllib.error import HTTPError

import test_people_directory as directory_fixtures

from people_context import (
    PeopleContextContinuationPreparation,
    PeopleContextPreparation,
    PeopleContextProjection,
    main,
)
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
        primary = self.run / "results.jsonl"
        pending = json.loads(primary.read_text())
        pending["input"]["questions"][0]["questionId"] = "pair-2"
        pending["result"].update(state="unresolved", decisions=[])
        with primary.open("a") as stream:
            stream.write(encoded(pending) + "\n")
        manifest.update(
            questions=20,
            plannedQuestions=20,
            state="cancelled",
            resultsSha256=hashlib.sha256(primary.read_bytes()).hexdigest(),
        )
        manifest_path.write_text(encoded(manifest))
        PeopleContextProjection(database, self.run).apply()
        original_run = self.run
        recovered = self.root / "recovered"
        recovered.mkdir()
        row = json.loads((original_run / "results.jsonl").read_text().splitlines()[0])
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
        self.assertEqual(summary["processedQuestions"], 2)
        self.assertEqual(summary["executionState"], "cancelled")
        self.assertEqual(summary["plannedQuestions"], 20)
        self.assertEqual(summary["knownTokenCostUsd"], 0.01)
        self.assertEqual(summary["accountedUsd"], 0.01)
        directory = PeopleDirectory(database, "http://files.test/", "http://core.test/")
        self.assertEqual(
            directory.relationship("pair-2")["recovery"]["basis"],
            "recovered_from_failed_batch",
        )
        self.assertEqual(directory.relationships("a", {})["total"], 2)

    def test_disjoint_continuation_adds_progress_without_counting_recovery_twice(self):
        database = self.contextual_run()
        PeopleContextProjection(database, self.run).apply()
        continuation = self.root / "continuation"
        continuation.mkdir()
        row = json.loads((self.run / "results.jsonl").read_text())
        row["input"]["questions"][0]["questionId"] = "another-pair"
        row["result"]["decisions"][0]["questionId"] = "another-pair"
        result = continuation / "results.jsonl"
        result.write_text(encoded(row) + "\n")
        manifest = json.loads((self.run / "manifest.json").read_text())
        manifest.update(
            resultsSha256=hashlib.sha256(result.read_bytes()).hexdigest(),
            questions=1,
            plannedQuestions=5,
        )
        (continuation / "manifest.json").write_text(encoded(manifest))
        summary = PeopleContextProjection(database, continuation).apply()
        self.assertEqual(summary["processedQuestions"], 2)
        self.assertEqual(summary["decisions"], 2)
        self.assertEqual(summary["plannedQuestions"], 5)
        self.assertEqual(
            PeopleContextProjection(database, continuation).apply()[
                "processedQuestions"
            ],
            2,
        )

    def continuation_runs(self):
        database = self.contextual_run()
        original = self.run
        original_row = json.loads((original / "results.jsonl").read_text())
        original_row["input"]["batchId"] = "recorded"
        original_row["result"]["batchId"] = "recorded"
        pending_row = json.loads(encoded(original_row))
        pending_row["input"]["batchId"] = "pending"
        pending_row["input"]["questions"][0]["questionId"] = "pair-2"
        pending_row["result"].update(batchId="pending", state="unresolved", decisions=[])
        original_inputs = (
            encoded(original_row["input"]) + "\n" + encoded(pending_row["input"]) + "\n"
        ).encode()
        (original / "inputs.jsonl").write_bytes(original_inputs)
        original_results = (encoded(original_row) + "\n").encode()
        (original / "results.jsonl").write_bytes(original_results)
        original_job = str(uuid4())
        original_manifest = {
            "state": "cancelled", "jobUuid": original_job,
            "method": "luna-people-context-v1",
            "inputBatches": 2, "questions": 2, "completedBatches": 1,
            "accountedUsd": 0.01, "knownActualUsd": 0.01,
            "inputSha256": hashlib.sha256(original_inputs).hexdigest(),
            "resultsSha256": hashlib.sha256(original_results).hexdigest(),
        }
        (original / "manifest.json").write_text(encoded(original_manifest))
        (original / "continuation.json").write_text(encoded({
            "jobUuid": original_job, "inputPath": str(original / "inputs.jsonl"),
            "inputSha256": original_manifest["inputSha256"],
            "completedBatchIds": ["recorded"], "pendingBatchIds": ["pending"],
            "recordedQuestions": 1, "pendingQuestions": 1,
        }))
        PeopleContextProjection(database, original).apply()
        old_recovery = self.root / "old-recovery"
        old_recovery.mkdir()
        (old_recovery / "results.jsonl").write_bytes(original_results)
        (old_recovery / "manifest.json").write_text(encoded({
            "state": "completed", "jobUuid": original_job, "originalRun": str(original),
            "resultsSha256": original_manifest["resultsSha256"],
            "accountedUsd": 0, "knownActualUsd": 0,
            "originalJobAccountedUsdAfter": 0.01,
            "recoveredDecisions": 1, "rejectedQuestions": 2,
        }))
        PeopleContextProjection(database, old_recovery).apply()
        new_job = str(uuid4())
        with patch(
            "people_context.shutil.disk_usage",
            return_value=SimpleNamespace(free=10 * 1024**3),
        ):
            plan = PeopleContextContinuationPreparation(
                original, self.root / "preparation", new_job
            ).prepare()
        run = self.root / "continued"
        run.mkdir()
        plan_bytes = (self.root / "preparation" / "plan.json").read_bytes()
        (run / "continuation-plan.json").write_bytes(plan_bytes)
        reference = {
            key: plan[key]
            for key in ("newJobUuid", "originalJobUuid", "originalManifestSha256")
        }
        reference["planSha256"] = hashlib.sha256(plan_bytes).hexdigest()
        (run / "continuation-reference.json").write_text(encoded(reference))
        (run / "inputs.jsonl").write_bytes(Path(plan["pendingInputsPath"]).read_bytes())
        new_results = (encoded(pending_row) + "\n").encode()
        (run / "results.jsonl").write_bytes(new_results)
        new_manifest = {
            "state": "completed_partial", "jobUuid": new_job,
            "method": "luna-people-context-v1",
            "inputBatches": 1, "questions": 1, "completedBatches": 1,
            "inputSha256": plan["pendingInputsSha256"],
            "resultsSha256": hashlib.sha256(new_results).hexdigest(),
            "accountedUsd": 0.05, "knownActualUsd": 0.04,
        }
        (run / "manifest.json").write_text(encoded(new_manifest))
        recovered = self.root / "new-recovery"
        recovered.mkdir()
        pending_row["result"].update(
            state="proposed", decisions=original_row["result"]["decisions"]
        )
        pending_row["result"]["decisions"][0]["questionId"] = "pair-2"
        recovered_results = (encoded(pending_row) + "\n").encode()
        (recovered / "results.jsonl").write_bytes(recovered_results)
        (recovered / "manifest.json").write_text(encoded({
            "state": "completed", "jobUuid": new_job, "originalRun": str(run),
            "originalManifestSha256": hashlib.sha256(
                (run / "manifest.json").read_bytes()
            ).hexdigest(),
            "resultsSha256": hashlib.sha256(recovered_results).hexdigest(),
            "accountedUsd": 0, "knownActualUsd": 0,
            "originalJobAccountedUsdAfter": 0.05,
            "recoveredDecisions": 1, "rejectedQuestions": 3,
        }))
        return database, run, recovered

    def test_new_job_continuation_keeps_coverage_costs_and_recovery_counts(self):
        database, run, recovered = self.continuation_runs()
        primary = PeopleContextProjection(database, run).apply()
        self.assertEqual(primary["processedQuestions"], 2)
        self.assertEqual(primary["plannedQuestions"], 2)
        self.assertEqual(primary["recoveredDecisions"], 1)
        self.assertEqual(primary["rejectedQuestions"], 2)
        self.assertAlmostEqual(primary["accountedUsd"], 0.06)
        self.assertEqual(primary["knownTokenCostUsd"], 0.05)
        self.assertEqual(primary["originalJob"]["accountedUsd"], 0.01)
        self.assertEqual(primary["currentJob"]["accountedUsd"], 0.05)
        for attachment in [recovered, recovered, run]:
            summary = PeopleContextProjection(database, attachment).apply()
            self.assertEqual(summary["processedQuestions"], 2)
            self.assertEqual(summary["plannedQuestions"], 2)
            self.assertEqual(summary["decisions"], 2)
            self.assertEqual(summary["recoveredDecisions"], 2)
            self.assertEqual(summary["rejectedQuestions"], 5)
            self.assertEqual(summary["originalJob"]["recovery"]["rejectedQuestions"], 2)
            self.assertEqual(summary["currentJob"]["recovery"]["rejectedQuestions"], 3)
            self.assertAlmostEqual(summary["accountedUsd"], 0.06)
            self.assertEqual(summary["knownTokenCostUsd"], 0.05)
            self.assertEqual(summary["executionState"], "completed_partial")
        directory = PeopleDirectory(database, "http://files.test/", "http://core.test/")
        self.assertIsNotNone(directory.relationship("pair-1"))
        self.assertIsNotNone(directory.relationship("pair-2"))

    def test_continuation_attachment_authenticates_receipt_scope_and_new_run(self):
        database, run, recovered = self.continuation_runs()
        PeopleContextProjection(database, run).apply()
        originals = {
            p: p.read_bytes()
            for folder in [run, recovered, self.run]
            for p in folder.iterdir()
        }
        with closing(sqlite3.connect(database)) as db:
            before = db.execute(
                "SELECT value FROM metadata WHERE key='peopleContext'"
            ).fetchone()[0]
        for change in [
            "plan_bytes", "plan_scope", "original_manifest",
            "new_input", "new_result", "recovery_job", "missing_plan",
        ]:
            with self.subTest(change=change):
                target = run
                if change == "plan_bytes":
                    path = run / "continuation-plan.json"
                    path.write_bytes(path.read_bytes() + b" ")
                elif change == "plan_scope":
                    plan_path = run / "continuation-plan.json"
                    plan = json.loads(plan_path.read_bytes())
                    plan["recordedQuestions"] = 100
                    plan_path.write_text(encoded(plan))
                    reference_path = run / "continuation-reference.json"
                    reference = json.loads(reference_path.read_text())
                    reference["planSha256"] = hashlib.sha256(plan_path.read_bytes()).hexdigest()
                    reference_path.write_text(encoded(reference))
                elif change == "original_manifest":
                    path = self.run / "manifest.json"
                    path.write_bytes(path.read_bytes() + b" ")
                elif change == "new_input":
                    path = run / "inputs.jsonl"
                    path.write_bytes(path.read_bytes() + b" ")
                elif change == "new_result":
                    path = run / "results.jsonl"
                    row = json.loads(path.read_bytes())
                    row["input"]["questions"][0]["questionId"] = "unplanned"
                    path.write_text(encoded(row) + "\n")
                    manifest_path = run / "manifest.json"
                    manifest = json.loads(manifest_path.read_bytes())
                    manifest["resultsSha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
                    manifest_path.write_text(encoded(manifest))
                elif change == "recovery_job":
                    target = recovered
                    path = recovered / "manifest.json"
                    manifest = json.loads(path.read_bytes())
                    manifest["jobUuid"] = str(uuid4())
                    path.write_text(encoded(manifest))
                else:
                    (run / "continuation-plan.json").unlink()
                with self.assertRaises((ValueError, FileNotFoundError)):
                    PeopleContextProjection(database, target).apply()
                with closing(sqlite3.connect(database)) as db:
                    self.assertEqual(
                        db.execute(
                            "SELECT value FROM metadata WHERE key='peopleContext'"
                        ).fetchone()[0],
                        before,
                    )
                for path, data in originals.items():
                    path.write_bytes(data)

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
        directory = PeopleDirectory(database, "http://files.test/", "http://core.test/")
        self.assertEqual(directory.relationships("a", {})["total"], 0)


class TestPeopleContextContinuation(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.run = self.root / "original"
        self.run.mkdir()
        self.output = self.root / "pending"
        self.original_job = str(uuid4())
        self.new_job = str(uuid4())
        # Different formatting, CRLF and a final unterminated line expose any
        # accidental reserialization or reordering during pending extraction.
        self.lines = [
            b'{ "batchId" : "first", "questions" : [{"questionId":"q1","name":"Jos\xc3\xa9"}] }\r\n',
            b'{"batchId":"completed","questions":[{"questionId":"q2"}]}\n',
            b'{"questions":[{"questionId":"q3"},{"questionId":"q4"}], "batchId":"last"}',
        ]
        (self.run / "inputs.jsonl").write_bytes(b"".join(self.lines))
        completed = json.loads(self.lines[1])
        self.rows = [
            {
                "input": completed,
                "result": {"batchId": "completed", "state": "unresolved"},
            }
        ]
        self.write_results()
        self.manifest = {
            "state": "cancelled",
            "jobUuid": self.original_job,
            "method": "luna-people-context-v1",
            "inputBatches": 3,
            "questions": 4,
            "completedBatches": 1,
        }
        self.continuation = {
            "jobUuid": self.original_job,
            "state": "awaiting_storage_decision",
            "inputPath": str(self.run / "inputs.jsonl"),
            "completedBatchIds": ["completed"],
            "pendingBatchIds": ["first", "last"],
            "recordedQuestions": 1,
            "pendingQuestions": 3,
            "unrecordedAttempts": [{"batchId": "first", "reservedUsd": 0.1}],
        }
        self.seal()
        disk = patch(
            "people_context.shutil.disk_usage",
            return_value=SimpleNamespace(free=10 * 1024**3),
        )
        disk.start()
        self.addCleanup(disk.stop)

    def write_results(self):
        (self.run / "results.jsonl").write_text(
            "".join(encoded(row) + "\n" for row in self.rows), encoding="utf-8"
        )

    def seal(self):
        for field, filename in [
            ("inputSha256", "inputs.jsonl"),
            ("resultsSha256", "results.jsonl"),
        ]:
            self.manifest[field] = hashlib.sha256(
                (self.run / filename).read_bytes()
            ).hexdigest()
        self.continuation["inputSha256"] = self.manifest["inputSha256"]
        self.write_metadata()

    def write_metadata(self):
        (self.run / "manifest.json").write_text(encoded(self.manifest))
        (self.run / "continuation.json").write_text(encoded(self.continuation))

    def prepare(self):
        return PeopleContextContinuationPreparation(
            self.run, self.output, self.new_job
        ).prepare()

    def test_copies_only_pending_original_bytes_and_seals_complete_plan(self):
        original_files = {p.name: p.read_bytes() for p in self.run.iterdir()}
        plan = self.prepare()
        pending = (self.output / "pending-inputs.jsonl").read_bytes()
        self.assertEqual(pending, self.lines[0] + self.lines[2])
        self.assertEqual(
            [json.loads(line)["batchId"] for line in pending.splitlines()],
            ["first", "last"],
        )
        self.assertEqual(json.loads((self.output / "plan.json").read_text()), plan)
        self.assertEqual((plan["pendingBatches"], plan["pendingQuestions"]), (2, 3))
        self.assertEqual((plan["recordedBatches"], plan["recordedQuestions"]), (1, 1))
        self.assertEqual(plan["newJobUuid"], self.new_job)
        self.assertEqual(plan["originalJobUuid"], self.original_job)
        self.assertEqual(plan["pendingInputsSha256"], hashlib.sha256(pending).hexdigest())
        for field, filename in [
            ("originalManifestSha256", "manifest.json"),
            ("continuationSha256", "continuation.json"),
            ("originalInputSha256", "inputs.jsonl"),
            ("originalResultsSha256", "results.jsonl"),
        ]:
            self.assertEqual(
                plan[field], hashlib.sha256(original_files[filename]).hexdigest()
            )
        self.assertEqual(
            {p.name: p.read_bytes() for p in self.run.iterdir()}, original_files
        )

    def test_refuses_changed_retained_files_before_creating_output(self):
        for filename in ["inputs.jsonl", "results.jsonl"]:
            with self.subTest(filename=filename):
                path = self.run / filename
                original = path.read_bytes()
                path.write_bytes(original + b"\n")
                with self.assertRaisesRegex(ValueError, "hashes"):
                    self.prepare()
                self.assertFalse(self.output.exists())
                path.write_bytes(original)

    def test_refuses_result_with_changed_batch_input_or_result_id(self):
        for change in ["input", "result", "unknown", "duplicate"]:
            with self.subTest(change=change):
                original = json.loads(encoded(self.rows))
                if change == "input":
                    self.rows[0]["input"]["questions"][0]["name"] = "changed witness"
                elif change == "result":
                    self.rows[0]["result"]["batchId"] = "first"
                elif change == "unknown":
                    self.rows[0]["input"]["batchId"] = "unknown"
                else:
                    self.rows.append(self.rows[0])
                self.write_results()
                self.seal()
                with self.assertRaisesRegex(ValueError, "exact original input"):
                    self.prepare()
                self.assertFalse(self.output.exists())
                self.rows = original

    def test_refuses_false_or_duplicate_pending_and_completed_ids(self):
        for field, value in [
            ("pendingBatchIds", ["first", "completed"]),
            ("pendingBatchIds", ["first"]),
            ("pendingBatchIds", ["first", "last", "last"]),
            ("completedBatchIds", []),
            ("recordedQuestions", 2),
            ("pendingQuestions", 4),
        ]:
            with self.subTest(field=field, value=value):
                original = self.continuation[field]
                self.continuation[field] = value
                self.write_metadata()
                with self.assertRaises(ValueError):
                    self.prepare()
                self.assertFalse(self.output.exists())
                self.continuation[field] = original

    def test_refuses_duplicate_or_missing_question_ids_and_duplicate_batch_ids(self):
        for change in ["duplicate_question", "missing_question", "duplicate_batch"]:
            with self.subTest(change=change):
                batch = json.loads(self.lines[2])
                if change == "duplicate_question":
                    batch["questions"][0]["questionId"] = "q1"
                elif change == "missing_question":
                    del batch["questions"][0]["questionId"]
                else:
                    batch["batchId"] = "first"
                (self.run / "inputs.jsonl").write_bytes(
                    self.lines[0] + self.lines[1] + encoded(batch).encode()
                )
                self.seal()
                with self.assertRaises((ValueError, KeyError)):
                    self.prepare()
                self.assertFalse(self.output.exists())

    def test_refuses_unsealed_run_changed_method_or_wrong_continuation_job(self):
        for owner, field, value in [
            (self.manifest, "state", "running"),
            (self.manifest, "method", "other-method"),
            (self.manifest, "completedBatches", 2),
            (self.continuation, "jobUuid", str(uuid4())),
            (self.continuation, "inputSha256", "wrong"),
            (self.continuation, "inputPath", str(self.root / "other-inputs.jsonl")),
        ]:
            with self.subTest(field=field):
                original = owner[field]
                owner[field] = value
                self.write_metadata()
                with self.assertRaises(ValueError):
                    self.prepare()
                self.assertFalse(self.output.exists())
                owner[field] = original

    def test_refuses_empty_pending_population_and_reused_or_empty_job(self):
        self.rows = [
            {
                "input": json.loads(line),
                "result": {"batchId": json.loads(line)["batchId"]},
            }
            for line in self.lines
        ]
        self.write_results()
        self.manifest["completedBatches"] = 3
        self.continuation.update(
            completedBatchIds=["first", "completed", "last"],
            pendingBatchIds=[],
            recordedQuestions=4,
            pendingQuestions=0,
        )
        self.seal()
        with self.assertRaisesRegex(ValueError, "partition"):
            self.prepare()
        for job in [self.original_job, "00000000-0000-0000-0000-000000000000"]:
            with self.subTest(job=job), self.assertRaises(ValueError):
                PeopleContextContinuationPreparation(self.run, self.output, job).prepare()
        self.assertFalse(self.output.exists())

    def test_preserves_existing_output_and_refuses_low_disk_before_writes(self):
        self.output.mkdir()
        marker = self.output / "keep"
        marker.write_bytes(b"original")
        with self.assertRaises(FileExistsError):
            self.prepare()
        self.assertEqual(marker.read_bytes(), b"original")
        marker.unlink()
        self.output.rmdir()
        with patch(
            "people_context.shutil.disk_usage",
            return_value=SimpleNamespace(free=5905580031),
        ):
            with self.assertRaisesRegex(RuntimeError, "5.5 GiB"):
                self.prepare()
        self.assertFalse(self.output.exists())

    def test_cli_prepares_without_database_and_preserves_legacy_requirements(self):
        args = [
            "people_context.py",
            "--continue-from", str(self.run),
            "--output", str(self.output),
            "--job-uuid", self.new_job,
        ]
        with patch.object(sys, "argv", args), patch("builtins.print"):
            main()
        self.assertTrue((self.output / "plan.json").exists())
        for invalid in [
            ["--output", str(self.root / "legacy")],
            ["--continue-from", str(self.run), "--output", str(self.output)],
            ["--database", "db", "--output", "out", "--job-uuid", self.new_job],
            [
                "--continue-from", str(self.run), "--output", "out",
                "--job-uuid", self.new_job, "--attach",
            ],
        ]:
            with (
                self.subTest(args=invalid),
                patch.object(sys, "argv", ["people_context.py"] + invalid),
                patch("sys.stderr"),
                self.assertRaises(SystemExit),
            ):
                main()


if __name__ == "__main__":
    unittest.main()
