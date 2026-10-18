#!/usr/bin/env python3
"""Read immutable shadow chunks without copying proposal/evidence bodies into SQLite.

The cache owns summaries and file/line pointers only. The runner owns chunks and
progress.json; checkpoint replays replace the logical (domain, inputKey) record.
Run this on a private interface: outputs can contain original recording prose.
"""

import argparse
import gzip
import json
import sqlite3
import threading
import time
import zlib
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit


class ShadowIndex:
    """A rebuildable local index. One background writer and independent WAL readers."""

    LABEL_FIELDS = {"name", "title", "displayText", "label", "literalDate", "performanceLabel", "originalTitle", "wording", "displayName"}
    OUTCOMES = {"proposed", "unresolved", "failed", "not_applicable"}

    def __init__(self, run):
        self.run = Path(run).resolve(strict=True)
        if not self.run.is_dir():
            raise ValueError("The existing run path must be a directory.")
        self.database = self.run / ".shadow-reader.sqlite3"
        self._status_lock = threading.Lock()
        self._active_file = None
        self._scan_error = None
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
                CREATE TABLE IF NOT EXISTS records (
                    id INTEGER PRIMARY KEY, domain TEXT NOT NULL, input_key TEXT NOT NULL,
                    input_kind TEXT NOT NULL, input_uuid TEXT NOT NULL,
                    artist_uuid TEXT NOT NULL, artist_name TEXT NOT NULL,
                    provider TEXT NOT NULL, outcome TEXT NOT NULL, reason TEXT,
                    method TEXT NOT NULL, label TEXT NOT NULL, search_text TEXT NOT NULL,
                    chunk_id INTEGER NOT NULL REFERENCES chunks(id), line_no INTEGER NOT NULL,
                    order_key TEXT NOT NULL, UNIQUE(domain, input_key)
                );
                CREATE TABLE IF NOT EXISTS entity_keys (
                    record_id INTEGER NOT NULL REFERENCES records(id), entity_key TEXT NOT NULL,
                    PRIMARY KEY(record_id, entity_key)
                );
                CREATE INDEX IF NOT EXISTS records_filters ON records(domain, provider, outcome);
                CREATE INDEX IF NOT EXISTS records_artist ON records(artist_uuid);
                CREATE INDEX IF NOT EXISTS entity_lookup ON entity_keys(entity_key, record_id);
            """)
            db.commit()

    def connect(self):
        db = sqlite3.connect(self.database, timeout=10)
        db.row_factory = sqlite3.Row
        return db

    def chunks(self):
        # Only the runner's completed, direct domain chunks are eligible. Neither
        # unfinished writes nor symlinks can broaden the reader's file boundary.
        return sorted(path for path in self.run.glob("*/*.jsonl.gz")
                      if path.is_file() and not path.is_symlink()
                      and not path.parent.is_symlink()
                      and path.resolve().parent.parent == self.run)

    def scan_once(self):
        """Commit each complete chunk atomically; a bad chunk does not hide later files."""
        try:
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
        previous = db.execute("SELECT id,order_key,line_no FROM records WHERE domain=? AND input_key=?",
                              (domain, record["inputKey"])).fetchone()
        # Discovery order can differ from append order when indexing a growing run.
        if previous and (previous["order_key"], previous["line_no"]) >= (order_key, line_no):
            return
        labels = self._labels(record.get("proposal"))
        label = labels[0] if labels else (record.get("reason") or record["inputKind"])[:240]
        values = (domain, record["inputKey"], record["inputKind"], record["inputUuid"],
                  record["artistUuid"], record["artistName"], record["provider"], record["outcome"],
                  (record.get("reason") or "")[:500], record["method"][:256], label,
                  " ".join([record["artistName"], record["inputKey"], *labels]).casefold()[:4096],
                  chunk_id, line_no, order_key)
        if previous:
            record_id = previous["id"]
            db.execute("""UPDATE records SET domain=?,input_key=?,input_kind=?,input_uuid=?,
                artist_uuid=?,artist_name=?,provider=?,outcome=?,reason=?,method=?,label=?,
                search_text=?,chunk_id=?,line_no=?,order_key=? WHERE id=?""", (*values, record_id))
            db.execute("DELETE FROM entity_keys WHERE record_id=?", (record_id,))
        else:
            record_id = db.execute("""INSERT INTO records(domain,input_key,input_kind,input_uuid,
                artist_uuid,artist_name,provider,outcome,reason,method,label,search_text,
                chunk_id,line_no,order_key) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", values).lastrowid
        db.executemany("INSERT INTO entity_keys VALUES(?,?)", ((record_id, key) for key in sorted(set(keys))))

    @classmethod
    def _labels(cls, proposal):
        labels = []

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
        for parameter, column in (("provider", "provider"), ("domain", "domain"), ("outcome", "outcome")):
            if filters.get(parameter):
                conditions.append(f"r.{column}=?")
                arguments.append(filters[parameter])
        if filters.get("artist"):
            conditions.append("(r.artist_uuid=? OR lower(r.artist_name) LIKE ? ESCAPE '\\')")
            arguments.extend((filters["artist"], self._like(filters["artist"].lower())))
        if filters.get("q"):
            conditions.append("r.search_text LIKE ? ESCAPE '\\'")
            arguments.append(self._like(filters["q"].casefold()))
        if filters.get("entityKey"):
            conditions.append("EXISTS(SELECT 1 FROM entity_keys e WHERE e.record_id=r.id AND e.entity_key=?)")
            arguments.append(filters["entityKey"])
        where = " WHERE " + " AND ".join(conditions) if conditions else ""
        limit = max(1, min(int(filters.get("limit", 40)), 100))
        offset = max(0, int(filters.get("offset", 0)))
        with closing(self.connect()) as db:
            total = db.execute("SELECT count(*) FROM records r JOIN chunks c ON c.id=r.chunk_id" + where, arguments).fetchone()[0]
            rows = db.execute("SELECT r.id,r.domain,r.input_key,r.input_kind,r.input_uuid,r.artist_uuid,"
                              "r.artist_name,r.provider,r.outcome,r.reason,r.method,r.label FROM records r JOIN chunks c ON c.id=r.chunk_id"
                              + where + " ORDER BY r.id DESC LIMIT ? OFFSET ?", (*arguments, limit, offset)).fetchall()
            items = []
            for row in rows:
                item = dict(row)
                item["entityKeys"] = [value[0] for value in db.execute(
                    "SELECT entity_key FROM entity_keys WHERE record_id=? ORDER BY entity_key", (row["id"],))]
                items.append(item)
        return {"items": items, "total": total, "limit": limit, "offset": offset}

    @staticmethod
    def _like(value):
        return "%" + value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"

    def detail(self, record_id):
        with closing(self.connect()) as db:
            row = db.execute("""SELECT r.*,c.path,c.bytes,c.mtime_ns,c.state FROM records r
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
            status = {"activeFile": self._active_file, "lastScan": self._last_scan, "error": self._scan_error}
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
        with closing(self.connect()) as db:
            db.execute("BEGIN")
            source = " FROM records r JOIN chunks c ON c.id=r.chunk_id WHERE c.state='indexed' "
            indexed = {(row["provider"], row["domain"]): row["n"] for row in db.execute(
                "SELECT r.provider,r.domain,count(*) n" + source + "GROUP BY r.provider,r.domain")}
            chunks = [dict(row) for row in db.execute("SELECT path,state,rows,error FROM chunks ORDER BY path")]
            facets = {name: [dict(row) for row in db.execute(
                f"SELECT r.{name} value,count(*) count" + source + f"GROUP BY r.{name} ORDER BY r.{name}")]
                for name in ("domain", "provider", "outcome")}
            facets["artists"] = [dict(row) for row in db.execute(
                "SELECT r.artist_uuid value,r.artist_name label,count(*) count" + source
                + "GROUP BY r.artist_uuid,r.artist_name ORDER BY r.artist_name")]
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
            except sqlite3.Error:
                return self.send_json(503, {"error": "Reader index is temporarily unavailable."})

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
    args = parser.parse_args()
    if not args.run.is_absolute() or not args.run.is_dir():
        parser.error("--run must name an absolute, existing directory.")
    index = ShadowIndex(args.run)
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
