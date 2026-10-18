#!/usr/bin/env python3
"""Read immutable shadow chunks without copying proposal/evidence bodies into SQLite.

The cache owns summaries and file/line pointers only. The runner owns chunks and
progress.json; checkpoint replays replace the logical (domain, inputKey) record.
Run this on a private interface: outputs can contain original recording prose.
"""

import argparse
import gzip
import hashlib
from functools import lru_cache
import json
import sqlite3
import shutil
import threading
import time
import zlib
import uuid
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit


def _pack_text(value):
    """Losslessly compact common UUID keys and long repeated label prefixes."""
    encoded = value.encode("utf-8")
    if len(value) >= 36:
        suffix = value[-36:]
        try:
            parsed = uuid.UUID(suffix)
            if str(parsed) == suffix:
                return b"u" + value[:-36].encode("utf-8") + parsed.bytes
        except ValueError:
            pass
    compressed = zlib.compress(encoded, level=1) if len(encoded) > 80 else encoded
    return b"z" + compressed if len(compressed) + 8 < len(encoded) else b"t" + encoded


def _unpack_text(value):
    if value[:1] == b"u":
        return value[1:-16].decode("utf-8") + str(uuid.UUID(bytes=value[-16:]))
    return (zlib.decompress(value[1:]) if value[:1] == b"z" else value[1:]).decode("utf-8")


@lru_cache(maxsize=1024)
def _summary(value):
    return json.loads(_unpack_text(value))


def _has_domain(bits, domain):
    offset = domain - 1
    return offset // 8 < len(bits) and bool(bits[offset // 8] & (1 << (offset % 8)))


def _change_domain(bits, domain, present):
    """Domain IDs are dense; expandable bytes avoid an arbitrary 64-domain limit."""
    offset = domain - 1
    result = bytearray(bits)
    if present:
        result.extend(b"\0" * max(0, offset // 8 + 1 - len(result)))
        result[offset // 8] |= 1 << (offset % 8)
    elif offset // 8 < len(result):
        result[offset // 8] &= ~(1 << (offset % 8))
    return bytes(result).rstrip(b"\0")


class ShadowIndex:
    """A rebuildable compact cache with one writer and independent WAL readers.

    Integer dictionaries share repeated headers, labels and entity names. Headers
    are immutable variants: a replay in one domain must not change another domain's
    artist/provider metadata. Only summaries and original chunk pointers live here.
    A versioned filename keeps older readers running during a separate rebuild.
    Version 3 rebuilds summaries with file titles ahead of recording dates.
    """

    LABEL_FIELDS = {"name", "title", "displayText", "label", "literalDate", "performanceLabel", "originalTitle", "wording", "displayName"}
    OUTCOMES = {"proposed", "unresolved", "failed", "not_applicable"}

    def __init__(self, run, minimum_free_bytes=0, pause_indexing=False):
        self.run = Path(run).resolve(strict=True)
        if not self.run.is_dir():
            raise ValueError("The existing run path must be a directory.")
        self.database = self.run / ".shadow-reader-v3.sqlite3"
        self.minimum_free_bytes = minimum_free_bytes
        self.pause_indexing = pause_indexing
        self._status_lock = threading.Lock()
        self._active_file = None
        self._scan_error = None
        self._pause_reason = ("Indexing paused by the operator. Existing results remain available."
                              if pause_indexing else None)
        self._last_scan = None
        self._revision = 0
        self._aggregate_cache = None
        with closing(self.connect()) as db:
            db.execute("PRAGMA journal_mode=WAL")
            db.executescript("""
                CREATE TABLE IF NOT EXISTS chunks (
                    id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL,
                    bytes INTEGER NOT NULL, mtime_ns INTEGER NOT NULL,
                    state TEXT NOT NULL, rows INTEGER NOT NULL DEFAULT 0, error TEXT
                );
                CREATE TABLE IF NOT EXISTS terms (id INTEGER PRIMARY KEY, value TEXT UNIQUE NOT NULL);
                CREATE TABLE IF NOT EXISTS domains (id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
                CREATE TABLE IF NOT EXISTS artists (
                    id INTEGER PRIMARY KEY, uuid TEXT NOT NULL, name TEXT NOT NULL, UNIQUE(uuid,name)
                );
                CREATE TABLE IF NOT EXISTS input_keys (
                    id INTEGER PRIMARY KEY, digest BLOB NOT NULL, data BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS input_digest ON input_keys(digest);
                CREATE TABLE IF NOT EXISTS headers (
                    id INTEGER PRIMARY KEY, input_id INTEGER NOT NULL, input_uuid TEXT,
                    kind_id INTEGER NOT NULL, artist_id INTEGER NOT NULL, provider_id INTEGER NOT NULL,
                    UNIQUE(input_id,input_uuid,kind_id,artist_id,provider_id)
                );
                CREATE INDEX IF NOT EXISTS headers_provider_artist ON headers(provider_id,artist_id);
                CREATE TABLE IF NOT EXISTS summaries (
                    id INTEGER PRIMARY KEY, digest BLOB NOT NULL, data BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS summary_digest ON summaries(digest);
                CREATE TABLE IF NOT EXISTS records (
                    id INTEGER PRIMARY KEY, domain_id INTEGER NOT NULL, input_id INTEGER NOT NULL,
                    header_id INTEGER NOT NULL, outcome_id INTEGER NOT NULL, reason_id INTEGER NOT NULL,
                    method_id INTEGER NOT NULL, summary_id INTEGER NOT NULL,
                    chunk_id INTEGER NOT NULL REFERENCES chunks(id), line_no INTEGER NOT NULL,
                    UNIQUE(input_id,domain_id)
                );
                CREATE INDEX IF NOT EXISTS records_filters ON records(domain_id,outcome_id);
                CREATE INDEX IF NOT EXISTS records_headers ON records(header_id);
                CREATE TABLE IF NOT EXISTS entities (
                    id INTEGER PRIMARY KEY, digest BLOB NOT NULL, data BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS entity_digest ON entities(digest);
                CREATE TABLE IF NOT EXISTS input_entities (
                    input_id INTEGER NOT NULL, entity_id INTEGER NOT NULL, domains BLOB NOT NULL,
                    PRIMARY KEY(input_id,entity_id)
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS entity_lookup ON input_entities(entity_id,input_id);
                CREATE VIEW IF NOT EXISTS result_rows AS
                SELECT r.id,d.name domain,key_text(i.data) input_key,k.value input_kind,
                    coalesce(h.input_uuid,substr(key_text(i.data),-36)) input_uuid,
                    a.uuid artist_uuid,a.name artist_name,p.value provider,o.value outcome,
                    reason.value reason,m.value method,summary_label(s.data) label,
                    substr(casefold(a.name)||' '||casefold(key_text(i.data))||
                        CASE WHEN summary_search(s.data)='' THEN '' ELSE ' '||summary_search(s.data) END,
                        1,4096) search_text,
                    r.input_id,r.domain_id,r.chunk_id,r.line_no
                FROM records r JOIN domains d ON d.id=r.domain_id
                JOIN input_keys i ON i.id=r.input_id JOIN headers h ON h.id=r.header_id
                JOIN artists a ON a.id=h.artist_id JOIN terms k ON k.id=h.kind_id
                JOIN terms p ON p.id=h.provider_id JOIN terms o ON o.id=r.outcome_id
                JOIN terms reason ON reason.id=r.reason_id JOIN terms m ON m.id=r.method_id
                JOIN summaries s ON s.id=r.summary_id;
            """)
            db.commit()

    def connect(self, read_timeout=None):
        db = sqlite3.connect(self.database, timeout=10)
        if read_timeout is not None:
            deadline = time.monotonic() + read_timeout
            db.set_progress_handler(lambda: time.monotonic() > deadline, 1000)
        db.row_factory = sqlite3.Row
        db.create_function("key_text", 1, _unpack_text, deterministic=True)
        db.create_function("casefold", 1, str.casefold, deterministic=True)
        db.create_function("summary_label", 1, lambda value: _summary(value)[0], deterministic=True)
        db.create_function("summary_search", 1, lambda value: _summary(value)[1], deterministic=True)
        db.create_function("has_domain", 2, _has_domain, deterministic=True)
        db.create_function("add_domain", 2, lambda bits, domain: _change_domain(bits, domain, True), deterministic=True)
        db.create_function("remove_domain", 2, lambda bits, domain: _change_domain(bits, domain, False), deterministic=True)
        return db

    def chunks(self):
        # Only the runner's completed, direct domain chunks are eligible. Neither
        # unfinished writes nor symlinks can broaden the reader's file boundary.
        paths = sorted((path for path in self.run.glob("*/*.jsonl.gz")
                        if path.is_file() and not path.is_symlink()
                        and not path.parent.is_symlink()
                        and path.resolve().parent.parent == self.run),
                       key=lambda path: path.name, reverse=True)
        paths.sort(key=lambda path: (path.parent.name != "file_contents", path.parent.name))

        # Surface the latest completed data in every domain before backfilling,
        # with playable files first. Older chunks cannot replace a newer result.
        latest = {}
        for path in paths:
            latest.setdefault(path.parent.name, path)
        heads = set(latest.values())
        return list(latest.values()) + [path for path in paths if path not in heads]

    def scan_once(self):
        """Commit each complete chunk atomically; a bad chunk does not hide later files."""
        if self.pause_indexing:
            return

        try:
            with self._status_lock:
                self._pause_reason = None
            with closing(self.connect()) as db:
                paths = self.chunks()
                present = {str(path.relative_to(self.run)) for path in paths}
                for saved in db.execute("SELECT id,path FROM chunks WHERE state='indexed'").fetchall():
                    if saved["path"] not in present:
                        db.execute("UPDATE chunks SET state='failed',error=? WHERE id=?",
                                   ("Completed chunk disappeared from this run.", saved["id"]))
                        db.commit()
                        self._changed()
                for path in paths:
                    relative = str(path.relative_to(self.run))
                    stat = path.stat()
                    saved = db.execute("SELECT * FROM chunks WHERE path=?", (relative,)).fetchone()
                    if saved is not None:
                        if saved["state"] == "indexed" and (saved["bytes"], saved["mtime_ns"]) != (stat.st_size, stat.st_mtime_ns):
                            db.execute("UPDATE chunks SET state='failed', error=? WHERE id=?",
                                       ("Completed chunk changed after discovery; immutable output required.", saved["id"]))
                            db.commit()
                            self._changed()
                        continue
                    # Readers can hold a WAL snapshot during a search. Reclaim its
                    # high-water allocation as soon as they finish, without waiting.
                    wal = Path(str(self.database) + "-wal")
                    if wal.exists() and wal.stat().st_size > 64 * 1024**2:
                        db.execute("PRAGMA busy_timeout=0")
                        try:
                            db.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()
                        finally:
                            db.execute("PRAGMA busy_timeout=10000")
                    # Leave the existing index available to HTTP readers when storage
                    # is tight. The reserve includes room for this bounded chunk write.
                    if self.minimum_free_bytes and shutil.disk_usage(self.run).free < self.minimum_free_bytes:
                        with self._status_lock:
                            self._pause_reason = "Indexing paused to preserve free disk space. Existing results remain available."
                        break
                    with self._status_lock:
                        self._active_file = relative
                    self._index_chunk(db, path, relative, stat)
            with self._status_lock:
                self._scan_error = None
        except (OSError, sqlite3.Error) as error:
            with self._status_lock:
                self._scan_error = str(error)
        finally:
            with self._status_lock:
                self._active_file = None
                self._last_scan = time.time()

    def _index_chunk(self, db, path, relative, stat):
        try:
            db.execute("BEGIN")
            chunk_id = db.execute(
                "INSERT INTO chunks(path,bytes,mtime_ns,state) VALUES(?,?,?,'indexing')",
                (relative, stat.st_size, stat.st_mtime_ns)).lastrowid
            rows = 0
            with gzip.open(path, "rt", encoding="utf-8") as stream:
                for line_no, line in enumerate(stream, 1):
                    if not line.strip():
                        continue
                    record = json.loads(line)
                    self._store_record(db, record, chunk_id, path.name, line_no, path.parent.name)
                    rows += 1
            if (path.stat().st_size, path.stat().st_mtime_ns) != (stat.st_size, stat.st_mtime_ns):
                raise ValueError("Completed chunk changed during indexing.")
            db.execute("UPDATE chunks SET state='indexed', rows=? WHERE id=?", (rows, chunk_id))
            db.commit()
            self._changed()
        except (OSError, EOFError, zlib.error, ValueError, KeyError, TypeError, sqlite3.Error) as error:
            db.rollback()
            db.execute("INSERT INTO chunks(path,bytes,mtime_ns,state,error) VALUES(?,?,?,'failed',?)",
                       (relative, stat.st_size, stat.st_mtime_ns, str(error)[:1000]))
            db.commit()
            self._changed()

    def _changed(self):
        with self._status_lock:
            self._revision += 1
            self._aggregate_cache = None

    @staticmethod
    def _term(db, value, table="terms", column="value"):
        row = db.execute(f"SELECT id FROM {table} WHERE {column}=?", (value,)).fetchone()
        if row is not None:
            return row[0]
        return db.execute(f"INSERT INTO {table}({column}) VALUES(?)", (value,)).lastrowid

    @staticmethod
    def _key(db, table, value):
        # A digest saves a second full copy of every UUID/title in a text index.
        # Exact comparison handles collisions; digest equality never joins identities.
        digest = hashlib.blake2b(value.encode("utf-8"), digest_size=16).digest()
        for row in db.execute(f"SELECT id,data FROM {table} WHERE digest=?", (digest,)):
            if _unpack_text(row["data"]) == value:
                return row["id"]
        return db.execute(f"INSERT INTO {table}(digest,data) VALUES(?,?)",
                          (digest, _pack_text(value))).lastrowid

    def _header(self, db, record, input_id):
        artist = db.execute("SELECT id FROM artists WHERE uuid=? AND name=?",
                            (record["artistUuid"], record["artistName"])).fetchone()
        artist_id = artist[0] if artist else db.execute(
            "INSERT INTO artists(uuid,name) VALUES(?,?)", (record["artistUuid"], record["artistName"])).lastrowid
        kind_id = self._term(db, record["inputKind"])
        provider_id = self._term(db, record["provider"])
        # Most native keys end in this exact UUID. NULL derives those original
        # 36 characters from the shared key, without rewriting other UUID forms.
        input_uuid = record["inputUuid"]
        try:
            if str(uuid.UUID(input_uuid)) == input_uuid and record["inputKey"].endswith(input_uuid):
                input_uuid = None
        except ValueError:
            pass
        values = (input_id, input_uuid, kind_id, artist_id, provider_id)
        row = db.execute("""SELECT id FROM headers WHERE input_id=? AND input_uuid IS ?
            AND kind_id=? AND artist_id=? AND provider_id=?""", values).fetchone()
        if row:
            return row[0]
        return db.execute("""INSERT INTO headers(input_id,input_uuid,kind_id,artist_id,provider_id)
            VALUES(?,?,?,?,?)""", values).lastrowid

    def _replace_entities(self, db, input_id, domain_id, keys):
        # One input/entity link can serve several outcomes. Replaying one domain
        # removes only its bit; another domain's current pointer stays unchanged.
        db.execute("""UPDATE input_entities SET domains=remove_domain(domains,?)
            WHERE input_id=? AND has_domain(domains,?)""", (domain_id, input_id, domain_id))
        db.execute("DELETE FROM input_entities WHERE input_id=? AND length(domains)=0", (input_id,))
        for key in sorted(set(keys)):
            entity_id = self._key(db, "entities", key)
            bits = _change_domain(b"", domain_id, True)
            db.execute("""INSERT INTO input_entities(input_id,entity_id,domains) VALUES(?,?,?)
                ON CONFLICT(input_id,entity_id) DO UPDATE SET domains=add_domain(domains,?)""",
                       (input_id, entity_id, bits, domain_id))

    def _store_record(self, db, record, chunk_id, order_key, line_no, domain):
        required = ("domain", "inputKey", "inputKind", "inputUuid", "artistUuid", "artistName",
                    "provider", "outcome", "method")
        if not isinstance(record, dict) or any(not isinstance(record.get(key), str) for key in required):
            raise ValueError("Chunk row lacks the shadow result header.")
        if record["domain"] != domain or record["outcome"] not in self.OUTCOMES:
            raise ValueError("Chunk domain or outcome does not match the shadow result contract.")
        keys = record.get("entityKeys", [])
        if not isinstance(keys, list) or any(not isinstance(key, str) for key in keys):
            raise ValueError("Entity keys must be strings.")
        input_id = self._key(db, "input_keys", record["inputKey"])
        domain_id = self._term(db, domain, "domains", "name")
        previous = db.execute("""SELECT r.id,c.path,r.line_no FROM records r
            JOIN chunks c ON c.id=r.chunk_id WHERE r.domain_id=? AND r.input_id=?""",
                              (domain_id, input_id)).fetchone()
        if previous and (Path(previous["path"]).name, previous["line_no"]) >= (order_key, line_no):
            return
        labels = self._labels(record.get("proposal"), prefer_file_title=(
            record["inputKind"] == "track_sample" and domain in ("repertoire", "file_contents")))
        label = labels[0] if labels else (record.get("reason") or record["inputKind"])[:240]
        summary = json.dumps([label, " ".join(labels).casefold()], ensure_ascii=False, separators=(",", ":"))
        values = (domain_id, input_id, self._header(db, record, input_id),
                  self._term(db, record["outcome"]), self._term(db, (record.get("reason") or "")[:500]),
                  self._term(db, record["method"][:256]), self._key(db, "summaries", summary), chunk_id, line_no)
        if previous:
            db.execute("""UPDATE records SET domain_id=?,input_id=?,header_id=?,outcome_id=?,reason_id=?,
                method_id=?,summary_id=?,chunk_id=?,line_no=? WHERE id=?""", (*values, previous["id"]))
        else:
            db.execute("""INSERT INTO records(domain_id,input_id,header_id,outcome_id,reason_id,method_id,
                summary_id,chunk_id,line_no) VALUES(?,?,?,?,?,?,?,?,?)""", values)
        self._replace_entities(db, input_id, domain_id, keys)

    @classmethod
    def _labels(cls, proposal, prefer_file_title=False):
        labels = []

        # A file's recording home is useful search context, but its date cannot
        # distinguish the files in that recording. Keep the literal title first.
        if prefer_file_title and isinstance(proposal, dict):
            interpretation = proposal.get("titleInterpretation", proposal)
            if isinstance(interpretation, dict):
                title = interpretation.get("originalTitle")
                if isinstance(title, str) and title.strip():
                    labels.append(title.strip()[:240])

        def visit(value):
            if len(labels) >= 16:
                return
            if isinstance(value, dict):
                for key, child in value.items():
                    if key in cls.LABEL_FIELDS and isinstance(child, str) and child.strip():
                        label = child.strip()[:240]
                        if label not in labels:
                            labels.append(label)
                    elif key == "aliases" and isinstance(child, list):
                        for alias in child:
                            if isinstance(alias, str) and alias.strip() and alias.strip()[:240] not in labels:
                                labels.append(alias.strip()[:240])
                            if len(labels) >= 16:
                                break
                    elif isinstance(child, (list, dict)):
                        visit(child)
                    if len(labels) >= 16:
                        break
            elif isinstance(value, list):
                for child in value:
                    visit(child)
                    if len(labels) >= 16:
                        break
        visit(proposal)
        return labels

    def search(self, filters):
        conditions, arguments = ["c.state='indexed'"], []
        selectors = (
            ("domain", "r.domain_id=(SELECT id FROM domains WHERE name=?)"),
            ("outcome", "r.outcome_id=(SELECT id FROM terms WHERE value=?)"),
            ("provider", "r.header_id IN (SELECT id FROM headers WHERE provider_id=(SELECT id FROM terms WHERE value=?))"),
        )
        for parameter, condition in selectors:
            if filters.get(parameter):
                conditions.append(condition)
                arguments.append(filters[parameter])
        if filters.get("artist"):
            conditions.append("r.header_id IN (SELECT h.id FROM headers h JOIN artists a ON a.id=h.artist_id "
                              "WHERE a.uuid=? OR lower(a.name) LIKE ? ESCAPE '\\')")
            arguments.extend((filters["artist"], self._like(filters["artist"].lower())))
        prefix = ""
        source = " FROM records r JOIN chunks c ON c.id=r.chunk_id"
        if filters.get("entityKey"):
            # Resolve the small entity membership first. A domain-first plan over
            # result_rows otherwise scans millions of files before checking the key.
            prefix = """WITH requested_records AS MATERIALIZED (
                SELECT matched.id FROM entities found
                JOIN input_entities requested ON requested.entity_id=found.id
                JOIN records matched ON matched.input_id=requested.input_id
                WHERE found.digest=? AND key_text(found.data)=?
                    AND has_domain(requested.domains,matched.domain_id)
            ) """
            source = (" FROM requested_records requested CROSS JOIN records r ON r.id=requested.id"
                      " JOIN chunks c ON c.id=r.chunk_id")
            arguments = [hashlib.blake2b(filters["entityKey"].encode("utf-8"), digest_size=16).digest(),
                         filters["entityKey"], *arguments]
        if filters.get("q"):
            source += " JOIN result_rows searched ON searched.id=r.id"
            conditions.append("searched.search_text LIKE ? ESCAPE '\\'")
            arguments.append(self._like(filters["q"].casefold()))
        where = " WHERE " + " AND ".join(conditions) if conditions else ""
        limit = max(1, min(int(filters.get("limit", 40)), 100))
        offset = max(0, int(filters.get("offset", 0)))
        with closing(self.connect(read_timeout=10)) as db:
            total = db.execute(prefix + "SELECT count(*)" + source + where, arguments).fetchone()[0]
            selected = db.execute(prefix + "SELECT r.id" + source + where
                                  + " ORDER BY r.id DESC LIMIT ? OFFSET ?", (*arguments, limit, offset)).fetchall()
            ids = [row[0] for row in selected]
            # Materialize only the current page's summaries. Counts and pagination
            # need no title decoding or wide metadata joins across every recording.
            rows = []
            if ids:
                rows = db.execute(
                    "SELECT id,domain,input_key,input_kind,input_uuid,artist_uuid,artist_name,provider,outcome,reason,method,label "
                    "FROM result_rows WHERE id IN (" + ",".join("?" for _ in ids) + ") ORDER BY id DESC", ids
                ).fetchall()
            items = []
            for row in rows:
                item = dict(row)
                item["entityKeys"] = [value[0] for value in db.execute(
                    """SELECT key_text(k.data) entity_key FROM input_entities e JOIN entities k ON k.id=e.entity_id
                    JOIN records r ON r.input_id=e.input_id WHERE r.id=? AND has_domain(e.domains,r.domain_id)
                    ORDER BY entity_key""", (row["id"],))]
                items.append(item)
        return {"items": items, "total": total, "limit": limit, "offset": offset}

    @staticmethod
    def _like(value):
        return "%" + value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"

    def detail(self, record_id):
        with closing(self.connect()) as db:
            row = db.execute("""SELECT r.*,c.path,c.bytes,c.mtime_ns,c.state FROM result_rows r
                JOIN chunks c ON c.id=r.chunk_id WHERE r.id=?""", (record_id,)).fetchone()
        if row is None:
            raise KeyError("Result no longer exists in this index.")
        path = (self.run / row["path"]).resolve(strict=True)
        if path.parent.parent != self.run or path.is_symlink():
            raise ValueError("Chunk is outside this run.")
        stat = path.stat()
        if row["state"] != "indexed" or (stat.st_size, stat.st_mtime_ns) != (row["bytes"], row["mtime_ns"]):
            raise ValueError("Original immutable chunk is no longer available unchanged.")
        with gzip.open(path, "rt", encoding="utf-8") as stream:
            for line_no, line in enumerate(stream, 1):
                if line_no == row["line_no"]:
                    record = json.loads(line)
                    if (record.get("domain"), record.get("inputKey")) != (row["domain"], row["input_key"]):
                        raise ValueError("Original chunk no longer matches its indexed coordinates.")
                    return {"id": row["id"], "record": record, "playback": self._playback(record)}
        raise ValueError("Original record is missing from its chunk.")

    @staticmethod
    def _playback(record):
        urls = []

        def visit(value):
            if isinstance(value, dict):
                for key, child in value.items():
                    if key in {"mp3Url", "mp3_url", "playbackUrl", "audioUrl"} and isinstance(child, str):
                        try:
                            parsed = urlsplit(child)
                        except ValueError:
                            continue
                        if parsed.scheme in {"http", "https"} and parsed.hostname and child not in urls:
                            urls.append(child)
                    elif isinstance(child, (dict, list)):
                        visit(child)
            elif isinstance(value, list):
                for child in value:
                    visit(child)
        visit(record)
        return urls

    def stats(self):
        progress = None
        progress_error = None
        try:
            with (self.run / "progress.json").open(encoding="utf-8") as stream:
                progress = json.load(stream)
        except FileNotFoundError:
            pass
        except (OSError, ValueError) as error:
            progress_error = str(error)
        aggregate = self._aggregates()
        indexed, chunks, facets = aggregate["indexed"], aggregate["chunks"], aggregate["facets"]
        expected = (progress or {}).get("population", {}).get("expectedOutcomes", {})
        expected = {tuple(key.split("|", 1)): value for key, value in expected.items()}
        processed = {(item["provider"], item["domain"]): item["processed"]
                     for item in (progress or {}).get("counts", [])}
        coverage = []
        for provider, domain in sorted(set(expected) | set(processed) | set(indexed)):
            target = expected.get((provider, domain))
            done = processed.get((provider, domain), 0)
            visible = indexed.get((provider, domain), 0)
            coverage.append({"provider": provider, "domain": domain, "expected": target,
                             "processed": done, "remaining": None if target is None else max(0, target-done),
                             "indexed": visible, "indexLag": max(0, done-visible),
                             "aheadOfCheckpoint": max(0, visible-done)})
        discovered = {str(path.relative_to(self.run)) for path in self.chunks()}
        indexed_files = {item["path"] for item in chunks}
        with self._status_lock:
            status = {"activeFile": self._active_file, "lastScan": self._last_scan,
                      "error": self._scan_error, "pauseReason": self._pause_reason}
        return {"run": str(self.run), "progress": progress, "progressError": progress_error,
                "coverage": coverage, "facets": facets, "indexedRecords": sum(indexed.values()),
                "pendingChunks": len(discovered-indexed_files),
                "indexedChunks": sum(item["state"] == "indexed" for item in chunks),
                "failedChunks": sum(item["state"] == "failed" for item in chunks),
                "chunks": [item for item in chunks if item["state"] == "failed"][:50],
                "indexRevision": aggregate["revision"], "index": status}

    def _aggregates(self):
        # Root progress can advance independently, but result facets change only when
        # a chunk commits or becomes unavailable. Reuse scans across phone refreshes.
        with self._status_lock:
            if self._aggregate_cache is not None:
                return self._aggregate_cache
            revision = self._revision
        with closing(self.connect(read_timeout=15)) as db:
            db.execute("BEGIN")
            groups = db.execute("""SELECT r.domain_id,h.provider_id,r.outcome_id,h.artist_id,count(*) n
                FROM records r JOIN chunks c ON c.id=r.chunk_id JOIN headers h ON h.id=r.header_id
                WHERE c.state='indexed' GROUP BY r.domain_id,h.provider_id,r.outcome_id,h.artist_id""").fetchall()
            terms = dict(db.execute("SELECT id,value FROM terms"))
            domains = dict(db.execute("SELECT id,name FROM domains"))
            artists = {row["id"]: (row["uuid"], row["name"]) for row in db.execute("SELECT * FROM artists")}
            chunks = [dict(row) for row in db.execute("SELECT path,state,rows,error FROM chunks ORDER BY path")]
        indexed, counts = {}, {name: {} for name in ("domain", "provider", "outcome", "artists")}
        for row in groups:
            domain, provider = domains[row["domain_id"]], terms[row["provider_id"]]
            pair = (provider, domain)
            indexed[pair] = indexed.get(pair, 0) + row["n"]
            for name, value in (("domain", domain), ("provider", provider),
                                ("outcome", terms[row["outcome_id"]]), ("artists", artists[row["artist_id"]])):
                counts[name][value] = counts[name].get(value, 0) + row["n"]
        facets = {name: [{"value": value, "count": count} for value, count in sorted(counts[name].items())]
                  for name in ("domain", "provider", "outcome")}
        facets["artists"] = [{"value": key[0], "label": key[1], "count": count}
                             for key, count in sorted(counts["artists"].items(), key=lambda item: (item[0][1], item[0][0]))]
        aggregate = {"indexed": indexed, "chunks": chunks, "facets": facets, "revision": revision}
        with self._status_lock:
            if self._revision == revision:
                self._aggregate_cache = aggregate
        return aggregate


def handler_for(index):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            route = urlsplit(self.path)
            query = {key: values[-1] for key, values in parse_qs(route.query).items()}
            try:
                if route.path in {"/", "/shadow-reader.html"}:
                    body = Path(__file__).with_name("shadow-reader.html").read_bytes()
                    return self.send_body(200, body, "text/html; charset=utf-8")
                if route.path == "/api/stats":
                    return self.send_json(200, index.stats())
                if route.path == "/api/records":
                    return self.send_json(200, index.search(query))
                if route.path == "/api/record":
                    return self.send_json(200, index.detail(int(query.get("id", ""))))
                return self.send_json(404, {"error": "Unknown reader route."})
            except KeyError as error:
                return self.send_json(404, {"error": str(error)})
            except (ValueError, OSError, EOFError, zlib.error) as error:
                return self.send_json(409, {"error": str(error)})
            except sqlite3.Error as error:
                message = ("This query took too long. Try fewer artists or a more specific search."
                           if str(error) == "interrupted" else "Reader index is temporarily unavailable.")
                return self.send_json(503, {"error": message})

        def send_json(self, status, payload):
            return self.send_body(status, json.dumps(payload, ensure_ascii=False).encode(), "application/json; charset=utf-8")

        def send_body(self, status, body, content_type):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; media-src https: http:; frame-ancestors 'none'; base-uri 'none'")
            self.end_headers()
            self.wfile.write(body)
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True, help="Absolute existing shadow output directory")
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=18764)
    parser.add_argument("--minimum-free-bytes", type=int, default=0,
                        help="Pause indexing below this free-space reserve; keep serving existing results")
    parser.add_argument("--pause-indexing", action="store_true",
                        help="Serve the existing cache without indexing new chunks; omit on restart to resume")
    args = parser.parse_args()
    if not args.run.is_absolute() or not args.run.is_dir():
        parser.error("--run must name an absolute, existing directory.")
    index = ShadowIndex(args.run, args.minimum_free_bytes, args.pause_indexing)
    stop = threading.Event()

    def follow():
        while not stop.is_set():
            index.scan_once()
            stop.wait(2)
    worker = threading.Thread(target=follow, name="shadow-index", daemon=True)
    worker.start()
    server = ThreadingHTTPServer((args.bind, args.port), handler_for(index))
    print(f"Shadow reader: http://{args.bind}:{server.server_port}/ — {index.run}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        server.server_close()
        worker.join(timeout=5)


if __name__ == "__main__":
    main()
