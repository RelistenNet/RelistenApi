"""Opt-in execution of the fixed domain SQL against an owned disposable local database."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from uuid import uuid4

import capture_domain_metadata_frame as capture
from domain_metadata_frame_validation import DomainMetadataFrameValidator, EXPECTED_COLUMNS
from test_capture_domain_metadata_frame import stable


@unittest.skipUnless(os.environ.get("RELISTEN_METADATA_FRAME_POSTGRES_TESTS") == "1",
                     "Set RELISTEN_METADATA_FRAME_POSTGRES_TESTS=1 for disposable local PostgreSQL coverage.")
class TestDomainMetadataFramePostgres(unittest.TestCase):
    def setUp(self):
        self.database = "relisten_domain_frame_test_" + uuid4().hex
        self.environment = {key: value for key, value in os.environ.items() if not key.startswith("PG")}
        self.environment.update(PGPASSWORD="local_dev_password", PGCLIENTENCODING="UTF8",
                                PGCONNECT_TIMEOUT="5", PGSSLMODE="disable", PGOPTIONS="")
        self.psql("postgres", 'CREATE DATABASE "' + self.database + '";')
        self.addCleanup(self.drop_database)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.schema = {}
        for table, column, data_type, nullable in EXPECTED_COLUMNS:
            self.schema.setdefault(table, []).append((column, data_type, nullable))
        declarations = []
        for table, columns in self.schema.items():
            fields = [f'"{column}" {data_type}' + (" NOT NULL" if nullable == "NO" else "")
                      + (" PRIMARY KEY" if column == "id" else "")
                      for column, data_type, nullable in columns]
            declarations.append(f'CREATE TABLE "{table}"(' + ",".join(fields) + ");")
        # No FK constraints or pair uniqueness in this synthetic catalog: the actual
        # fixed LEFT JOINs and multiplicity aggregation must preserve bad old pointers.
        self.psql(self.database, "\n".join(declarations))
        for table, overrides in (
            ("artists", {"id": 1}), ("artists", {"id": 2}),
            ("sources", {"id": 10, "artist_id": 1, "show_id": 20, "venue_id": 30}),
            ("sources", {"id": 11, "artist_id": 2, "show_id": None, "venue_id": 999}),
            ("shows", {"id": 20, "artist_id": 1, "venue_id": 30}),
            ("venues", {"id": 30, "artist_id": 1, "name": None, "location": "e\u0301🐢", "past_names": ""}),
            ("source_sets", {"id": 40, "source_id": 11}),
            ("source_sets", {"id": 41, "source_id": 999}),
            ("source_tracks", {"id": 50, "artist_id": 1, "source_id": 10, "source_set_id": 40}),
            ("source_tracks", {"id": 51, "artist_id": None, "source_id": 999, "source_set_id": 999, "is_orphaned": True}),
            ("source_tracks", {"id": 52, "artist_id": None, "source_id": None, "source_set_id": 41}),
            ("setlist_shows", {"id": 60, "artist_id": 1, "venue_id": 30}),
            ("setlist_shows", {"id": 61, "artist_id": 2, "venue_id": 999}),
            ("setlist_songs", {"id": 70, "artist_id": 2}),
            ("setlist_songs", {"id": 71, "artist_id": 1}),
            ("setlist_songs_plays", {"played_setlist_song_id": 70, "played_setlist_show_id": 60}),
            ("setlist_songs_plays", {"played_setlist_song_id": 70, "played_setlist_show_id": 60}),
            ("setlist_songs_plays", {"played_setlist_song_id": 71, "played_setlist_show_id": 999}),
            ("setlist_songs_plays", {"played_setlist_song_id": 999, "played_setlist_show_id": 61}),
        ):
            self.insert(table, overrides)

    def command(self, database):
        return ["psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-h", "127.0.0.1",
                "-p", "15432", "-U", "relisten", "-d", database]

    def psql(self, database, sql):
        return subprocess.run(self.command(database), input=sql.encode("utf-8"), env=self.environment,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True, timeout=60).stdout

    def insert(self, table, overrides):
        values = []
        for column, data_type, nullable in self.schema[table]:
            if column in overrides:
                value = overrides[column]
            elif column == "uuid":
                value = stable(overrides["id"])
            elif nullable == "YES":
                value = None
            elif data_type == "text":
                value = "SYNTHETIC_PRIVATE_" + column + "_https://example.invalid/contact"
            elif data_type == "timestamp with time zone":
                value = "2026-10-18T00:00:00+00:00"
            elif data_type == "date":
                value = "1970-01-01"
            elif data_type == "boolean":
                value = False
            else:
                value = 0
            values.append("NULL" if value is None else str(value).lower() if type(value) in (bool, int)
                          else "'" + str(value).replace("'", "''") + "'")
        self.psql(self.database, 'INSERT INTO "' + table + '" VALUES(' + ",".join(values) + ");")

    def drop_database(self):
        self.assertTrue(self.database.startswith("relisten_domain_frame_test_"))
        self.psql("postgres", 'DROP DATABASE "' + self.database + '" WITH (FORCE);')

    def test_actual_sql_preserves_complete_graph_profiles_and_unordered_multiset(self):
        query = self.directory / "executed.sql"
        query.write_bytes(capture.SQL_PATH.read_bytes())
        validator = DomainMetadataFrameValidator(self.directory / "validation.sqlite", require_replica=False)
        process = None
        try:
            process = subprocess.Popen(self.command(self.database) + ["-f", str(query)],
                                       env=self.environment, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            frame = self.directory / "frame.partial.jsonl"
            with frame.open("xb") as output:
                stream_metadata = capture._consume_lines(process.stdout, output, validator)
            self.assertEqual(0, process.wait(timeout=60), "psql must succeed after COMMIT")
            summary = validator.finish()
        finally:
            if process is not None:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=10)
                process.stdout.close()
            validator.close()
        raw = frame.read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), stream_metadata["sha256"])
        for sentinel in (b"SYNTHETIC_PRIVATE", b"example.invalid", "e\u0301🐢".encode("utf-8")):
            self.assertNotIn(sentinel, raw)
        self.assertEqual({"artists": 2, "sources": 2, "shows": 1, "venues": 1, "source_sets": 2,
                          "source_tracks": 3, "setlist_shows": 2, "setlist_songs": 2,
                          "setlist_songs_plays": 4, "setlist_song_play_groups": 3}, summary["sameSnapshotCounts"])
        diagnostics = summary["joinDiagnostics"]
        self.assertEqual(1, diagnostics["tracks_direct_and_set_source_disagree"])
        self.assertEqual(1, diagnostics["source_track_source_id_dangling"])
        self.assertEqual(1, diagnostics["source_track_source_set_id_dangling"])
        self.assertEqual(2, diagnostics["play_rows_song_and_show_artist_disagree"])
        rows = [json.loads(line) for line in raw.splitlines()]
        venue = next(row for row in rows if row["kind"] == "venue")
        self.assertEqual({"is_null": False, "characters": 3, "utf8_bytes": 7,
                          "md5_utf8": hashlib.md5("e\u0301🐢".encode("utf-8")).hexdigest()}, venue["text_profiles"]["location"])
        self.assertTrue(venue["text_profiles"]["name"]["is_null"])
        self.assertEqual(0, venue["text_profiles"]["past_names"]["characters"])
        track = next(row for row in rows if row["kind"] == "source_track" and row["id"] == 50)
        self.assertEqual((stable(10), stable(40)), (track["source_uuid"], track["source_set_uuid"]))
        self.assertEqual(2, next(row for row in rows if row["kind"] == "setlist_song_play")["multiplicity"])


if __name__ == "__main__":
    unittest.main()
