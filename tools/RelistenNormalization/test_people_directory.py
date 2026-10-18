"""Public directory behavior: source ownership, inferred groups and evidence survive."""

import gzip
from contextlib import closing
import json
import sqlite3
import sys
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
    SourceDirectoryRefresh,
    NativePeopleOverlay,
    DirectoryBuilder,
    PeopleDirectory,
    group_id,
    handler_for,
    name_key,
)
from person_hypotheses import PersonHypotheses


def create_context_table(db):
    db.execute("""CREATE TABLE context_relationships(
        id TEXT PRIMARY KEY,left_group TEXT,right_group TEXT,left_credit INTEGER,right_credit INTEGER,
        relation TEXT,reason TEXT,evidence TEXT,retrieval_basis TEXT,run TEXT,line INTEGER,
        cache_key TEXT,job_uuid TEXT,basis TEXT)""")


def add_context(db, identifier, left, right, relation, run='', line=1):
    groups = dict(db.execute('SELECT id,group_id FROM credits WHERE id IN (?,?)', (left, right)))
    db.execute('INSERT INTO context_relationships VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
               (identifier, groups[left], groups[right], left, right, relation, 'Retained direct source support',
                '[]', 'development-fixture', str(run), line, 'cache:' + identifier, 'fixture-job', 'validated_batch'))


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
            create_context_table(db)
            add_context(db, 'existing-question', 1, 1, 'insufficient_evidence')
            retained_context = db.execute('SELECT * FROM context_relationships').fetchone()
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
            self.assertEqual(tuple(db.execute("SELECT * FROM context_relationships").fetchone()), retained_context)
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

    def test_shared_person_http_combines_supported_aliases_artists_roles_and_exact_playback(self):
        directory = self.build([
            ('one', 'artist-a', [self.credit('one', 'artist-a', 'Pat Myers', 'taper', 'DATPAT')]),
            ('two', 'artist-b', [self.credit('two', 'artist-b', 'DATPAT', 'transferrer')]),
            ('namesake', 'artist-c', [self.credit('namesake', 'artist-c', 'Pat Myers', 'taper')]),
        ])
        retained = self.root / 'context'
        retained.mkdir()
        with closing(sqlite3.connect(self.database)) as db, db:
            identifiers = dict(db.execute('SELECT source_uuid,id FROM credits'))
            create_context_table(db)
            add_context(db, 'direct-alias', identifiers['one'], identifiers['two'], 'same_person', retained)
        def actor(source, name, artist, role):
            return dict(creditId=identifiers[source], sourceUuid=source, name=name, artistName=artist, roles=[role], evidence=[])
        question = dict(questionId='direct-alias', left=actor('one', 'Pat Myers', 'artist-a', 'taper'),
                        right=actor('two', 'DATPAT', 'artist-b', 'transferrer'))
        (retained / 'results.jsonl').write_text(json.dumps(dict(input=dict(questions=[question]),
            result=dict(cacheKey='cache:direct-alias', decisions=[dict(questionId='direct-alias', relation='same_person')]))))
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(directory))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        base = 'http://127.0.0.1:' + str(server.server_port)
        def get(path):
            with urlopen(base + path) as response:
                return json.load(response)
        self.assertTrue(get('/api/stats')['capabilities']['sharedPeople'])
        found = get('/api/people?q=DATPAT')
        self.assertEqual(found['personHypothesesTotal'], 1)
        person = found['personHypotheses'][0]
        self.assertEqual((person['recordingCount'], person['artistCount'], person['creditCount']), (2, 2, 2))
        self.assertFalse(person['acceptedIdentity'])
        self.assertEqual(set(person['names']), {'Pat Myers', 'DATPAT'})
        page = get('/api/shared-person?id=' + person['id'])
        self.assertEqual({r['sourceUuid'] for r in page['recordings']}, {'one', 'two'})
        self.assertEqual({r['role'] for r in page['roles']}, {'taper', 'transferrer'})
        self.assertEqual(get(page['support'][0]['evidenceUrl'])['question'], question | {
            'left': question['left'] | {'sourceEvidenceUrl': '/api/credit?id=' + str(identifiers['one']),
                'playableFilesUrl': 'http://files.test/?domain=file_contents&entityKey=source%3Aone'},
            'right': question['right'] | {'sourceEvidenceUrl': '/api/credit?id=' + str(identifiers['two']),
                'playableFilesUrl': 'http://files.test/?domain=file_contents&entityKey=source%3Atwo'}})
        for recording in page['recordings']:
            self.assertEqual(parse_qs(urlsplit(recording['playableFilesUrl']).query)['entityKey'], ['source:' + recording['sourceUuid']])
            self.assertEqual(get(recording['credits'][0]['sourceEvidenceUrl'])['retainedOutcome']['inputUuid'], recording['sourceUuid'])
        self.assertEqual(get('/api/shared-person?id=' + person['id'] + '&artist=artist-b')['recordings'][0]['sourceUuid'], 'two')
        self.assertEqual(get('/api/shared-person?id=' + person['id'] + '&role=taper')['recordings'][0]['sourceUuid'], 'one')
        self.assertEqual(len(get('/api/shared-person?id=' + person['id'] + '&limit=1&offset=1')['recordings']), 1)
        raw = get('/api/person?id=' + group_id(name_key('Pat Myers')))
        self.assertEqual(raw['recording_count'], 2)
        self.assertEqual(raw['personHypotheses'][0]['id'], person['id'])
        with self.assertRaises(HTTPError) as error:
            get('/api/shared-person?id=missing')
        self.assertEqual(error.exception.code, 404)
        error.exception.close()

    def test_shared_people_never_close_chains_or_absorb_namesakes_and_keep_conflicts_visible(self):
        directory = self.build([(source, 'artist-' + source, [self.credit(source, 'artist-' + source, name, 'taper')])
                                for source, name in [('a', 'Pat'), ('b', 'DATPAT'), ('c', 'Patrick'), ('d', 'P Myers'), ('e', 'Pat')]])
        with closing(sqlite3.connect(self.database)) as db, db:
            ids = dict(db.execute('SELECT source_uuid,id FROM credits'))
            create_context_table(db)
            for identifier, left, right in [('ab', 'a', 'b'), ('bc', 'b', 'c'), ('cd', 'c', 'd')]:
                add_context(db, identifier, ids[left], ids[right], 'same_person')
            add_context(db, 'unknown', ids['a'], ids['e'], 'insufficient_evidence')
        found = directory.search({})['personHypotheses']
        self.assertEqual(len(found), 2)
        self.assertTrue(all(person['creditCount'] == 3 for person in found))
        for person in found:
            detail = directory.shared_person(person['id'], {})
            self.assertNotIn('e', {recording['sourceUuid'] for recording in detail['recordings']})
            self.assertTrue(detail['overlappingHypotheses'])
        original_id = found[0]['id']
        # A new contradictory decision invalidates unsafe membership immediately;
        # stable anchor IDs remain addressable even when their search view changes.
        with closing(sqlite3.connect(self.database)) as db, db:
            add_context(db, 'ac-conflict', ids['a'], ids['c'], 'different_people')
        detail = directory.shared_person(original_id, {})
        self.assertTrue(detail['conflicts'])
        members = {recording['sourceUuid'] for recording in detail['recordings']}
        self.assertFalse({'a', 'c'} <= members)
        self.assertTrue(any(person['insufficientEvidence'] for person in
            (directory.shared_person(item['id'], {}) for item in directory.search({})['personHypotheses'])))

    def supported_pair(self):
        directory = self.build([
            ('one', 'artist-a', [self.credit('one', 'artist-a', 'Pat Myers', 'taper')]),
            ('two', 'artist-b', [self.credit('two', 'artist-b', 'DATPAT', 'transferrer')]),
        ])
        with closing(sqlite3.connect(self.database)) as db, db:
            ids = dict(db.execute('SELECT source_uuid,id FROM credits'))
            create_context_table(db)
            add_context(db, 'direct-alias', ids['one'], ids['two'], 'same_person')
        return directory, ids

    def test_shared_people_read_one_snapshot_when_credit_and_comparison_are_withdrawn_mid_read(self):
        directory, ids = self.supported_pair()
        with closing(sqlite3.connect(self.database)) as writer:
            writer.execute('PRAGMA journal_mode=WAL')
            read_credits = PersonHypotheses._read_credits
            def withdraw_before_credit_read(snapshot):
                with writer:
                    writer.execute('DELETE FROM context_relationships')
                    writer.execute('DELETE FROM credits WHERE id=?', (ids['two'],))
                return read_credits(snapshot)
            with patch.object(PersonHypotheses, '_read_credits', side_effect=withdraw_before_credit_read):
                prior = directory.search({})
            self.assertEqual(prior['personHypothesesTotal'], 1)
            self.assertEqual(prior['personHypotheses'][0]['creditCount'], 2)
            self.assertEqual(directory.search({})['personHypothesesTotal'], 0)

    def test_atomic_replacement_never_caches_old_people_under_the_new_file_generation(self):
        directory, _ = self.supported_pair()
        replacement = self.root / 'replacement.sqlite3'
        with closing(sqlite3.connect(self.database)) as source, closing(sqlite3.connect(replacement)) as target:
            source.backup(target)
            target.execute('DELETE FROM context_relationships')
            target.commit()
        shared_people = directory._shared_people
        def replace_after_snapshot_open(snapshot):
            replacement.replace(self.database)
            return shared_people(snapshot)
        with patch.object(directory, '_shared_people', side_effect=replace_after_snapshot_open):
            prior = directory.search({})
        self.assertEqual(prior['personHypothesesTotal'], 1)
        self.assertEqual(directory.search({})['personHypothesesTotal'], 0)

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


class TestSourceDirectoryRefresh(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.old_run = self.root / "old"
        self.run = self.root / "fresh"
        self.old_database = self.root / "old.sqlite3"
        self.database = self.root / "fresh.sqlite3"
        self.raw = {field: None for field in SourceDirectoryRefresh.FIELDS}
        self.raw.update(taper="Pat", description="Alex aka Raven; Sam")
        self.old = [self.outcome("one", "artist-a", "Pat"), self.outcome("two", "artist-b", "Changed")]
        self.write_run(self.old_run, self.old)
        DirectoryBuilder(self.old_run, self.old_database, minimum_free=0).build()
        with closing(sqlite3.connect(self.old_database)) as db, db:
            self.old_ids = dict(db.execute("SELECT source_uuid,id FROM credits"))
            create_context_table(db)
            add_context(db, 'keep', self.old_ids['one'], self.old_ids['one'], 'same_person')
            add_context(db, 'remove', self.old_ids['one'], self.old_ids['two'], 'different_people')
            db.execute("INSERT INTO metadata VALUES('peopleContext',?)", (json.dumps(dict(decisions=2)),))
            db.execute("CREATE TABLE context_questions(id TEXT PRIMARY KEY,question TEXT)")
            db.execute("INSERT INTO context_questions VALUES('frozen','original witness IDs')")
        self.new = [self.outcome("one", "artist-a", "Pat"), self.outcome("two", "artist-b", "Corrected")]
        self.write_run(self.run, self.new)

    def outcome(self, source, artist, name):
        evidence = dict(field="taper", quote=name, startCodePoint=0, endCodePoint=len(name))
        credit = dict(entityKey="credit:" + source, personEntityKey="person:" + source,
                      sourceUuid=source, artistUuid=artist, name=name, role="taper", evidence=evidence)
        return dict(domain="credits", inputKind="source_sample", inputKey="source_sample:" + source,
                    inputUuid=source, artistUuid=artist, artistName=artist, provider="catalog:unattributed",
                    method="shadow-people-v1", outcome="proposed", proposal=dict(credits=[credit]),
                    evidence=dict(originalInputs=dict(self.raw), creditExtraction=dict(credits=[dict(credit, actorFacts=dict(aliases=[]))])))

    def people(self, credit):
        source = credit["inputUuid"]
        reference = "ref:" + source
        actor = dict(entityKey="actor:" + source, name="Alex", sourceUuid=source,
                     artistUuid=credit["artistUuid"], sourceReferenceUuid=reference)
        assertion = dict(actorReferenceUuid=reference, assertionUuid="assertion:" + source, name="Alex",
                         aliases=[dict(wording="Raven", evidence=[dict(field="description", quote="Alex aka Raven")])],
                         context=dict(statement="Alex aka Raven; Sam"), relationships=[], unresolved=[])
        return dict(credit, domain="people", proposal=dict(people=[actor]),
                    evidence=dict(originalInputs=credit["evidence"]["originalInputs"], sourceActorAliases=dict(assertions=[assertion])))

    def write_run(self, run, credits, split=False):
        run.mkdir(exist_ok=True)
        counts = [dict(domain=domain, provider="catalog:unattributed", processed=len({c['inputUuid'] for c in credits})) for domain in ("credits", "people")]
        (run / "manifest.json").write_text(json.dumps(dict(state="completed", methods=dict(ShadowPeopleAdapter="shadow-people-v1", CatalogRowExtraction="credit-v5/alias-v4"), counts=counts)))
        for domain in ("credits", "people"):
            (run / domain).mkdir(exist_ok=True)
            for old in (run / domain).glob("*.jsonl.gz"):
                old.unlink()
            groups = [[row] for row in credits] if split else [credits]
            for index, rows in enumerate(groups):
                with gzip.open(run / domain / f"{index:03}.jsonl.gz", "wt") as stream:
                    for row in rows:
                        stream.write(json.dumps(self.people(row) if domain == "people" else row) + "\n")

    def refresh(self, **options):
        return SourceDirectoryRefresh(self.run, self.old_database, self.database, context_sealed=True,
                                      minimum_free=0, **options).apply()

    def test_exact_witnesses_keep_ids_and_context_new_credits_get_new_ids_and_original_owners_survive(self):
        # Native overlays and homes are independent populations copied unchanged.
        with closing(sqlite3.connect(self.old_database)) as db, db:
            db.execute("CREATE TABLE native_marker(value TEXT)")
            db.execute("INSERT INTO native_marker VALUES('retained namespace')")
            db.execute("INSERT INTO source_homes VALUES('one','show-one','1970-01-01','concert','Exact label','proposed','[]','captured','performances/original.jsonl.gz',17)")
            original_home = db.execute("SELECT * FROM source_homes WHERE source_uuid='one'").fetchone()
        original = self.old_database.read_bytes()
        summary = self.refresh()
        self.assertEqual((summary['keptCredits'], summary['removedCredits'], summary['insertedCredits']), (1, 1, 1))
        self.assertEqual(self.old_database.read_bytes(), original)
        with closing(sqlite3.connect(self.database)) as db, db:
            rows = dict(db.execute("SELECT source_uuid,id FROM credits"))
            self.assertEqual(rows['one'], self.old_ids['one'])
            self.assertGreater(rows['two'], max(self.old_ids.values()))
            kept = db.execute("SELECT origin_run,chunk,line FROM credits WHERE source_uuid='one'").fetchone()
            self.assertEqual(kept, (None, 'credits/000.jsonl.gz', 1))
            self.assertEqual(db.execute("SELECT id FROM context_relationships").fetchall(), [('keep',)])
            self.assertEqual(db.execute("SELECT question FROM context_questions").fetchone()[0], 'original witness IDs')
            self.assertEqual(db.execute("SELECT * FROM native_marker").fetchone()[0], 'retained namespace')
            self.assertEqual(db.execute("SELECT * FROM source_homes WHERE source_uuid='one'").fetchone(), original_home)
            metadata = dict(db.execute("SELECT key,value FROM metadata"))
            self.assertEqual(json.loads(metadata['sourceRun']), str(self.old_run))
            self.assertEqual(json.loads(metadata['sourceRefreshRun']), str(self.run))
        directory = PeopleDirectory(self.database, 'http://files.test', 'http://core.test')
        self.assertEqual(directory.stats()['credits'], 2)
        self.assertEqual(directory.stats()['sourceActors'], 2)

    def test_source_role_free_alias_http_search_paging_filter_and_original_evidence(self):
        self.refresh()
        directory = PeopleDirectory(self.database, 'http://files.test', 'http://core.test')
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(directory))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        base = 'http://127.0.0.1:' + str(server.server_port)
        def get(path):
            with urlopen(base + path) as response:
                return json.load(response)
        stats = get('/api/stats')
        self.assertTrue(stats['capabilities']['sourceActors'])
        self.assertNotIn('sourceRefreshProgress', stats)
        group = get('/api/people?q=Raven')['groups'][0]
        self.assertEqual((group['credit_count'], group['source_actor_count']), (0, 2))
        detail = get('/api/person?id=' + group['id'] + '&limit=1')
        self.assertEqual((detail['total'], detail['sourceTotal']), (0, 2))
        self.assertEqual(detail['roles'], [])
        first = detail['sourceObservations'][0]
        second = get('/api/person?id=' + group['id'] + '&limit=1&sourceOffset=1')['sourceObservations'][0]
        self.assertNotEqual(first['id'], second['id'])
        filtered = get('/api/person?id=' + group['id'] + '&sourceArtist=artist-a')
        self.assertEqual(filtered['sourceTotal'], 1)
        self.assertEqual(filtered['sourceObservations'][0]['source_uuid'], 'one')
        retained = get(first['sourceEvidenceUrl'])
        self.assertEqual(retained['originalInputs'], self.raw)
        self.assertEqual(retained['assertion']['context']['statement'], 'Alex aka Raven; Sam')
        self.assertFalse(retained['acceptedIdentity'])
        self.assertNotIn('role', first)
        self.assertNotIn('kind', first)
        self.assertEqual(parse_qs(urlsplit(first['playableFilesUrl']).query)['entityKey'], ['source:' + first['source_uuid']])
        with self.assertRaises(HTTPError) as error:
            get('/api/source-actor?id=missing')
        self.assertEqual(error.exception.code, 404)
        error.exception.close()
        # Kept credits continue to expose their original producer, guarded by
        # its own pin rather than trusting the same source UUID in changed bytes.
        kept_detail = get('/api/person?id=' + group_id(name_key('Pat')))
        kept_url = kept_detail['recordings'][0]['credits'][0]['sourceEvidenceUrl']
        self.assertEqual(get(kept_url)['credit']['id'], self.old_ids['one'])
        with gzip.open(self.old_run / 'credits/000.jsonl.gz', 'wt') as stream:
            stream.write(json.dumps(dict(self.old[0], proposal=dict(credits=[]))) + '\n')
        with self.assertRaises(HTTPError) as error:
            get(kept_url)
        self.assertEqual(error.exception.code, 400)
        error.exception.close()
        with gzip.open(self.run / 'people/000.jsonl.gz', 'at') as stream:
            stream.write('{}\n')
        with self.assertRaises(HTTPError) as error:
            get(first['sourceEvidenceUrl'])
        self.assertEqual(error.exception.code, 400)
        error.exception.close()

    def test_raw_input_change_or_missing_domain_never_publishes_or_changes_old_generation(self):
        original = self.old_database.read_bytes()
        self.new[0]['evidence']['originalInputs']['description'] = 'different evidence'
        self.write_run(self.run, self.new)
        with self.assertRaisesRegex(ValueError, 'original recording evidence'):
            self.refresh()
        self.assertFalse(self.database.exists())
        self.assertEqual(self.old_database.read_bytes(), original)
        # A new target can never silently turn missing evidence into a retraction.
        self.database = self.root / 'other.sqlite3'
        (self.run / 'people/000.jsonl.gz').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing source people'):
            self.refresh()
        self.assertFalse(self.database.exists())

    def test_atomic_checkpoint_resume_and_same_input_pins(self):
        self.write_run(self.run, self.new, split=True)
        original = SourceDirectoryRefresh._check_space
        def stop_after_one(refresh):
            original(refresh)
            if refresh.staging.exists():
                with closing(sqlite3.connect(refresh.staging)) as db, db:
                    if db.execute("SELECT 1 FROM sqlite_master WHERE name='refresh_done'").fetchone() and db.execute('SELECT count(*) FROM refresh_done').fetchone()[0] == 1:
                        raise RuntimeError('fixture pause')
        with patch.object(SourceDirectoryRefresh, '_check_space', stop_after_one):
            with self.assertRaisesRegex(RuntimeError, 'fixture pause'):
                self.refresh()
        self.assertFalse(self.database.exists())
        summary = self.refresh()
        self.assertEqual((summary['sources'], summary['keptCredits'], summary['insertedCredits']), (2, 1, 1))
        self.assertFalse(self.database.with_name(self.database.name + '.building').exists())

    def test_alias_removal_and_empty_credit_are_valid_complete_latest_retractions(self):
        self.new[1]['proposal']['credits'] = []
        self.write_run(self.run, self.new)
        # A replay's latest empty proposal must replace earlier people observations.
        with gzip.open(self.run / 'people/001.jsonl.gz', 'wt') as stream:
            stream.write(json.dumps(dict(self.people(self.new[0]), proposal=dict(people=[]), evidence=dict(originalInputs=self.raw, sourceActorAliases=dict(assertions=[])))) + '\n')
        summary = self.refresh()
        self.assertEqual((summary['keptCredits'], summary['removedCredits'], summary['insertedCredits']), (1, 1, 0))
        directory = PeopleDirectory(self.database, 'http://files.test', 'http://core.test')
        self.assertEqual(directory.stats()['sourceActors'], 1)
        self.assertEqual(directory.detail(group_id(name_key('Alex')), {})['sourceObservations'][0]['source_uuid'], 'two')

    def test_changed_actor_facts_or_model_basis_do_not_reuse_credit_identity(self):
        self.new[0]['evidence']['creditExtraction']['credits'][0]['actorFacts']['contacts'] = [dict(value='new@example.test')]
        self.write_run(self.run, self.new)
        summary = self.refresh()
        self.assertEqual(summary['keptCredits'], 0)
        with closing(sqlite3.connect(self.database)) as db, db:
            self.assertEqual(db.execute('SELECT count(*) FROM context_relationships').fetchone()[0], 0)

    def test_latest_replay_success_replaces_failure_but_latest_failure_blocks_publication(self):
        # Replay order is physical chunk/line order; failed historical rows are
        # retained evidence, not the surviving logical result.
        for domain in ("people", "credits"):
            path = self.run / domain / '000.jsonl.gz'
            path.rename(path.with_name('001.jsonl.gz'))
            row = self.people(self.new[0]) if domain == 'people' else self.new[0]
            with gzip.open(path, 'wt') as stream:
                stream.write(json.dumps(dict(row, outcome='failed', proposal=None)) + '\n')
        self.assertEqual(self.refresh()['sources'], 2)
        self.database = self.root / 'failed.sqlite3'
        with gzip.open(self.run / 'credits/002.jsonl.gz', 'wt') as stream:
            stream.write(json.dumps(dict(self.new[0], outcome='failed', proposal=None)) + '\n')
        with self.assertRaisesRegex(ValueError, 'Latest failed'):
            self.refresh()
        self.assertFalse(self.database.exists())

    def test_resume_rejects_changed_inputs_and_finalization_can_resume_without_replacement(self):
        original_finish = SourceDirectoryRefresh._finish
        def pause_after_finish(refresh, db, state, manifest):
            original_finish(refresh, db, state, manifest)
            raise RuntimeError('fixture finalization pause')
        with patch.object(SourceDirectoryRefresh, '_finish', pause_after_finish):
            with self.assertRaisesRegex(RuntimeError, 'fixture finalization pause'):
                self.refresh()
        self.assertFalse(self.database.exists())
        summary = self.refresh()
        self.assertEqual((summary['sources'], summary['keptCredits']), (2, 1))
        self.database = self.root / 'mismatch.sqlite3'
        with patch.object(SourceDirectoryRefresh, '_finish', pause_after_finish):
            with self.assertRaisesRegex(RuntimeError, 'fixture finalization pause'):
                self.refresh()
        with gzip.open(self.run / 'people/000.jsonl.gz', 'at') as stream:
            stream.write('{}\n')
        with self.assertRaisesRegex(ValueError, 'Resume inputs differ'):
            self.refresh()
        self.assertFalse(self.database.exists())

    def test_changed_producer_keys_preserve_unique_witness_but_duplicate_new_witnesses_do_not(self):
        # Identity is the exact witnessed occurrence, not a freshly generated
        # adapter key. Historical DB keys and context inputs remain untouched.
        self.new[0]['proposal']['credits'][0]['entityKey'] = 'new-producer-key'
        self.new[0]['proposal']['credits'][0]['personEntityKey'] = 'new-person-key'
        self.write_run(self.run, self.new)
        self.assertEqual(self.refresh()['keptCredits'], 1)
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT credit_key,person_key FROM credits WHERE id=?", (self.old_ids['one'],)).fetchone(), ('credit:one', 'person:one'))
        self.database = self.root / 'ambiguous.sqlite3'
        self.new[0]['proposal']['credits'].append(dict(self.new[0]['proposal']['credits'][0]))
        self.write_run(self.run, self.new)
        self.assertEqual(self.refresh()['keptCredits'], 0)
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute('SELECT count(*) FROM context_relationships').fetchone()[0], 0)

    def test_credit_id_high_water_mark_survives_multiple_generations(self):
        self.new[1]['proposal']['credits'] = []
        self.write_run(self.run, self.new)
        self.refresh()
        second_database = self.database
        self.old_database = second_database
        self.database = self.root / 'third.sqlite3'
        self.run = self.root / 'third-run'
        self.new[1] = self.outcome('two', 'artist-b', 'Later person')
        self.write_run(self.run, self.new)
        self.refresh()
        with closing(sqlite3.connect(self.database)) as db:
            self.assertGreater(db.execute("SELECT id FROM credits WHERE source_uuid='two'").fetchone()[0], self.old_ids['two'])

    def test_historical_null_credit_key_is_preserved_without_relabeling(self):
        with closing(sqlite3.connect(self.old_database)) as db, db:
            db.execute("UPDATE credits SET credit_key=NULL WHERE source_uuid='one'")
        self.assertEqual(self.refresh()['keptCredits'], 1)
        with closing(sqlite3.connect(self.database)) as db:
            self.assertEqual(db.execute("SELECT id,credit_key FROM credits WHERE source_uuid='one'").fetchone(), (self.old_ids['one'], None))
            self.assertEqual(db.execute("SELECT id FROM context_relationships").fetchall(), [('keep',)])

    def test_source_alias_selects_its_own_actor_when_credit_shares_exact_reference(self):
        row = self.people(self.new[0])
        alias_actor = row['proposal']['people'][0]
        alias_actor['name'] = 'Pat'
        alias_actor['aliases'] = []
        assertion = row['evidence']['sourceActorAliases']['assertions'][0]
        assertion.update(name='Pat', aliases=[], context=dict(statement='Pat'))
        credited_actor = dict(alias_actor, entityKey=self.new[0]['proposal']['credits'][0]['personEntityKey'])
        row['proposal']['people'].insert(0, credited_actor)
        with gzip.open(self.run / 'people/000.jsonl.gz', 'wt') as stream:
            stream.write(json.dumps(row) + '\n')
            stream.write(json.dumps(self.people(self.new[1])) + '\n')
        self.refresh()
        directory = PeopleDirectory(self.database, 'http://files.test', 'http://core.test')
        detail = directory.detail(group_id(name_key('Pat')), {})
        self.assertEqual(detail['credit_count'], 1)
        self.assertEqual(detail['sourceTotal'], 1)
        observation = detail['sourceObservations'][0]
        self.assertEqual(observation['person_key'], alias_actor['entityKey'])
        self.assertNotEqual(observation['person_key'], credited_actor['entityKey'])
        self.assertNotIn('role', observation)
        self.assertEqual(directory.source_actor(observation['id'])['actor']['entityKey'], alias_actor['entityKey'])

    def test_cli_requires_seal_and_budget_pause_is_not_publication(self):
        import subprocess
        command = [sys.executable, str(Path(__file__).with_name('people_directory.py')), 'refresh-sources',
                   '--run', str(self.run), '--source-database', str(self.old_database), '--database', str(self.database),
                   '--minimum-free-bytes', '0']
        failed = subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn('Seal context attachment', failed.stderr)
        self.assertFalse(self.database.exists())
        failed = subprocess.run(command + ['--context-sealed', '--maximum-bytes', '1'], capture_output=True, text=True)
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn('combined run and new-generation', failed.stderr)
        self.assertFalse(self.database.exists())
        completed = subprocess.run(command + ['--context-sealed'], capture_output=True, text=True)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertEqual(json.loads(completed.stdout.splitlines()[-1])['sources'], 2)

if __name__ == "__main__":
    unittest.main()
