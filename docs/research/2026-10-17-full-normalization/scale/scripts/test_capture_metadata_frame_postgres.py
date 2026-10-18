"""Opt-in fixed SQL/validator integration on an exclusively owned local database."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid

from test_capture_metadata_frame import SCHEMA, ARTIST, ZERO_ARTIST, SOURCE, SHOW, TRACK, SET, seeds
import capture_metadata_frame as capture
from metadata_frame_validation import MetadataFrameValidator


@unittest.skipUnless(os.environ.get("RELISTEN_METADATA_FRAME_POSTGRES_TESTS") == "1",
                     "Set RELISTEN_METADATA_FRAME_POSTGRES_TESTS=1 for disposable local PostgreSQL coverage.")
class TestCaptureMetadataFramePostgres(unittest.TestCase):
    def setUp(self):
        self.database = "relisten_metadata_frame_test_" + uuid.uuid4().hex
        # Inherited PGHOSTADDR/PGSERVICE can redirect libpq despite the explicit -h.
        self.environment = {key: value for key, value in os.environ.items() if not key.startswith("PG")}
        self.environment.update(PGPASSWORD="local_dev_password", PGCLIENTENCODING="UTF8",
                                PGCONNECT_TIMEOUT="5", PGSSLMODE="disable", PGOPTIONS="")
        self.psql("postgres", 'CREATE DATABASE "' + self.database + '";')
        self.addCleanup(self.drop_database)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        declarations = []
        for table, columns in SCHEMA.items():
            fields = [f'"{name}" {data_type}' + (" NOT NULL" if nullable == "NO" else "")
                      + (" PRIMARY KEY" if name == "id" else " UNIQUE" if name == "uuid" else "")
                      for name, data_type, nullable in columns]
            declarations.append(f'CREATE TABLE "{table}" (' + ",".join(fields) + ");")
        self.psql(self.database, "\n".join(declarations) + f"""
            CREATE TABLE source_sets(id bigint PRIMARY KEY,uuid uuid UNIQUE,source_id bigint);
            CREATE TABLE source_tracks(id bigint PRIMARY KEY,uuid uuid UNIQUE,artist_id integer,
                source_id bigint,source_set_id bigint,title text,mp3_url text);
            INSERT INTO artists(id,uuid,created_at,updated_at,name,featured,slug,sort_name)
                VALUES(1,'{ARTIST}',now(),now(),'SYNTHETIC_PRIVATE_ARTIST',0,'private-slug',NULL),
                      (2,'{ZERO_ARTIST}',now(),now(),'SYNTHETIC_ZERO_ARTIST',0,'zero-slug','');
            INSERT INTO shows(id,uuid,artist_id,date,display_date,created_at,updated_at)
                VALUES(20,'{SHOW}',1,'1970-01-01','SYNTHETIC_PRIVATE_DATE_WORDING',now(),now());
            INSERT INTO sources(id,uuid,artist_id,show_id,is_soundboard,is_remaster,avg_rating,
                num_reviews,upstream_identifier,has_jamcharts,description,taper_notes,taper,
                display_date,flac_type,created_at,updated_at)
                VALUES(10,'{SOURCE}',1,20,false,false,0,0,'SYNTHETIC_PRIVATE_NATIVE_KEY',false,
                    'aé','SYNTHETIC_PRIVATE_CONTACT@example.invalid','',
                    'SYNTHETIC_SOURCE_DATE_WORDING',0,now(),now());
            INSERT INTO source_sets VALUES(30,'{SET}',10);
            INSERT INTO source_tracks VALUES(42,'{TRACK}',1,10,30,
                'SYNTHETIC_PRIVATE_TRACK_TITLE','https://example.invalid/private-media');
        """)

    def command(self, database):
        return ["psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-h", "127.0.0.1",
                "-p", "15432", "-U", "relisten", "-d", database]

    def psql(self, database, sql):
        return subprocess.run(self.command(database), input=sql.encode("utf-8"), env=self.environment,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True, timeout=60).stdout

    def drop_database(self):
        # The name is generated locally, never supplied by a caller or a provider.
        self.assertTrue(self.database.startswith("relisten_metadata_frame_test_"))
        self.psql("postgres", 'DROP DATABASE "' + self.database + '" WITH (FORCE);')

    def test_real_fixed_query_has_complete_rosters_qualified_profiles_and_parent_coordinates(self):
        lookup_seeds = seeds()
        query = capture.render_sql(capture.SQL_PATH.read_bytes(), lookup_seeds)
        query_path = self.directory / "executed.sql"
        query_path.write_bytes(query)
        frame_path = self.directory / "frame.partial.jsonl"
        validator = MetadataFrameValidator(self.directory / "validation.sqlite", lookup_seeds,
                                           require_replica=False)
        process = None
        try:
            process = subprocess.Popen(self.command(self.database) + ["-f", str(query_path)],
                                       env=self.environment, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            with frame_path.open("xb") as output:
                while True:
                    line = process.stdout.readline(65537)
                    if not line:
                        break
                    validator.accept(line)
                    output.write(line)
            self.assertEqual(0, process.wait(timeout=60), "psql must succeed after COMMIT")
            summary = validator.finish()
        finally:
            if process is not None:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=10)
                process.stdout.close()
            validator.close()
        self.assertEqual({"artists": 2, "sources": 1, "shows": 1}, summary["sameSnapshotCounts"])
        self.assertEqual(1, summary["joinDiagnostics"]["artists_without_sources"])
        self.assertEqual({"found_current_row": 2, "missing_current_row": 1,
                          "different_catalog_not_queried": 1}, summary["seedOutcomeCounts"])
        content = frame_path.read_bytes()
        for sentinel in (b"SYNTHETIC_", b"private-slug", b"zero-slug", b"example.invalid", b"a\xc3\xa9"):
            self.assertNotIn(sentinel, content)
        records = [json.loads(line) for line in content.splitlines()]
        artists = [r for r in records if r["kind"] == "artist"]
        self.assertEqual([1, 0], [r["source_count"] for r in artists])
        self.assertTrue(artists[0]["text_profiles"]["sort_name"]["is_null"])
        self.assertFalse(artists[1]["text_profiles"]["sort_name"]["is_null"])
        source = next(r for r in records if r["kind"] == "source")
        self.assertIsNone(source["provider"])
        self.assertIsNone(source["knowledge_at"])
        self.assertIsNone(next(r for r in records if r["kind"] == "show")["event_date_precision"])
        self.assertEqual({"is_null": False, "characters": 2, "utf8_bytes": 3,
                          "md5_utf8": hashlib.md5("aé".encode("utf-8")).hexdigest()},
                         source["text_profiles"]["description"])
        self.assertEqual(hashlib.md5(b"").hexdigest(), source["text_profiles"]["taper"]["md5_utf8"])
        self.assertIsNone(source["text_profiles"]["source"]["md5_utf8"])
        found = [r for r in records if r["kind"] == "track_parent_lookup" and r["lookup_status"] == "found_current_row"]
        self.assertEqual({"native_id", "uuid"}, {r["coordinate"] for r in found})
        for result in found:
            self.assertEqual((SOURCE, SHOW, SOURCE, SHOW), tuple(result[k] for k in
                             ("source_uuid", "source_show_uuid", "set_source_uuid", "set_source_show_uuid")))
            self.assertIsNone(result["historical_binding_verified"])
        local = next(r for r in records if r["kind"] == "track_parent_lookup"
                     and r["requested"]["catalog_namespace"] == "local-catalog")
        self.assertEqual("different_catalog_not_queried", local["lookup_status"])
        self.assertTrue(all(value is None for key, value in local.items()
                            if key not in {"kind", "requested", "coordinate", "lookup_status"}))


if __name__ == "__main__":
    unittest.main()
