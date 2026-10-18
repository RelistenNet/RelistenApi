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
from contextlib import closing
import gzip
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import shutil
import sqlite3
import unicodedata
from urllib.parse import parse_qs, urlencode, urlsplit

VERSION = "people-name-directory-v3"


def name_key(name):
    """Mirror the catalog's name retrieval key; retain punctuation and accents."""
    return " ".join(unicodedata.normalize("NFKC", name).lower().split())


def group_id(key):
    return "draft-name:" + hashlib.sha256(key.encode()).hexdigest()[:24]


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def retained_actor_facts(record, credit):
    """Read attributed facts from the extractor that supplied this exact credit."""
    evidence = record.get("evidence", {})
    basis = credit.get("interpretationBasis", credit.get("interpretation_basis"))
    if basis == "retained_luna_extraction":
        retained = (evidence.get("model") or {}).get("originalLunaResult") or {}
        extraction = retained.get("extraction") or {}
    else:
        extraction = evidence.get("creditExtraction") or {}
    return next(
        (
            item.get("actorFacts") or {}
            for item in extraction.get("credits", [])
            if item.get("name") == credit["name"]
            and item.get("role") == credit["role"]
            and item.get("evidence") == credit.get("evidence")
        ),
        {},
    )


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
        with closing(sqlite3.connect(self.output)) as db, db:
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
                    chunk TEXT NOT NULL, line INTEGER NOT NULL,
                    interpretation_basis TEXT, credit_key TEXT, origin_run TEXT);
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
            self._rebuild_groups(db)
            self._refresh_stats(db)
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

    def _rebuild_groups(self, db):
        db.execute("DELETE FROM names")
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
        # execute keeps this phase inside the caller's transaction. executescript
        # would commit source replacements before their counts become consistent.
        for statement in (
            "CREATE INDEX IF NOT EXISTS credits_group_source ON credits(group_id,source_uuid)",
            "DELETE FROM groups WHERE NOT EXISTS(SELECT 1 FROM credits WHERE credits.group_id=groups.id)",
            "DELETE FROM names WHERE group_id NOT IN(SELECT id FROM groups)",
            "CREATE INDEX IF NOT EXISTS credits_group_artist ON credits(group_id,artist_uuid)",
            "CREATE INDEX IF NOT EXISTS names_lookup ON names(lookup)",
            """UPDATE groups SET
                recording_count=(SELECT count(DISTINCT source_uuid) FROM credits c WHERE c.group_id=groups.id),
                artist_count=(SELECT count(DISTINCT artist_uuid) FROM credits c WHERE c.group_id=groups.id),
                credit_count=(SELECT count(*) FROM credits c WHERE c.group_id=groups.id)""",
            "CREATE INDEX IF NOT EXISTS groups_recordings ON groups(recording_count DESC,id)",
        ):
            db.execute(statement)

    def _check_space(self):
        if shutil.disk_usage(self.output.parent).free < self.minimum_free:
            raise RuntimeError(
                "Directory build stopped to preserve the host disk reserve"
            )

    @staticmethod
    def _ensure_credit_provenance_columns(db):
        columns = {column[1] for column in db.execute("PRAGMA table_info(credits)")}
        for column in ("interpretation_basis", "credit_key", "origin_run"):
            if column not in columns:
                db.execute(f"ALTER TABLE credits ADD COLUMN {column} TEXT")

    @staticmethod
    def _refresh_stats(db):
        # Counts change only when the projection writer replaces credits. Keep
        # cold full-table scans out of HTTP requests and commit counts with data.
        counts = {
            key: db.execute(query).fetchone()[0]
            for key, query in (
                ("groups", "SELECT count(*) FROM groups"),
                (
                    "creditedRecordings",
                    "SELECT count(DISTINCT source_uuid) FROM credits",
                ),
                ("creditedArtists", "SELECT count(DISTINCT artist_uuid) FROM credits"),
                ("credits", "SELECT count(*) FROM credits"),
                (
                    "modelCreditMentions",
                    "SELECT count(*) FROM credits WHERE interpretation_basis='retained_luna_extraction'",
                ),
            )
        }
        db.execute(
            "INSERT OR REPLACE INTO metadata VALUES('directoryStats',?)",
            (encoded(counts),),
        )
        return counts

    def _add_record(self, db, record, chunk, line, origin_run=None):
        for credit in (record.get("proposal") or {}).get("credits", []):
            key = name_key(credit["name"])
            if not key:
                continue
            identifier = group_id(key)
            db.execute(
                "INSERT OR IGNORE INTO groups(id,name_key,display_name) VALUES(?,?,?)",
                (identifier, key, credit["name"]),
            )
            actor_facts = retained_actor_facts(record, credit)
            aliases = actor_facts.get("aliases", [])
            artist_uuid = credit.get("artistUuid") or record.get("artistUuid")
            if artist_uuid:
                db.execute(
                    "INSERT OR IGNORE INTO artists VALUES(?,?)",
                    (artist_uuid, record.get("artistName") or artist_uuid),
                )
            db.execute(
                """INSERT INTO credits(group_id,source_uuid,artist_uuid,name,role,person_key,
                evidence,aliases,chunk,line,interpretation_basis,credit_key,origin_run) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
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
                    credit.get("interpretationBasis"),
                    credit.get("entityKey"),
                    origin_run,
                ),
            )


class CreditOverlay:
    """Replace a selected source population with its complete retained credits.

    A replay outcome already contains both local and model interpretations. Replace
    the source's old credits together, then rebuild the derived directory once.
    Source homes and the remaining catalog credits stay intact.
    """

    def __init__(self, run, selection, database, minimum_free=4.5 * 1024**3):
        self.run = Path(run).resolve()
        self.selection = Path(selection).resolve()
        self.database = Path(database).resolve()
        self.writer = DirectoryBuilder(self.run, self.database, minimum_free)

    def apply(self):
        manifest = json.loads((self.run / "manifest.json").read_text())
        if manifest["state"] != "completed":
            raise ValueError("Credit overlay requires a completed retained run")
        sources = json.loads(self.selection.read_text())["sourceUuids"]
        selected = set(sources)
        if len(selected) != len(sources):
            raise ValueError("Credit selection contains duplicate source UUIDs")
        observed = set()
        chunks = sorted((self.run / "credits").glob("*.jsonl.gz"))
        for _, _, record in self._records(chunks):
            source_uuid = record["inputUuid"]
            if record.get("outcome") == "failed":
                raise ValueError(
                    "Failed credit outcomes cannot replace existing credits"
                )
            if (
                source_uuid not in selected
                or record.get("inputKind") != "source_sample"
            ):
                raise ValueError(
                    "Credit outcome is outside the selected source population"
                )
            if any(
                credit["sourceUuid"] != source_uuid
                for credit in (record.get("proposal") or {}).get("credits", [])
            ):
                raise ValueError("Credit outcome changes recording ownership")
            observed.add(source_uuid)
        if observed != selected:
            raise ValueError("Credit overlay is missing selected source outcomes")
        self.writer._check_space()
        with closing(sqlite3.connect(self.database)) as db, db:
            if any(
                not db.execute(
                    "SELECT 1 FROM processed_sources WHERE uuid=?", (source,)
                ).fetchone()
                for source in selected
            ):
                raise ValueError(
                    "Credit overlay includes a source outside this directory"
                )
            self.writer._ensure_credit_provenance_columns(db)
            for chunk, line, record in self._records(chunks):
                self.writer._check_space()
                db.execute(
                    "DELETE FROM credits WHERE source_uuid=?", (record["inputUuid"],)
                )
                self.writer._add_record(
                    db, record, chunk, line, origin_run=str(self.run)
                )
            self.writer._rebuild_groups(db)
            self.writer._refresh_stats(db)
            metadata = {
                "version": VERSION,
                "creditOverlaySources": len(selected),
                "creditOverlayRun": str(self.run),
                "creditOverlayManifestSha256": hashlib.sha256(
                    (self.run / "manifest.json").read_bytes()
                ).hexdigest(),
                "creditOverlaySelectionSha256": hashlib.sha256(
                    self.selection.read_bytes()
                ).hexdigest(),
            }
            issues = json.loads(
                db.execute(
                    "SELECT value FROM metadata WHERE key='knownIssues'"
                ).fetchone()[0]
            )
            metadata["knownIssues"] = [
                issue
                for issue in issues
                if not issue.startswith("This source run predates")
            ]
            model_issue = "Luna tags identify retained model credit proposals on selected recordings. Their quoted evidence is preserved; person identities remain unverified."
            if model_issue not in metadata["knownIssues"]:
                metadata["knownIssues"].append(model_issue)
            db.executemany(
                "INSERT OR REPLACE INTO metadata VALUES(?,?)",
                [(key, encoded(value)) for key, value in metadata.items()],
            )
            db.commit()
        return metadata

    def _records(self, chunks):
        for chunk in chunks:
            with gzip.open(chunk, "rt", encoding="utf-8") as stream:
                for line, text in enumerate(stream, 1):
                    yield str(chunk.relative_to(self.run)), line, json.loads(text)


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
        with closing(sqlite3.connect(self.database)) as db, db:
            expected = db.execute(
                "SELECT value FROM metadata WHERE key='sourceManifestSha256'"
            ).fetchone()
            if expected is None or json.loads(expected[0]) != manifest_sha:
                raise ValueError(
                    "Recording homes must come from the same completed core run"
                )
            DirectoryBuilder._ensure_credit_provenance_columns(db)
            if not db.execute(
                "SELECT 1 FROM metadata WHERE key='directoryStats'"
            ).fetchone():
                DirectoryBuilder._refresh_stats(db)
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
        with closing(self.connect()) as db:
            result = {
                row["key"]: json.loads(row["value"])
                for row in db.execute("SELECT * FROM metadata")
            }
            counts = result.pop("directoryStats", None)
            if counts is None:
                raise ValueError(
                    "Directory count snapshot is missing; refresh the projection"
                )
            result.update(counts)
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
        with closing(self.connect()) as db:
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
        with closing(self.connect()) as db:
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
                        "/api/credit?" + urlencode({"id": credit["id"]})
                        if credit["origin_run"]
                        else self.core_url
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

    def relationships(self, identifier, parameters):
        """Page witnessed pair proposals; name groups and recordings stay independent."""
        offset, limit = self.page(parameters)
        with closing(self.connect()) as db:
            if not db.execute(
                "SELECT 1 FROM sqlite_master WHERE name='context_relationships'"
            ).fetchone():
                return {
                    "relationships": [],
                    "total": 0,
                    "offset": offset,
                    "limit": limit,
                }
            where = "WHERE r.left_group=? OR r.right_group=?" if identifier else ""
            values = (identifier, identifier) if identifier else ()
            total = db.execute(
                f"SELECT count(*) FROM context_relationships r {where}", values
            ).fetchone()[0]
            rows = [
                dict(row)
                for row in db.execute(
                    f"""SELECT r.*,l.display_name left_name,g.display_name right_name
                FROM context_relationships r JOIN groups l ON l.id=r.left_group JOIN groups g ON g.id=r.right_group
                {where} ORDER BY CASE r.relation WHEN 'same_person' THEN 0 WHEN 'different_people' THEN 1 ELSE 2 END,r.id LIMIT ? OFFSET ?""",
                    values + (limit, offset),
                )
            ]
            for row in rows:
                row["evidence"] = json.loads(row["evidence"])
        return {
            "relationships": rows,
            "total": total,
            "offset": offset,
            "limit": limit,
            "basis": "contextual_proposal",
        }

    def relationship(self, identifier):
        """Read exact model input witnesses and the validated decision from its retained row."""
        with closing(self.connect()) as db:
            if not db.execute(
                "SELECT 1 FROM sqlite_master WHERE name='context_relationships'"
            ).fetchone():
                return None
            row = db.execute(
                "SELECT * FROM context_relationships WHERE id=?", (identifier,)
            ).fetchone()
            if row is None:
                return None
            projection = dict(row)
        with (Path(projection["run"]) / "results.jsonl").open(
            encoding="utf-8"
        ) as stream:
            for line, text in enumerate(stream, 1):
                if line == projection["line"]:
                    retained = json.loads(text)
                    break
            else:
                raise ValueError("Retained contextual row is unavailable")
        question = next(
            q for q in retained["input"]["questions"] if q["questionId"] == identifier
        )
        decision = next(
            d for d in retained["result"]["decisions"] if d["questionId"] == identifier
        )
        for side in ("left", "right"):
            actor = question[side]
            actor["sourceEvidenceUrl"] = "/api/credit?" + urlencode(
                {"id": actor["creditId"]}
            )
            actor["playableFilesUrl"] = (
                self.files_url
                + "?"
                + urlencode(
                    {
                        "domain": "file_contents",
                        "entityKey": "source:" + actor["sourceUuid"],
                    }
                )
            )
        return {
            "question": question,
            "decision": decision,
            "cacheKey": retained["result"]["cacheKey"],
            "jobUuid": projection["job_uuid"],
            "basis": "contextual_proposal",
            "recovery": retained.get("recovery"),
        }

    def credit(self, identifier):
        """Return an overlay credit's exact retained source outcome on demand."""
        with closing(self.connect()) as db:
            row = db.execute(
                "SELECT * FROM credits WHERE id=?", (identifier,)
            ).fetchone()
            if row is None:
                return None
            credit = dict(row)
            run = credit["origin_run"] or json.loads(
                db.execute(
                    "SELECT value FROM metadata WHERE key='sourceRun'"
                ).fetchone()[0]
            )
        with gzip.open(Path(run) / credit["chunk"], "rt", encoding="utf-8") as stream:
            for line, text in enumerate(stream, 1):
                if line == credit["line"]:
                    outcome = json.loads(text)
                    break
            else:
                raise ValueError("Retained credit outcome is unavailable")
        if outcome["inputUuid"] != credit["source_uuid"]:
            raise ValueError("Retained credit source does not match the directory")
        credit["evidence"] = json.loads(credit["evidence"])
        credit["aliases"] = json.loads(credit["aliases"])
        return {"credit": credit, "retainedOutcome": outcome}

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
                elif url.path == "/api/relationships":
                    result = directory.relationships(
                        parameters.get("group", ""), parameters
                    )
                elif url.path == "/api/relationship":
                    result = directory.relationship(parameters.get("id", ""))
                    if result is None:
                        self.respond(
                            404, encoded({"error": "Unknown relationship"}).encode()
                        )
                        return
                elif url.path == "/api/credit":
                    result = directory.credit(int(parameters.get("id", "")))
                    if result is None:
                        self.respond(404, encoded({"error": "Unknown credit"}).encode())
                        return
                else:
                    self.respond(404, b'{"error":"Not found"}')
                    return
                self.respond(200, encoded(result).encode())
            except ValueError as error:
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
    build.add_argument("--database", required=True)
    homes = sub.add_parser("enrich-homes")
    homes.add_argument("--run", required=True)
    homes.add_argument("--database", required=True)
    overlay = sub.add_parser("overlay-credits")
    overlay.add_argument("--run", required=True)
    overlay.add_argument("--selection", required=True)
    overlay.add_argument("--database", required=True)
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
    elif args.command == "overlay-credits":
        print(
            encoded(CreditOverlay(args.run, args.selection, args.database).apply()),
            flush=True,
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
