"""Public directory behavior: source ownership, inferred groups and evidence survive."""

import gzip
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import urlopen
from http.server import ThreadingHTTPServer

from people_directory import (
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
                "domain=file_contents&entity=source%3Asource-1"
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


if __name__ == "__main__":
    unittest.main()
