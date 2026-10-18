"""Public directory behavior: source ownership, inferred groups and evidence survive."""

import gzip
import json
import sqlite3
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import parse_qs, urlsplit

from shadow_reader import ShadowIndex
from urllib.request import urlopen
from http.server import ThreadingHTTPServer

from people_directory import (
    CreditOverlay,
    NativePeopleOverlay,
    DirectoryBuilder,
    PeopleDirectory,
    group_id,
    handler_for,
    name_key,
)


class TestPeopleDirectory(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "credits").mkdir()
        (self.root / "manifest.json").write_text(
            json.dumps(
                {
                    "state": "completed",
                    "methods": {"ShadowPeopleAdapter": "shadow-people-v1"},
                }
            )
        )
        self.database = self.root / "directory.sqlite3"

    def credit(self, source, artist, name, role, alias=None):
        evidence = {
            "field": role,
            "quote": name,
            "startCodePoint": 0,
            "endCodePoint": len(name),
        }
        credit = {
            "name": name,
            "role": role,
            "evidence": evidence,
            "sourceUuid": source,
            "artistUuid": artist,
            "personEntityKey": "person:" + source + ":" + role,
        }
        extracted = {
            "name": name,
            "role": role,
            "evidence": evidence,
            "actorFacts": {
                "aliases": [{"wording": alias, "evidence": [evidence]}] if alias else []
            },
        }
        return credit, extracted

    def build(self, inputs):
        rows = []
        for source, artist, credits in inputs:
            rows.append(
                {
                    "inputKind": "source_sample",
                    "inputUuid": source,
                    "artistUuid": artist,
                    "artistName": artist,
                    "proposal": {"credits": [c[0] for c in credits]},
                    "evidence": {
                        "creditExtraction": {"credits": [c[1] for c in credits]}
                    },
                }
            )
        with gzip.open(self.root / "credits" / "1.jsonl.gz", "wt") as stream:
            for row in rows:
                stream.write(json.dumps(row) + "\n")
        DirectoryBuilder(self.root, self.database, minimum_free=0).build()
        return PeopleDirectory(self.database, "http://files.test/", "http://core.test/")

    def native_actor(self, key, name="Native Pat", aliases=None, role_free=False, file_origin=None):
        origin = {"item": {"provider": "local", "resourceType": "directory_metadata", "providerKey": "[\"catalog\",\"one\"]"},
                  "evidence": {"inputRevisionUuid": "root-input", "jsonPointer": "/data/1970-xx-xx/0"}}
        return dict(entityKey=key, name=name, aliases=aliases or [], kind=None,
                    inputRevisionUuid="root-input", recordingOrigin=origin, fileOrigin=file_origin,
                    catalogSourceUuid="source-1", catalogShowUuid="native-show",
                    catalogBindingReason=None, evidence=[dict(inputRevisionUuid="root-input", jsonPointer="/data/1970-xx-xx/0/taper_notes", quote=name)],
                    actorFacts=None, sourceActorAlias={"name": name} if role_free else None,
                    automaticAcceptanceAuthorized=False)

    def native_run(self, actors, credits=None, provider="local"):
        run = self.root / "native"
        run.mkdir(exist_ok=True)
        method = "shadow-native-people-v2/test"
        (run / "manifest.json").write_text(json.dumps(dict(state="completed", inputIdentity="separate-local-capture",
            methods={"ShadowNativePeopleAdapter": method}, counts=[dict(domain=domain, provider=provider, processed=1) for domain in ("people", "credits")])))
        header = dict(inputKind="provider_input", inputKey="provider_input:directory-input", inputUuid="directory-input",
                      artistUuid="artist-a", artistName="Native artist", provider=provider, method=method, outcome="proposed")
        if credits is None:
            credits = [dict(entityKey="native-credit:" + actor["entityKey"], personEntityKey=actor["entityKey"],
                            role="taper", originalRole="Taped by", **{field: actor[field] for field in
                            ("name", "inputRevisionUuid", "recordingOrigin", "fileOrigin", "catalogSourceUuid", "catalogShowUuid")},
                            evidence=actor["evidence"][0]) for actor in actors if actor["sourceActorAlias"] is None]
        for domain, proposal in (("people", {"people": actors}), ("credits", {"credits": credits})):
            (run / domain).mkdir(exist_ok=True)
            with gzip.open(run / domain / "1.jsonl.gz", "wt") as stream:
                stream.write(json.dumps(dict(header, domain=domain, proposal=proposal)) + "\n")
        return run

    def test_native_names_aliases_and_role_proposals_share_lookup_without_inflating_catalog_counts(self):
        (self.root / "performances").mkdir()
        with gzip.open(self.root / "performances" / "1.jsonl.gz", "wt") as stream:
            stream.write(json.dumps(dict(inputKind="source_sample", inputUuid="source-1", outcome="proposed",
                proposal=dict(sourceUuid="source-1", showUuid="production-show", literalDate="1970-xx-xx"))) + "\n")
        directory = self.build([("source-1", "artist-a", [self.credit("source-1", "artist-a", "Native Pat", "taper")])])
        before = directory.stats()
        with directory.connect() as db:
            base_homes = [tuple(row) for row in db.execute("SELECT * FROM source_homes")]
        with sqlite3.connect(self.database) as db:
            db.execute("CREATE TABLE context_relationships(id TEXT PRIMARY KEY, decision TEXT)")
            db.execute("INSERT INTO context_relationships VALUES('existing-question','retained decision')")
        role = self.native_actor("native:role")
        alias = self.native_actor("native:alias", aliases=["Lark"], role_free=True)
        file_actor = self.native_actor("native:file", "File Avery", file_origin={"evidence": {"jsonPointer": "/files/0"}})
        file_actor["catalogSourceUuid"] = None
        file_actor["catalogShowUuid"] = None
        run = self.native_run([role, alias, file_actor])
        with gzip.open(run / "credits" / "1.jsonl.gz", "rt") as stream:
            repeated = json.load(stream)
        repeated["proposal"]["credits"].append(repeated["proposal"]["credits"][0])
        with gzip.open(run / "credits" / "1.jsonl.gz", "wt") as stream:
            stream.write(json.dumps(repeated) + "\n")
        result = NativePeopleOverlay(run, self.database, "http://native.test/", minimum_free=0).apply()

        self.assertEqual((result["actors"], result["creditMentions"], result["aliasActors"], result["recordingAccounts"]), (3, 2, 1, 1))
        stats = directory.stats()
        for field in ("credits", "creditedRecordings", "creditedArtists", "sourceRows"):
            self.assertEqual(stats[field], before[field])
        matched = directory.search({"q": "Lark"})["groups"]
        self.assertEqual(len(matched), 1)
        detail = directory.detail(matched[0]["id"], {})
        self.assertEqual((detail["credit_count"], detail["recording_count"], detail["native_credit_count"]), (1, 1, 1))
        self.assertEqual(detail["nativeTotal"], 2)
        self.assertEqual(len(detail["recordings"]), 1)
        self.assertEqual(detail["recordings"][0]["capturedHome"]["showUuid"], "production-show")
        self.assertTrue(all("capturedHome" not in item and "playableFilesUrl" not in item for item in detail["nativeObservations"]))
        with directory.connect() as db:
            self.assertEqual([tuple(row) for row in db.execute("SELECT * FROM source_homes")], base_homes)
            self.assertEqual(tuple(db.execute("SELECT * FROM context_relationships").fetchone()), ("existing-question", "retained decision"))
        observed = detail["nativeObservations"]
        self.assertTrue(all(item["artist_key"] != item["artist_uuid"] for item in observed))
        self.assertEqual([len(item["credits"]) for item in observed if item["role_free"]], [0])
        self.assertTrue(all(item["inputFilesUrl"].startswith("http://native.test/") for item in observed))
        self.assertTrue(all(parse_qs(urlsplit(item["inputFilesUrl"]).query)["q"] == ["directory-input"] for item in observed))
        filtered = directory.detail(matched[0]["id"], {"nativeArtist": observed[0]["artist_key"], "limit": "1"})
        self.assertEqual(filtered["nativeTotal"], 2)
        self.assertEqual(len(filtered["nativeObservations"]), 1)
        self.assertEqual(directory.detail(group_id(name_key("File Avery")), {})["recordings"], [])
        unbound = directory.detail(group_id(name_key("File Avery")), {})["nativeObservations"][0]
        self.assertIsNone(unbound["catalog_source_uuid"])
        self.assertEqual(unbound["file_origin"]["evidence"]["jsonPointer"], "/files/0")
        retained = directory.native_actor(observed[0]["id"])
        self.assertEqual(retained["actor"]["recordingOrigin"], role["recordingOrigin"])
        self.assertEqual(retained["originRun"], str(run.resolve()))
        self.assertFalse(retained["acceptedIdentity"])

    def test_native_account_counts_preserve_distinct_origin_locations_on_one_resource(self):
        directory = self.build([])
        first = self.native_actor("first", "Alice", role_free=True)
        second = self.native_actor("second", "Bob", role_free=True)
        second["recordingOrigin"]["evidence"]["jsonPointer"] = "/data/1970-xx-xx/1"
        run = self.native_run([first, second])
        counts = NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        self.assertEqual(counts["recordingAccounts"], 2)
        self.assertEqual(directory.stats()["creditedRecordings"], 0)

    def test_native_replay_removes_old_aliases_and_preserves_catalog_context_and_ids(self):
        directory = self.build([("source-1", "artist-a", [self.credit("source-1", "artist-a", "Catalog Pat", "taper")])])
        with directory.connect() as db:
            base_credit = dict(db.execute("SELECT * FROM credits").fetchone())
        run = self.native_run([self.native_actor("native:old", "Old Name", aliases=["Old Alias"], role_free=True)])
        NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        old = directory.detail(group_id(name_key("Old Name")), {})["nativeObservations"][0]["id"]
        self.native_run([self.native_actor("native:new", "New Name", role_free=True)])
        NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()

        self.assertEqual(directory.search({"q": "Old Alias"})["total"], 0)
        self.assertIsNone(directory.native_actor(old))
        self.assertEqual(directory.search({"q": "New Name"})["total"], 1)
        with directory.connect() as db:
            self.assertEqual(dict(db.execute("SELECT * FROM credits").fetchone()), base_credit)
        self.assertEqual(directory.stats()["credits"], 1)

    def test_native_invalid_scope_or_missing_population_rolls_back_prior_projection(self):
        directory = self.build([])
        actor = self.native_actor("native:stable", role_free=True)
        run = self.native_run([actor])
        NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        prior = directory.detail(group_id(name_key(actor["name"])), {})
        bad_credit = dict(entityKey="bad-credit", personEntityKey=actor["entityKey"], role="taper")
        self.native_run([actor], [bad_credit])
        with self.assertRaises(ValueError):
            NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        self.assertEqual(directory.detail(prior["id"], {})["nativeTotal"], 1)
        (run / "credits" / "1.jsonl.gz").unlink()
        with self.assertRaises(ValueError):
            NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        self.assertEqual(directory.stats()["nativeDirectory"]["actors"], 1)

    def test_native_projection_bounds_refuse_before_replacing_prior_population(self):
        directory = self.build([])
        run = self.native_run([self.native_actor("retained", role_free=True)])
        NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        with patch.object(NativePeopleOverlay, "MAXIMUM_DECODED_BYTES", 64):
            with self.assertRaisesRegex(ValueError, "decoded projection bound"):
                NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        manifest = json.loads((run / "manifest.json").read_text())
        manifest["counts"][0]["processed"] = 2001
        (run / "manifest.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "parent projection bound"):
            NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        self.assertEqual(directory.stats()["nativeDirectory"]["actors"], 1)

    def test_native_actor_endpoint_verifies_original_chunk_and_http_unknown_ids(self):
        directory = self.build([])
        actor = self.native_actor("native:alias", aliases=["Alias"], role_free=True)
        run = self.native_run([actor])
        NativePeopleOverlay(run, self.database, "http://native.test", minimum_free=0).apply()
        identifier = directory.detail(group_id(name_key(actor["name"])), {})["nativeObservations"][0]["id"]
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            url = "http://127.0.0.1:" + str(server.server_port)
            with urlopen(url + "/api/native-actor?id=" + identifier) as response:
                self.assertEqual(json.load(response)["actor"], actor)
            with self.assertRaises(HTTPError) as error:
                urlopen(url + "/api/native-actor?id=missing")
            self.assertEqual(error.exception.code, 404)
            error.exception.close()
            (run / "people" / "1.jsonl.gz").write_bytes(b"changed")
            with self.assertRaises(HTTPError) as error:
                urlopen(url + "/api/native-actor?id=" + identifier)
            self.assertEqual(error.exception.code, 400)
            error.exception.close()
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_cross_artist_group_counts_sources_once_and_preserves_each_role_and_quote(
        self,
    ):
        directory = self.build(
            [
                (
                    "source-1",
                    "artist-a",
                    [
                        self.credit("source-1", "artist-a", "Rob Berger", "taper"),
                        self.credit(
                            "source-1", "artist-a", "Rob Berger", "transferrer"
                        ),
                    ],
                ),
                (
                    "source-2",
                    "artist-b",
                    [self.credit("source-2", "artist-b", " ROB  BERGER ", "taper")],
                ),
            ]
        )
        groups = directory.search({"q": "rob berger"})["groups"]
        self.assertEqual(len(groups), 1)
        group = groups[0]
        self.assertEqual(
            (group["recording_count"], group["artist_count"], group["credit_count"]),
            (2, 2, 3),
        )
        detail = directory.detail(group["id"], {"limit": "1"})
        self.assertEqual(detail["basis"], "inferred_name_group")
        self.assertEqual(detail["total"], 2)
        self.assertEqual(
            {c["role"] for c in detail["recordings"][0]["credits"]},
            {"taper", "transferrer"},
        )
        self.assertTrue(
            detail["recordings"][0]["playableFilesUrl"].endswith(
                "domain=file_contents&entityKey=source%3Asource-1"
            )
        )
        second = directory.detail(group["id"], {"offset": "1", "limit": "1"})
        self.assertEqual(second["recordings"][0]["sourceUuid"], "source-2")
        filtered = directory.detail(group["id"], {"artist": "artist-b"})
        self.assertEqual(filtered["total"], 1)
        self.assertEqual(
            filtered["recordings"][0]["credits"][0]["evidence"]["quote"],
            " ROB  BERGER ",
        )

    def test_explicit_alias_is_searchable_without_transitive_identity_merge(self):
        directory = self.build(
            [
                ("one", "a", [self.credit("one", "a", "MFTS", "taper", alias="morst")]),
                (
                    "two",
                    "b",
                    [
                        self.credit(
                            "two", "b", "morst", "transferrer", alias="Another Name"
                        )
                    ],
                ),
                ("three", "c", [self.credit("three", "c", "Another Name", "taper")]),
            ]
        )
        self.assertEqual(len(directory.search({"q": "morst"})["groups"]), 2)
        mfts = directory.detail(group_id(name_key("MFTS")), {})
        self.assertEqual(mfts["recording_count"], 1)
        self.assertEqual([g["display_name"] for g in mfts["relatedGroups"]], ["morst"])
        self.assertEqual(
            mfts["recordings"][0]["credits"][0]["aliases"][0]["wording"], "morst"
        )

    def test_later_source_result_replaces_old_credits_without_double_counting(self):
        directory = self.build(
            [
                ("one", "a", [self.credit("one", "a", "Wrong Name", "taper")]),
                ("one", "a", [self.credit("one", "a", "Right Name", "taper")]),
                ("two", "a", [self.credit("two", "a", "Removed Credit", "taper")]),
                ("two", "a", []),
            ]
        )
        self.assertEqual(directory.search({"q": "wrong"})["total"], 0)
        self.assertEqual(directory.search({"q": "removed"})["total"], 0)
        self.assertEqual(directory.stats()["sourceRows"], 2)
        self.assertEqual(directory.stats()["creditedRecordings"], 1)

    def test_http_search_detail_missing_group_and_invalid_page(self):
        directory = self.build(
            [
                (
                    "source",
                    "artist",
                    [self.credit("source", "artist", "Aadam Jacobs", "taper")],
                )
            ]
        )
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        base = "http://127.0.0.1:" + str(server.server_port)
        with urlopen(base + "/api/people?q=Aadam") as response:
            group = json.load(response)["groups"][0]
        with urlopen(base + "/api/person?id=" + group["id"]) as response:
            self.assertEqual(
                json.load(response)["recordings"][0]["sourceUuid"], "source"
            )
        with urlopen(base + "/api/stats") as response:
            self.assertEqual(json.load(response)["creditedRecordings"], 1)
        for path, status in [
            ("/api/person?id=missing", 404),
            ("/api/people?offset=bad", 400),
        ]:
            with self.assertRaises(HTTPError) as result:
                urlopen(base + path)
            self.assertEqual(result.exception.code, status)
            result.exception.close()

    def test_recording_home_preserves_partial_dates_labels_and_source_roles(self):
        (self.root / "performances").mkdir()
        home = {
            "inputKind": "source_sample",
            "inputUuid": "source",
            "outcome": "unresolved",
            "proposal": {
                "sourceUuid": "source",
                "showUuid": "stable-show",
                "literalDate": "1968-??-??",
                "performanceType": "late",
                "performanceLabel": "Late show",
                "unresolved": ["partial_recording_date"],
            },
            "evidence": {"basis": "captured_catalog_assignment"},
        }
        with gzip.open(self.root / "performances" / "1.jsonl.gz", "wt") as stream:
            stream.write(json.dumps(home) + "\n")
        directory = self.build(
            [
                (
                    "source",
                    "artist",
                    [
                        self.credit("source", "artist", "Rob Berger", "taper"),
                        self.credit("source", "artist", "Rob Berger", "transferrer"),
                    ],
                ),
            ]
        )
        detail = directory.detail(group_id(name_key("Rob Berger")), {})
        self.assertEqual(detail["recording_count"], 1)
        recording = detail["recordings"][0]
        self.assertEqual(recording["sourceUuid"], "source")
        self.assertEqual(len(recording["credits"]), 2)
        self.assertEqual(recording["capturedHome"]["literalDate"], "1968-??-??")
        self.assertEqual(recording["capturedHome"]["performanceLabel"], "Late show")
        self.assertEqual(recording["capturedHome"]["showUuid"], "stable-show")
        self.assertEqual(
            recording["capturedHome"]["unresolved"], ["partial_recording_date"]
        )
        self.assertEqual(recording["capturedHome"]["outcome"], "unresolved")

    def test_generated_links_filter_the_actual_shadow_reader_to_the_selected_source(
        self,
    ):
        directory = self.build(
            [
                (
                    source,
                    "artist",
                    [self.credit(source, "artist", "Shared Name", "taper")],
                )
                for source in ("one", "two")
            ]
        )
        run = self.root / "reader"
        run.mkdir()
        for domain in ("credits", "file_contents"):
            (run / domain).mkdir()
            with gzip.open(run / domain / "1.jsonl.gz", "wt") as stream:
                for source in ("one", "two"):
                    is_file = domain == "file_contents"
                    input_uuid = "file-" + source if is_file else source
                    kind = "track_sample" if is_file else "source_sample"
                    record = {
                        "domain": domain,
                        "inputKey": kind + ":" + input_uuid,
                        "inputKind": kind,
                        "inputUuid": input_uuid,
                        "artistUuid": "artist",
                        "artistName": "Artist",
                        "provider": "catalog",
                        "outcome": "proposed",
                        "reason": None,
                        "method": "contract-fixture",
                        "entityKeys": [
                            "source:" + source
                            if is_file
                            else "person:" + source + ":taper"
                        ],
                        "proposal": {"sourceUuid": source},
                        "evidence": {},
                    }
                    stream.write(json.dumps(record) + "\n")
        reader = ShadowIndex(run)
        reader.scan_once()
        recording = directory.detail(group_id(name_key("Shared Name")), {"limit": "1"})[
            "recordings"
        ][0]
        for link in (
            recording["playableFilesUrl"],
            recording["credits"][0]["sourceEvidenceUrl"],
        ):
            filters = {
                key: values[-1]
                for key, values in parse_qs(urlsplit(link).query).items()
            }
            response = reader.search(filters)
            self.assertEqual(response["total"], 1)
            result = reader.detail(response["items"][0]["id"])["record"]
            self.assertEqual(result["proposal"]["sourceUuid"], recording["sourceUuid"])

    def test_selected_source_overlay_preserves_other_sources_homes_and_model_evidence(
        self,
    ):
        (self.root / "performances").mkdir()
        with gzip.open(self.root / "performances" / "1.jsonl.gz", "wt") as stream:
            stream.write(
                json.dumps(
                    {
                        "inputKind": "source_sample",
                        "inputUuid": "one",
                        "outcome": "proposed",
                        "proposal": {
                            "sourceUuid": "one",
                            "showUuid": "show-one",
                            "literalDate": "1972-00-00",
                        },
                    }
                )
                + "\n"
            )
        directory = self.build(
            [
                (
                    "one",
                    "artist-a",
                    [
                        self.credit(
                            "one", "artist-a", "Old Name", "taper", alias="Old Alias"
                        )
                    ],
                ),
                (
                    "two",
                    "artist-b",
                    [
                        self.credit(
                            "two", "artist-b", "Old Name", "taper", alias="Keep Alias"
                        )
                    ],
                ),
            ]
        )
        home = directory.detail(group_id(name_key("Old Name")), {})["recordings"][0][
            "capturedHome"
        ]
        overlay = self.root / "overlay"
        (overlay / "credits").mkdir(parents=True)
        (overlay / "manifest.json").write_text(json.dumps({"state": "completed"}))
        selection = overlay / "selection.json"
        selection.write_text(json.dumps({"sourceUuids": ["one"]}))
        local, local_facts = self.credit(
            "one", "artist-a", "Rob Berger", "taper", alias="New Alias"
        )
        model, model_facts = self.credit("one", "artist-a", "Rob Berger", "transferrer")
        local["entityKey"] = "credit:local"
        model.update(
            entityKey="credit:model", interpretationBasis="retained_luna_extraction"
        )
        outcome = {
            "inputKind": "source_sample",
            "inputUuid": "one",
            "artistUuid": "artist-a",
            "artistName": "artist-a",
            "proposal": {"credits": [local, model]},
            "evidence": {
                "creditExtraction": {"credits": [local_facts]},
                "model": {
                    "originalLunaResult": {"extraction": {"credits": [model_facts]}}
                },
            },
        }
        with gzip.open(overlay / "credits" / "1.jsonl.gz", "wt") as stream:
            stream.write(json.dumps(outcome) + "\n")
        CreditOverlay(overlay, selection, self.database, minimum_free=0).apply()
        self.assertEqual(directory.search({"q": "Old Alias"})["total"], 0)
        self.assertEqual(directory.search({"q": "Keep Alias"})["total"], 1)
        old = directory.detail(group_id(name_key("Old Name")), {})
        self.assertEqual(old["recording_count"], 1)
        self.assertEqual(old["recordings"][0]["sourceUuid"], "two")
        detail = directory.detail(group_id(name_key("Rob Berger")), {})
        self.assertEqual(detail["recording_count"], 1)
        recording = detail["recordings"][0]
        self.assertEqual(recording["capturedHome"], home)
        self.assertEqual(
            {credit["role"] for credit in recording["credits"]},
            {"taper", "transferrer"},
        )
        self.assertEqual(
            {credit["interpretation_basis"] for credit in recording["credits"]},
            {None, "retained_luna_extraction"},
        )
        model_credit = next(
            credit for credit in recording["credits"] if credit["interpretation_basis"]
        )
        self.assertEqual(model_credit["evidence"], model["evidence"])
        self.assertEqual(model_credit["aliases"], [])
        self.assertTrue(model_credit["sourceEvidenceUrl"].startswith("/api/credit?"))
        retained = directory.credit(model_credit["id"])
        self.assertEqual(retained["retainedOutcome"], outcome)
        self.assertEqual(retained["credit"]["source_uuid"], "one")
        self.assertEqual(directory.stats()["creditedRecordings"], 2)
        self.assertEqual(directory.stats()["modelCreditMentions"], 1)
        failed = self.root / "failed-overlay"
        (failed / "credits").mkdir(parents=True)
        (failed / "manifest.json").write_text(json.dumps({"state": "completed"}))
        with gzip.open(failed / "credits" / "1.jsonl.gz", "wt") as stream:
            stream.write(
                json.dumps(
                    {
                        "inputKind": "source_sample",
                        "inputUuid": "one",
                        "outcome": "failed",
                    }
                )
                + "\n"
            )
        with self.assertRaises(ValueError):
            CreditOverlay(failed, selection, self.database, minimum_free=0).apply()
        self.assertEqual(
            directory.credit(model_credit["id"])["retainedOutcome"], outcome
        )
        self.assertEqual(directory.stats()["modelCreditMentions"], 1)


if __name__ == "__main__":
    unittest.main()
