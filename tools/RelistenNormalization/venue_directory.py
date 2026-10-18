#!/usr/bin/env python3
"""Browse retained venue hypotheses and their captured recording/show homes.

The C# venue adapter owns place hypotheses, history and ambiguity. This rebuildable
projection adds literal search and catalog links; it never allocates venue identity.
Full proposals/native evidence stay in their original immutable gzip chunks.
"""
import argparse
from contextlib import closing
from functools import lru_cache
import gzip
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import shutil
import sqlite3
import unicodedata
from urllib.parse import parse_qs, urlencode, urlsplit

VERSION = "retained-venue-directory-v1"
FLOOR = 4.5 * 1024**3


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def folded(value):
    """Literal retrieval spelling, unrelated to physical-place qualification."""
    return " ".join(unicodedata.normalize("NFKC", value or "").casefold().split())


def file_hash(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


class VenueDirectoryBuilder:
    """Commit one immutable input chunk at a time so a floor stop is resumable."""

    def __init__(self, run, raw, database, minimum_free=FLOOR):
        self.run = Path(run).resolve()
        self.raw = Path(raw).resolve()
        self.database = Path(database).resolve()
        self.minimum_free = minimum_free

    def build(self, resume=False):
        manifest = json.loads((self.run / "manifest.json").read_text())
        if manifest["state"] != "completed":
            raise ValueError("Venue input must be a completed retained run")
        raw_manifest = self.raw / "core-inputs-manifest-v1.json"
        raw = json.loads(raw_manifest.read_text())
        plans = {item["id"]: item for item in raw["plan"]}
        sources = [item for item in raw["completedShards"] if plans[item["shardId"]]["domain"] == "sources"]
        self.database.parent.mkdir(parents=True, exist_ok=True)
        if self.database.exists() and not resume:
            raise FileExistsError("Use --resume for this existing partial projection")
        identity = {"version": VERSION, "sourceRun": str(self.run), "rawRun": str(self.raw),
                    "sourceManifestSha256": file_hash(self.run / "manifest.json"),
                    "rawManifestSha256": file_hash(raw_manifest)}
        self._space()
        with closing(sqlite3.connect(self.database)) as db:
            db.execute("PRAGMA journal_mode=DELETE")
            db.execute("PRAGMA temp_store=MEMORY")
            self._schema(db)
            previous = dict(db.execute("SELECT key,value FROM metadata"))
            if previous:
                if any(json.loads(previous.get(key, "null")) != value for key, value in identity.items()):
                    raise ValueError("Resume input differs from the original projection")
            self._metadata(db, **identity, buildState="building", pauseReason=None, expectedSourceShards=len(sources), sourceMethods=manifest.get("methods", {}))
            db.commit()
            try:
                for chunk in sorted((self.run / "venues").glob("*.jsonl.gz")):
                    self._chunk(db, "venues", chunk, self._venue_rows)
                for shard in sources:
                    path = self.raw / shard["gzipPath"]
                    receipt_path = self.raw / shard["receipt"]
                    if file_hash(receipt_path) != shard["receiptFingerprint"]["sha256"]:
                        raise ValueError("Captured source receipt changed")
                    self._chunk(db, "sources", path, self._source_rows,
                                shard["compressed"]["sha256"], shard["actualRows"])
                self._space()
                self._refresh_counts(db)
                self._metadata(db, buildState="completed", expectedSourceShards=len(sources))
                db.commit()
            except (RuntimeError, ValueError, sqlite3.Error) as error:
                db.rollback()
                self._record_stats(db)
                self._metadata(db, buildState="paused" if isinstance(error, RuntimeError) else "failed", pauseReason=str(error))
                db.commit()
                raise
        return VenueDirectory(self.database, "", "").stats()

    @staticmethod
    def _schema(db):
        db.executescript("""
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS completed_chunks(kind TEXT,path TEXT,bytes INTEGER,mtime INTEGER,
                rows INTEGER,PRIMARY KEY(kind,path));
            CREATE TABLE IF NOT EXISTS venues(uuid TEXT PRIMARY KEY,legacy_id INTEGER UNIQUE,
                place_key TEXT,name TEXT,location TEXT,city TEXT,region TEXT,country TEXT,address TEXT,
                scope TEXT,grouping_basis TEXT,outcome TEXT,reason TEXT,artist_uuid TEXT,artist_name TEXT,
                method TEXT,history_names TEXT,history_count INTEGER,unknown_count INTEGER,
                alternative_count INTEGER,alternatives_truncated INTEGER,search_text TEXT,
                chunk TEXT,line INTEGER,source_count INTEGER DEFAULT 0,show_count INTEGER DEFAULT 0);
            CREATE INDEX IF NOT EXISTS venues_place ON venues(place_key);
            CREATE INDEX IF NOT EXISTS venues_artist ON venues(artist_uuid);
            CREATE TABLE IF NOT EXISTS artists(id INTEGER PRIMARY KEY,uuid TEXT,name TEXT);
            CREATE TABLE IF NOT EXISTS recordings(source_uuid TEXT PRIMARY KEY,artist_id INTEGER,
                source_venue_id INTEGER,show_venue_id INTEGER,show_uuid TEXT,
                source_date TEXT,show_date TEXT);
            CREATE INDEX IF NOT EXISTS recordings_venue ON recordings(source_venue_id);
            CREATE INDEX IF NOT EXISTS recordings_show_venue ON recordings(show_venue_id);
        """)

    @staticmethod
    def _metadata(db, **values):
        db.executemany("INSERT OR REPLACE INTO metadata VALUES(?,?)",
                       [(key, encoded(value)) for key, value in values.items()])

    def _space(self):
        if shutil.disk_usage(self.database.parent).free < self.minimum_free:
            raise RuntimeError("Paused to preserve the host disk reserve; resume this projection")

    def _chunk(self, db, kind, path, reader, expected_sha=None, expected_rows=None):
        self._space()
        stat = path.stat()
        prior = db.execute("SELECT bytes,mtime FROM completed_chunks WHERE kind=? AND path=?", (kind, str(path))).fetchone()
        if prior:
            if prior != (stat.st_size, stat.st_mtime_ns):
                raise ValueError("Previously projected immutable chunk changed")
            return
        if expected_sha and file_hash(path) != expected_sha:
            raise ValueError("Captured source chunk changed")
        rows = reader(db, path)
        after = path.stat()
        if (stat.st_size, stat.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise ValueError("Input changed during projection")
        if expected_rows is not None and rows != expected_rows:
            raise ValueError("Source shard count differs from captured receipt")
        db.execute("INSERT INTO completed_chunks VALUES(?,?,?,?,?)", (kind, str(path), stat.st_size, stat.st_mtime_ns, rows))
        db.commit()
        print(encoded({"kind": kind, "chunk": path.name, "rows": rows}), flush=True)

    def _venue_rows(self, db, path):
        rows = 0
        with gzip.open(path, "rt", encoding="utf-8") as stream:
            for line, text in enumerate(stream, 1):
                record = json.loads(text)
                if record.get("domain") != "venues":
                    raise ValueError("Unexpected outcome in venue chunk")
                native = (record.get("evidence") or {}).get("nativeVenue") or {}
                proposal = record.get("proposal") or {}
                locality = proposal.get("locality") or {}
                history = [item["name"] for item in proposal.get("pastNames", [])]
                name = proposal.get("displayName") or native.get("name") or "Unnamed venue"
                location = native.get("location") or locality.get("originalWording") or ""
                search = folded(" ".join(str(value or "") for value in
                    [name, location, *history, locality.get("city"), locality.get("region"), locality.get("countryCode"), locality.get("address")]))
                db.execute("""INSERT OR REPLACE INTO venues(
                    uuid,legacy_id,place_key,name,location,city,region,country,address,scope,grouping_basis,
                    outcome,reason,artist_uuid,artist_name,method,history_names,history_count,unknown_count,
                    alternative_count,alternatives_truncated,search_text,chunk,line) VALUES(""" + ",".join("?" * 24) + ")", (
                    record["inputUuid"], native.get("id"), proposal.get("proposedPlaceKey"), name, location,
                    locality.get("city"), locality.get("region"), locality.get("countryCode"), locality.get("address"),
                    proposal.get("scope", "unknown"), proposal.get("groupingBasis", "unavailable"), record["outcome"],
                    record.get("reason"), record["artistUuid"], record["artistName"], record["method"],
                    "\n".join(history), len(history), len(proposal.get("protectedUnknowns", [])),
                    proposal.get("alternativeCount", 0), int(proposal.get("alternativesTruncated", False)), search,
                    str(path.relative_to(self.run)), line))
                rows += 1
                if rows % 1000 == 0:
                    self._space()
        return rows

    def _source_rows(self, db, path):
        rows = 0
        with gzip.open(path, "rt", encoding="utf-8") as stream:
            for text in stream:
                record = json.loads(text)
                if record["kind"] == "artist":
                    artist = record["row"]
                    db.execute("INSERT OR REPLACE INTO artists VALUES(?,?,?)", (artist["id"], artist["uuid"], artist["name"]))
                if record["kind"] != "source_sample":
                    continue
                source = record["row"]
                show = record.get("show_context") or {}
                if show and (source.get("show_id") is None or show.get("id") != source["show_id"] or show.get("artist_id", source["artist_id"]) != source["artist_id"]):
                    raise ValueError("Captured source/show context disagrees")
                db.execute("INSERT OR REPLACE INTO recordings VALUES(?,?,?,?,?,?,?)", (
                    source["uuid"], source["artist_id"], source.get("venue_id"), show.get("venue_id"),
                    show.get("uuid"), source.get("display_date"), show.get("display_date") or show.get("date")))
                rows += 1
                if rows % 1000 == 0:
                    self._space()
        return rows

    @staticmethod
    def _refresh_counts(db):
        # Distinct captured source UUIDs and show UUIDs are browsing counts. They
        # are not accepted performances or evidence that venue records co-refer.
        db.execute("""UPDATE venues SET
            source_count=(SELECT count(*) FROM recordings r WHERE r.source_venue_id=venues.legacy_id OR r.show_venue_id=venues.legacy_id),
            show_count=(SELECT count(DISTINCT show_uuid) FROM recordings r WHERE r.source_venue_id=venues.legacy_id OR r.show_venue_id=venues.legacy_id)""")
        VenueDirectoryBuilder._record_stats(db)

    @staticmethod
    def _record_stats(db):
        stats = {"venueRecords": db.execute("SELECT count(*) FROM venues").fetchone()[0],
                 "placeHypotheses": db.execute("SELECT count(DISTINCT place_key) FROM venues").fetchone()[0],
                 "artists": db.execute("SELECT count(DISTINCT artist_uuid) FROM venues").fetchone()[0],
                 "recordings": db.execute("SELECT count(*) FROM recordings").fetchone()[0],
                 "venuesWithRecordings": db.execute("SELECT count(*) FROM venues WHERE source_count>0").fetchone()[0],
                 "historicalNames": db.execute("SELECT coalesce(sum(history_count),0) FROM venues").fetchone()[0],
                 "outcomes": dict(db.execute("SELECT outcome,count(*) FROM venues GROUP BY outcome")),
                 "scopes": dict(db.execute("SELECT scope,count(*) FROM venues GROUP BY scope"))}
        VenueDirectoryBuilder._metadata(db, coverage=stats)


@lru_cache(maxsize=32)
def retained_outcome(run, chunk, line):
    with gzip.open(Path(run) / chunk, "rt", encoding="utf-8") as stream:
        for number, text in enumerate(stream, 1):
            if number == line:
                return json.loads(text)
    raise ValueError("Retained venue evidence is unavailable")


class VenueDirectory:
    def __init__(self, database, files_url, core_url):
        self.database = Path(database).resolve()
        self.files_url = files_url.rstrip("/") + "/"
        self.core_url = core_url.rstrip("/") + "/"

    def connect(self):
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True, timeout=5)
        db.row_factory = sqlite3.Row
        db.create_function("folded", 1, folded, deterministic=True)
        db.execute("BEGIN")
        return db

    @staticmethod
    def page(parameters):
        return max(0, int(parameters.get("offset", 0))), max(1, min(100, int(parameters.get("limit", 30))))

    def stats(self):
        with closing(self.connect()) as db:
            result = {row["key"]: json.loads(row["value"]) for row in db.execute("SELECT * FROM metadata")}
        result.update(result.pop("coverage", {}))
        result["countsReady"] = result.get("buildState") == "completed"
        if not result["countsReady"]:
            result["venuesWithRecordings"] = None
        result.update(filesUrl=self.files_url, coreUrl=self.core_url)
        return result

    def search(self, parameters):
        offset, limit = self.page(parameters)
        conditions = []
        values = []
        for parameter, expression in (("q", "v.search_text"), ("locality", "v.location")):
            value = folded(parameters.get(parameter, ""))
            if value:
                # Literal substring matching keeps user '%'/'_' text literal and
                # never promotes a retrieval hit into a physical identity claim.
                if parameter == "locality":
                    expression = "folded(coalesce(v.location,'')||' '||coalesce(v.city,'')||' '||coalesce(v.region,'')||' '||coalesce(v.country,''))"
                for word in value.split():
                    conditions.append(f"instr({expression},?)>0")
                    values.append(word)
        artist = parameters.get("artist", "").strip()
        if artist:
            conditions.append("(v.artist_uuid=? OR instr(folded(v.artist_name),?)>0)")
            values.extend([artist, folded(artist)])
        for parameter in ("scope", "outcome"):
            if parameters.get(parameter):
                conditions.append("v." + parameter + "=?")
                values.append(parameters[parameter])
        where = " WHERE " + " AND ".join(conditions) if conditions else ""
        with closing(self.connect()) as db:
            total = db.execute("SELECT count(*) FROM venues v" + where, values).fetchone()[0]
            rows = [dict(row) for row in db.execute("SELECT v.* FROM venues v" + where + " ORDER BY source_count DESC,name,uuid LIMIT ? OFFSET ?", values + [limit, offset])]
            ready = self._complete(db)
        if not ready:
            for row in rows:
                row.update(source_count=None, show_count=None)
        return {"rows": rows, "total": total, "offset": offset, "limit": limit,
                "identityBasis": "retained_unaccepted_place_hypotheses", "countsReady": ready}

    @staticmethod
    def _complete(db):
        row = db.execute("SELECT value FROM metadata WHERE key='buildState'").fetchone()
        return row is not None and json.loads(row[0]) == "completed"

    def evidence(self, identifier):
        with closing(self.connect()) as db:
            venue = db.execute("SELECT chunk,line FROM venues WHERE uuid=?", (identifier,)).fetchone()
            root = db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()
        if venue is None:
            return None
        outcome = retained_outcome(json.loads(root[0]), venue["chunk"], venue["line"])
        if outcome["inputUuid"] != identifier or outcome["domain"] != "venues":
            raise ValueError("Evidence locator no longer describes this venue")
        return outcome

    def detail(self, identifier, parameters):
        offset, limit = self.page(parameters)
        with closing(self.connect()) as db:
            selected = db.execute("SELECT * FROM venues WHERE uuid=?", (identifier,)).fetchone()
            if selected is None:
                return None
            venue = dict(selected)
            ready = self._complete(db)
            source_run = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0])
            related = [dict(row) for row in db.execute("SELECT uuid,name,location,artist_name,artist_uuid,outcome,grouping_basis FROM venues WHERE place_key=? ORDER BY artist_name,uuid LIMIT 100", (venue["place_key"],))]
            related_total = db.execute("SELECT count(*) FROM venues WHERE place_key=?", (venue["place_key"],)).fetchone()[0]
            conditions = ["(r.source_venue_id=? OR r.show_venue_id=?)"]
            values = [venue["legacy_id"], venue["legacy_id"]]
            artist = parameters.get("artist", "").strip()
            if artist:
                conditions.append("(a.uuid=? OR instr(folded(a.name),?)>0)")
                values.extend([artist, folded(artist)])
            where = " WHERE " + " AND ".join(conditions)
            join = " FROM recordings r LEFT JOIN artists a ON a.id=r.artist_id"
            total = db.execute("SELECT count(*)" + join + where, values).fetchone()[0]
            recordings = [dict(row) for row in db.execute("SELECT r.*,a.uuid artist_uuid,a.name artist_name" + join + where + " ORDER BY coalesce(source_date,show_date) DESC,source_uuid LIMIT ? OFFSET ?", values + [limit, offset])]
        outcome = retained_outcome(source_run, venue["chunk"], venue["line"])
        if outcome["inputUuid"] != identifier or outcome["domain"] != "venues":
            raise ValueError("Evidence locator no longer describes this venue")
        if not ready:
            venue.update(source_count=None, show_count=None)
        proposal = outcome.get("proposal") or {}
        venue.update(history=proposal.get("pastNames", []), locality=proposal.get("locality", {}),
                     protected_unknowns=proposal.get("protectedUnknowns", []), enrichment_tasks=proposal.get("enrichmentTasks", []),
                     coordinates=proposal.get("coordinates"), provider_reference=proposal.get("providerReference"),
                     accepted_identity=proposal.get("acceptedIdentity", False),
                     alternatives=proposal.get("alternatives", []))
        for recording in recordings:
            recording["relationship"] = "recording_and_show_venue" if recording["source_venue_id"] == recording["show_venue_id"] == venue["legacy_id"] else "recording_venue" if recording["source_venue_id"] == venue["legacy_id"] else "show_venue"
            recording["filesUrl"] = self.files_url + "?" + urlencode({"domain": "file_contents", "entityKey": "source:" + recording["source_uuid"]})
            recording["coreUrl"] = self.core_url + "?" + urlencode({"domain": "performances", "entityKey": "source:" + recording["source_uuid"]})
        return {"venue": venue, "relatedRecords": related, "relatedTotal": related_total,
                "recordings": recordings, "recordingsTotal": total, "recordingsScope": "selected_legacy_venue_record", "recordingsComplete": ready, "countsReady": ready, "offset": offset, "limit": limit,
                "evidenceUrl": "/api/evidence?" + urlencode({"id": identifier}),
                "coreUrl": self.core_url, "filesUrl": self.files_url}


def handler_for(directory):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            url = urlsplit(self.path)
            params = {key: values[-1] for key, values in parse_qs(url.query).items()}
            try:
                if url.path == "/":
                    self.respond(200, Path(__file__).with_name("venue-directory.html").read_bytes(),
                                 "text/html; charset=utf-8")
                    return

                if url.path == "/api/stats":
                    result = directory.stats()
                elif url.path == "/api/venues":
                    result = directory.search(params)
                elif url.path == "/api/venue":
                    result = directory.detail(params.get("id", ""), params)
                elif url.path == "/api/evidence":
                    result = directory.evidence(params.get("id", ""))
                else:
                    self.respond(404, b'{"error":"Not found"}')
                    return

                if result is None:
                    self.respond(404, b'{"error":"Unknown venue"}')
                    return
                self.respond(200, encoded(result).encode())
            except (ValueError, sqlite3.Error) as error:
                self.respond(400, encoded({"error": str(error)}).encode())

        def respond(self, status, body, content_type="application/json; charset=utf-8"):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    build = sub.add_parser("build")
    build.add_argument("--run", required=True)
    build.add_argument("--raw", required=True)
    build.add_argument("--database", required=True)
    build.add_argument("--resume", action="store_true")
    serve = sub.add_parser("serve")
    serve.add_argument("--database", required=True)
    serve.add_argument("--files-url", required=True)
    serve.add_argument("--core-url", required=True)
    serve.add_argument("--bind", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=18770)
    args = parser.parse_args()

    if args.command == "build":
        result = VenueDirectoryBuilder(args.run, args.raw, args.database).build(args.resume)
        print(encoded(result), flush=True)
    else:
        directory = VenueDirectory(args.database, args.files_url, args.core_url)
        print(encoded({"listening": f"http://{args.bind}:{args.port}", "stats": directory.stats()}), flush=True)
        ThreadingHTTPServer((args.bind, args.port), handler_for(directory)).serve_forever()


if __name__ == "__main__":
    main()
