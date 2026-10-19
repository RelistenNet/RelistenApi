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
import os
from pathlib import Path
import shutil
import sqlite3
import unicodedata
from urllib.parse import parse_qs, urlencode, urlsplit

VERSION = "retained-venue-directory-v2"
NATIVE_VERSION = "native-venue-overlay-v2"
FLOOR = 4.5 * 1024**3
NORMALIZATION_COLUMNS = (
    "normalization_key", "normalization_status", "normalization_basis", "original_name",
    "normalized_location", "hypothesis_member_count", "hypothesis_artist_count", "hypothesis_source_count",
)


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
            if db.execute("SELECT 1 FROM sqlite_master WHERE name='metadata'").fetchone():
                version = db.execute("SELECT value FROM metadata WHERE key='version'").fetchone()
                if version and json.loads(version[0]) != VERSION:
                    raise ValueError("Build this projection version into a new database")
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
                chunk TEXT,line INTEGER,source_count INTEGER DEFAULT 0,show_count INTEGER DEFAULT 0,
                normalization_key TEXT,normalization_status TEXT,normalization_basis TEXT,original_name TEXT,
                normalized_location TEXT,hypothesis_member_count INTEGER,hypothesis_artist_count INTEGER,
                hypothesis_source_count INTEGER);
            CREATE INDEX IF NOT EXISTS venues_place ON venues(place_key);
            CREATE INDEX IF NOT EXISTS venues_normalization ON venues(normalization_key);
            CREATE INDEX IF NOT EXISTS venues_artist ON venues(artist_uuid);
            CREATE TABLE IF NOT EXISTS normalization_counts(place_key TEXT PRIMARY KEY,source_count INTEGER,show_count INTEGER);
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
                normalization = proposal.get("normalization") or {}
                hypothesis = normalization.get("hypothesis") or {}
                if hypothesis and hypothesis.get("key") != proposal.get("proposedPlaceKey"):
                    raise ValueError("Selected normalization hypothesis disagrees with the proposed place key")
                locality = proposal.get("locality") or {}
                history = [item["name"] for item in proposal.get("pastNames", [])]
                name = hypothesis.get("preferredName") or proposal.get("displayName") or native.get("name") or "Unnamed venue"
                location = native.get("location") or locality.get("originalWording") or ""
                search = folded(" ".join(str(value or "") for value in
                    [name, native.get("name"), normalization.get("originalName"), location,
                     normalization.get("displayLocation"), *hypothesis.get("names", []), *history,
                     locality.get("city"), locality.get("region"), locality.get("countryCode"), locality.get("address")]))
                db.execute("""INSERT OR REPLACE INTO venues(
                    uuid,legacy_id,place_key,name,location,city,region,country,address,scope,grouping_basis,
                    outcome,reason,artist_uuid,artist_name,method,history_names,history_count,unknown_count,
                    alternative_count,alternatives_truncated,search_text,chunk,line,
                    normalization_key,normalization_status,normalization_basis,original_name,normalized_location,
                    hypothesis_member_count,hypothesis_artist_count,hypothesis_source_count) VALUES(""" + ",".join("?" * 32) + ")", (
                    record["inputUuid"], native.get("id"), proposal.get("proposedPlaceKey"), name, location,
                    locality.get("city"), locality.get("region"), locality.get("countryCode"), locality.get("address"),
                    proposal.get("scope", "unknown"), proposal.get("groupingBasis", "unavailable"), record["outcome"],
                    record.get("reason"), record["artistUuid"], record["artistName"], record["method"],
                    "\n".join(history), len(history), len(proposal.get("protectedUnknowns", [])),
                    proposal.get("alternativeCount", 0), int(proposal.get("alternativesTruncated", False)), search,
                    str(path.relative_to(self.run)), line,
                    hypothesis.get("key"), normalization.get("status"), normalization.get("membershipBasis"),
                    normalization.get("originalName") or native.get("name"), normalization.get("displayLocation"),
                    hypothesis.get("memberCount"), hypothesis.get("artistCount"), hypothesis.get("sourceObservationCount")))
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
        # Aggregate original associations once. Source/show references to two members of one
        # hypothesis must not double count a recording or change its captured home.
        db.execute("DELETE FROM normalization_counts")
        db.execute("""INSERT INTO normalization_counts
            SELECT place_key,count(DISTINCT source_uuid),count(DISTINCT show_uuid) FROM (
                SELECT v.normalization_key place_key,r.source_uuid,r.show_uuid FROM recordings r
                JOIN venues v ON v.legacy_id=r.source_venue_id WHERE v.normalization_key IS NOT NULL
                UNION ALL
                SELECT v.normalization_key,r.source_uuid,r.show_uuid FROM recordings r
                JOIN venues v ON v.legacy_id=r.show_venue_id WHERE v.normalization_key IS NOT NULL)
            GROUP BY place_key""")
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
                 "scopes": dict(db.execute("SELECT scope,count(*) FROM venues GROUP BY scope")),
                 "normalizedHypotheses": db.execute("SELECT count(DISTINCT normalization_key) FROM venues").fetchone()[0],
                 "normalizedMembers": db.execute("SELECT count(*) FROM venues WHERE normalization_key IS NOT NULL").fetchone()[0]}
        VenueDirectoryBuilder._metadata(db, coverage=stats)


class NativeVenueOverlayBuilder:
    """Replace a small native-only projection atomically; never open the legacy DB."""

    def __init__(self, run, database, minimum_free=FLOOR, maximum_bytes=32 * 1024**2):
        self.run = Path(run).resolve()
        self.database = Path(database).resolve()
        self.minimum_free = minimum_free
        self.maximum_bytes = maximum_bytes

    def _budget(self, path):
        if shutil.disk_usage(path.parent).free < self.minimum_free:
            raise RuntimeError("Native overlay stopped to preserve the host disk reserve")
        if sum(p.stat().st_size for p in (path, Path(str(path) + "-journal")) if p.exists()) > self.maximum_bytes:
            raise RuntimeError("Native overlay exceeds its storage bound")

    def build(self):
        manifest_path = self.run / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        if manifest["state"] != "completed":
            raise ValueError("Native venue input must be a completed retained run")
        expected = {(item["domain"], item["provider"]): item["processed"]
                    for item in manifest.get("counts", []) if item["domain"] in ("venues", "file_contents")}
        if not all(any(domain == key[0] for key in expected) for domain in ("venues", "file_contents")):
            raise ValueError("Native manifest must declare venue and file_contents coverage")
        self.database.parent.mkdir(parents=True, exist_ok=True)
        if self.database.exists():
            with closing(sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)) as existing:
                version = existing.execute("SELECT value FROM metadata WHERE key='version'").fetchone()
                if version is None or json.loads(version[0]) not in ("native-venue-overlay-v1", NATIVE_VERSION):
                    raise ValueError("Native update target is not a native overlay")
        staging = self.database.with_name(self.database.name + ".building")
        # Exclusive staging ownership prevents competing writers. A failed update
        # leaves the previously published overlay intact and removes only our temp.
        with staging.open("xb"):
            pass
        try:
            self._budget(staging)
            with closing(sqlite3.connect(staging)) as db:
                db.executescript("""
                    CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                    CREATE TABLE parents(uuid TEXT PRIMARY KEY,provider TEXT,outcome TEXT);
                    CREATE TABLE domain_inputs(domain TEXT,uuid TEXT,provider TEXT,PRIMARY KEY(domain,provider,uuid));
                    CREATE TABLE accounts(uuid TEXT PRIMARY KEY,parent_uuid TEXT,observation_key TEXT,
                        summary TEXT,chunk TEXT,line INTEGER);
                    CREATE TABLE recording_accounts(id INTEGER PRIMARY KEY,parent_uuid TEXT,origin TEXT,
                        UNIQUE(parent_uuid,origin));
                    CREATE TABLE files(native_key TEXT PRIMARY KEY,recording_id INTEGER,
                        ordinal INTEGER,label TEXT,chunk TEXT,line INTEGER);
                    CREATE INDEX file_recordings ON files(recording_id);
                """)
                self._project_domain(db, staging, "venues", self._venues)
                self._project_domain(db, staging, "file_contents", self._files)
                actual = {(domain, provider): count for domain, provider, count in db.execute(
                    "SELECT domain,provider,count(*) FROM domain_inputs GROUP BY domain,provider")}
                if actual != {key: count for key, count in expected.items() if count > 0}:
                    raise ValueError("Native logical parent coverage differs from completed manifest")
                coverage = dict(observations=db.execute("SELECT count(*) FROM accounts").fetchone()[0],
                                parents=db.execute("SELECT count(*) FROM parents").fetchone()[0],
                                files=db.execute("SELECT count(*) FROM files").fetchone()[0],
                                outcomes=dict(db.execute("SELECT outcome,count(*) FROM parents GROUP BY outcome")))
                VenueDirectoryBuilder._metadata(db, version=NATIVE_VERSION, sourceRun=str(self.run),
                    sourceManifestSha256=file_hash(manifest_path), coverage=coverage, buildState="completed")
                db.commit()
                self._budget(staging)
            os.replace(staging, self.database)
        finally:
            staging.unlink(missing_ok=True)
        return coverage

    def _project_domain(self, db, staging, domain, project):
        count = 0
        for path in sorted((self.run / domain).glob("*.jsonl.gz")):
            before = path.stat()
            with gzip.open(path, "rt", encoding="utf-8") as stream:
                for line, text in enumerate(stream, 1):
                    record = json.loads(text)
                    if record.get("domain") != domain or record.get("inputKind") != "provider_input":
                        raise ValueError("Native overlay requires provider_input outcomes only")
                    db.execute("INSERT OR REPLACE INTO domain_inputs VALUES(?,?,?)",
                               (domain, record["inputUuid"], record["provider"]))
                    project(db, record, path, line)
                    count += 1
                    if count > 10000:
                        raise ValueError("Native overlay exceeds its parent-row bound")
                    if count % 100 == 0:
                        self._budget(staging)
            after = path.stat()
            if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                raise ValueError("Native immutable chunk changed during projection")
        db.commit()
        self._budget(staging)

    def _venues(self, db, record, path, line):
        parent = record["inputUuid"]
        db.execute("DELETE FROM accounts WHERE parent_uuid=?", (parent,))
        db.execute("INSERT OR REPLACE INTO parents VALUES(?,?,?)", (parent, record["provider"], record["outcome"]))
        accounts = (record.get("proposal") or {}).get("venues", [])
        if db.execute("SELECT count(*) FROM accounts").fetchone()[0] + len(accounts) > 10000:
            raise ValueError("Native overlay exceeds its observation bound")
        for account in accounts:
            venue = account["venue"]
            locality = account["locality"]
            text = [entry["text"]["value"] for entry in venue.get("text", [])]
            assertions = [entry["value"] for entry in account.get("localityAssertions", [])]
            names = [entry["value"] for entry in venue.get("names", [])]
            locations = [locality.get("originalWording"), *assertions, locality.get("city"),
                         locality.get("region"), locality.get("countryCode"), locality.get("address")]
            identifier = "native:" + parent + ":" + account["observationKey"]
            links = account.get("recordingLinks", [])
            summary = dict(uuid=identifier, legacy_id=None, place_key=account["observationKey"],
                name=account.get("displayName") or next(iter(names), "Unnamed native venue"),
                location=" · ".join(dict.fromkeys(value for value in locations if value)),
                city=locality.get("city"), region=locality.get("region"), country=locality.get("countryCode"),
                address=locality.get("address"), scope=account["scope"], grouping_basis="original_native_account",
                outcome=account["outcome"], reason=record.get("reason"), artist_uuid=record["artistUuid"],
                artist_name=record["artistName"], method=record["method"], history_names="", history_count=0,
                unknown_count=len(account.get("protectedUnknowns", [])), alternative_count=0, alternatives_truncated=0,
                search_text=folded(" ".join(value for value in [*names, *locations, *text] if value)),
                chunk=str(path.relative_to(self.run)), line=line,
                source_count=len({link["catalogSourceUuid"] for link in links if link.get("catalogSourceUuid")}),
                show_count=len({link["catalogShowUuid"] for link in links if link.get("catalogShowUuid")}),
                provider=record["provider"])
            db.execute("INSERT INTO accounts VALUES(?,?,?,?,?,?)", (identifier, parent,
                account["observationKey"], encoded(summary), str(path.relative_to(self.run)), line))

    def _files(self, db, record, path, line):
        parent = record["inputUuid"]
        db.execute("DELETE FROM files WHERE recording_id IN (SELECT id FROM recording_accounts WHERE parent_uuid=?)", (parent,))
        db.execute("DELETE FROM recording_accounts WHERE parent_uuid=?", (parent,))
        # A recording origin can contain long original evidence quotes. Store it
        # once per input/account, rather than once in every file and wide index.
        recordings = {}
        for child in (record.get("proposal") or {}).get("files", []):
            file = child["file"]
            label = (file.get("title") or {}).get("value") or (file.get("filename") or {}).get("value") or child["entityKey"]
            origin = self.origin_key(child["recordingOrigin"])
            if origin not in recordings:
                recordings[origin] = db.execute("INSERT INTO recording_accounts(parent_uuid,origin) VALUES(?,?)",
                                                (parent, origin)).lastrowid
            db.execute("INSERT INTO files VALUES(?,?,?,?,?,?)", (child["entityKey"], recordings[origin],
                child["fileOrdinal"], label, str(path.relative_to(self.run)), line))
        if db.execute("SELECT count(*) FROM files").fetchone()[0] > 100000:
            raise ValueError("Native overlay exceeds its file bound")

    @staticmethod
    def origin_key(origin):
        return json.dumps(origin, sort_keys=True, separators=(",", ":"))


@lru_cache(maxsize=32)
def retained_outcome(run, chunk, line):
    with gzip.open(Path(run) / chunk, "rt", encoding="utf-8") as stream:
        for number, text in enumerate(stream, 1):
            if number == line:
                return json.loads(text)
    raise ValueError("Retained venue evidence is unavailable")


class VenueDirectory:
    def __init__(self, database, files_url, core_url, native_database=None, native_url=None):
        self.database = Path(database).resolve()
        self.files_url = files_url.rstrip("/") + "/"
        self.core_url = core_url.rstrip("/") + "/"
        self.native_database = Path(native_database).resolve() if native_database else None
        self.native_url = native_url.rstrip("/") + "/" if native_url else None
        if self.native_database == self.database:
            raise ValueError("Native overlay and legacy database must be different files")
        if self.native_database and not self.native_url:
            raise ValueError("A native reader URL is required with a native overlay")

    def connect(self):
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True, timeout=5)
        db.row_factory = sqlite3.Row
        db.create_function("folded", 1, folded, deterministic=True)
        db.execute("BEGIN")
        columns = [row[1] for row in db.execute("PRAGMA table_info(venues)")]
        missing = [name for name in NORMALIZATION_COLUMNS if name not in columns]
        # Read older immutable projections without altering their schema or inventing groups.
        catalog = "SELECT v.*" + "".join(",NULL AS " + name for name in missing) + " FROM venues v"
        db.execute("CREATE TEMP VIEW catalog_venues AS " + catalog)
        counts = "SELECT * FROM normalization_counts" if db.execute(
            "SELECT 1 FROM sqlite_master WHERE name='normalization_counts'").fetchone() else (
            "SELECT NULL place_key,NULL source_count,NULL show_count WHERE 0")
        db.execute("CREATE TEMP VIEW hypothesis_counts AS " + counts)
        selection = "SELECT v.*, 'legacy_catalog' record_kind, NULL provider FROM catalog_venues v"
        columns += missing
        if self.native_database:
            db.execute("ATTACH DATABASE ? AS native", (self.native_database.as_uri() + "?mode=ro",))
            projection = ",".join("json_extract(summary,'$." + name + "') AS " + name for name in columns)
            selection += " UNION ALL SELECT " + projection + ", 'native_observation', json_extract(summary,'$.provider') FROM native.accounts"
        db.execute("CREATE TEMP VIEW venue_accounts AS " + selection)
        return db

    @staticmethod
    def page(parameters):
        return max(0, int(parameters.get("offset", 0))), max(1, min(100, int(parameters.get("limit", 30))))

    def stats(self):
        with closing(self.connect()) as db:
            result = {row["key"]: json.loads(row["value"]) for row in db.execute("SELECT * FROM metadata")}
            native = {row["key"]: json.loads(row["value"]) for row in db.execute("SELECT * FROM native.metadata")} if self.native_database else {}
        result.update(result.pop("coverage", {}))
        result["countsReady"] = result.get("buildState") == "completed"
        if not result["countsReady"]:
            result["venuesWithRecordings"] = None
        result.update(nativeCoverage=native.get("coverage", {"observations": 0, "parents": 0, "files": 0}),
                      nativeRun=native.get("sourceRun"), nativeUrl=self.native_url,
                      filesUrl=self.files_url, coreUrl=self.core_url)
        result["directoryRecords"] = result.get("venueRecords", 0) + result["nativeCoverage"]["observations"]
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
                    expression = "folded(coalesce(v.location,'')||' '||coalesce(v.normalized_location,'')||' '||coalesce(v.city,'')||' '||coalesce(v.region,'')||' '||coalesce(v.country,''))"
                for word in value.split():
                    # C# alone selects membership. Every emitted member contributes its raw
                    # retrieval spellings, even when the retained exemplar list is truncated.
                    member_expression = expression.replace("v.", "member.")
                    conditions.append(f"(instr({expression},?)>0 OR (v.normalization_key IS NOT NULL AND EXISTS("
                        f"SELECT 1 FROM catalog_venues member WHERE member.normalization_key=v.normalization_key "
                        f"AND instr({member_expression},?)>0)))")
                    values.extend([word, word])
        artist = parameters.get("artist", "").strip()
        if artist:
            conditions.append("(v.artist_uuid=? OR instr(folded(v.artist_name),?)>0)")
            values.extend([artist, folded(artist)])
        for parameter in ("scope", "outcome"):
            if parameters.get(parameter):
                conditions.append("v." + parameter + "=?")
                values.append(parameters[parameter])
        if parameters.get("kind"):
            conditions.append("v.record_kind=?")
            values.append(parameters["kind"])
        where = " WHERE " + " AND ".join(conditions) if conditions else ""
        # A matching member supplies the stable original UUID, including under artist filters.
        # Unselected/old/native accounts remain individual rows rather than name-based groups.
        selection = "WITH matched AS (SELECT v.* FROM venue_accounts v" + where + "), selected AS ("
        selection += "SELECT min(uuid) uuid FROM matched GROUP BY coalesce(normalization_key,uuid)) "
        with closing(self.connect()) as db:
            total = db.execute(selection + "SELECT count(*) FROM selected", values).fetchone()[0]
            rows = [dict(row) for row in db.execute(selection + "SELECT v.*,c.source_count group_source_count,c.show_count group_show_count "
                "FROM matched v JOIN selected s ON s.uuid=v.uuid LEFT JOIN hypothesis_counts c ON c.place_key=v.normalization_key"
                " ORDER BY coalesce(c.source_count,v.source_count) DESC,name,v.uuid LIMIT ? OFFSET ?", values + [limit, offset])]
            for row in rows:
                if row["normalization_key"]:
                    row.update(source_count=row["group_source_count"] or 0, show_count=row["group_show_count"] or 0,
                               counts_scope="shared_normalization_hypothesis")
                del row["group_source_count"], row["group_show_count"]
            ready = self._complete(db)
        if not ready:
            for row in rows:
                if row["record_kind"] == "legacy_catalog":
                    row.update(source_count=None, show_count=None)
        return {"rows": rows, "total": total, "offset": offset, "limit": limit,
                "identityBasis": "retained_unaccepted_catalog_and_native_accounts" if self.native_database else "retained_unaccepted_place_hypotheses", "countsReady": ready}

    @staticmethod
    def _complete(db):
        row = db.execute("SELECT value FROM metadata WHERE key='buildState'").fetchone()
        return row is not None and json.loads(row[0]) == "completed"

    def evidence(self, identifier):
        with closing(self.connect()) as db:
            if identifier.startswith("native:"):
                return self._native_evidence(db, identifier)[0] if self.native_database else None
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
            if identifier.startswith("native:"):
                return self._native_detail(db, identifier, parameters) if self.native_database else None
            selected = db.execute("SELECT * FROM venue_accounts WHERE uuid=? AND record_kind='legacy_catalog'", (identifier,)).fetchone()
            if selected is None:
                return None
            venue = dict(selected)
            venue["record_kind"] = "legacy_catalog"
            ready = self._complete(db)
            source_run = json.loads(db.execute("SELECT value FROM metadata WHERE key='sourceRun'").fetchone()[0])
            related = [dict(row) for row in db.execute("SELECT uuid,name,original_name,location,normalized_location,artist_name,artist_uuid,outcome,grouping_basis "
                "FROM venue_accounts WHERE place_key=? AND record_kind='legacy_catalog' ORDER BY artist_name,uuid LIMIT 100", (venue["place_key"],))]
            related_total = db.execute("SELECT count(*) FROM venues WHERE place_key=?", (venue["place_key"],)).fetchone()[0]
            group_recordings = parameters.get("recordings") == "group" and venue["normalization_key"] is not None
            members = {row["legacy_id"]: row["uuid"] for row in db.execute(
                "SELECT legacy_id,uuid FROM venues WHERE place_key=?", (venue["place_key"],))}
            if group_recordings:
                conditions = ["(r.source_venue_id IN (SELECT legacy_id FROM venues WHERE normalization_key=?) "
                              "OR r.show_venue_id IN (SELECT legacy_id FROM venues WHERE normalization_key=?))"]
                values = [venue["normalization_key"], venue["normalization_key"]]
            else:
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
                     alternatives=proposal.get("alternatives", []), normalization=proposal.get("normalization"))
        selected_ids = members if group_recordings else {venue["legacy_id"]: identifier}
        for recording in recordings:
            source_member = selected_ids.get(recording["source_venue_id"])
            show_member = selected_ids.get(recording["show_venue_id"])
            recording["relationship"] = "recording_and_show_venue" if source_member and show_member else "recording_venue" if source_member else "show_venue"
            recording.update(source_venue_member_uuid=source_member, show_venue_member_uuid=show_member)
            recording["filesUrl"] = self.files_url + "?" + urlencode({"domain": "file_contents", "entityKey": "source:" + recording["source_uuid"]})
            recording["coreUrl"] = self.core_url + "?" + urlencode({"domain": "performances", "entityKey": "source:" + recording["source_uuid"]})
        return {"venue": venue, "relatedRecords": related, "relatedTotal": related_total,
                "recordings": recordings, "recordingsTotal": total,
                "recordingsScope": "shared_normalization_hypothesis" if group_recordings else "selected_legacy_venue_record",
                "recordingsComplete": ready, "countsReady": ready, "offset": offset, "limit": limit,
                "evidenceUrl": "/api/evidence?" + urlencode({"id": identifier}),
                "coreUrl": self.core_url, "filesUrl": self.files_url}

    def _native_evidence(self, db, identifier):
        row = db.execute("SELECT * FROM native.accounts WHERE uuid=?", (identifier,)).fetchone()
        if row is None:
            return None, None
        run = json.loads(db.execute("SELECT value FROM native.metadata WHERE key='sourceRun'").fetchone()[0])
        outcome = retained_outcome(run, row["chunk"], row["line"])
        if outcome["domain"] != "venues" or outcome["inputUuid"] != row["parent_uuid"]:
            raise ValueError("Native evidence locator no longer describes this input")
        account = next((item for item in outcome["proposal"]["venues"] if item["observationKey"] == row["observation_key"]), None)
        if account is None:
            raise ValueError("Native observation is absent from its original outcome")
        return outcome, account

    def _native_detail(self, db, identifier, parameters):
        outcome, account = self._native_evidence(db, identifier)
        if outcome is None:
            return None
        offset, limit = self.page(parameters)
        summary = db.execute("SELECT summary FROM native.accounts WHERE uuid=?", (identifier,)).fetchone()[0]
        venue = json.loads(summary)
        venue.update(record_kind="native_observation", history=[], locality=account["locality"],
            locality_assertions=account.get("localityAssertions", []), history_prose=account["venue"].get("text", []),
            names=account["venue"].get("names", []), protected_unknowns=account.get("protectedUnknowns", []),
            coordinates=account.get("coordinates"), provider_reference=account["venue"].get("identities", []),
            accepted_identity=account.get("acceptedIdentity", False), alternatives=[], enrichment_tasks=[],
            original_origin=account["venue"]["origin"], setlist_links=account.get("setlistLinks", []))
        recordings = self._native_recordings(outcome, account, parameters.get("artist", "").strip())
        files, total_files, file_offset, file_limit = self._native_file_page(db, outcome["inputUuid"], recordings, parameters)
        return dict(venue=venue, relatedRecords=[], relatedTotal=0, recordings=recordings[offset:offset+limit],
            recordingsTotal=len(recordings), recordingsScope="original_native_recording_accounts", recordingsComplete=True,
            countsReady=True, offset=offset, limit=limit, files=files, filesTotal=total_files,
            fileOffset=file_offset, fileLimit=file_limit, evidenceUrl="/api/evidence?" + urlencode({"id": identifier}),
            coreUrl=self.native_url, filesUrl=self.native_url)


    def _native_recordings(self, outcome, account, artist):
        recordings = []
        for link in account.get("recordingLinks", []):
            source, show = link.get("catalogSourceUuid"), link.get("catalogShowUuid")
            recordings.append(dict(source_uuid=source, show_uuid=show, artist_uuid=outcome["artistUuid"],
                artist_name=outcome["artistName"], source_date=(link.get("literalDate") or {}).get("value"),
                show_date=None, relationship="original_native_recording_account", recording_origin=link["recording"],
                literal_date=link.get("literalDate"), catalog_binding_reason=link.get("catalogBindingReason"),
                filesUrl=self.native_url + "?" + urlencode({"domain": "file_contents", "q": outcome["inputUuid"],
                    "entityKey": "source:" + source}) if source else None,
                coreUrl=self.native_url + "?" + urlencode({"domain": "venues", "entityKey": account["observationKey"]})))
        if artist and artist != outcome["artistUuid"] and folded(artist) not in folded(outcome["artistName"]):
            recordings = []
        return recordings

    def _native_file_page(self, db, parent_uuid, recordings, parameters):
        # The original input AND recording origin establish the relationship.
        # Root citations, matching names and catalog numeric IDs cannot supply it.
        origins = [NativeVenueOverlayBuilder.origin_key(row["recording_origin"]) for row in recordings]
        file_offset, file_limit = self.page({"offset": parameters.get("fileOffset", 0), "limit": parameters.get("fileLimit", 40)})
        files, total_files = [], 0
        if origins:
            join = " FROM native.files f JOIN native.recording_accounts r ON r.id=f.recording_id"
            where = " WHERE r.parent_uuid=? AND r.origin IN (" + ",".join("?" for _ in origins) + ")"
            values = [parent_uuid, *origins]
            total_files = db.execute("SELECT count(*)" + join + where, values).fetchone()[0]
            files = [dict(row) for row in db.execute(
                "SELECT f.native_key,r.parent_uuid,r.origin recording_origin,f.ordinal,f.label,f.chunk,f.line"
                + join + where + " ORDER BY f.ordinal LIMIT ? OFFSET ?", values + [file_limit, file_offset])]
            for file in files:
                file["url"] = self.native_url + "?" + urlencode({"view": "files", "entityKey": file["native_key"]})
        return files, total_files, file_offset, file_limit


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
    native = sub.add_parser("native")
    native.add_argument("--run", required=True)
    native.add_argument("--database", required=True)
    serve = sub.add_parser("serve")
    serve.add_argument("--database", required=True)
    serve.add_argument("--files-url", required=True)
    serve.add_argument("--core-url", required=True)
    serve.add_argument("--native-database")
    serve.add_argument("--native-url")
    serve.add_argument("--bind", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=18770)
    args = parser.parse_args()

    if args.command == "build":
        result = VenueDirectoryBuilder(args.run, args.raw, args.database).build(args.resume)
        print(encoded(result), flush=True)
    elif args.command == "native":
        print(encoded(NativeVenueOverlayBuilder(args.run, args.database).build()), flush=True)
    else:
        directory = VenueDirectory(args.database, args.files_url, args.core_url, args.native_database, args.native_url)
        print(encoded({"listening": f"http://{args.bind}:{args.port}", "stats": directory.stats()}), flush=True)
        ThreadingHTTPServer((args.bind, args.port), handler_for(directory)).serve_forever()


if __name__ == "__main__":
    main()
