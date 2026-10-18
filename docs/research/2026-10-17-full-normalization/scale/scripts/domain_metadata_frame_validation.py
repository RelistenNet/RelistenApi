"""Validate one full-domain metadata stream and reconcile its current parent tuples.

SQLite holds native coordinates, never source text. Text fields have closed metadata
profiles. Duplicate play associations retain multiplicity rather than occurrence IDs.
"""
from collections import Counter
import json
from pathlib import Path
import re
import sqlite3

from metadata_frame_validation import (
    EXPECTED_COLUMNS as PARENT_COLUMNS,
    FrameCaptureError,
    closed,
    integer,
    invalid_constant,
    real,
    require,
    timestamp,
    unique_object,
    uuid,
)

VERSION = "full-domain-metadata-frame-v1"
MAXIMUM_LINE_BYTES = 65536
MAXIMUM_ROWS = 20_000_000
MAXIMUM_INDEX_BYTES = 8 * 1024**3
TABLES = (
    "artists", "sources", "shows", "venues", "source_sets", "source_tracks",
    "setlist_shows", "setlist_songs", "setlist_songs_plays",
)
KINDS = (
    "metadata_snapshot", "metadata_counts", "metadata_columns", "artist", "source",
    "show", "venue", "source_set", "source_track", "setlist_show", "setlist_song",
    "setlist_song_play", "snapshot_complete",
)
ROSTER_TABLES = dict(zip(KINDS[3:-1], TABLES))
PROFILE_FIELDS = {
    "venue": ("name", "location", "upstream_identifier", "slug", "past_names"),
    "source_set": ("name",),
    "source_track": ("title", "slug", "mp3_url", "mp3_md5", "flac_md5", "flac_url"),
    "setlist_show": ("upstream_identifier",),
    "setlist_song": ("name", "slug", "upstream_identifier"),
}
# Exact deployed columns, not a wildcard: listener/user/play-history tables are absent.
DOMAIN_COLUMNS = (
    ("venues", "id", "bigint", "NO"),
    ("venues", "artist_id", "integer", "YES"),
    ("venues", "latitude", "double precision", "YES"),
    ("venues", "longitude", "double precision", "YES"),
    ("venues", "name", "text", "YES"),
    ("venues", "location", "text", "YES"),
    ("venues", "upstream_identifier", "text", "YES"),
    ("venues", "created_at", "timestamp with time zone", "NO"),
    ("venues", "updated_at", "timestamp with time zone", "NO"),
    ("venues", "slug", "text", "YES"),
    ("venues", "past_names", "text", "YES"),
    ("venues", "uuid", "uuid", "NO"),
    ("source_sets", "id", "bigint", "NO"),
    ("source_sets", "source_id", "bigint", "NO"),
    ("source_sets", "index", "integer", "NO"),
    ("source_sets", "is_encore", "boolean", "NO"),
    ("source_sets", "name", "text", "NO"),
    ("source_sets", "created_at", "timestamp with time zone", "NO"),
    ("source_sets", "updated_at", "timestamp with time zone", "NO"),
    ("source_sets", "uuid", "uuid", "NO"),
    ("source_tracks", "id", "bigint", "NO"),
    ("source_tracks", "source_id", "bigint", "YES"),
    ("source_tracks", "source_set_id", "bigint", "YES"),
    ("source_tracks", "track_position", "integer", "NO"),
    ("source_tracks", "duration", "integer", "YES"),
    ("source_tracks", "title", "text", "NO"),
    ("source_tracks", "slug", "text", "NO"),
    ("source_tracks", "mp3_url", "text", "YES"),
    ("source_tracks", "mp3_md5", "text", "YES"),
    ("source_tracks", "created_at", "timestamp with time zone", "NO"),
    ("source_tracks", "updated_at", "timestamp with time zone", "NO"),
    ("source_tracks", "flac_md5", "text", "YES"),
    ("source_tracks", "flac_url", "text", "YES"),
    ("source_tracks", "artist_id", "integer", "YES"),
    ("source_tracks", "uuid", "uuid", "NO"),
    ("source_tracks", "is_orphaned", "boolean", "NO"),
    ("setlist_shows", "id", "bigint", "NO"),
    ("setlist_shows", "artist_id", "integer", "NO"),
    ("setlist_shows", "venue_id", "bigint", "NO"),
    ("setlist_shows", "upstream_identifier", "text", "NO"),
    ("setlist_shows", "date", "date", "NO"),
    ("setlist_shows", "created_at", "timestamp with time zone", "NO"),
    ("setlist_shows", "updated_at", "timestamp with time zone", "NO"),
    ("setlist_shows", "tour_id", "bigint", "YES"),
    ("setlist_shows", "era_id", "bigint", "YES"),
    ("setlist_shows", "uuid", "uuid", "NO"),
    ("setlist_songs", "id", "bigint", "NO"),
    ("setlist_songs", "artist_id", "integer", "NO"),
    ("setlist_songs", "name", "text", "NO"),
    ("setlist_songs", "slug", "text", "NO"),
    ("setlist_songs", "created_at", "timestamp with time zone", "NO"),
    ("setlist_songs", "updated_at", "timestamp with time zone", "NO"),
    ("setlist_songs", "upstream_identifier", "text", "NO"),
    ("setlist_songs", "uuid", "uuid", "NO"),
    ("setlist_songs_plays", "played_setlist_song_id", "bigint", "NO"),
    ("setlist_songs_plays", "played_setlist_show_id", "bigint", "NO"),
)
# Column order within each table matters; the SQL emits tables alphabetically.
EXPECTED_COLUMNS = tuple(
    column for table in sorted(TABLES)
    for column in (*PARENT_COLUMNS, *DOMAIN_COLUMNS) if column[0] == table
)
NULLABLE = {(table, column): nullable == "YES" for table, column, _, nullable in EXPECTED_COLUMNS}
FIELDS = {
    "metadata_snapshot": "kind version captured_at snapshot read_only isolation server_version server_encoding client_encoding in_recovery replay_lsn last_replayed_transaction_at provider_attribution date_precision knowledge_clock text_profile_algorithm content_derived_metadata_computed server_source_processing semantic_parsing performance_interpretation play_association_interpretation exposure_history",
    "metadata_counts": "kind " + " ".join(TABLES) + " setlist_song_play_groups",
    "metadata_columns": "kind table_name column_name data_type is_nullable",
    "artist": "kind id uuid created_at updated_at",
    "source": "kind id uuid artist_id artist_uuid show_id show_uuid venue_id venue_uuid created_at updated_at",
    "show": "kind id uuid artist_id artist_uuid venue_id venue_uuid catalog_sort_date event_date_precision created_at updated_at",
    "venue": "kind id uuid artist_id artist_uuid latitude longitude created_at updated_at text_profiles",
    "source_set": "kind id uuid source_id source_uuid index is_encore created_at updated_at text_profiles",
    "source_track": "kind id uuid artist_id artist_uuid source_id source_uuid source_set_id source_set_uuid track_position duration is_orphaned created_at updated_at text_profiles",
    "setlist_show": "kind id uuid artist_id artist_uuid venue_id venue_uuid tour_id era_id catalog_sort_date event_date_precision created_at updated_at text_profiles",
    "setlist_song": "kind id uuid artist_id artist_uuid created_at updated_at text_profiles",
    "setlist_song_play": "kind played_setlist_song_id played_setlist_song_uuid played_setlist_show_id played_setlist_show_uuid multiplicity",
    "snapshot_complete": "kind completed_at",
}
# Store only the coordinates required for independent same-snapshot reconciliation.
INDEX_FIELDS = {
    "artist": ("id", "uuid"),
    "source": ("id", "uuid", "artist_id", "artist_uuid", "show_id", "show_uuid", "venue_id", "venue_uuid"),
    "show": ("id", "uuid", "artist_id", "artist_uuid", "venue_id", "venue_uuid"),
    "venue": ("id", "uuid", "artist_id", "artist_uuid"),
    "source_set": ("id", "uuid", "source_id", "source_uuid"),
    "source_track": ("id", "uuid", "artist_id", "artist_uuid", "source_id", "source_uuid", "source_set_id", "source_set_uuid", "is_orphaned"),
    "setlist_show": ("id", "uuid", "artist_id", "artist_uuid", "venue_id", "venue_uuid"),
    "setlist_song": ("id", "uuid", "artist_id", "artist_uuid"),
    "setlist_song_play": ("played_setlist_song_id", "played_setlist_song_uuid", "played_setlist_show_id", "played_setlist_show_uuid", "multiplicity"),
}
PARENTS = {
    "source": (("artist", "artist_id", "artist_uuid"), ("show", "show_id", "show_uuid"), ("venue", "venue_id", "venue_uuid")),
    "show": (("artist", "artist_id", "artist_uuid"), ("venue", "venue_id", "venue_uuid")),
    "venue": (("artist", "artist_id", "artist_uuid"),),
    "source_set": (("source", "source_id", "source_uuid"),),
    "source_track": (("artist", "artist_id", "artist_uuid"), ("source", "source_id", "source_uuid"), ("source_set", "source_set_id", "source_set_uuid")),
    "setlist_show": (("artist", "artist_id", "artist_uuid"), ("venue", "venue_id", "venue_uuid")),
    "setlist_song": (("artist", "artist_id", "artist_uuid"),),
    "setlist_song_play": (("setlist_song", "played_setlist_song_id", "played_setlist_song_uuid"), ("setlist_show", "played_setlist_show_id", "played_setlist_show_uuid")),
}


def catalog_date(value):
    require(type(value) is str and len(value) <= 32 and (
        value in ("infinity", "-infinity") or re.fullmatch(
            r"[0-9]{4,7}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])(?: BC)?", value
        ) is not None
    ), "invalid_catalog_date")


def validate_profiles(kind, value):
    closed(value, " ".join(PROFILE_FIELDS[kind]))
    for field, profile in value.items():
        closed(profile, "is_null characters utf8_bytes md5_utf8")
        require(type(profile["is_null"]) is bool, "invalid_text_profile")
        if profile["is_null"]:
            require(NULLABLE[(ROSTER_TABLES[kind], field)] and all(
                profile[key] is None for key in ("characters", "utf8_bytes", "md5_utf8")
            ), "invalid_text_profile")
        else:
            characters, size = profile["characters"], profile["utf8_bytes"]
            integer(characters, nonnegative=True)
            integer(size, nonnegative=True)
            require(characters <= size <= characters * 4, "invalid_text_profile")
            require(type(profile["md5_utf8"]) is str and re.fullmatch(r"[0-9a-f]{32}", profile["md5_utf8"]), "invalid_text_profile")
            require(characters != 0 or profile["md5_utf8"] == "d41d8cd98f00b204e9800998ecf8427e", "invalid_text_profile")


class DomainMetadataFrameValidator:
    """Bound memory while retaining every native parent pointer and play multiplicity."""

    def __init__(self, index_path: Path, require_replica=True):
        require(type(require_replica) is bool, "invalid_configuration")
        self.connection = None
        self.finished = self.failed = False
        self.phase = -1
        self.rows = Counter()
        self.last_keys = {}
        self.column_position = 0
        self.header = self.counts = None
        self.require_replica = require_replica
        try:
            with Path(index_path).open("xb"):
                pass
            self.connection = sqlite3.connect(index_path, isolation_level=None)
            self.connection.execute("PRAGMA page_size=4096")
            self.connection.execute(f"PRAGMA max_page_count={MAXIMUM_INDEX_BYTES // 4096}")
            self.connection.execute("PRAGMA cache_size=-8192")
            self.connection.execute("PRAGMA temp_store=FILE")
            self.connection.execute("BEGIN")
            for kind, fields in INDEX_FIELDS.items():
                columns = [field + (" TEXT" if field.endswith("uuid") else " INTEGER") for field in fields]
                primary = "played_setlist_song_id,played_setlist_show_id" if kind == "setlist_song_play" else "id"
                self.connection.execute(f"CREATE TABLE {kind}({','.join(columns)},PRIMARY KEY({primary}))")
                if kind != "setlist_song_play":
                    self.connection.execute(f"CREATE INDEX {kind}_uuid ON {kind}(uuid)")
        except (OSError, sqlite3.Error):
            self.close()
            raise FrameCaptureError("index_initialization_failed") from None

    def accept(self, line):
        require(not self.finished and not self.failed, "capture_not_active")
        try:
            require(type(line) is bytes and len(line) <= MAXIMUM_LINE_BYTES
                    and line.endswith(b"\n") and line.count(b"\n") == 1, "invalid_line")
            require(sum(self.rows.values()) < MAXIMUM_ROWS, "metadata_row_capacity_exceeded")
            row = json.loads(line, object_pairs_hook=unique_object, parse_constant=invalid_constant)
            require(type(row) is dict and type(row.get("kind")) is str and row["kind"] in FIELDS, "invalid_row_kind")
            kind = row["kind"]
            closed(row, FIELDS[kind])
            phase = KINDS.index(kind)
            require(phase >= self.phase, "invalid_row_order")
            self._accept_row(kind, row)
            self.phase = phase
            self.rows[kind] += 1
        except FrameCaptureError:
            self.failed = True
            raise
        except (ValueError, UnicodeError, RecursionError, TypeError, sqlite3.Error, OverflowError):
            self.failed = True
            raise FrameCaptureError("invalid_metadata_stream") from None

    def _accept_row(self, kind, row):
        if kind == "metadata_snapshot":
            require(self.header is None and not self.rows, "duplicate_snapshot")
            self._accept_header(row)
        elif kind == "metadata_counts":
            require(self.header is not None and self.counts is None, "invalid_counts")
            for table in (*TABLES, "setlist_song_play_groups"):
                integer(row[table], nonnegative=True)
            require(row["setlist_song_play_groups"] <= row["setlist_songs_plays"], "invalid_play_counts")
            minimum_rows = sum(row[table] for table in TABLES[:-1]) + row["setlist_song_play_groups"] + len(EXPECTED_COLUMNS) + 3
            require(minimum_rows <= MAXIMUM_ROWS, "metadata_row_capacity_exceeded")
            self.counts = {key: value for key, value in row.items() if key != "kind"}
        elif kind == "metadata_columns":
            require(self.counts is not None and self.column_position < len(EXPECTED_COLUMNS), "invalid_columns")
            require(tuple(row[key] for key in ("table_name", "column_name", "data_type", "is_nullable"))
                    == EXPECTED_COLUMNS[self.column_position], "schema_changed")
            self.column_position += 1
        elif kind == "snapshot_complete":
            require(self.rows[kind] == 0, "duplicate_completion")
            timestamp(row["completed_at"])
        else:
            require(self.counts is not None and self.column_position == len(EXPECTED_COLUMNS), "columns_incomplete")
            self._accept_catalog_row(kind, row)

    def _accept_header(self, row):
        for key, expected in {
            "version": VERSION, "read_only": "on", "isolation": "repeatable read",
            "server_encoding": "UTF8", "client_encoding": "UTF8",
            "provider_attribution": "unavailable", "date_precision": "unavailable",
            "knowledge_clock": "unavailable", "text_profile_algorithm": "md5_utf8_not_identity_proof",
            "content_derived_metadata_computed": True,
            "server_source_processing": "nullness_length_md5_only", "semantic_parsing": False,
            "performance_interpretation": "unavailable", "play_association_interpretation": "unordered_multiset",
            "exposure_history": "unavailable",
        }.items():
            require(type(row[key]) is type(expected) and row[key] == expected, "invalid_snapshot_contract")
        require(type(row["in_recovery"]) is bool and (row["in_recovery"] or not self.require_replica), "replica_required")
        require(type(row["snapshot"]) is str and len(row["snapshot"]) <= 1024
                and re.fullmatch(r"[0-9]+:[0-9]+:(?:[0-9]+(?:,[0-9]+)*)?", row["snapshot"]), "invalid_snapshot")
        require(type(row["server_version"]) is str and re.fullmatch(r"[A-Za-z0-9 .()+~:_/-]{1,200}", row["server_version"]), "invalid_server_version")
        timestamp(row["captured_at"])
        timestamp(row["last_replayed_transaction_at"], nullable=True)
        require(row["replay_lsn"] is None or type(row["replay_lsn"]) is str
                and re.fullmatch(r"[0-9A-F]+/[0-9A-F]+", row["replay_lsn"]), "invalid_replay_lsn")
        self.header = row

    def _accept_catalog_row(self, kind, row):
        if kind == "setlist_song_play":
            for field in ("played_setlist_song_id", "played_setlist_show_id"):
                integer(row[field])
                uuid(row[field.removesuffix("id") + "uuid"], nullable=True, allow_zero=True)
            integer(row["multiplicity"], positive=True)
            key = (row["played_setlist_song_id"], row["played_setlist_show_id"])
        else:
            integer(row["id"])
            uuid(row["uuid"], allow_zero=True)
            key = (row["id"],)
            timestamp(row["created_at"])
            timestamp(row["updated_at"])
            for field in INDEX_FIELDS[kind]:
                if field in ("id", "uuid", "is_orphaned"):
                    continue
                if field.endswith("uuid"):
                    uuid(row[field], nullable=True, allow_zero=True)
                else:
                    # Raw NULL pointers and signed/zero IDs are preserved. Catalog joins
                    # distinguish NULL, a dangling pointer, and a resolved parent later.
                    integer(row[field], nullable=NULLABLE[(ROSTER_TABLES[kind], field)])
            if kind in PROFILE_FIELDS:
                validate_profiles(kind, row["text_profiles"])
            if kind in ("show", "setlist_show"):
                catalog_date(row["catalog_sort_date"])
                require(row["event_date_precision"] is None, "invented_event_precision")
            if kind == "setlist_show":
                integer(row["tour_id"], nullable=True)
                integer(row["era_id"], nullable=True)
            if kind == "source_set":
                integer(row["index"])
                require(type(row["is_encore"]) is bool, "invalid_boolean")
            if kind == "source_track":
                integer(row["track_position"])
                integer(row["duration"], nullable=True)
                require(type(row["is_orphaned"]) is bool, "invalid_boolean")
            if kind == "venue":
                real(row["latitude"], nullable=True)
                real(row["longitude"], nullable=True)
        require(kind not in self.last_keys or key > self.last_keys[kind], "duplicate_or_unordered_key")
        fields = INDEX_FIELDS[kind]
        self.connection.execute(f"INSERT INTO {kind} VALUES({','.join('?' for _ in fields)})", tuple(row[field] for field in fields))
        self.last_keys[kind] = key

    def finish(self):
        require(not self.failed and not self.finished, "capture_not_active")
        try:
            require(self.header is not None and self.counts is not None
                    and self.column_position == len(EXPECTED_COLUMNS)
                    and self.rows["snapshot_complete"] == 1, "incomplete_snapshot")
            for kind, table in ROSTER_TABLES.items():
                expected = self.counts["setlist_song_play_groups" if kind == "setlist_song_play" else table]
                require(self.rows[kind] == expected, "row_count_mismatch")
            # Python integers retain an exact total even if a malformed stream would
            # overflow SQLite's signed SUM. The input's declared native count bounds it.
            total = 0
            for (multiplicity,) in self.connection.execute("SELECT multiplicity FROM setlist_song_play"):
                total += multiplicity
                require(total <= self.counts["setlist_songs_plays"], "play_multiplicity_mismatch")
            require(total == self.counts["setlist_songs_plays"], "play_multiplicity_mismatch")
            diagnostics = self._reconcile_parents()
            diagnostics.update(self._relationship_diagnostics())
            self.connection.execute("COMMIT")
            self.finished = True
            return {"header": self.header, "sameSnapshotCounts": self.counts,
                    "rowsByKind": dict(self.rows), "joinDiagnostics": diagnostics}
        except FrameCaptureError:
            self.failed = True
            raise
        except (sqlite3.Error, OverflowError):
            self.failed = True
            raise FrameCaptureError("reconciliation_failed") from None

    def _reconcile_parents(self):
        diagnostics = {}
        for kind, parents in PARENTS.items():
            for parent, native, stable in parents:
                mismatch, absent, dangling = self.connection.execute(f"""
                    SELECT coalesce(sum(c.{stable} IS NOT p.uuid),0),
                        coalesce(sum(c.{native} IS NULL),0),
                        coalesce(sum(c.{native} IS NOT NULL AND p.id IS NULL),0)
                    FROM {kind} c LEFT JOIN {parent} p ON p.id=c.{native}
                """).fetchone()
                require(mismatch == 0, "parent_tuple_mismatch")
                diagnostics[f"{kind}_{native}_null"] = absent
                diagnostics[f"{kind}_{native}_dangling"] = dangling
        return diagnostics

    def _relationship_diagnostics(self):
        queries = {
            "tracks_direct_and_set_source_disagree": "SELECT count(*) FROM source_track t JOIN source_set st ON st.id=t.source_set_id WHERE t.source_id IS NOT NULL AND t.source_id<>st.source_id",
            "tracks_artist_and_direct_source_disagree": "SELECT count(*) FROM source_track t JOIN source s ON s.id=t.source_id WHERE t.artist_id IS NOT NULL AND t.artist_id<>s.artist_id",
            "tracks_artist_and_set_source_disagree": "SELECT count(*) FROM source_track t JOIN source_set st ON st.id=t.source_set_id JOIN source s ON s.id=st.source_id WHERE t.artist_id IS NOT NULL AND t.artist_id<>s.artist_id",
            "sources_artist_and_show_disagree": "SELECT count(*) FROM source s JOIN show sh ON sh.id=s.show_id WHERE s.artist_id<>sh.artist_id",
            "sources_venue_and_show_disagree": "SELECT count(*) FROM source s JOIN show sh ON sh.id=s.show_id WHERE s.venue_id IS NOT NULL AND sh.venue_id IS NOT NULL AND s.venue_id<>sh.venue_id",
            "play_groups_song_and_show_artist_disagree": "SELECT count(*) FROM setlist_song_play p JOIN setlist_song song ON song.id=p.played_setlist_song_id JOIN setlist_show sh ON sh.id=p.played_setlist_show_id WHERE song.artist_id<>sh.artist_id",
            "play_rows_song_and_show_artist_disagree": "SELECT coalesce(sum(p.multiplicity),0) FROM setlist_song_play p JOIN setlist_song song ON song.id=p.played_setlist_song_id JOIN setlist_show sh ON sh.id=p.played_setlist_show_id WHERE song.artist_id<>sh.artist_id",
            "tracks_stored_orphaned_true": "SELECT count(*) FROM source_track WHERE is_orphaned=1",
            "duplicate_play_groups": "SELECT count(*) FROM setlist_song_play WHERE multiplicity>1",
        }
        result = {name: self.connection.execute(query).fetchone()[0] for name, query in queries.items()}
        for kind in INDEX_FIELDS:
            if kind != "setlist_song_play":
                result[f"{kind}_duplicate_uuid_groups"] = self.connection.execute(
                    f"SELECT count(*) FROM (SELECT uuid FROM {kind} GROUP BY uuid HAVING count(*)>1)"
                ).fetchone()[0]
        return result

    def close(self):
        if self.connection is not None:
            if self.connection.in_transaction:
                self.connection.execute("ROLLBACK")
            self.connection.close()
            self.connection = None
