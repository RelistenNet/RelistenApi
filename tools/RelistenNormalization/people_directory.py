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
from collections import Counter, OrderedDict
import os
from contextlib import closing
import gzip
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import shutil
import sqlite3
import threading
import unicodedata
from urllib.parse import parse_qs, urlencode, urlsplit

from person_hypotheses import PersonHypotheses

VERSION = "people-name-directory-v5"


def name_key(name):
    """Mirror the catalog's name retrieval key; retain punctuation and accents."""
    return " ".join(unicodedata.normalize("NFKC", name).lower().split())


def group_id(key):
    return "draft-name:" + hashlib.sha256(key.encode()).hexdigest()[:24]


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


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
        has_source_actors = db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone()
        if has_source_actors:
            for row in db.execute("SELECT group_id,name,aliases FROM source_actors"):
                for wording, basis in [(row[1], "source_actor_name")] + [
                    (alias["wording"], "source_actor_alias") for alias in json.loads(row[2])
                ]:
                    if name_key(wording):
                        db.execute("INSERT OR IGNORE INTO names VALUES(?,?,?,?)",
                                   (row[0], wording, name_key(wording), basis))
        surviving_actor = " OR EXISTS(SELECT 1 FROM source_actors a WHERE a.group_id=groups.id)" if has_source_actors else ""
        # execute keeps this phase inside the caller's transaction. executescript
        # would commit source replacements before their counts become consistent.
        for statement in (
            "CREATE INDEX IF NOT EXISTS credits_group_source ON credits(group_id,source_uuid)",
            "DELETE FROM groups WHERE NOT (EXISTS(SELECT 1 FROM credits WHERE credits.group_id=groups.id)" + surviving_actor + ")",
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
        if db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone():
            for label, column in (("sourceActors", "id"), ("sourceActorRecordings", "source_uuid"), ("sourceActorArtists", "artist_uuid")):
                counts[label] = db.execute("SELECT count(DISTINCT " + column + ") FROM source_actors").fetchone()[0]
        if db.execute("SELECT 1 FROM sqlite_master WHERE name='native_groups'").fetchone():
            counts["catalogGroups"] = counts["groups"]
            counts["groups"] += db.execute(
                "SELECT count(*) FROM native_groups n WHERE NOT EXISTS(SELECT 1 FROM groups g WHERE g.id=n.id)"
            ).fetchone()[0]
        db.execute(
            "INSERT OR REPLACE INTO metadata VALUES('directoryStats',?)",
            (encoded(counts),),
        )
        return counts

    def _add_record(self, db, record, chunk, line, origin_run=None, credit_ids=None):
        for index, credit in enumerate((record.get("proposal") or {}).get("credits", [])):
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
                """INSERT INTO credits(id,group_id,source_uuid,artist_uuid,name,role,person_key,
                evidence,aliases,chunk,line,interpretation_basis,credit_key,origin_run) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                (
                    credit_ids[index] if credit_ids is not None else None,
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
            has_context = (
                db.execute(
                    "SELECT 1 FROM sqlite_master WHERE name='context_relationships'"
                ).fetchone()
                is not None
            )
            for chunk, line, record in self._records(chunks):
                self.writer._check_space()
                if has_context:
                    self._invalidate_source_context(db, record["inputUuid"])
                db.execute(
                    "DELETE FROM credits WHERE source_uuid=?", (record["inputUuid"],)
                )
                self.writer._add_record(
                    db, record, chunk, line, origin_run=str(self.run)
                )
            self.writer._rebuild_groups(db)
            self.writer._refresh_stats(db)
            if has_context:
                self._refresh_context_counts(db)
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

    @staticmethod
    def _invalidate_source_context(db, source_uuid):
        # Relationships refer to exact credit occurrences. Invalidate their source
        # evidence atomically with replacement; unrelated proposals survive.
        db.execute(
            """DELETE FROM context_relationships
            WHERE left_credit IN (SELECT id FROM credits WHERE source_uuid=?)
               OR right_credit IN (SELECT id FROM credits WHERE source_uuid=?)""",
            (source_uuid, source_uuid),
        )

    @staticmethod
    def _refresh_context_counts(db):
        row = db.execute(
            "SELECT value FROM metadata WHERE key='peopleContext'"
        ).fetchone()
        if row:
            context = json.loads(row[0])
            context["relations"] = dict(
                db.execute(
                    "SELECT relation,count(*) FROM context_relationships GROUP BY relation"
                )
            )
            remaining = sum(context["relations"].values())
            context["invalidatedByCreditReplacement"] = (
                context.get("invalidatedByCreditReplacement", 0)
                + context["decisions"]
                - remaining
            )
            context["decisions"] = remaining
            db.execute(
                "UPDATE metadata SET value=? WHERE key='peopleContext'",
                (encoded(context),),
            )

    def _records(self, chunks):
        for chunk in chunks:
            with gzip.open(chunk, "rt", encoding="utf-8") as stream:
                for line, text in enumerate(stream, 1):
                    yield str(chunk.relative_to(self.run)), line, json.loads(text)


class RetainedOutcomeCache:
    """Keep at most three decoded chunks; locators always refer to original lines."""

    def __init__(self, check_space):
        self.check_space = check_space
        self.chunks = OrderedDict()
        self.pins = {}

    def read(self, run, chunk, line):
        path = (Path(run) / chunk).resolve()
        if not path.is_relative_to(Path(run).resolve()):
            raise ValueError("Retained outcome escapes its run")
        if path not in self.chunks:
            self.check_space()
            digest = file_sha256(path)
            if path in self.pins and self.pins[path] != digest:
                raise ValueError("Retained outcome chunk changed")
            records, decoded = [], 0
            with gzip.open(path, "rb") as stream:
                for text in stream:
                    decoded += len(text)
                    if decoded > 128 * 1024**2:
                        raise ValueError("Retained chunk exceeds the 128 MiB decode bound")
                    records.append(json.loads(text))
            if file_sha256(path) != digest:
                raise ValueError("Retained outcome chunk changed while reading")
            self.pins[path] = digest
            self.chunks[path] = records
            if len(self.chunks) > 3:
                self.chunks.popitem(last=False)
        self.chunks.move_to_end(path)
        try:
            return self.chunks[path][line - 1]
        except IndexError as error:
            raise ValueError("Retained outcome line is missing") from error


class SourceDirectoryRefresh:
    """Publish a new generation without rewriting the old cohort's witnesses.

    The operator seals context attachment before invoking this command. SQLite
    backup then snapshots the old generation; all replacements and checkpoints
    occur in a private .building file. A resumed refresh must use identical sealed
    input files. Only the final atomic publish makes the new generation available.
    """

    FIELDS = {"taper", "transferrer", "description", "source", "lineage", "taper_notes"}

    def __init__(self, run, source_database, output, context_sealed=False,
                 minimum_free=5.5 * 1024**3, maximum_bytes=3 * 1024**3):
        self.run = Path(run).resolve()
        self.source_database = Path(source_database).resolve()
        self.output = Path(output).resolve()
        self.staging = self.output.with_name(self.output.name + ".building")
        self.context_sealed = context_sealed
        self.minimum_free = minimum_free
        self.maximum_bytes = maximum_bytes
        self.writer = DirectoryBuilder(self.run, self.staging, minimum_free)
        self.cache = RetainedOutcomeCache(self._check_space)

    def apply(self):
        if not self.context_sealed:
            raise ValueError("Seal context attachment before starting a source refresh (--context-sealed)")
        if self.output.exists() or self.output == self.source_database:
            raise FileExistsError(self.output)
        self.output.parent.mkdir(parents=True, exist_ok=True)
        manifest = json.loads((self.run / "manifest.json").read_text())
        if manifest.get("state") != "completed" or not manifest.get("methods", {}).get("CatalogRowExtraction"):
            raise ValueError("Source refresh requires a completed extraction run")
        self._check_space()
        pins = self._input_pins()
        self._prepare_staging(pins)
        with closing(sqlite3.connect(self.staging)) as db:
            db.row_factory = sqlite3.Row
            state = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRefreshProgress'").fetchone()[0])
            self._verify_retained_pins(state)
            if not state.get("finalized"):
                self._index_population(db, state)
                self._replace_sources(db, state)
                self._finish(db, state, manifest)
            self._verify_retained_pins(state)
        self._check_space()
        if self._input_pins() != pins:
            raise ValueError("Sealed source generation or refresh run changed")
        # Link publishes one closed SQLite file atomically and refuses a target
        # created by another operator since preflight. Both names share a folder.
        os.link(self.staging, self.output)
        self.staging.unlink()
        with closing(sqlite3.connect(self.output)) as db:
            return json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRefresh'").fetchone()[0])

    def _check_space(self):
        if shutil.disk_usage(self.output.parent).free < self.minimum_free:
            raise RuntimeError("Source refresh paused to preserve the host disk reserve")
        run_bytes = sum(path.stat().st_size for path in self.run.rglob("*") if path.is_file())
        staging_bytes = sum(path.stat().st_size for path in self.output.parent.glob(self.staging.name + "*"))
        if run_bytes + staging_bytes > self.maximum_bytes:
            raise RuntimeError("Source refresh exceeds the combined run and new-generation byte bound")

    def _input_pins(self):
        # WAL is included when present; the explicit operator seal is still
        # required because hashes cannot establish that another writer is idle.
        paths = [self.source_database, self.run / "manifest.json"]
        paths += sorted(self.run.glob("people/*.jsonl.gz")) + sorted(self.run.glob("credits/*.jsonl.gz"))
        wal = Path(str(self.source_database) + "-wal")
        if wal.exists():
            paths.append(wal)
        return {str(path): file_sha256(path) for path in paths}

    def _prepare_staging(self, pins):
        if self.staging.exists():
            with closing(sqlite3.connect(self.staging)) as db:
                row = db.execute("SELECT value FROM metadata WHERE key='sourceRefreshProgress'").fetchone()
                if row is None or json.loads(row[0])["pins"] != pins:
                    raise ValueError("Resume inputs differ from the sealed refresh generation")
            return
        try:
            with closing(sqlite3.connect(self.source_database.as_uri() + "?mode=ro", uri=True)) as source:
                with closing(sqlite3.connect(self.staging)) as target:
                    source.backup(target, pages=1024, progress=lambda *_: self._check_space())
                    target.execute("PRAGMA journal_mode=DELETE")
                    self.writer._ensure_credit_provenance_columns(target)
                    target.executescript("""
                        CREATE TABLE IF NOT EXISTS refresh_locators(domain TEXT, source_uuid TEXT, provider TEXT,
                            chunk TEXT, line INTEGER, outcome TEXT, PRIMARY KEY(domain,source_uuid));
                        CREATE TABLE IF NOT EXISTS refresh_done(source_uuid TEXT PRIMARY KEY);
                        CREATE TABLE IF NOT EXISTS source_actors(id TEXT PRIMARY KEY, group_id TEXT, source_uuid TEXT,
                            artist_uuid TEXT, name TEXT, person_key TEXT, actor_reference TEXT,
                            aliases TEXT, chunk TEXT, line INTEGER, origin_run TEXT);
                        CREATE INDEX IF NOT EXISTS source_actors_group ON source_actors(group_id,artist_uuid,id);
                        CREATE INDEX IF NOT EXISTS source_actors_source ON source_actors(source_uuid);
                    """)
                    target.execute("DELETE FROM refresh_locators")
                    target.execute("DELETE FROM refresh_done")
                    previous = target.execute("SELECT value FROM metadata WHERE key='sourceRefreshProgress'").fetchone()
                    previous_next_id = json.loads(previous[0])["nextCreditId"] if previous else 1
                    # Removed IDs remain historical links and frozen question
                    # witnesses. A later generation must never assign them again.
                    next_id = max(previous_next_id, target.execute("SELECT coalesce(max(id),0)+1 FROM credits").fetchone()[0])
                    state = dict(pins=pins, retainedPins={}, indexed=[], keptCredits=0, insertedCredits=0, removedCredits=0,
                                 nextCreditId=next_id)
                    self._save_progress(target, state)
                    target.commit()
            if self._input_pins() != pins:
                raise ValueError("Sealed source generation changed during backup")
        except BaseException:
            self.staging.unlink(missing_ok=True)
            raise

    @staticmethod
    def _verify_retained_pins(state):
        for path, expected in state["retainedPins"].items():
            if file_sha256(Path(path)) != expected:
                raise ValueError("Retained witness changed since the refresh checkpoint")

    @staticmethod
    def _save_progress(db, state):
        db.execute("INSERT OR REPLACE INTO metadata VALUES('sourceRefreshProgress',?)", (encoded(state),))

    def _index_population(self, db, state):
        manifest = json.loads((self.run / "manifest.json").read_text())
        method = manifest["methods"].get("ShadowPeopleAdapter")
        if not method:
            raise ValueError("Source people producer is missing")
        base_run = Path(json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0]))
        baseline_manifest = base_run / "manifest.json"
        baseline_sha = file_sha256(baseline_manifest)
        original_sha = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceManifestSha256'").fetchone()[0])
        if baseline_sha != original_sha:
            raise ValueError("Original source manifest changed")
        state["retainedPins"][str(baseline_manifest)] = baseline_sha
        for domain, run, directory in (("baseline", base_run, "credits"), ("people", self.run, "people"), ("credits", self.run, "credits")):
            chunks = sorted((run / directory).glob("*.jsonl.gz"))
            if not chunks:
                raise ValueError("Missing source " + domain + " chunks")
            for path in chunks:
                checkpoint = str(path)
                if checkpoint in state["indexed"]:
                    continue
                self._check_space()
                before = file_sha256(path)
                with db:
                    with gzip.open(path, "rt", encoding="utf-8") as stream:
                        for line, text in enumerate(stream, 1):
                            record = json.loads(text)
                            if record.get("inputKind") != "source_sample":
                                if domain == "baseline":
                                    continue
                                raise ValueError("Source refresh contains non-source outcomes")
                            if domain != "baseline" and (record.get("domain") != domain or record.get("method") != method):
                                raise ValueError("Source outcome domain or producer differs from manifest")
                            source = record["inputUuid"]
                            if not db.execute("SELECT 1 FROM processed_sources WHERE uuid=?", (source,)).fetchone():
                                raise ValueError("Source refresh changes the recording population")
                            db.execute("INSERT OR REPLACE INTO refresh_locators VALUES(?,?,?,?,?,?)",
                                       (domain, source, record.get("provider"), str(path.relative_to(run)), line, record.get("outcome")))
                    if file_sha256(path) != before:
                        raise ValueError("Source chunk changed while indexing")
                    # Store original baseline pins durably too: resumed work must
                    # not trust a different old extraction behind the same locator.
                    if domain == "baseline":
                        state["retainedPins"][str(path)] = before
                    state["indexed"].append(checkpoint)
                    self._save_progress(db, state)
        expected_sources = db.execute("SELECT count(*) FROM processed_sources").fetchone()[0]
        manifest = json.loads((self.run / "manifest.json").read_text())
        for domain in ("baseline", "people", "credits"):
            if db.execute("SELECT 1 FROM refresh_locators WHERE domain=? AND outcome='failed' LIMIT 1", (domain,)).fetchone():
                raise ValueError("Latest failed source outcome cannot replace a generation")
            if db.execute("SELECT count(*) FROM refresh_locators WHERE domain=?", (domain,)).fetchone()[0] != expected_sources:
                raise ValueError("Source refresh is missing source outcomes")
            if domain != "baseline":
                expected = {item["provider"]: item["processed"] for item in manifest.get("counts", []) if item["domain"] == domain}
                actual = dict(db.execute("SELECT provider,count(*) FROM refresh_locators WHERE domain=? GROUP BY provider", (domain,)))
                if not expected or actual != expected:
                    raise ValueError("Source refresh logical population differs from manifest counts")

    def _outcome(self, db, domain, source):
        row = db.execute("SELECT * FROM refresh_locators WHERE domain=? AND source_uuid=?", (domain, source)).fetchone()
        run = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0]) if domain == "baseline" else self.run
        return self.cache.read(run, row["chunk"], row["line"]), row

    @classmethod
    def _raw_fields(cls, record):
        fields = (record.get("evidence") or {}).get("originalInputs")
        if not isinstance(fields, dict) or set(fields) != cls.FIELDS:
            raise ValueError("Source outcome lacks the complete six original input fields")
        return fields

    @classmethod
    def _witness(cls, record, credit):
        facts = dict(sourceUuid=credit.get("sourceUuid"), artistUuid=credit.get("artistUuid") or record.get("artistUuid"),
                     name=credit.get("name"), role=credit.get("role"), originalRole=credit.get("originalRole"),
                     evidence=credit.get("evidence"), interpretationBasis=credit.get("interpretationBasis"),
                     originalInputs=cls._raw_fields(record), actorFacts=retained_actor_facts(record, credit))
        if credit.get("interpretationBasis") == "retained_luna_extraction":
            facts["modelBasis"] = record.get("evidence", {}).get("model")
        return hashlib.sha256(json.dumps(facts, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()

    def _old_witness(self, row, base_run):
        record = self.cache.read(row["origin_run"] or base_run, row["chunk"], row["line"])
        projected = (row["source_uuid"], row["artist_uuid"], row["name"], row["role"],
                     row["person_key"], json.loads(row["evidence"]), row["interpretation_basis"])
        matches = []
        for credit in (record.get("proposal") or {}).get("credits", []):
            original = (credit.get("sourceUuid"), credit.get("artistUuid") or record.get("artistUuid"),
                        credit.get("name"), credit.get("role"), credit.get("personEntityKey"),
                        credit.get("evidence"), credit.get("interpretationBasis"))
            # Early directory generations predate credit_key storage. NULL is a
            # historical value, never permission to invent/relabel the old key.
            if original == projected and (row["credit_key"] is None or credit.get("entityKey") == row["credit_key"]):
                matches.append(credit)
        if record.get("inputUuid") != row["source_uuid"] or not matches:
            raise ValueError("Original credit witness is unavailable")
        if any(retained_actor_facts(record, credit).get("aliases", []) != json.loads(row["aliases"]) for credit in matches):
            raise ValueError("Original credit actor aliases differ from the projection")
        witnesses = {self._witness(record, credit) for credit in matches}
        if len(witnesses) != 1:
            raise ValueError("Original credit witness is ambiguous")
        # Repeated identical original occurrences share this fingerprint. The
        # old/new multiplicity check below withholds reuse for those duplicates.
        return witnesses.pop()

    def _replace_sources(self, db, state):
        base_run = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0])
        chunks = [row[0] for row in db.execute("SELECT DISTINCT chunk FROM refresh_locators WHERE domain='credits' ORDER BY chunk")]
        for chunk in chunks:
            self._check_space()
            with db, gzip.open(self.run / chunk, "rt", encoding="utf-8") as stream:
                for line, text in enumerate(stream, 1):
                    credit_record = json.loads(text)
                    source = credit_record["inputUuid"]
                    locator = db.execute("""SELECT * FROM refresh_locators l WHERE domain='credits' AND source_uuid=?
                        AND chunk=? AND line=? AND NOT EXISTS(SELECT 1 FROM refresh_done d WHERE d.source_uuid=l.source_uuid)""",
                                         (source, chunk, line)).fetchone()
                    if locator is None:
                        continue
                    self._replace_source(db, state, credit_record, locator, base_run)
                state["retainedPins"].update({str(path): digest for path, digest in self.cache.pins.items()
                                             if str(path) not in state["pins"]})
                self._save_progress(db, state)
                self._check_space()
            print(encoded(dict(completedSources=db.execute("SELECT count(*) FROM refresh_done").fetchone()[0],
                               keptCredits=state["keptCredits"], insertedCredits=state["insertedCredits"])), flush=True)

    def _replace_source(self, db, state, credit_record, locator, base_run):
        source = credit_record["inputUuid"]
        people_record, people_locator = self._outcome(db, "people", source)
        baseline, _ = self._outcome(db, "baseline", source)
        for record in (people_record, baseline):
            if any(record.get(field) != credit_record.get(field) for field in ("inputUuid", "artistUuid", "provider", "inputKey")):
                raise ValueError("Paired source outcome ownership differs")
            if self._raw_fields(record) != self._raw_fields(credit_record):
                raise ValueError("Source refresh changes original recording evidence")
        old_rows = db.execute("SELECT * FROM credits WHERE source_uuid=? ORDER BY id", (source,)).fetchall()
        old = [(row, self._old_witness(row, base_run)) for row in old_rows]
        proposed = (credit_record.get("proposal") or {}).get("credits", [])
        if any(credit.get("sourceUuid") != source or (credit.get("artistUuid") or credit_record.get("artistUuid")) != credit_record.get("artistUuid") for credit in proposed):
            raise ValueError("Credit proposal changes source or artist ownership")
        new = [(credit, self._witness(credit_record, credit)) for credit in proposed if name_key(credit["name"])]
        old_counts, new_counts = Counter(key for _, key in old), Counter(key for _, key in new)
        unchanged = {key for key in old_counts if old_counts[key] == new_counts[key] == 1}
        removed = [row["id"] for row, key in old if key not in unchanged]
        for identifier in removed:
            if db.execute("SELECT 1 FROM sqlite_master WHERE name='context_relationships'").fetchone():
                db.execute("DELETE FROM context_relationships WHERE left_credit=? OR right_credit=?", (identifier, identifier))
            db.execute("DELETE FROM credits WHERE id=?", (identifier,))
        additions = [credit for credit, key in new if key not in unchanged]
        identifiers = list(range(state["nextCreditId"], state["nextCreditId"] + len(additions)))
        self.writer._add_record(db, dict(credit_record, proposal=dict(credits=additions)),
                                locator["chunk"], locator["line"], str(self.run), identifiers)
        state["nextCreditId"] += len(additions)
        state["keptCredits"] += len(unchanged)
        state["insertedCredits"] += len(additions)
        state["removedCredits"] += len(removed)
        self._add_source_actors(db, people_record, people_locator, credit_record)
        db.execute("INSERT INTO refresh_done VALUES(?)", (source,))

    def _add_source_actors(self, db, record, locator, credit_record):
        source = record["inputUuid"]
        credited_people = {credit["personEntityKey"] for credit in
                           (credit_record.get("proposal") or {}).get("credits", [])}
        db.execute("DELETE FROM source_actors WHERE source_uuid=?", (source,))
        aliases = (record.get("evidence") or {}).get("sourceActorAliases") or {}
        people = (record.get("proposal") or {}).get("people", [])
        for assertion in aliases.get("assertions", []):
            reference = assertion["actorReferenceUuid"]
            # Credit and alias actors can cite the same reference. The paired
            # typed credits identify role-bearing actors; the independent alias
            # observation must select its own actor without inventing a role.
            actors = [actor for actor in people if actor.get("sourceReferenceUuid") == reference
                      and actor["entityKey"] not in credited_people]
            if len(actors) != 1:
                raise ValueError("Source alias assertion lacks one exact actor")
            actor = actors[0]
            if actor.get("sourceUuid") != source or actor.get("artistUuid") != record.get("artistUuid") or actor.get("name") != assertion["name"]:
                raise ValueError("Source alias actor changes source, artist or wording")
            key = name_key(actor["name"])
            if not key:
                continue
            identifier = group_id(key)
            db.execute("INSERT OR IGNORE INTO groups(id,name_key,display_name) VALUES(?,?,?)", (identifier, key, actor["name"]))
            db.execute("INSERT OR IGNORE INTO artists VALUES(?,?)", (record["artistUuid"], record.get("artistName") or record["artistUuid"]))
            actor_id = "source-actor:" + hashlib.sha256(encoded([source, actor["entityKey"], reference]).encode()).hexdigest()
            db.execute("INSERT INTO source_actors VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                       (actor_id, identifier, source, record["artistUuid"], actor["name"], actor["entityKey"], reference,
                        encoded(assertion.get("aliases", [])), locator["chunk"], locator["line"], str(self.run)))

    def _finish(self, db, state, manifest):
        with db:
            self.writer._rebuild_groups(db)
            self.writer._refresh_stats(db)
            if db.execute("SELECT 1 FROM sqlite_master WHERE name='context_relationships'").fetchone():
                CreditOverlay._refresh_context_counts(db)
            summary = dict(run=str(self.run), sources=db.execute("SELECT count(*) FROM refresh_done").fetchone()[0],
                           keptCredits=state["keptCredits"], insertedCredits=state["insertedCredits"], removedCredits=state["removedCredits"],
                           methods=manifest["methods"], manifestSha256=file_sha256(self.run / "manifest.json"),
                           sealedSourceDatabase=str(self.source_database), maximumBytes=self.maximum_bytes, minimumFreeBytes=self.minimum_free)
            issues = json.loads(db.execute("SELECT value FROM metadata WHERE key='knownIssues'").fetchone()[0])
            issues = [issue for issue in issues if not issue.startswith("This source run predates")]
            for key, value in dict(version=VERSION, sourceRefreshRun=str(self.run), sourceRefresh=summary, knownIssues=issues).items():
                db.execute("INSERT OR REPLACE INTO metadata VALUES(?,?)", (key, encoded(value)))
            # Pins and progress remain available until publication; scratch rows
            # are disposable indexes, not another copy of retained source bodies.
            db.execute("DELETE FROM refresh_locators")
            db.execute("DELETE FROM refresh_done")
            state["finalized"] = True
            self._save_progress(db, state)


class NativePeopleOverlay:
    """Add native observations beside catalog credits, never replacing their authority.

    This is one replaceable native population. Only compact actor facts and original
    locators are stored; the original output owns projections and provider evidence.
    Native artist/source coordinates stay in the run namespace, including Local.
    """

    MAXIMUM_PARENTS = 2_000
    MAXIMUM_DECODED_BYTES = 64 * 1024**2

    def __init__(self, run, database, native_url, minimum_free=4.5 * 1024**3):
        self.run = Path(run).resolve()
        self.database = Path(database).resolve()
        parsed = urlsplit(native_url)
        parsed.port
        if (parsed.scheme not in {"http", "https"} or not parsed.hostname
                or parsed.username is not None or parsed.password is not None
                or parsed.path not in {"", "/"} or parsed.query or parsed.fragment
                or any(ord(character) < 33 or character == "\\" for character in native_url)):
            raise ValueError("Native reader must be an HTTP(S) origin")
        self.native_url = native_url.rstrip("/") + "/"
        self.writer = DirectoryBuilder(run, database, minimum_free)

    def apply(self):
        manifest_bytes = (self.run / "manifest.json").read_bytes()
        manifest = json.loads(manifest_bytes)
        if manifest.get("state") != "completed" or not manifest.get("inputIdentity"):
            raise ValueError("Native overlay requires a completed, identified run")
        namespace = manifest["inputIdentity"]
        method = manifest.get("methods", {}).get("ShadowNativePeopleAdapter")
        if not method:
            raise ValueError("Native people producer is missing")
        expected_people = sum(item["processed"] for item in manifest.get("counts", []) if item["domain"] == "people")
        if expected_people > self.MAXIMUM_PARENTS:
            raise ValueError("Native overlay exceeds the 2000-parent projection bound")
        people, people_pins, people_bytes = self._population("people", method, self.MAXIMUM_DECODED_BYTES)
        credits, credit_pins, credit_bytes = self._population("credits", method, self.MAXIMUM_DECODED_BYTES - people_bytes)
        if people.keys() != credits.keys():
            raise ValueError("Native people and credit populations differ")
        for domain, population in (("people", people), ("credits", credits)):
            expected = {item["provider"]: item["processed"] for item in manifest.get("counts", [])
                        if item["domain"] == domain}
            actual = {}
            for _, _, record in population.values():
                actual[record["provider"]] = actual.get(record["provider"], 0) + 1
            if not expected or actual != expected:
                raise ValueError("Native population differs from the completed run counts")
        self.writer._check_space()
        with closing(sqlite3.connect(self.database)) as db, db:
            # DDL and replacement share one transaction, so a bad new observation
            # leaves the prior population and all catalog/context tables intact.
            db.execute("BEGIN")
            self._schema(db)
            for table in ("native_credits", "native_actors", "native_names", "native_groups"):
                db.execute("DELETE FROM " + table)
            for key, (chunk, line, record) in people.items():
                self.writer._check_space()
                other = credits[key][2]
                for field in ("inputUuid", "artistUuid", "artistName", "provider"):
                    if record.get(field) != other.get(field):
                        raise ValueError("Native paired outcome headers differ")
                self._add_parent(db, namespace, record, other, chunk, line)
            self._summarize(db)
            counts = {
                label: db.execute(query).fetchone()[0]
                for label, query in (
                    ("actors", "SELECT count(*) FROM native_actors"),
                    ("creditMentions", "SELECT count(*) FROM native_credits"),
                    ("aliasActors", "SELECT count(*) FROM native_actors WHERE role_free=1"),
                    ("recordingAccounts", "SELECT count(DISTINCT recording_key) FROM native_actors"),
                    ("artistContexts", "SELECT count(DISTINCT artist_key) FROM native_actors"),
                )
            }
            counts.update(parents=len(people), run=str(self.run), readerUrl=self.native_url,
                          namespace=namespace, manifestSha256=hashlib.sha256(manifest_bytes).hexdigest(),
                          chunks=people_pins + credit_pins, method=method,
                          bounds=dict(parents=self.MAXIMUM_PARENTS, decodedBytes=self.MAXIMUM_DECODED_BYTES),
                          decodedBytes=people_bytes + credit_bytes)
            db.execute("INSERT OR REPLACE INTO metadata VALUES('nativeDirectory',?)", (encoded(counts),))
            # Reuse the existing small count snapshot; base credits and homes are
            # deliberately not rescanned or rewritten by this additive phase.
            stats = json.loads(db.execute("SELECT value FROM metadata WHERE key='directoryStats'").fetchone()[0])
            stats["catalogGroups"] = stats.get("catalogGroups", stats["groups"])
            stats["groups"] = stats["catalogGroups"] + db.execute(
                "SELECT count(*) FROM native_groups n WHERE NOT EXISTS(SELECT 1 FROM groups g WHERE g.id=n.id)"
            ).fetchone()[0]
            db.execute("UPDATE metadata SET value=? WHERE key='directoryStats'", (encoded(stats),))
            if (self.run / "manifest.json").read_bytes() != manifest_bytes:
                raise ValueError("Native run manifest changed during projection")
        return counts

    def _population(self, domain, method, maximum_bytes):
        parents, pins, decoded_bytes = {}, [], 0
        for chunk in sorted((self.run / domain).glob("*.jsonl.gz")):
            before = file_sha256(chunk)
            with gzip.open(chunk, "rb") as stream:
                line = 0
                while text := stream.readline(maximum_bytes - decoded_bytes + 1):
                    line += 1
                    decoded_bytes += len(text)
                    if decoded_bytes > maximum_bytes:
                        raise ValueError("Native overlay exceeds the 64 MiB decoded projection bound")
                    record = json.loads(text)
                    if (record.get("domain") != domain or record.get("inputKind") != "provider_input"
                            or record.get("method") != method or record.get("outcome") == "failed"):
                        raise ValueError("Native overlay contains an unsupported or failed outcome")
                    parents[record["inputKey"]] = (str(chunk.relative_to(self.run)), line, record)
                    if len(parents) > self.MAXIMUM_PARENTS:
                        raise ValueError("Native overlay exceeds the 2000-parent projection bound")
            if file_sha256(chunk) != before:
                raise ValueError("Native chunk changed during projection")
            pins.append(dict(path=str(chunk.relative_to(self.run)), sha256=before))
        return parents, pins, decoded_bytes

    @staticmethod
    def _schema(db):
        for statement in (
            """CREATE TABLE IF NOT EXISTS native_groups(id TEXT PRIMARY KEY,name_key TEXT,display_name TEXT,
                actor_count INTEGER,credit_count INTEGER,recording_count INTEGER,artist_count INTEGER)""",
            """CREATE TABLE IF NOT EXISTS native_names(group_id TEXT,wording TEXT,lookup TEXT,basis TEXT,
                PRIMARY KEY(group_id,wording,basis))""",
            "CREATE INDEX IF NOT EXISTS native_names_lookup ON native_names(lookup)",
            """CREATE TABLE IF NOT EXISTS native_actors(id TEXT PRIMARY KEY,group_id TEXT,actor_key TEXT,
                input_key TEXT,input_uuid TEXT,input_revision_uuid TEXT,recording_key TEXT,
                recording_origin TEXT,file_origin TEXT,catalog_source_uuid TEXT,catalog_show_uuid TEXT,
                binding_reason TEXT,artist_key TEXT,artist_uuid TEXT,artist_name TEXT,provider TEXT,
                name TEXT,aliases TEXT,evidence TEXT,role_free INTEGER,chunk TEXT,line INTEGER)""",
            "CREATE INDEX IF NOT EXISTS native_actors_group ON native_actors(group_id,artist_key,id)",
            """CREATE TABLE IF NOT EXISTS native_credits(id TEXT PRIMARY KEY,actor_id TEXT,role TEXT,
                original_role TEXT,evidence TEXT)""",
            "CREATE INDEX IF NOT EXISTS native_credits_actor ON native_credits(actor_id)",
        ):
            db.execute(statement)

    @staticmethod
    def _add_parent(db, namespace, record, credit_record, chunk, line):
        actors = {}
        for actor in (record.get("proposal") or {}).get("people", []):
            key = actor["entityKey"]
            if key in actors:
                raise ValueError("Repeated native actor key")
            if actor.get("automaticAcceptanceAuthorized"):
                raise ValueError("Native directory accepts proposals only")
            aliases = actor.get("aliases", [])
            if not isinstance(actor["name"], str) or any(not isinstance(alias, str) for alias in aliases):
                raise ValueError("Native names must retain literal wording")
            lookup = name_key(actor["name"])
            if not lookup:
                raise ValueError("Native actor lacks usable wording")
            identifier = group_id(lookup)
            actor_id = "native-actor:" + hashlib.sha256(encoded([namespace, record["inputKey"], key]).encode()).hexdigest()
            artist_key = "native-artist:" + hashlib.sha256(encoded([namespace, record["artistUuid"]]).encode()).hexdigest()
            origin = actor["recordingOrigin"]
            recording_key = encoded([namespace, record["inputKey"], origin])
            role_free = actor.get("sourceActorAlias") is not None
            if role_free and (actor.get("actorFacts") is not None or actor.get("kind") is not None):
                raise ValueError("Role-free actor carries credited facts or kind")
            db.execute("INSERT OR IGNORE INTO native_groups VALUES(?,?,?,0,0,0,0)",
                       (identifier, lookup, actor["name"]))
            db.execute("INSERT OR IGNORE INTO native_names VALUES(?,?,?,?)",
                       (identifier, actor["name"], lookup, "native_name"))
            for alias in aliases:
                db.execute("INSERT OR IGNORE INTO native_names VALUES(?,?,?,?)",
                           (identifier, alias, name_key(alias), "explicit_source_alias"))
            db.execute("INSERT INTO native_actors VALUES(" + ",".join("?" for _ in range(22)) + ")", (
                actor_id, identifier, key, record["inputKey"], record["inputUuid"], actor["inputRevisionUuid"],
                recording_key, encoded(origin), encoded(actor.get("fileOrigin")), actor.get("catalogSourceUuid"),
                actor.get("catalogShowUuid"), actor.get("catalogBindingReason"), artist_key,
                record["artistUuid"], record["artistName"], record["provider"], actor["name"],
                encoded(aliases), encoded(actor.get("evidence", [])), int(role_free), chunk, line))
            actors[key] = (actor_id, actor)
        seen = {}
        for credit in (credit_record.get("proposal") or {}).get("credits", []):
            key = credit["entityKey"]
            if key in seen:
                if credit != seen[key]:
                    raise ValueError("Native credit key has contradictory facts")
                continue
            seen[key] = credit
            if credit.get("personEntityKey") not in actors:
                raise ValueError("Native credit has no corresponding actor")
            actor_id, actor = actors[credit["personEntityKey"]]
            if actor.get("sourceActorAlias") is not None or any(
                credit.get(field) != actor.get(field) for field in
                ("name", "inputRevisionUuid", "recordingOrigin", "fileOrigin", "catalogSourceUuid", "catalogShowUuid")
            ):
                raise ValueError("Native credit changes its actor scope")
            if not isinstance(credit.get("role"), str) or not credit["role"]:
                raise ValueError("Native credit lacks an observed role")
            db.execute("INSERT INTO native_credits VALUES(?,?,?,?,?)", (
                encoded([record["inputKey"], key]), actor_id, credit["role"], credit.get("originalRole"), encoded(credit.get("evidence"))))

    @staticmethod
    def _summarize(db):
        db.execute("""UPDATE native_groups SET
            actor_count=(SELECT count(*) FROM native_actors a WHERE a.group_id=native_groups.id),
            credit_count=(SELECT count(*) FROM native_credits c JOIN native_actors a ON a.id=c.actor_id WHERE a.group_id=native_groups.id),
            recording_count=(SELECT count(DISTINCT recording_key) FROM native_actors a WHERE a.group_id=native_groups.id),
            artist_count=(SELECT count(DISTINCT artist_key) FROM native_actors a WHERE a.group_id=native_groups.id)""")


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


class DirectoryReadConnection(sqlite3.Connection):
    """A read transaction and its observed file generation travel together."""

    generation_stamp = None


class PeopleDirectory:
    """Read-only search and recording pages; each request uses its own connection."""

    def __init__(self, database, files_url, core_url):
        self.database = Path(database).resolve()
        self.files_url = files_url.rstrip("/") + "/"
        self.core_url = core_url.rstrip("/") + "/"
        self._people_lock = threading.Lock()
        self._people_stamp = None
        self._people_index = None

    def _database_stamp(self):
        paths = (self.database, Path(str(self.database) + '-wal'))
        stamps = []
        for path in paths:
            try:
                value = path.stat()
                stamps.append((value.st_ino, value.st_size, value.st_mtime_ns))
            except FileNotFoundError:
                stamps.append(None)
        return tuple(stamps)

    def _shared_people(self, db):
        # The stamp belongs to the open read snapshot, not the current path.
        # A replacement can occur after connect without relabeling that snapshot.
        stamp = db.generation_stamp
        if stamp is None:
            return PersonHypotheses(db, name_key)
        with self._people_lock:
            if stamp != self._people_stamp:
                self._people_index = PersonHypotheses(db, name_key)
                self._people_stamp = stamp
            return self._people_index

    def connect(self):
        before = self._database_stamp()
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True, factory=DirectoryReadConnection)
        db.row_factory = sqlite3.Row
        db.execute('BEGIN')
        db.execute("SELECT value FROM metadata WHERE key='version'").fetchone()
        after = self._database_stamp()
        # If a writer committed while the snapshot opened, its reads are still
        # coherent, but filesystem observations cannot safely name its generation.
        db.generation_stamp = before if before == after else None
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
            # Checkpoints include private locator indexes and file pins. Expose
            # their compact publication summary, not the resumable writer state.
            result.pop("sourceRefreshProgress", None)
            counts = result.pop("directoryStats", None)
            if counts is None:
                raise ValueError(
                    "Directory count snapshot is missing; refresh the projection"
                )
            result.update(counts)
            result["capabilities"] = dict(sourceActors=db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone() is not None)
            result['capabilities']['sharedPeople'] = True
            shared = self._shared_people(db)
            result['personHypotheses'] = len(shared.visible)
            result['personHypothesisCredits'] = len({credit for person in shared.visible for credit in person['members']})
            result.update(filesUrl=self.files_url, coreUrl=self.core_url)
            return result

    @staticmethod
    def _population_sql(db):
        """Share retrieval groups while retaining separate observation counters."""
        native = db.execute("SELECT 1 FROM sqlite_master WHERE name='native_groups'").fetchone()
        source = db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone()
        native_groups = "SELECT * FROM native_groups" if native else "SELECT NULL id,NULL name_key,NULL display_name,0 actor_count,0 credit_count,0 recording_count,0 artist_count WHERE 0"
        native_names = "SELECT * FROM native_names" if native else "SELECT * FROM names WHERE 0"
        source_groups = "SELECT group_id,count(*) actor_count,count(DISTINCT source_uuid) recording_count,count(DISTINCT artist_uuid) artist_count FROM source_actors GROUP BY group_id" if source else "SELECT NULL group_id,0 actor_count,0 recording_count,0 artist_count WHERE 0"
        return """WITH native AS (""" + native_groups + """), source AS (""" + source_groups + """), catalog AS (
            SELECT * FROM groups UNION ALL
            SELECT n.id,n.name_key,n.display_name,0,0,0 FROM native n WHERE NOT EXISTS(SELECT 1 FROM groups g WHERE g.id=n.id)
            ), directory_groups AS (
            SELECT g.*,coalesce(n.actor_count,0) native_actor_count,coalesce(n.credit_count,0) native_credit_count,
                coalesce(n.recording_count,0) native_recording_count,coalesce(n.artist_count,0) native_artist_count,
                coalesce(s.actor_count,0) source_actor_count,coalesce(s.recording_count,0) source_recording_count,
                coalesce(s.artist_count,0) source_artist_count
            FROM catalog g LEFT JOIN native n ON n.id=g.id LEFT JOIN source s ON s.group_id=g.id
            ), directory_names AS (SELECT * FROM names UNION """ + native_names + ") "

    def search(self, parameters):
        offset, limit = self.page(parameters)
        query = name_key(parameters.get("q", ""))
        # instr gives literal substring search, including names containing % or _.
        where = (
            "WHERE EXISTS(SELECT 1 FROM directory_names n WHERE n.group_id=g.id AND instr(n.lookup,?)>0)"
            if query
            else ""
        )
        values = [query] if query else []
        with closing(self.connect()) as db:
            total = db.execute(
                self._population_sql(db) + f"SELECT count(*) FROM directory_groups g {where}", values
            ).fetchone()[0]
            groups = [
                dict(row)
                for row in db.execute(
                    self._population_sql(db) + f"SELECT g.* FROM directory_groups g {where} ORDER BY recording_count DESC,id LIMIT ? OFFSET ?",
                    values + [limit, offset],
                )
            ]
            person_offset, person_limit = self.page(dict(offset=parameters.get('personOffset', '0'), limit=parameters.get('limit', '30')))
            shared = self._shared_people(db).search(parameters.get('q', ''), person_offset, person_limit)
        return {
            "groups": groups,
            "total": total,
            "offset": offset,
            "limit": limit,
            "basis": "inferred_name_group",
            **shared,
        }

    def detail(self, identifier, parameters):
        offset, limit = self.page(parameters)
        artist = parameters.get("artist", "")
        with closing(self.connect()) as db:
            row = db.execute(
                self._population_sql(db) + "SELECT * FROM directory_groups WHERE id=?", (identifier,)
            ).fetchone()
            if row is None:
                return None
            result = dict(row)
            result["basis"] = "inferred_name_group"
            result["names"] = [
                dict(row)
                for row in db.execute(
                    self._population_sql(db) + "SELECT wording,basis FROM directory_names WHERE group_id=? ORDER BY basis,wording",
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
                    self._population_sql(db) + """SELECT DISTINCT g.id,g.display_name,g.recording_count FROM directory_names mine
                JOIN directory_names other ON mine.lookup=other.lookup JOIN directory_groups g ON g.id=other.group_id
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
            source_run = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0])
            refreshed = db.execute("SELECT 1 FROM metadata WHERE key='sourceRefresh'").fetchone() is not None
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
                    credit["originRun"] = credit["origin_run"] or source_run
                    credit["sourceEvidenceUrl"] = (
                        "/api/credit?" + urlencode({"id": credit["id"]})
                        if credit["origin_run"] or refreshed
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
            result.update(self._native_observations(db, identifier, parameters))
            result.update(self._source_observations(db, identifier, parameters))
            result['personHypotheses'] = self._shared_people(db).for_group(identifier)
            return result

    def shared_person(self, identifier, parameters):
        """Combine exact supported occurrences without expanding their name buckets."""
        offset, limit = self.page(parameters)
        artist, role = parameters.get('artist', ''), parameters.get('role', '')
        with closing(self.connect()) as db:
            projected = self._shared_people(db).detail(identifier)
            if projected is None:
                return None
            result, members = projected
            source_run = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0])
            artist_sources, role_sources = {}, {}
            for credit in members:
                artist_sources.setdefault(credit['artist_uuid'], set()).add(credit['source_uuid'])
                role_sources.setdefault(credit['role'], set()).add(credit['source_uuid'])
            artist_names = {credit['artist_uuid']: credit['artist_name'] or credit['artist_uuid'] for credit in members}
            result['artists'] = [dict(uuid=identifier, name=artist_names[identifier], recordingCount=len(sources))
                                 for identifier, sources in sorted(artist_sources.items(), key=lambda pair: (artist_names[pair[0]], pair[0]))]
            result['roles'] = [dict(role=wording, recordingCount=len(sources)) for wording, sources in sorted(role_sources.items())]
            selected = [credit for credit in members if (not artist or credit['artist_uuid'] == artist)
                        and (not role or credit['role'] == role)]
            recordings = {}
            for member in selected:
                source = member['source_uuid']
                if source not in recordings:
                    recordings[source] = dict(sourceUuid=source, capturedHome=self._home(db, source),
                        playableFilesUrl=self.files_url + '?' + urlencode(dict(domain='file_contents', entityKey='source:' + source)), credits=[])
                credit = dict(member)
                credit.update(evidence=json.loads(member['evidence']), aliases=json.loads(member['aliases']),
                              originRun=member['origin_run'] or source_run,
                              sourceEvidenceUrl='/api/credit?' + urlencode(dict(id=member['id'])))
                recordings[source]['credits'].append(credit)
            result.update(total=len(recordings), offset=offset, limit=limit,
                          recordings=[recordings[source] for source in sorted(recordings)[offset:offset + limit]])
            return result

    def _source_observations(self, db, identifier, parameters):
        empty = dict(sourceObservations=[], sourceTotal=0, sourceArtists=[])
        if not db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone():
            return empty
        offset, limit = self.page(dict(offset=parameters.get("sourceOffset", "0"), limit=parameters.get("limit", "30")))
        artist = parameters.get("sourceArtist", "")
        condition = "s.group_id=?" + (" AND s.artist_uuid=?" if artist else "")
        values = [identifier] + ([artist] if artist else [])
        result = dict(sourceTotal=db.execute("SELECT count(*) FROM source_actors s WHERE " + condition, values).fetchone()[0],
                      sourceOffset=offset, sourceLimit=limit, sourceObservations=[],
                      sourceArtists=[dict(row) for row in db.execute("""SELECT s.artist_uuid uuid,a.name,count(*) observations
                          FROM source_actors s JOIN artists a ON a.uuid=s.artist_uuid WHERE s.group_id=?
                          GROUP BY s.artist_uuid ORDER BY a.name,s.artist_uuid""", (identifier,))])
        for row in db.execute("""SELECT s.*,a.name artist_name FROM source_actors s LEFT JOIN artists a ON a.uuid=s.artist_uuid
                WHERE """ + condition + " ORDER BY s.id LIMIT ? OFFSET ?", values + [limit, offset]):
            actor = dict(row)
            actor["aliases"] = json.loads(actor["aliases"])
            actor.update(capturedHome=self._home(db, actor["source_uuid"]),
                         sourceEvidenceUrl="/api/source-actor?" + urlencode(dict(id=actor["id"])),
                         playableFilesUrl=self.files_url + "?" + urlencode(dict(domain="file_contents", entityKey="source:" + actor["source_uuid"])),
                         originRun=actor.pop("origin_run"), basis="source_actor_observation", acceptedIdentity=False)
            result["sourceObservations"].append(actor)
        return result

    def source_actor(self, identifier):
        """Return the typed original alias assertion, without inventing a role."""
        with closing(self.connect()) as db:
            if not db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone():
                return None
            row = db.execute("SELECT * FROM source_actors WHERE id=?", (identifier,)).fetchone()
            if row is None:
                return None
            indexed = dict(row)
            progress = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRefreshProgress'").fetchone()[0])
        path = (Path(indexed["origin_run"]) / indexed["chunk"]).resolve()
        expected = progress["pins"].get(str(path))
        if not path.is_relative_to(Path(indexed["origin_run"])) or not expected or file_sha256(path) != expected:
            raise ValueError("Original source actor evidence changed")
        with gzip.open(path, "rt", encoding="utf-8") as stream:
            record = next((json.loads(text) for line, text in enumerate(stream, 1) if line == indexed["line"]), None)
        if record is None or record.get("inputKind") != "source_sample" or (record.get("inputUuid"), record.get("artistUuid")) != (indexed["source_uuid"], indexed["artist_uuid"]):
            raise ValueError("Original source actor ownership differs")
        actors = [actor for actor in (record.get("proposal") or {}).get("people", []) if actor.get("entityKey") == indexed["person_key"]]
        assertions = [item for item in ((record.get("evidence") or {}).get("sourceActorAliases") or {}).get("assertions", [])
                      if item.get("actorReferenceUuid") == indexed["actor_reference"]]
        if len(actors) != 1 or len(assertions) != 1 or actors[0].get("name") != indexed["name"] or assertions[0].get("aliases", []) != json.loads(indexed["aliases"]):
            raise ValueError("Original source actor assertion differs")
        return dict(actor=actors[0], assertion=assertions[0], originalInputs=record["evidence"]["originalInputs"],
                    parent={field: record.get(field) for field in ("inputUuid", "artistUuid", "artistName", "provider", "method")},
                    originRun=indexed["origin_run"], basis="source_actor_observation", acceptedIdentity=False)

    def _native_observations(self, db, identifier, parameters):
        empty = dict(nativeObservations=[], nativeTotal=0, nativeArtists=[])
        if not db.execute("SELECT 1 FROM sqlite_master WHERE name='native_actors'").fetchone():
            return empty
        offset, limit = self.page({"offset": parameters.get("nativeOffset", "0"), "limit": parameters.get("limit", "30")})
        artist = parameters.get("nativeArtist", "")
        where = "group_id=?" + (" AND artist_key=?" if artist else "")
        values = [identifier] + ([artist] if artist else [])
        metadata = json.loads(db.execute("SELECT value FROM metadata WHERE key='nativeDirectory'").fetchone()[0])
        result = dict(nativeTotal=db.execute("SELECT count(*) FROM native_actors WHERE " + where, values).fetchone()[0],
                      nativeOffset=offset, nativeLimit=limit,
                      nativeArtists=[dict(row) for row in db.execute(
                          "SELECT artist_key,artist_name,count(*) observations FROM native_actors WHERE group_id=? GROUP BY artist_key ORDER BY artist_name,artist_key",
                          (identifier,))], nativeObservations=[])
        for row in db.execute("SELECT * FROM native_actors WHERE " + where + " ORDER BY id LIMIT ? OFFSET ?", values + [limit, offset]):
            actor = dict(row)
            for field in ("recording_origin", "file_origin", "aliases", "evidence"):
                actor[field] = json.loads(actor[field])
            actor["credits"] = [dict(credit) for credit in db.execute(
                "SELECT role,original_role,evidence FROM native_credits WHERE actor_id=? ORDER BY id", (actor["id"],))]
            for credit in actor["credits"]:
                credit["evidence"] = json.loads(credit["evidence"])
            actor["sourceEvidenceUrl"] = "/api/native-actor?" + urlencode({"id": actor["id"]})
            # This is deliberately the native input's file result, not a guessed
            # production home or a claim that every input file belongs to this actor.
            actor["inputFilesUrl"] = metadata["readerUrl"] + "?" + urlencode({"domain": "file_contents", "q": actor["input_uuid"]})
            actor["originRun"] = metadata["run"]
            actor["catalogNamespace"] = metadata["namespace"]
            result["nativeObservations"].append(actor)
        return result

    def native_actor(self, identifier):
        """Load one original actor by its retained key, scope and physical line."""
        with closing(self.connect()) as db:
            if not db.execute("SELECT 1 FROM sqlite_master WHERE name='native_actors'").fetchone():
                return None
            row = db.execute("SELECT * FROM native_actors WHERE id=?", (identifier,)).fetchone()
            if row is None:
                return None
            indexed = dict(row)
            metadata = json.loads(db.execute("SELECT value FROM metadata WHERE key='nativeDirectory'").fetchone()[0])
        run = Path(metadata["run"])
        path = (run / indexed["chunk"]).resolve()
        expected = next(pin["sha256"] for pin in metadata["chunks"] if pin["path"] == indexed["chunk"])
        try:
            actual = file_sha256(path)
        except OSError as error:
            raise ValueError("Original native chunk is unavailable") from error
        if not path.is_relative_to(run) or actual != expected:
            raise ValueError("Original native chunk changed")
        with gzip.open(path, "rt", encoding="utf-8") as stream:
            for line, text in enumerate(stream, 1):
                if line == indexed["line"]:
                    record = json.loads(text)
                    break
            else:
                raise ValueError("Original native outcome is unavailable")
        if (record.get("domain"), record.get("inputKind"), record.get("inputKey"), record.get("inputUuid")) != (
                "people", "provider_input", indexed["input_key"], indexed["input_uuid"]):
            raise ValueError("Original native parent coordinates differ")
        actors = [actor for actor in (record.get("proposal") or {}).get("people", []) if actor.get("entityKey") == indexed["actor_key"]]
        if len(actors) != 1:
            raise ValueError("Original native actor is unavailable or ambiguous")
        actor = actors[0]
        if (actor.get("inputRevisionUuid"), actor.get("recordingOrigin"), actor.get("fileOrigin")) != (
                indexed["input_revision_uuid"], json.loads(indexed["recording_origin"]), json.loads(indexed["file_origin"])):
            raise ValueError("Original native actor scope differs")
        return dict(actor=actor, parent={key: record[key] for key in
                    ("inputKey", "inputUuid", "artistUuid", "artistName", "provider", "method")},
                    originRun=metadata["run"], catalogNamespace=metadata["namespace"],
                    basis="native_source_observation", acceptedIdentity=False)

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
            refresh = db.execute("SELECT value FROM metadata WHERE key='sourceRefreshProgress'").fetchone()
        path = (Path(run) / credit["chunk"]).resolve()
        if refresh:
            progress = json.loads(refresh[0])
            expected = {**progress["pins"], **progress["retainedPins"]}.get(str(path))
            if not path.is_relative_to(Path(run).resolve()) or not expected or file_sha256(path) != expected:
                raise ValueError("Original credit evidence changed")
        with gzip.open(path, "rt", encoding="utf-8") as stream:
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
        return {"credit": credit, "retainedOutcome": outcome, "originRun": run}

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
                elif url.path == '/api/shared-person':
                    result = directory.shared_person(parameters.get('id', ''), parameters)
                    if result is None:
                        self.respond(404, encoded({'error': 'Unknown person hypothesis'}).encode())
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
                elif url.path == "/api/source-actor":
                    result = directory.source_actor(parameters.get("id", ""))
                    if result is None:
                        self.respond(404, encoded({"error": "Unknown source actor"}).encode())
                        return
                elif url.path == "/api/native-actor":
                    result = directory.native_actor(parameters.get("id", ""))
                    if result is None:
                        self.respond(404, encoded({"error": "Unknown native actor"}).encode())
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
    refresh = sub.add_parser("refresh-sources", help="Publish a new sealed source generation, preserving exact unchanged witnesses")
    refresh.add_argument("--run", required=True)
    refresh.add_argument("--source-database", required=True)
    refresh.add_argument("--database", required=True)
    refresh.add_argument("--context-sealed", action="store_true")
    refresh.add_argument("--minimum-free-bytes", type=int, default=int(5.5 * 1024**3))
    refresh.add_argument("--maximum-bytes", type=int, default=3 * 1024**3)
    native = sub.add_parser("overlay-native")
    native.add_argument("--run", required=True)
    native.add_argument("--database", required=True)
    native.add_argument("--native-url", required=True)
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
    elif args.command == "refresh-sources":
        print(encoded(SourceDirectoryRefresh(args.run, args.source_database, args.database,
            args.context_sealed, args.minimum_free_bytes, args.maximum_bytes).apply()), flush=True)
    elif args.command == "overlay-native":
        print(encoded(NativePeopleOverlay(args.run, args.database, args.native_url).apply()), flush=True)
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
