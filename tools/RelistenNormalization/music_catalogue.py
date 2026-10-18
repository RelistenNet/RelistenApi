"""Small, rebuildable music browse projection over existing shadow reader indexes.

Only titles, scoped keys and immutable witness locators are retained. This module
does not index files, normalize music, or promote shadow proposals into identities.
Each source uses one read-only index snapshot; different runs have independent
snapshot times. Later sources replace the display witness for the same scoped key.
The default byte budget is 256 MiB; an explicit measured build may select up to
384 MiB. The caller supplies a higher free-space floor when concurrent jobs need it.
"""

from contextlib import closing
from dataclasses import dataclass
import gzip
import itertools
import json
from pathlib import Path
import shutil
import sqlite3
import time
from urllib.parse import urlsplit
import uuid

from shadow_reader import _has_domain, _pack_text, _unpack_text


VERSION = "music-catalogue-v1"
DEFAULT_MAXIMUM_BYTES = 256 * 1024**2
MAXIMUM_BYTES = 384 * 1024**2
MINIMUM_FREE_BYTES = int(4.5 * 1024**3)
MAXIMUM_LINE_BYTES = 16 * 1024**2
PREFIXES = (b"repertoire:", b"composition:musicbrainz:", b"setlist:", b"setlist_account:")


@dataclass(frozen=True)
class Source:
    scope: str
    run: Path
    reader_url: str
    index_path: Path | None = None


class CatalogueBuildPaused(RuntimeError):
    """The old completed projection remains usable after a bounded build refusal."""


def _kind(packed):
    # The runner's short hashed keys and UUID suffixes use the lossless t/u codecs.
    # Avoid decoding millions of unrelated file keys just to recognize a prefix.
    if packed[:1] in (b"t", b"u"):
        value = packed[1:]
    else:
        value = _unpack_text(packed).encode("utf-8")
    for index, prefix in enumerate(PREFIXES):
        if value.startswith(prefix):
            return min(index + 1, 3)
    return 0


class MusicCatalogueBuilder:
    """Build a replacement privately, then publish it with one atomic rename."""

    def __init__(self, sources, database, minimum_free_bytes=MINIMUM_FREE_BYTES,
                 maximum_bytes=DEFAULT_MAXIMUM_BYTES):
        self.sources = tuple(sources)
        self.database = Path(database).resolve()
        self.minimum_free_bytes = minimum_free_bytes
        if not 4096 <= maximum_bytes <= MAXIMUM_BYTES:
            raise ValueError("The projection cap must be between one SQLite page and 384 MiB.")
        self.maximum_bytes = maximum_bytes

    def build(self):
        if not self.sources:
            raise ValueError("At least one existing reader index is required.")
        self.database.parent.mkdir(parents=True, exist_ok=True)
        self._check_space()
        building = self.database.with_name(self.database.name + "." + uuid.uuid4().hex + ".building")
        building.touch(mode=0o600, exist_ok=False)
        try:
            with closing(sqlite3.connect(building)) as db:
                self._initialize(db)
                sources = []
                for source_id, source in enumerate(self.sources, 1):
                    self._check_space()
                    sources.append(self._select_source(db, source_id, source))
                    db.commit()
                    self._read_selected_chunks(db, source_id, sources[-1])
                    db.commit()
                db.execute("DROP TABLE pending")
                self._finish(db, sources)
                db.commit()
                if building.stat().st_size > self.maximum_bytes:
                    raise CatalogueBuildPaused("The music projection reached its byte cap.")
            self._check_space()
            building.replace(self.database)
        except sqlite3.OperationalError as error:
            if "full" in str(error).lower():
                raise CatalogueBuildPaused("The music projection reached its byte cap.") from error
            raise
        finally:
            building.unlink(missing_ok=True)
        return MusicCatalogue(self.database).stats()

    def _check_space(self):
        if shutil.disk_usage(self.database.parent).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Music projection paused to preserve the free-space floor.")

    def _initialize(self, db):
        db.execute("PRAGMA page_size=4096")
        db.execute("PRAGMA journal_mode=MEMORY")
        db.execute("PRAGMA temp_store=MEMORY")
        db.execute("PRAGMA max_page_count=" + str(self.maximum_bytes // 4096))
        db.executescript("""
            CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE artists(scope TEXT,uuid TEXT,name TEXT,PRIMARY KEY(scope,uuid)) WITHOUT ROWID;
            CREATE TABLE songs(scope TEXT,entity_key TEXT,artist_uuid TEXT,title TEXT,basis TEXT,
                legacy_uuid TEXT,status TEXT,witness_run INTEGER,witness_record INTEGER,
                witness_chunk TEXT,witness_line INTEGER,PRIMARY KEY(scope,entity_key)) WITHOUT ROWID;
            CREATE TABLE works(entity_key TEXT PRIMARY KEY,title TEXT,mbid TEXT,basis TEXT,status TEXT,
                witness_run INTEGER,witness_record INTEGER,witness_chunk TEXT,witness_line INTEGER) WITHOUT ROWID;
            CREATE TABLE work_songs(work_key TEXT,scope TEXT,song_key TEXT,
                PRIMARY KEY(work_key,scope,song_key)) WITHOUT ROWID;
            CREATE TABLE setlists(scope TEXT,entity_key TEXT,artist_uuid TEXT,label TEXT,literal_date TEXT,
                ordered INTEGER,status TEXT,witness_run INTEGER,witness_record INTEGER,
                witness_chunk TEXT,witness_line INTEGER,PRIMARY KEY(scope,entity_key)) WITHOUT ROWID;
            CREATE TABLE pending(kind INTEGER,entity_key TEXT,record_id INTEGER,domain TEXT,input_key BLOB,
                artist_uuid TEXT,chunk TEXT,line_no INTEGER,bytes INTEGER,mtime_ns INTEGER,
                PRIMARY KEY(kind,entity_key)) WITHOUT ROWID;
            CREATE INDEX pending_chunk ON pending(chunk,line_no);
            CREATE INDEX songs_artist ON songs(scope,artist_uuid);
            CREATE INDEX work_songs_song ON work_songs(scope,song_key);
            CREATE INDEX setlists_artist ON setlists(scope,artist_uuid);
        """)

    def _select_source(self, output, source_id, source):
        run = Path(source.run).resolve(strict=True)
        parsed = urlsplit(source.reader_url)
        if not source.scope or parsed.scheme not in ("http", "https") or not parsed.hostname:
            raise ValueError("Sources need an explicit scope and HTTP reader URL.")
        index = Path(source.index_path).resolve(strict=True) if source.index_path else next(
            (run / name for name in (".shadow-reader-v3.sqlite3", ".shadow-reader-v2.sqlite3")
             if (run / name).is_file()), None)
        if index is None or index == self.database:
            raise ValueError("An existing compact reader index is required; it is never created or upgraded.")
        with closing(sqlite3.connect(index.as_uri() + "?mode=ro", uri=True)) as db:
            db.row_factory = sqlite3.Row
            db.create_function("key_text", 1, _unpack_text, deterministic=True)
            db.create_function("has_domain", 2, _has_domain, deterministic=True)
            db.create_function("music_kind", 1, _kind, deterministic=True)
            db.execute("PRAGMA query_only=ON")
            db.execute("PRAGMA temp_store=MEMORY")
            db.execute("BEGIN")
            self._require_schema(db)
            info = {"id": source_id, "scope": source.scope, "run": str(run),
                    "reader_url": source.reader_url.rstrip("/"), "index": str(index),
                    "indexVersion": 3 if "v3" in index.name else 2,
                    "visibility": "indexed_current_records_only", "snapshot_at": time.time(),
                    "indexed_chunks": db.execute("SELECT count(*) FROM chunks WHERE state='indexed'").fetchone()[0],
                    "current_records": db.execute("SELECT count(*) FROM records").fetchone()[0]}
            self._select_artists(db, output, source.scope)
            self._select_entities(db, output)
            self._select_work_links(db, output)
            info["selected_entities"] = output.execute("SELECT count(*) FROM pending WHERE kind<>4").fetchone()[0]
            info["selected_records"] = output.execute("SELECT count(DISTINCT record_id) FROM pending").fetchone()[0]
            db.rollback()
        return info

    @staticmethod
    def _require_schema(db):
        required = {"entities": {"id", "data"}, "input_entities": {"input_id", "entity_id", "domains"},
                    "records": {"id", "domain_id", "input_id", "header_id", "chunk_id", "line_no"},
                    "chunks": {"id", "path", "state", "bytes", "mtime_ns"},
                    "headers": {"id", "artist_id", "kind_id"}, "artists": {"id", "uuid", "name"},
                    "input_keys": {"id", "data"}, "domains": {"id", "name"}, "terms": {"id", "value"}}
        for table, columns in required.items():
            actual = {row[1] for row in db.execute("PRAGMA table_info(" + table + ")")}
            if not columns <= actual:
                raise ValueError("The existing reader index lacks the compact music lookup schema.")

    @staticmethod
    def _select_artists(source, output, scope):
        rows = source.execute("""
            WITH current_artists AS MATERIALIZED (
                SELECT h.artist_id FROM records r JOIN headers h ON h.id=r.header_id
                JOIN chunks c ON c.id=r.chunk_id WHERE c.state='indexed'
                GROUP BY h.artist_id
            )
            SELECT a.uuid,a.name FROM current_artists current
            JOIN artists a ON a.id=current.artist_id ORDER BY a.id
        """)
        output.executemany("INSERT OR REPLACE INTO artists VALUES(?,?,?)",
                           ((scope, row[0], row[1]) for row in rows))

    def _select_entities(self, source, output):
        # CROSS JOIN is SQLite's loop-order fence. Without it, a domain-first plan
        # can rescan every music record for each entity instead of its memberships.
        rows = source.execute("""
            SELECT e.data,music_kind(e.data) kind,(
                SELECT coalesce(min(CASE WHEN d.name='repertoire' AND k.value='song_sample' THEN r.id END),
                    min(CASE WHEN d.name IN ('repertoire','file_contents') THEN r.id END),min(r.id))
                FROM input_entities ie INDEXED BY entity_lookup
                CROSS JOIN records r ON r.input_id=ie.input_id
                    AND has_domain(ie.domains,r.domain_id)
                CROSS JOIN domains d ON d.id=r.domain_id
                CROSS JOIN chunks c ON c.id=r.chunk_id
                CROSS JOIN headers h ON h.id=r.header_id
                CROSS JOIN terms k ON k.id=h.kind_id
                WHERE ie.entity_id=e.id AND c.state='indexed'
                    AND d.name IN ('repertoire','file_contents','compositions','setlists')
            ) record_id FROM entities e WHERE music_kind(e.data)>0 ORDER BY e.id
        """)
        for number, row in enumerate(rows):
            if number % 1000 == 0:
                self._check_space()
            if row["record_id"] is not None:
                self._queue_record(source, output, row["kind"], _unpack_text(row["data"]), row["record_id"])

    def _select_work_links(self, source, output):
        # A single selected work witness cannot supply every supported song relationship.
        # Queue only current composition rows containing actual work keys, not all files.
        rows = source.execute("""
            SELECT r.id FROM records r JOIN domains d ON d.id=r.domain_id
            JOIN chunks c ON c.id=r.chunk_id WHERE d.name='compositions' AND c.state='indexed'
            AND EXISTS(SELECT 1 FROM input_entities ie JOIN entities e ON e.id=ie.entity_id
                WHERE ie.input_id=r.input_id AND has_domain(ie.domains,r.domain_id) AND music_kind(e.data)=2)
        """)
        for number, row in enumerate(rows):
            if number % 1000 == 0:
                self._check_space()
            self._queue_record(source, output, 4, str(row[0]), row[0])

    @staticmethod
    def _queue_record(source, output, kind, key, record_id):
        row = source.execute("""
            SELECT d.name domain,i.data input_key,a.uuid artist_uuid,c.path chunk,r.line_no,c.bytes,c.mtime_ns
            FROM records r JOIN domains d ON d.id=r.domain_id JOIN input_keys i ON i.id=r.input_id
            JOIN headers h ON h.id=r.header_id JOIN artists a ON a.id=h.artist_id
            JOIN chunks c ON c.id=r.chunk_id WHERE r.id=?
        """, (record_id,)).fetchone()
        output.execute("INSERT OR IGNORE INTO pending VALUES(?,?,?,?,?,?,?,?,?,?)",
                       (kind, key, record_id, row["domain"], _pack_text(_unpack_text(row["input_key"])),
                        row["artist_uuid"], row["chunk"], row["line_no"], row["bytes"], row["mtime_ns"]))

    def _read_selected_chunks(self, db, source_id, source):
        chunks = db.execute("SELECT chunk,min(bytes),min(mtime_ns) FROM pending GROUP BY chunk ORDER BY chunk").fetchall()
        for relative, size, modified in chunks:
            self._check_space()
            path = Path(source["run"]) / relative
            if (len(Path(relative).parts) != 2 or path.is_symlink() or path.parent.is_symlink()
                    or path.resolve().parent.parent != Path(source["run"])):
                raise ValueError("A witness chunk must be an immutable direct domain chunk.")
            self._check_chunk(path, size, modified)
            rows = db.execute("SELECT * FROM pending WHERE chunk=? ORDER BY line_no", (relative,))
            rows.row_factory = sqlite3.Row
            targets = iter(itertools.groupby(rows, key=lambda row: row["line_no"]))
            target = next(targets, None)
            with gzip.open(path, "rb") as stream:
                line_no = 0
                while target is not None:
                    line = stream.readline(MAXIMUM_LINE_BYTES + 1)
                    if not line:
                        raise ValueError("An indexed witness line disappeared.")
                    line_no += 1
                    if len(line) > MAXIMUM_LINE_BYTES:
                        raise CatalogueBuildPaused("A witness chunk exceeds the bounded line size.")
                    if line_no != target[0]:
                        continue
                    record = json.loads(line)
                    witnesses = list(target[1])
                    self._project_record(db, source_id, source, record, witnesses)
                    self._check_space()
                    target = next(targets, None)
            rows.close()
            self._check_chunk(path, size, modified)
            # The exhausted cursor no longer reads these private staging rows. Reuse
            # their pages for later chunks without changing the shared reader index.
            db.execute("DELETE FROM pending WHERE chunk=?", (relative,))

    @staticmethod
    def _check_chunk(path, size, modified):
        actual = path.stat()
        if (actual.st_size, actual.st_mtime_ns) != (size, modified):
            raise ValueError("An indexed immutable witness chunk changed.")

    def _project_record(self, db, source_id, source, record, witnesses):
        first = witnesses[0]
        if (record.get("domain") != first["domain"]
                or record.get("inputKey") != _unpack_text(first["input_key"])):
            raise ValueError("The witness body no longer matches its indexed logical record.")
        scope = source["scope"]
        locator = (source_id, first["record_id"], first["chunk"], first["line_no"])
        songs = {song["entity_key"]: song for song in _songs(record)}
        accounts = {account["entityKey"]: account for account in _accounts(record)}
        for witness in witnesses:
            key = witness["entity_key"]
            if witness["kind"] == 1:
                song = songs.get(key, {"title": None, "basis": "indexed_reference_only", "legacy_uuid": None,
                                       "artist_uuid": first["artist_uuid"], "status": "referenced_only"})
                db.execute("INSERT OR REPLACE INTO songs VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                           (scope, key, song["artist_uuid"], song["title"], song["basis"], song["legacy_uuid"],
                            song["status"], *locator))
            elif witness["kind"] == 3 and key in accounts:
                account = accounts[key]
                db.execute("INSERT OR REPLACE INTO setlists VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                           (scope, key, first["artist_uuid"], account.get("literalDate") or key,
                            account.get("literalDate"), int(account.get("ordered") is True),
                            record.get("outcome"), *locator))
        for mention, work in _links(record):
            # Indexed references can include retained rivals. Only an actual supported
            # link creates a work, with this genuine link's own immutable witness.
            db.execute("INSERT OR REPLACE INTO works VALUES(?,?,?,?,?,?,?,?,?)",
                       (work["entityKey"], work.get("title"), work.get("musicBrainzWorkMbid"),
                        work.get("correspondenceBasis"), "supported_shadow_link", *locator))
            keys = set(mention.get("repertoireEntityKeys", []))
            if len(keys) == 1:
                key = next(iter(keys))
                if key.startswith("repertoire:"):
                    db.execute("INSERT OR IGNORE INTO work_songs VALUES(?,?,?)", (work["entityKey"], scope, key))

    @staticmethod
    def _finish(db, sources):
        # Rebuild from each index's current logical membership. Retained rival keys
        # without an actual supported link never create a work relationship.
        db.execute("DELETE FROM work_songs WHERE work_key NOT IN (SELECT entity_key FROM works)")
        values = {"version": VERSION, "sources": sources, "visibility": "independent_index_snapshots_only",
                  "built_at": time.time(), "status": "completed_projection", "maximum_line_bytes": MAXIMUM_LINE_BYTES,
                  "source_union": "later_sources_replace_display_witnesses;cross_run_withdrawals_not_reconciled"}
        db.executemany("INSERT INTO metadata VALUES(?,?)", ((key, json.dumps(value)) for key, value in values.items()))


def _accounts(record):
    if record.get("domain") != "setlists":
        return []
    proposal = record.get("proposal")
    values = proposal if isinstance(proposal, list) else [proposal]
    return [value for value in values if isinstance(value, dict)
            and value.get("entityKey", "").startswith(("setlist:", "setlist_account:"))]


def _segments(track):
    if not isinstance(track, dict):
        return
    for segment in track.get("segments", []):
        if segment.get("song"):
            yield segment["song"], segment.get("outcome", "proposed")
        for song in segment.get("candidateSongs", []):
            yield song, "candidate"
    for candidate in track.get("wholeFileDictionaryCandidates", []):
        yield candidate["song"], "candidate"


def _songs(record):
    proposal = record.get("proposal")
    artist = record.get("artistUuid")
    values = []
    if isinstance(proposal, dict):
        if proposal.get("entityKey", "").startswith("repertoire:"):
            values.append((proposal, "dictionary"))
        track = proposal.get("titleInterpretation") if record["domain"] == "file_contents" else proposal
        values.extend(_segments(track))
    for account in _accounts(record):
        for member in account.get("memberships", []):
            if member.get("songEntityKey"):
                values.append(({"entityKey": member["songEntityKey"], "name": member.get("title"),
                                "legacySongUuid": member.get("legacySongUuid"),
                                "basis": "captured_unordered_membership"}, "membership"))
        for occurrence in account.get("occurrences", []):
            interpretation = occurrence.get("songInterpretation") or {}
            values.extend(_segments({"segments": [interpretation]}))
    if record.get("domain") == "compositions" and isinstance(proposal, dict):
        for mention in proposal.get("mentions", []):
            for key in mention.get("repertoireEntityKeys", []):
                values.append(({"entityKey": key, "name": mention.get("title"),
                                "basis": "composition_repertoire_reference"}, "referenced_only"))
    for song, status in reversed(values):
        key = song.get("entityKey", "")
        if key.startswith("repertoire:"):
            yield {"entity_key": key, "artist_uuid": song.get("artistUuid") or artist,
                   "title": song.get("name"), "basis": song.get("basis"),
                   "legacy_uuid": song.get("legacySongUuid"), "status": status}


def _links(record):
    if record.get("domain") == "compositions" and isinstance(record.get("proposal"), dict):
        for mention in record["proposal"].get("mentions", []):
            for work in mention.get("links", []):
                if isinstance(work, dict) and work.get("entityKey", "").startswith("composition:musicbrainz:"):
                    yield mention, work


class MusicCatalogue:
    """Independent read connections keep a completed projection available during rebuilds."""

    def __init__(self, database):
        self.database = Path(database).resolve()

    def _connect(self):
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)
        db.row_factory = sqlite3.Row
        db.create_function("casefold", 1, lambda value: (value or "").casefold(), deterministic=True)
        return db

    @staticmethod
    def _metadata(db):
        return {row[0]: json.loads(row[1]) for row in db.execute("SELECT key,value FROM metadata")}

    @staticmethod
    def _enrich(db, row):
        result = dict(row)
        if "artist_uuid" in result:
            artist = db.execute("SELECT name FROM artists WHERE scope=? AND uuid=?",
                                (result["scope"], result["artist_uuid"])).fetchone()
            result["artist_name"] = artist[0] if artist else None
        if "witness_run" in result:
            sources = json.loads(db.execute("SELECT value FROM metadata WHERE key='sources'").fetchone()[0])
            source = next(source for source in sources if source["id"] == result["witness_run"])
            result["witness_reader_url"] = source["reader_url"]
            result["witness_run_path"] = source["run"]
        if "ordered" in result:
            result["ordered"] = bool(result["ordered"])
        return result

    def _search(self, table, q="", artist="", scope="", limit=40, offset=0):
        limit, offset = self._bounds(limit, offset)
        clauses, arguments = [], []
        if q:
            field = "name" if table == "artists" else "label" if table == "setlists" else "title"
            title_match = "instr(casefold(" + field + "),?)>0"
            arguments.append(q.casefold())
            if table in ("songs", "setlists"):
                title_match += (" OR EXISTS(SELECT 1 FROM artists a WHERE a.scope=" + table
                                + ".scope AND a.uuid=" + table + ".artist_uuid AND instr(casefold(a.name),?)>0)")
                arguments.append(q.casefold())
            clauses.append("(" + title_match + ")")
        if artist:
            clauses.append("artist_uuid=?")
            arguments.append(artist)
        if scope:
            clauses.append("scope=?")
            arguments.append(scope)
        where = " WHERE " + " AND ".join(clauses) if clauses else ""
        order = {"artists": "casefold(name),scope,uuid", "songs": "casefold(title),scope,entity_key",
                 "works": "casefold(title),entity_key", "setlists": "casefold(label),scope,entity_key"}[table]
        with closing(self._connect()) as db:
            db.execute("BEGIN")
            total = db.execute("SELECT count(*) FROM " + table + where, arguments).fetchone()[0]
            rows = db.execute("SELECT * FROM " + table + where + " ORDER BY " + order + " LIMIT ? OFFSET ?",
                              (*arguments, limit, offset))
            return {"items": [self._enrich(db, row) for row in rows], "total": total, "limit": limit, "offset": offset}

    def artists(self, q="", limit=40, offset=0, scope=""):
        return self._search("artists", q=q, scope=scope, limit=limit, offset=offset)

    def songs(self, q="", artist="", scope="", limit=40, offset=0):
        return self._search("songs", q, artist, scope, limit, offset)

    def works(self, q="", limit=40, offset=0):
        return self._search("works", q=q, limit=limit, offset=offset)

    def setlists(self, artist="", scope="", limit=40, offset=0, q=""):
        return self._search("setlists", q=q, artist=artist, scope=scope, limit=limit, offset=offset)

    @staticmethod
    def _bounds(limit, offset):
        limit, offset = int(limit), int(offset)
        if not 1 <= limit <= 200 or offset < 0:
            raise ValueError("Use a limit of 1–200 and a nonnegative offset.")
        return limit, offset

    def song(self, scope, key, limit=40, offset=0):
        limit, offset = self._bounds(limit, offset)
        with closing(self._connect()) as db:
            db.execute("BEGIN")
            row = db.execute("SELECT * FROM songs WHERE scope=? AND entity_key=?", (scope, key)).fetchone()
            if row is None:
                return None
            result = self._enrich(db, row)
            result["works_total"] = db.execute("""
                SELECT count(*) FROM works w JOIN work_songs link ON link.work_key=w.entity_key
                WHERE link.scope=? AND link.song_key=?
            """, (scope, key)).fetchone()[0]
            result["works_limit"], result["works_offset"] = limit, offset
            result["works"] = [self._enrich(db, work) for work in db.execute("""
                SELECT w.* FROM works w JOIN work_songs link ON link.work_key=w.entity_key
                WHERE link.scope=? AND link.song_key=? ORDER BY w.entity_key LIMIT ? OFFSET ?
            """, (scope, key, limit, offset))]
            return result

    def work(self, key, limit=40, offset=0):
        limit, offset = self._bounds(limit, offset)
        with closing(self._connect()) as db:
            db.execute("BEGIN")
            row = db.execute("SELECT * FROM works WHERE entity_key=?", (key,)).fetchone()
            if row is None:
                return None
            result = self._enrich(db, row)
            result["songs_total"] = db.execute("""
                SELECT count(*) FROM songs s JOIN work_songs link ON link.scope=s.scope AND link.song_key=s.entity_key
                WHERE link.work_key=?
            """, (key,)).fetchone()[0]
            result["songs_limit"], result["songs_offset"] = limit, offset
            result["songs"] = [self._enrich(db, song) for song in db.execute("""
                SELECT s.* FROM songs s JOIN work_songs link ON link.scope=s.scope AND link.song_key=s.entity_key
                WHERE link.work_key=? ORDER BY s.scope,s.entity_key LIMIT ? OFFSET ?
            """, (key, limit, offset))]
            return result

    def setlist(self, scope, key):
        with closing(self._connect()) as db:
            row = db.execute("SELECT * FROM setlists WHERE scope=? AND entity_key=?", (scope, key)).fetchone()
            return self._enrich(db, row) if row else None

    def stats(self):
        with closing(self._connect()) as db:
            db.execute("BEGIN")
            result = self._metadata(db)
            result["counts"] = {table: db.execute("SELECT count(*) FROM " + table).fetchone()[0]
                                for table in ("artists", "songs", "works", "work_songs", "setlists")}
            result["bytes"] = self.database.stat().st_size
            return result
