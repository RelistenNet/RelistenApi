"""Closed metadata stream validation and one-capture SQLite reconciliation.

No source values, transport, or manifest publication belong here. Exceptions carry
only fixed codes so an upstream parser/SQLite error cannot expose input content.
"""
from collections import Counter
import json
import math
from pathlib import Path
import re
import sqlite3
from uuid import UUID


VERSION = "full-catalog-metadata-frame-v1"
MAXIMUM_LINE_BYTES = 65536
MAXIMUM_ROWS = 5_000_000
NAMESPACES = {"catalog-2026-10-17", "local-catalog"}
# Ordered exactly as the deployed census/SQL information_schema projection.
EXPECTED_COLUMNS = (
    ("artists", "id", "integer", "NO"),
    ("artists", "musicbrainz_id", "text", "YES"),
    ("artists", "created_at", "timestamp with time zone", "NO"),
    ("artists", "updated_at", "timestamp with time zone", "NO"),
    ("artists", "name", "text", "NO"),
    ("artists", "featured", "integer", "NO"),
    ("artists", "slug", "text", "NO"),
    ("artists", "sort_name", "text", "YES"),
    ("artists", "uuid", "uuid", "NO"),
    ("shows", "id", "bigint", "NO"),
    ("shows", "artist_id", "integer", "NO"),
    ("shows", "tour_id", "bigint", "YES"),
    ("shows", "year_id", "bigint", "YES"),
    ("shows", "era_id", "bigint", "YES"),
    ("shows", "date", "date", "NO"),
    ("shows", "avg_rating", "real", "YES"),
    ("shows", "avg_duration", "real", "YES"),
    ("shows", "display_date", "text", "NO"),
    ("shows", "created_at", "timestamp with time zone", "NO"),
    ("shows", "updated_at", "timestamp with time zone", "NO"),
    ("shows", "venue_id", "integer", "YES"),
    ("shows", "uuid", "uuid", "NO"),
    ("sources", "id", "bigint", "NO"),
    ("sources", "show_id", "bigint", "YES"),
    ("sources", "is_soundboard", "boolean", "NO"),
    ("sources", "is_remaster", "boolean", "NO"),
    ("sources", "avg_rating", "real", "NO"),
    ("sources", "num_reviews", "integer", "NO"),
    ("sources", "upstream_identifier", "text", "NO"),
    ("sources", "has_jamcharts", "boolean", "NO"),
    ("sources", "description", "text", "YES"),
    ("sources", "taper_notes", "text", "YES"),
    ("sources", "source", "text", "YES"),
    ("sources", "taper", "text", "YES"),
    ("sources", "transferrer", "text", "YES"),
    ("sources", "lineage", "text", "YES"),
    ("sources", "created_at", "timestamp with time zone", "NO"),
    ("sources", "updated_at", "timestamp with time zone", "NO"),
    ("sources", "artist_id", "integer", "NO"),
    ("sources", "avg_rating_weighted", "real", "YES"),
    ("sources", "duration", "real", "YES"),
    ("sources", "venue_id", "bigint", "YES"),
    ("sources", "display_date", "text", "NO"),
    ("sources", "num_ratings", "integer", "YES"),
    ("sources", "flac_type", "integer", "NO"),
    ("sources", "uuid", "uuid", "NO"),
)

FIELDS = {
    "metadata_snapshot": "kind version captured_at snapshot read_only isolation server_version server_encoding client_encoding in_recovery replay_lsn last_replayed_transaction_at provider_attribution date_precision knowledge_clock text_profile_algorithm content_derived_metadata_computed server_source_processing semantic_parsing",
    "metadata_counts": "kind artists sources shows",
    "metadata_columns": "kind table_name column_name data_type is_nullable",
    "artist": "kind id uuid created_at updated_at featured source_count text_profiles",
    "source": "kind id uuid artist_id artist_uuid show_id show_uuid show_artist_id venue_id show_venue_id created_at updated_at is_soundboard is_remaster has_jamcharts avg_rating num_reviews avg_rating_weighted duration num_ratings stored_flac_type provider verified_audio_format knowledge_at input_revision_uuid input_generation text_profiles",
    "show": "kind id uuid artist_id artist_uuid venue_id tour_id year_id era_id catalog_sort_date event_date_precision avg_rating avg_duration created_at updated_at text_profiles",
    "track_parent_lookup": "kind requested coordinate lookup_status id uuid artist_id source_id source_uuid source_artist_id source_show_id source_show_uuid source_set_id source_set_uuid set_source_id set_source_uuid set_source_artist_id set_source_show_id set_source_show_uuid historical_binding_verified",
    "snapshot_complete": "kind completed_at",
}
ORDER = {kind: index for index, kind in enumerate(FIELDS)}
PROFILE_FIELDS = {
    "artist": ("name", "slug", "sort_name", "musicbrainz_id"),
    "source": ("upstream_identifier", "display_date", "description", "taper_notes", "source", "taper", "transferrer", "lineage"),
    "show": ("display_date",),
}
COLUMN_NULLABLE = {(table, column): nullable == "YES" for table, column, _type, nullable in EXPECTED_COLUMNS}
SEED_FIELDS = "catalog_namespace track_id track_uuid"
LOOKUP_VALUES = FIELDS["track_parent_lookup"].split()[4:-1]


class FrameCaptureError(ValueError):
    """A bounded safe token, never a parser message, SQL value, or input line."""

    def __init__(self, code):
        self.code = code if isinstance(code, str) and re.fullmatch(r"[a-z][a-z0-9_]{0,63}", code) else "capture_failed"
        super().__init__(self.code)


def require(condition, code):
    if not condition:
        raise FrameCaptureError(code)


def closed(value, fields):
    require(type(value) is dict and set(value) == set(fields.split()), "invalid_fields")


def integer(value, *, nullable=False, positive=False, nonnegative=False):
    if value is None and nullable:
        return
    require(type(value) is int and -(2**63) <= value < 2**63, "invalid_integer")
    require(not positive or value > 0, "invalid_integer")
    require(not nonnegative or value >= 0, "invalid_integer")


def uuid(value, *, nullable=False, allow_zero=False):
    if value is None and nullable:
        return
    require(type(value) is str and len(value) == 36, "invalid_uuid")
    try:
        parsed = UUID(value)
    except (ValueError, AttributeError):
        raise FrameCaptureError("invalid_uuid") from None
    require(str(parsed) == value and (allow_zero or parsed.int != 0), "invalid_uuid")


def timestamp(value, *, nullable=False):
    if value is None and nullable:
        return
    require(type(value) is str and len(value) <= 64, "invalid_timestamp")
    if value in ("infinity", "-infinity"):
        return
    # PostgreSQL catalog literals include BC and extended years, beyond Python's
    # datetime range. Check the closed lexical form without inventing date precision.
    require(re.fullmatch(
        r"[0-9]{4,7}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
        r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,6})?"
        r"(?:Z|[+-][0-9]{2}:[0-5][0-9])(?: BC)?", value
    ) is not None, "invalid_timestamp")


def real(value, *, nullable=False):
    if value is None and nullable:
        return
    if type(value) is str:
        require(value in ("NaN", "Infinity", "-Infinity"), "invalid_real")
    else:
        require(type(value) in (int, float) and math.isfinite(value), "invalid_real")


def seed_key(seed, coordinate):
    return json.dumps([seed["catalog_namespace"], seed["track_id"], seed["track_uuid"], coordinate], separators=(",", ":"))


def validate_seed(seed):
    closed(seed, SEED_FIELDS)
    require(seed["catalog_namespace"] in NAMESPACES, "invalid_seed")
    integer(seed["track_id"], nullable=True, positive=True)
    uuid(seed["track_uuid"], nullable=True)
    require(seed["track_id"] is not None or seed["track_uuid"] is not None, "invalid_seed")


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "duplicate_json_key")
        value[key] = item
    return value


def invalid_constant(_value):
    raise FrameCaptureError("invalid_json")


class MetadataFrameValidator:
    """Index only the tuples needed for full-roster and parent reconciliation."""

    def __init__(self, index_path: Path, seeds: list[dict], require_replica: bool = True):
        self.connection = None
        self.finished = False
        self.failed = False
        self.phase = -1
        self.column_position = 0
        self.rows = Counter()
        self.last_ids = {}
        self.header = None
        self.counts = None
        self.outcomes = Counter()
        self.lookup_diagnostics = Counter()
        self.require_replica = require_replica
        try:
            require(type(require_replica) is bool and type(seeds) is list, "invalid_configuration")
            require(not Path(index_path).exists(), "index_exists")
            with Path(index_path).open("xb"):
                pass
            self.connection = sqlite3.connect(index_path, isolation_level=None)
            self.connection.executescript("""
                BEGIN;
                CREATE TABLE artists(id INTEGER PRIMARY KEY,uuid TEXT NOT NULL UNIQUE,source_count INTEGER NOT NULL);
                CREATE TABLE sources(id INTEGER PRIMARY KEY,uuid TEXT NOT NULL UNIQUE,artist_id INTEGER NOT NULL,
                    artist_uuid TEXT,show_id INTEGER,show_uuid TEXT,show_artist_id INTEGER,venue_id INTEGER,show_venue_id INTEGER);
                CREATE INDEX sources_artist ON sources(artist_id);
                CREATE TABLE shows(id INTEGER PRIMARY KEY,uuid TEXT NOT NULL UNIQUE,artist_id INTEGER NOT NULL,
                    artist_uuid TEXT,venue_id INTEGER);
                CREATE TABLE expected_lookups(key TEXT PRIMARY KEY,seen INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE looked_tracks(id INTEGER PRIMARY KEY,uuid TEXT NOT NULL UNIQUE,artist_id INTEGER,source_id INTEGER,set_id INTEGER);
                CREATE TABLE looked_sets(id INTEGER PRIMARY KEY,uuid TEXT UNIQUE,source_id INTEGER);
            """)
            seen_seeds = set()
            for seed in seeds:
                validate_seed(seed)
                identity = seed_key(seed, "seed")
                require(identity not in seen_seeds, "duplicate_seed")
                seen_seeds.add(identity)
                for field, coordinate in (("track_id", "native_id"), ("track_uuid", "uuid")):
                    if seed[field] is not None:
                        self.connection.execute("INSERT INTO expected_lookups(key) VALUES(?)", (seed_key(seed, coordinate),))
        except FrameCaptureError:
            self.close()
            raise
        except (OSError, sqlite3.Error, ValueError, TypeError, OverflowError):
            self.close()
            raise FrameCaptureError("index_initialization_failed") from None

    def accept(self, line: bytes) -> dict:
        """Validate and index before the caller writes this exact physical line."""
        require(self.connection is not None and not self.finished and not self.failed, "validator_not_active")
        try:
            require(type(line) is bytes, "invalid_line")
            require(len(line) <= MAXIMUM_LINE_BYTES, "line_capacity")
            require(len(line) > 0 and line.endswith(b"\n") and b"\n" not in line[:-1], "invalid_line")
            require(sum(self.rows.values()) < MAXIMUM_ROWS, "row_capacity")
            row = json.loads(line.decode("utf-8"), object_pairs_hook=unique_object, parse_constant=invalid_constant)
            require(type(row) is dict and type(row.get("kind")) is str and row["kind"] in FIELDS, "invalid_row_kind")
            kind = row["kind"]
            closed(row, FIELDS[kind])
            self._advance(kind)
            self._accept_row(kind, row)
            self.rows[kind] += 1
            return row
        except FrameCaptureError:
            self.failed = True
            raise
        except (ValueError, TypeError, KeyError, OverflowError, RecursionError, sqlite3.Error):
            self.failed = True
            raise FrameCaptureError("invalid_metadata_row") from None

    def _advance(self, kind):
        phase = ORDER[kind]
        require(phase >= self.phase and self.phase != ORDER["snapshot_complete"], "invalid_row_order")
        if phase == 0:
            require(self.phase == -1, "invalid_row_order")
        elif phase == 1:
            require(self.phase == 0, "invalid_row_order")
        else:
            require(self.header is not None and self.counts is not None, "invalid_row_order")
            if phase > ORDER["metadata_columns"]:
                require(self.column_position == len(EXPECTED_COLUMNS), "incomplete_columns")
        self.phase = phase

    def _accept_row(self, kind, row):
        if kind == "metadata_snapshot":
            self._header(row)
        elif kind == "metadata_counts":
            for table in ("artists", "sources", "shows"):
                integer(row[table], nonnegative=True)
            self.counts = {table: row[table] for table in ("artists", "sources", "shows")}
        elif kind == "metadata_columns":
            require(self.column_position < len(EXPECTED_COLUMNS), "invalid_columns")
            require(tuple(row[field] for field in ("table_name", "column_name", "data_type", "is_nullable"))
                    == EXPECTED_COLUMNS[self.column_position], "invalid_columns")
            self.column_position += 1
        elif kind in PROFILE_FIELDS:
            self._roster(kind, row)
        elif kind == "track_parent_lookup":
            self._lookup(row)
        else:
            timestamp(row["completed_at"])

    def _header(self, row):
        expected = {
            "version": VERSION, "read_only": "on", "isolation": "repeatable read",
            "server_encoding": "UTF8", "client_encoding": "UTF8",
            "provider_attribution": "unavailable_on_sources", "date_precision": "unavailable",
            "knowledge_clock": "unavailable", "text_profile_algorithm": "md5_utf8_not_identity_proof",
            "server_source_processing": "nullness_length_md5_only",
        }
        require(all(row[key] == value for key, value in expected.items()), "invalid_header")
        require(row["content_derived_metadata_computed"] is True and row["semantic_parsing"] is False
                and type(row["in_recovery"]) is bool, "invalid_header")
        require(not self.require_replica or row["in_recovery"], "replica_required")
        require(type(row["snapshot"]) is str and re.fullmatch(r"\d+:\d+:(?:\d+(?:,\d+)*)?", row["snapshot"]) is not None, "invalid_header")
        require(type(row["server_version"]) is str and re.fullmatch(r"\d[\w .()+~\-]{0,199}", row["server_version"], re.ASCII) is not None, "invalid_header")
        require(row["replay_lsn"] is None or type(row["replay_lsn"]) is str
                and re.fullmatch(r"[0-9A-F]+/[0-9A-F]+", row["replay_lsn"]) is not None, "invalid_header")
        timestamp(row["captured_at"])
        timestamp(row["last_replayed_transaction_at"], nullable=True)
        self.header = dict(row)

    def _profiles(self, kind, profiles):
        require(type(profiles) is dict and set(profiles) == set(PROFILE_FIELDS[kind]), "invalid_profiles")
        for field, value in profiles.items():
            closed(value, "is_null characters utf8_bytes md5_utf8")
            require(type(value["is_null"]) is bool, "invalid_profiles")
            if value["is_null"]:
                require(all(value[key] is None for key in ("characters", "utf8_bytes", "md5_utf8")), "invalid_profiles")
            else:
                integer(value["characters"], nonnegative=True)
                integer(value["utf8_bytes"], nonnegative=True)
                require(value["characters"] <= value["utf8_bytes"] <= value["characters"] * 4, "invalid_profiles")
                require(type(value["md5_utf8"]) is str and re.fullmatch(r"[0-9a-f]{32}", value["md5_utf8"]) is not None, "invalid_profiles")
                require(value["characters"] != 0 or value["md5_utf8"] == "d41d8cd98f00b204e9800998ecf8427e", "invalid_profiles")
            table = {"artist": "artists", "source": "sources", "show": "shows"}[kind]
            nullable = COLUMN_NULLABLE[(table, field)]
            require(nullable or not value["is_null"], "invalid_profiles")

    def _roster(self, kind, row):
        # This is a catalog inventory, not accepted identity enrollment. The verified
        # schema permits signed native IDs and canonical zero UUIDs; preserve them.
        integer(row["id"])
        uuid(row["uuid"], allow_zero=True)
        previous_id = self.last_ids.get(kind)
        require(previous_id is None or row["id"] > previous_id, "invalid_roster_order")
        timestamp(row["created_at"])
        timestamp(row["updated_at"])
        self._profiles(kind, row["text_profiles"])
        if kind == "artist":
            integer(row["featured"])
            integer(row["source_count"], nonnegative=True)
            self.connection.execute("INSERT INTO artists VALUES(?,?,?)", (row["id"], row["uuid"], row["source_count"]))
        else:
            integer(row["artist_id"])
            uuid(row["artist_uuid"], nullable=True, allow_zero=True)
            integer(row["venue_id"], nullable=True)
            if kind == "source":
                self._source(row)
            else:
                self._show(row)
        self.last_ids[kind] = row["id"]

    def _source(self, row):
        for key in ("show_id", "show_artist_id", "show_venue_id"):
            integer(row[key], nullable=True)
        uuid(row["show_uuid"], nullable=True, allow_zero=True)
        for key in ("is_soundboard", "is_remaster", "has_jamcharts"):
            require(type(row[key]) is bool, "invalid_source_scalar")
        for key in ("num_reviews", "num_ratings", "stored_flac_type"):
            integer(row[key], nullable=key == "num_ratings")
        for key in ("avg_rating", "avg_rating_weighted", "duration"):
            real(row[key], nullable=key != "avg_rating")
        require(all(row[key] is None for key in ("provider", "verified_audio_format", "knowledge_at", "input_revision_uuid", "input_generation")), "invented_source_authority")
        self.connection.execute("INSERT INTO sources VALUES(?,?,?,?,?,?,?,?,?)", tuple(row[key] for key in
            ("id", "uuid", "artist_id", "artist_uuid", "show_id", "show_uuid", "show_artist_id", "venue_id", "show_venue_id")))

    def _show(self, row):
        for key in ("tour_id", "year_id", "era_id"):
            integer(row[key], nullable=True)
        require(row["event_date_precision"] is None, "invented_date_precision")
        value = row["catalog_sort_date"]
        # Preserve PostgreSQL's date domain, including infinity, BC and extended
        # years. The literal is a catalog sort value and supplies no event precision.
        require(type(value) is str and (value in ("infinity", "-infinity") or re.fullmatch(
            r"[0-9]{4,7}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])(?: BC)?", value
        ) is not None), "invalid_catalog_date")
        real(row["avg_rating"], nullable=True)
        real(row["avg_duration"], nullable=True)
        self.connection.execute("INSERT INTO shows VALUES(?,?,?,?,?)", tuple(row[key] for key in ("id", "uuid", "artist_id", "artist_uuid", "venue_id")))

    def _lookup(self, row):
        requested = row["requested"]
        validate_seed(requested)
        coordinate = row["coordinate"]
        require(coordinate in ("native_id", "uuid"), "invalid_coordinate")
        key = seed_key(requested, coordinate)
        result = self.connection.execute("UPDATE expected_lookups SET seen=1 WHERE key=? AND seen=0", (key,))
        require(result.rowcount == 1, "unexpected_or_duplicate_lookup")
        require(row["historical_binding_verified"] is None, "invented_historical_binding")
        for field in LOOKUP_VALUES:
            if field.endswith("uuid"):
                uuid(row[field], nullable=True, allow_zero=True)
            else:
                integer(row[field], nullable=True)
        status = row["lookup_status"]
        if requested["catalog_namespace"] != "catalog-2026-10-17":
            require(status == "different_catalog_not_queried" and all(row[field] is None for field in LOOKUP_VALUES), "invalid_namespace_result")
        elif status == "missing_current_row":
            require(all(row[field] is None for field in LOOKUP_VALUES), "invalid_missing_result")
        else:
            require(status == "found_current_row" and row["id"] is not None and row["uuid"] is not None, "invalid_lookup_status")
            require(row["id"] == requested["track_id"] if coordinate == "native_id" else row["uuid"] == requested["track_uuid"], "coordinate_mismatch")
            self._remember_tuple("looked_tracks", row["id"], (row["uuid"], row["artist_id"], row["source_id"], row["source_set_id"]))
            self._lookup_parents(row)
        self.outcomes[status] += 1

    def _remember_tuple(self, table, identity, values):
        previous = self.connection.execute(f"SELECT * FROM {table} WHERE id=?", (identity,)).fetchone()
        if previous is None:
            placeholders = ",".join("?" for _ in range(len(values) + 1))
            self.connection.execute(f"INSERT INTO {table} VALUES({placeholders})", (identity, *values))
        else:
            require(previous == (identity, *values), "inconsistent_lookup_tuple")

    def _lookup_parents(self, row):
        self._lookup_source(row, "source_id", "source_uuid", "source_artist_id", "source_show_id", "source_show_uuid")
        if row["source_set_id"] is None:
            require(all(row[key] is None for key in ("source_set_uuid", "set_source_id", "set_source_uuid", "set_source_artist_id", "set_source_show_id", "set_source_show_uuid")), "invalid_set_parent")
        else:
            self._remember_tuple("looked_sets", row["source_set_id"], (row["source_set_uuid"], row["set_source_id"]))
            if row["source_set_uuid"] is None:
                require(row["set_source_id"] is None, "invalid_set_parent")
            else:
                require(row["set_source_id"] is not None, "invalid_set_parent")
        self._lookup_source(row, "set_source_id", "set_source_uuid", "set_source_artist_id", "set_source_show_id", "set_source_show_uuid")
        if row["source_id"] != row["set_source_id"] and row["source_set_uuid"] is not None:
            self.lookup_diagnostics["lookup_direct_set_parent_disagreements"] += 1

    def _lookup_source(self, row, id_key, uuid_key, artist_key, show_key, show_uuid_key):
        source = self.connection.execute("SELECT uuid,artist_id,show_id,show_uuid FROM sources WHERE id=?", (row[id_key],)).fetchone()
        expected = source if source is not None else (None, None, None, None)
        require(tuple(row[key] for key in (uuid_key, artist_key, show_key, show_uuid_key)) == expected, "lookup_parent_mismatch")

    def finish(self) -> dict:
        require(self.connection is not None and not self.finished and not self.failed, "validator_not_active")
        try:
            require(self.phase == ORDER["snapshot_complete"] and self.rows["snapshot_complete"] == 1
                    and self.column_position == len(EXPECTED_COLUMNS), "incomplete_frame")
            for kind, table in (("artist", "artists"), ("source", "sources"), ("show", "shows")):
                require(self.rows[kind] == self.counts[table], "roster_count_mismatch")
            require(self.connection.execute("SELECT count(*) FROM expected_lookups WHERE seen=0").fetchone()[0] == 0, "missing_lookup")
            self._reconcile_joins()
            diagnostics = self._diagnostics()
            self.connection.commit()
            self.finished = True
            return {"header": self.header, "rowsByKind": {kind: self.rows[kind] for kind in FIELDS},
                    "sameSnapshotCounts": self.counts, "joinDiagnostics": diagnostics,
                    "seedOutcomeCounts": {status: self.outcomes[status] for status in
                        ("found_current_row", "missing_current_row", "different_catalog_not_queried")}}
        except FrameCaptureError:
            self.failed = True
            raise
        except (sqlite3.Error, ValueError, TypeError, OverflowError):
            self.failed = True
            raise FrameCaptureError("index_reconciliation_failed") from None

    def _reconcile_joins(self):
        queries = (
            "SELECT count(*) FROM sources s LEFT JOIN artists a ON a.id=s.artist_id WHERE s.artist_uuid IS NOT a.uuid",
            "SELECT count(*) FROM shows s LEFT JOIN artists a ON a.id=s.artist_id WHERE s.artist_uuid IS NOT a.uuid",
            "SELECT count(*) FROM sources s LEFT JOIN shows h ON h.id=s.show_id WHERE s.show_uuid IS NOT h.uuid OR s.show_artist_id IS NOT h.artist_id OR s.show_venue_id IS NOT h.venue_id",
            "SELECT count(*) FROM artists a LEFT JOIN (SELECT artist_id,count(*) n FROM sources GROUP BY artist_id) s ON s.artist_id=a.id WHERE a.source_count!=coalesce(s.n,0)",
        )
        for query in queries:
            require(self.connection.execute(query).fetchone()[0] == 0, "joined_tuple_mismatch")

    def _diagnostics(self):
        queries = {
            "artists_without_sources": "SELECT count(*) FROM artists WHERE source_count=0",
            "sources_without_artist": "SELECT count(*) FROM sources s LEFT JOIN artists a ON a.id=s.artist_id WHERE a.id IS NULL",
            "sources_null_show": "SELECT count(*) FROM sources WHERE show_id IS NULL",
            "sources_missing_show": "SELECT count(*) FROM sources s LEFT JOIN shows h ON h.id=s.show_id WHERE s.show_id IS NOT NULL AND h.id IS NULL",
            "sources_show_artist_disagreements": "SELECT count(*) FROM sources s JOIN shows h ON h.id=s.show_id WHERE s.artist_id!=h.artist_id",
            "shows_without_artist": "SELECT count(*) FROM shows s LEFT JOIN artists a ON a.id=s.artist_id WHERE a.id IS NULL",
        }
        result = {key: self.connection.execute(query).fetchone()[0] for key, query in queries.items()}
        result["lookup_direct_set_parent_disagreements"] = self.lookup_diagnostics["lookup_direct_set_parent_disagreements"]
        return result

    def close(self):
        if self.connection is not None:
            try:
                try:
                    if not self.finished:
                        self.connection.rollback()
                finally:
                    self.connection.close()
            except sqlite3.Error:
                raise FrameCaptureError("index_close_failed") from None
            finally:
                self.connection = None
