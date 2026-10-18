#!/usr/bin/env python3
"""Browse draft cross-artist credit name groups from a completed shadow run.

This projection uses the NormalizedPeopleService.NameKey contract (NFKC, invariant
lowercase, collapsed whitespace). A shared name is a retrieval group, not proof of
one person. Explicit aliases stay attached to the credit that supplied them; they
make that group searchable but never transitively merge groups. Source UUIDs and
source-local person keys are preserved. Nothing writes accepted catalog identities.

Build once into a new SQLite file, then serve read-only. See people-directory.md for
the HTTP contract, coverage limits and links to the source and playable-file readers.
"""

import argparse
import gzip
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import shutil
import sqlite3
import unicodedata
from urllib.parse import parse_qs, urlencode, urlsplit

VERSION = "people-name-directory-v2"


def name_key(name):
    """Mirror the catalog's name retrieval key; retain punctuation and accents."""
    return " ".join(unicodedata.normalize("NFKC", name).lower().split())


def group_id(key):
    return "draft-name:" + hashlib.sha256(key.encode()).hexdigest()[:24]


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


class DirectoryBuilder:
    """Stream credited actors into a small projection, with exact evidence locators."""

    def __init__(self, run, output, minimum_free=4.5 * 1024**3):
        self.run = Path(run).resolve()
        self.output = Path(output).resolve()
        self.minimum_free = minimum_free

    def build(self):
        manifest = json.loads((self.run / "manifest.json").read_text())
        if manifest["state"] != "completed":
            raise ValueError("Build the directory from a completed shadow run")
        self.output.parent.mkdir(parents=True, exist_ok=True)
        if self.output.exists():
            raise FileExistsError(self.output)
        self._check_space()
        with sqlite3.connect(self.output) as db:
            db.executescript("""
                PRAGMA journal_mode=DELETE;
                CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE groups(id TEXT PRIMARY KEY, name_key TEXT UNIQUE NOT NULL,
                    display_name TEXT NOT NULL, recording_count INTEGER DEFAULT 0,
                    artist_count INTEGER DEFAULT 0, credit_count INTEGER DEFAULT 0);
                CREATE TABLE names(group_id TEXT, wording TEXT, lookup TEXT,
                    basis TEXT, PRIMARY KEY(group_id,wording,basis));
                CREATE TABLE artists(uuid TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE processed_sources(uuid TEXT PRIMARY KEY);
                CREATE TABLE credits(id INTEGER PRIMARY KEY, group_id TEXT NOT NULL,
                    source_uuid TEXT NOT NULL, artist_uuid TEXT, name TEXT, role TEXT,
                    person_key TEXT, evidence TEXT, aliases TEXT,
                    chunk TEXT NOT NULL, line INTEGER NOT NULL);
                CREATE INDEX credits_source ON credits(source_uuid);
            """)
            source_rows = 0
            chunks = sorted((self.run / "credits").glob("*.jsonl.gz"))
            for index, chunk in enumerate(chunks):
                self._check_space()
                with gzip.open(chunk, "rt", encoding="utf-8") as stream:
                    for line, text in enumerate(stream, 1):
                        record = json.loads(text)
                        if record.get("inputKind") != "source_sample":
                            continue
                        source_rows += 1
                        source_uuid = record["inputUuid"]
                        # Completed runs can retain replay chunks. Last source result wins,
                        # including a later result that no longer proposes any credits.
                        if (
                            db.execute(
                                "INSERT OR IGNORE INTO processed_sources VALUES(?)",
                                (source_uuid,),
                            ).rowcount
                            == 0
                        ):
                            db.execute(
                                "DELETE FROM credits WHERE source_uuid=?",
                                (source_uuid,),
                            )
                        self._add_record(
                            db, record, str(chunk.relative_to(self.run)), line
                        )
                db.commit()
                print(
                    encoded({"chunks": index + 1, "sourceRows": source_rows}),
                    flush=True,
                )
            # Build the searchable spellings only from the surviving source results.
            # A replay that removes an alias must remove its search hit as well.
            db.execute(
                "INSERT OR IGNORE INTO names SELECT c.group_id,c.name,g.name_key,'credited_name' FROM credits c JOIN groups g ON g.id=c.group_id"
            )
            for identifier, aliases_json in db.execute(
                "SELECT group_id,aliases FROM credits WHERE aliases<>'[]'"
            ):
                for alias in json.loads(aliases_json):
                    wording = alias["wording"]
                    if name_key(wording):
                        db.execute(
                            "INSERT OR IGNORE INTO names VALUES(?,?,?,?)",
                            (
                                identifier,
                                wording,
                                name_key(wording),
                                "explicit_source_alias",
                            ),
                        )
            db.executescript("""
                CREATE INDEX credits_group_source ON credits(group_id,source_uuid);
                DELETE FROM groups WHERE NOT EXISTS(SELECT 1 FROM credits WHERE credits.group_id=groups.id);
                DELETE FROM names WHERE group_id NOT IN(SELECT id FROM groups);
                CREATE INDEX credits_group_artist ON credits(group_id,artist_uuid);
                CREATE INDEX names_lookup ON names(lookup);
                UPDATE groups SET
                    recording_count=(SELECT count(DISTINCT source_uuid) FROM credits c WHERE c.group_id=groups.id),
                    artist_count=(SELECT count(DISTINCT artist_uuid) FROM credits c WHERE c.group_id=groups.id),
                    credit_count=(SELECT count(*) FROM credits c WHERE c.group_id=groups.id);
                CREATE INDEX groups_recordings ON groups(recording_count DESC,id);
            """)
            metadata = {
                "version": VERSION,
                "sourceRun": str(self.run),
                "sourceManifestSha256": hashlib.sha256(
                    (self.run / "manifest.json").read_bytes()
                ).hexdigest(),
                "sourceMethods": manifest.get("methods", {}),
                "sourceRows": db.execute(
                    "SELECT count(*) FROM processed_sources"
                ).fetchone()[0],
                "processedResults": source_rows,
                "chunks": len(chunks),
                "knownIssues": [
                    "Equal names may belong to different people. These are draft name groups, not accepted identities.",
                    "Different names remain separate. Explicit source aliases are searchable and related groups are shown without merging them.",
                    "The retained local credit parser can truncate names, miss prose credits, or credit equipment and groups as people.",
                    "This source run predates the newer people identity matcher and model-assisted credit replay. No model identity claims are added here.",
                    "Recording counts count distinct source UUIDs, not performances. Several recordings can cover one show.",
                ],
            }
            db.executemany(
                "INSERT INTO metadata VALUES(?,?)",
                [(key, encoded(value)) for key, value in metadata.items()],
            )
            db.commit()
        metadata.update(
            SourceHomeEnrichment(self.run, self.output, self.minimum_free).enrich()
        )
        return metadata

    def _check_space(self):
        if shutil.disk_usage(self.output.parent).free < self.minimum_free:
            raise RuntimeError(
                "Directory build stopped to preserve the host disk reserve"
            )

    def _add_record(self, db, record, chunk, line):
        facts = (record.get("evidence", {}).get("creditExtraction") or {}).get(
            "credits", []
        )
        for credit in (record.get("proposal") or {}).get("credits", []):
            key = name_key(credit["name"])
            if not key:
                continue
            identifier = group_id(key)
            db.execute(
                "INSERT OR IGNORE INTO groups(id,name_key,display_name) VALUES(?,?,?)",
                (identifier, key, credit["name"]),
            )
            actor_facts = next(
                (
                    item.get("actorFacts") or {}
                    for item in facts
                    if item.get("name") == credit["name"]
                    and item.get("role") == credit["role"]
                    and item.get("evidence") == credit.get("evidence")
                ),
                {},
            )
            aliases = actor_facts.get("aliases", [])
            artist_uuid = credit.get("artistUuid") or record.get("artistUuid")
            if artist_uuid:
                db.execute(
                    "INSERT OR IGNORE INTO artists VALUES(?,?)",
                    (artist_uuid, record.get("artistName") or artist_uuid),
                )
            db.execute(
                """INSERT INTO credits(group_id,source_uuid,artist_uuid,name,role,person_key,
                evidence,aliases,chunk,line) VALUES(?,?,?,?,?,?,?,?,?,?)""",
                (
                    identifier,
                    credit["sourceUuid"],
                    artist_uuid,
                    credit["name"],
                    credit["role"],
                    credit.get("personEntityKey"),
                    encoded(credit.get("evidence")),
                    encoded(aliases),
                    chunk,
                    line,
                ),
            )


class SourceHomeEnrichment:
    """Add captured recording homes without rebuilding credits or changing ownership.

    Existing readers can keep serving during this explicit enrichment phase. The
    final transaction adds all homes and its coverage metadata together. Later
    retained source outcomes replace earlier homes, including unresolved outcomes.
    Dates remain the original literals: no completion of partial or unknown dates.
    """

    def __init__(self, run, database, minimum_free=4.5 * 1024**3):
        self.run = Path(run).resolve()
        self.database = Path(database).resolve()
        self.minimum_free = minimum_free

    def enrich(self):
        manifest_sha = hashlib.sha256(
            (self.run / "manifest.json").read_bytes()
        ).hexdigest()
        with sqlite3.connect(self.database) as db:
            expected = db.execute(
                "SELECT value FROM metadata WHERE key='sourceManifestSha256'"
            ).fetchone()
            if expected is None or json.loads(expected[0]) != manifest_sha:
                raise ValueError(
                    "Recording homes must come from the same completed core run"
                )
            db.execute("""CREATE TABLE IF NOT EXISTS source_homes(
                source_uuid TEXT PRIMARY KEY, show_uuid TEXT, literal_date TEXT,
                performance_type TEXT, performance_label TEXT, outcome TEXT,
                unresolved TEXT NOT NULL, basis TEXT, chunk TEXT, line INTEGER)""")
            chunks = sorted((self.run / "performances").glob("*.jsonl.gz"))
            results = 0
            for chunk in chunks:
                if shutil.disk_usage(self.database.parent).free < self.minimum_free:
                    raise RuntimeError(
                        "Recording home enrichment stopped to preserve the host disk reserve"
                    )
                with gzip.open(chunk, "rt", encoding="utf-8") as stream:
                    for line, text in enumerate(stream, 1):
                        record = json.loads(text)
                        if record.get("inputKind") != "source_sample":
                            continue
                        proposal = record.get("proposal") or {}
                        source_uuid = record["inputUuid"]
                        if proposal.get("sourceUuid") not in (None, source_uuid):
                            raise ValueError(
                                "Captured recording home has a different source UUID"
                            )
                        db.execute(
                            "INSERT OR REPLACE INTO source_homes VALUES(?,?,?,?,?,?,?,?,?,?)",
                            (
                                source_uuid,
                                proposal.get("showUuid"),
                                proposal.get("literalDate"),
                                proposal.get("performanceType"),
                                proposal.get("performanceLabel"),
                                record.get("outcome"),
                                encoded(proposal.get("unresolved", [])),
                                record.get("evidence", {}).get("basis"),
                                str(chunk.relative_to(self.run)),
                                line,
                            ),
                        )
                        results += 1
            metadata = {
                "version": VERSION,
                "sourceHomeResults": results,
                "sourceHomes": db.execute(
                    "SELECT count(*) FROM source_homes"
                ).fetchone()[0],
                "sourceHomeChunks": len(chunks),
            }
            db.executemany(
                "INSERT OR REPLACE INTO metadata VALUES(?,?)",
                [(key, encoded(value)) for key, value in metadata.items()],
            )
            db.commit()
        return metadata


class PeopleDirectory:
    """Read-only search and recording pages; each request uses its own connection."""

    def __init__(self, database, files_url, core_url):
        self.database = Path(database).resolve()
        self.files_url = files_url.rstrip("/") + "/"
        self.core_url = core_url.rstrip("/") + "/"

    def connect(self):
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)
        db.row_factory = sqlite3.Row
        return db

    @staticmethod
    def page(parameters):
        return max(0, int(parameters.get("offset", "0"))), max(
            1, min(100, int(parameters.get("limit", "30")))
        )

    def stats(self):
        with self.connect() as db:
            result = {
                row["key"]: json.loads(row["value"])
                for row in db.execute("SELECT * FROM metadata")
            }
            result.update(
                groups=db.execute("SELECT count(*) FROM groups").fetchone()[0],
                creditedRecordings=db.execute(
                    "SELECT count(DISTINCT source_uuid) FROM credits"
                ).fetchone()[0],
                creditedArtists=db.execute(
                    "SELECT count(DISTINCT artist_uuid) FROM credits"
                ).fetchone()[0],
                credits=db.execute("SELECT count(*) FROM credits").fetchone()[0],
            )
            result.update(filesUrl=self.files_url, coreUrl=self.core_url)
            return result

    def search(self, parameters):
        offset, limit = self.page(parameters)
        query = name_key(parameters.get("q", ""))
        # instr gives literal substring search, including names containing % or _.
        where = (
            "WHERE EXISTS(SELECT 1 FROM names n WHERE n.group_id=g.id AND instr(n.lookup,?)>0)"
            if query
            else ""
        )
        values = [query] if query else []
        with self.connect() as db:
            total = db.execute(
                f"SELECT count(*) FROM groups g {where}", values
            ).fetchone()[0]
            groups = [
                dict(row)
                for row in db.execute(
                    f"SELECT g.* FROM groups g {where} ORDER BY recording_count DESC,id LIMIT ? OFFSET ?",
                    values + [limit, offset],
                )
            ]
        return {
            "groups": groups,
            "total": total,
            "offset": offset,
            "limit": limit,
            "basis": "inferred_name_group",
        }

    def detail(self, identifier, parameters):
        offset, limit = self.page(parameters)
        artist = parameters.get("artist", "")
        with self.connect() as db:
            row = db.execute(
                "SELECT * FROM groups WHERE id=?", (identifier,)
            ).fetchone()
            if row is None:
                return None
            result = dict(row)
            result["basis"] = "inferred_name_group"
            result["names"] = [
                dict(row)
                for row in db.execute(
                    "SELECT wording,basis FROM names WHERE group_id=? ORDER BY basis,wording",
                    (identifier,),
                )
            ]
            result["artists"] = [
                dict(row)
                for row in db.execute(
                    """SELECT a.uuid,a.name,count(DISTINCT c.source_uuid) recording_count
                FROM credits c JOIN artists a ON a.uuid=c.artist_uuid WHERE group_id=? GROUP BY a.uuid ORDER BY recording_count DESC,a.name""",
                    (identifier,),
                )
            ]
            result["roles"] = [
                dict(row)
                for row in db.execute(
                    "SELECT role,count(DISTINCT source_uuid) recording_count FROM credits WHERE group_id=? GROUP BY role ORDER BY role",
                    (identifier,),
                )
            ]
            result["relatedGroups"] = [
                dict(row)
                for row in db.execute(
                    """SELECT DISTINCT g.id,g.display_name,g.recording_count FROM names mine
                JOIN names other ON mine.lookup=other.lookup JOIN groups g ON g.id=other.group_id
                WHERE mine.group_id=? AND other.group_id<>? ORDER BY g.recording_count DESC,g.id LIMIT 30""",
                    (identifier, identifier),
                )
            ]
            condition = "group_id=?" + (" AND artist_uuid=?" if artist else "")
            values = [identifier] + ([artist] if artist else [])
            result["total"] = db.execute(
                f"SELECT count(DISTINCT source_uuid) FROM credits WHERE {condition}",
                values,
            ).fetchone()[0]
            sources = db.execute(
                f"SELECT source_uuid FROM credits WHERE {condition} GROUP BY source_uuid ORDER BY source_uuid LIMIT ? OFFSET ?",
                values + [limit, offset],
            ).fetchall()
            result["recordings"] = []
            for source in sources:
                source_uuid = source[0]
                credits = [
                    dict(row)
                    for row in db.execute(
                        """SELECT c.*,a.name artist_name FROM credits c LEFT JOIN artists a ON a.uuid=c.artist_uuid
                    WHERE c.group_id=? AND source_uuid=? ORDER BY c.id""",
                        (identifier, source_uuid),
                    )
                ]
                for credit in credits:
                    credit["evidence"] = json.loads(credit["evidence"])
                    credit["aliases"] = json.loads(credit["aliases"])
                    credit["sourceEvidenceUrl"] = (
                        self.core_url
                        + "?"
                        + urlencode(
                            {"domain": "credits", "entityKey": credit["person_key"]}
                        )
                    )
                result["recordings"].append(
                    {
                        "sourceUuid": source_uuid,
                        "capturedHome": self._home(db, source_uuid),
                        "credits": credits,
                        "playableFilesUrl": self.files_url
                        + "?"
                        + urlencode(
                            {
                                "domain": "file_contents",
                                "entityKey": "source:" + source_uuid,
                            }
                        ),
                    }
                )
            result.update(offset=offset, limit=limit)
            return result

    @staticmethod
    def _home(db, source_uuid):
        row = db.execute(
            "SELECT * FROM source_homes WHERE source_uuid=?", (source_uuid,)
        ).fetchone()
        if row is None:
            return None
        return {
            "showUuid": row["show_uuid"],
            "literalDate": row["literal_date"],
            "performanceType": row["performance_type"],
            "performanceLabel": row["performance_label"],
            "outcome": row["outcome"],
            "unresolved": json.loads(row["unresolved"]),
            "basis": row["basis"],
            "chunk": row["chunk"],
            "line": row["line"],
        }


def handler_for(directory):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            url = urlsplit(self.path)
            parameters = {
                key: values[-1] for key, values in parse_qs(url.query).items()
            }
            try:
                if url.path == "/":
                    body = (
                        Path(__file__).with_name("people-directory.html").read_bytes()
                    )
                    self.respond(200, body, "text/html; charset=utf-8")
                    return
                if url.path == "/api/stats":
                    result = directory.stats()
                elif url.path == "/api/people":
                    result = directory.search(parameters)
                elif url.path == "/api/person":
                    result = directory.detail(parameters.get("id", ""), parameters)
                    if result is None:
                        self.respond(
                            404, encoded({"error": "Unknown draft group"}).encode()
                        )
                        return
                else:
                    self.respond(404, b'{"error":"Not found"}')
                    return
                self.respond(200, encoded(result).encode())
            except ValueError:
                self.respond(400, b'{"error":"offset and limit must be integers"}')

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
    build.add_argument("--database", required=True)
    homes = sub.add_parser("enrich-homes")
    homes.add_argument("--run", required=True)
    homes.add_argument("--database", required=True)
    serve = sub.add_parser("serve")
    serve.add_argument("--database", required=True)
    serve.add_argument("--files-url", required=True)
    serve.add_argument("--core-url", required=True)
    serve.add_argument("--bind", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=18768)
    args = parser.parse_args()
    if args.command == "build":
        print(encoded(DirectoryBuilder(args.run, args.database).build()), flush=True)
    elif args.command == "enrich-homes":
        print(
            encoded(SourceHomeEnrichment(args.run, args.database).enrich()), flush=True
        )
    else:
        directory = PeopleDirectory(args.database, args.files_url, args.core_url)
        print(
            encoded(
                {
                    "listening": f"http://{args.bind}:{args.port}",
                    "stats": directory.stats(),
                }
            ),
            flush=True,
        )
        ThreadingHTTPServer(
            (args.bind, args.port), handler_for(directory)
        ).serve_forever()


if __name__ == "__main__":
    main()
