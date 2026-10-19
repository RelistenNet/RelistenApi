"""Project retained composition suggestions beside the existing music image.

The C# producer owns retrieval and its complete song population. This projection
stores qualified work display facts and candidate-bearing relationships only.
Full assertions stay in their pinned chunks; files stay in the existing readers.
"""

import argparse
from collections import Counter
from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import time
import uuid

from music_catalogue import CatalogueBuildPaused
from music_consolidation import sha256_file
from music_groups import MusicNormalizationChanged


MAXIMUM_LINE_BYTES = 32 * 1024 * 1024
MAXIMUM_LOGICAL_ROWS = 2_000_000


def _hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode()).digest()


def _metadata(db, prefix=""):
    return {key: json.loads(value) for key, value in db.execute("SELECT key,value FROM " + prefix + "metadata")}


def _stamp(path):
    stat = path.stat()
    return stat.st_ino, stat.st_size, stat.st_mtime_ns


def _identity(value):
    identity = value["identity"]
    if identity["provider"] != "musicbrainz.org" or identity["resourceType"] != "work":
        raise ValueError("A retained composition must keep its qualified work identity")
    uuid.UUID(identity["providerKey"])
    return identity["provider"], identity["resourceType"], identity["providerKey"]


def _artifact(root, artifact):
    path = (root / artifact["path"]).resolve(strict=True)
    if not path.is_relative_to(root) or path.stat().st_size != artifact["bytes"] or sha256_file(path) != artifact["sha256"]:
        raise MusicNormalizationChanged("A retained composition artifact differs from its completion manifest")
    return path


class CompositionCatalogueBuilder:
    """Publish a separate, bounded projection after authenticating completed output."""

    def __init__(self, normalization, manifest, database, *, manifest_sha256,
                 minimum_free_bytes=int(5.5 * 1024**3), maximum_bytes=256 * 1024**2):
        self.normalization = Path(normalization).resolve(strict=True)
        self.manifest = Path(manifest).resolve(strict=True)
        self.database = Path(database).resolve()
        self.manifest_sha = manifest_sha256
        self.minimum_free_bytes = minimum_free_bytes
        self.maximum_bytes = maximum_bytes

    def build(self):
        raw = self.manifest.read_bytes()
        if hashlib.sha256(raw).hexdigest() != self.manifest_sha:
            raise ValueError("The composition manifest differs from its supplied pin")
        manifest = json.loads(raw)
        normalization_stamp = _stamp(self.normalization)
        with closing(sqlite3.connect(self.normalization.as_uri() + "?mode=ro&immutable=1", uri=True)) as source:
            music = _metadata(source)
        self._require_generation(manifest, music)
        self.database.parent.mkdir(parents=True, exist_ok=True)
        self._space()
        building = self.database.with_name(self.database.name + "." + uuid.uuid4().hex + ".building")
        try:
            with closing(sqlite3.connect(building)) as db:
                db.row_factory = sqlite3.Row
                db.execute("PRAGMA journal_mode=MEMORY")
                db.execute("PRAGMA max_page_count=" + str(self.maximum_bytes // 4096))
                self._schema(db)
                db.execute("ATTACH DATABASE ? AS music", (self.normalization.as_uri() + "?mode=ro&immutable=1",))
                counts, outcomes = self._consume(db, manifest)
                self._require_population(db, manifest, counts, outcomes)
                metadata = {"version": "composition-catalogue-v1", "state": "completed",
                    "manifest": str(self.manifest), "manifestSha256": self.manifest_sha,
                    "normalizationOutputSha256": music["outputManifestSha256"],
                    "catalogueSha256": music["catalogueSha256"], "songs": manifest["songs"],
                    "works": manifest["works"], "outcomes": dict(outcomes),
                    "candidateSongs": db.execute("SELECT count(*) FROM songs").fetchone()[0],
                    "candidateRelationships": db.execute("SELECT count(*) FROM candidates").fetchone()[0],
                    "retainedSearchPages": counts["retained_work_search_pages"],
                    "externalUniverseComplete": False, "publicationPermitted": False,
                    "inferenceAvailable": False, "projectorVersion": manifest["projectorVersion"]}
                db.executemany("INSERT INTO metadata VALUES(?,?)", ((key, json.dumps(value)) for key, value in metadata.items()))
                db.commit()
            self._space()
            if _stamp(self.normalization) != normalization_stamp or sha256_file(self.manifest) != self.manifest_sha:
                raise MusicNormalizationChanged("The composition inputs changed before publication")
            for artifact in [*manifest["chunks"], manifest["progress"]]:
                _artifact(self.manifest.parent, artifact)
            building.replace(self.database)
            return metadata
        finally:
            building.unlink(missing_ok=True)

    @staticmethod
    def _require_generation(manifest, music):
        if (manifest.get("version") != "composition-hypothesis-output-v1" or manifest.get("complete") is not True
                or music.get("state") != "completed"
                or manifest["inputs"]["musicOutputManifest"]["sha256"] != music["outputManifestSha256"]
                or manifest["inputs"]["musicInputManifest"]["sha256"] != music["inputManifestSha256"]
                or not 0 <= manifest["songs"] <= MAXIMUM_LOGICAL_ROWS or not 0 <= manifest["works"] <= 100_000):
            raise ValueError("Composition suggestions require the exact completed music image")

    @staticmethod
    def _schema(db):
        db.executescript("""
            CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE works(work_key TEXT PRIMARY KEY,provider TEXT,resource_type TEXT,provider_key TEXT,
                title TEXT,title_names TEXT,aliases TEXT,writer_names TEXT,iswcs TEXT,search_text TEXT,
                chunk TEXT,line INTEGER,proof_hash BLOB,UNIQUE(provider,resource_type,provider_key)) WITHOUT ROWID;
            CREATE TABLE songs(scope TEXT,song_key TEXT,artist_uuid TEXT,title TEXT,artist_name TEXT,basis TEXT,
                legacy_uuid TEXT,original_memberships INTEGER,candidate_count INTEGER,outcome TEXT,reason TEXT,
                method TEXT,chunk TEXT,line INTEGER,proof_hash BLOB,PRIMARY KEY(scope,song_key)) WITHOUT ROWID;
            CREATE INDEX songs_artist ON songs(scope,artist_uuid);
            CREATE TABLE candidates(work_key TEXT,scope TEXT,song_key TEXT,ordinal INTEGER,retrieval_basis TEXT,
                matching_titles TEXT,work_provider TEXT,work_type TEXT,work_provider_key TEXT,
                PRIMARY KEY(scope,song_key,work_key)) WITHOUT ROWID;
            CREATE INDEX candidates_work ON candidates(work_key,scope,song_key);
        """)

    def _consume(self, db, manifest):
        # Hashes deduplicate exact checkpoint replays without keeping a second
        # copy of the million missing-candidate seed records in this database.
        seen, counts, outcomes = {}, Counter(), Counter()
        for artifact in manifest["chunks"]:
            path = _artifact(self.manifest.parent, artifact)
            physical = 0
            with gzip.open(path, "rb") as stream:
                while line := stream.readline(MAXIMUM_LINE_BYTES + 1):
                    physical += 1
                    if len(line) > MAXIMUM_LINE_BYTES or not line.endswith(b"\n"):
                        raise ValueError("A retained composition result exceeds its line boundary")
                    record = json.loads(line)
                    domain = record["domain"]
                    if domain not in {"retained_work_candidates", "retained_work_search_pages", "composition_hypotheses"}:
                        raise ValueError("An unrelated result is in the composition output")
                    key, proof_hash = _hash([domain, record["inputKey"]]), _hash(record)
                    if key in seen:
                        if seen[key] != proof_hash:
                            raise ValueError("A composition replay changes its original result")
                        continue
                    seen[key] = proof_hash
                    if len(seen) > MAXIMUM_LOGICAL_ROWS:
                        raise ValueError("The retained composition population exceeds its row bound")
                    counts[domain] += 1
                    if domain == "retained_work_candidates":
                        self._work(db, record["proposal"], artifact["path"], physical, proof_hash)
                    elif domain == "composition_hypotheses":
                        proposal = record["proposal"]
                        seed = proposal["song"]
                        if json.loads(record["inputKey"]) != [seed["scope"], seed["artistUuid"], seed["song"]["entityKey"]]:
                            raise ValueError("A composition seed changes its original scoped target")
                        if proposal.get("externalUniverseComplete") is not False or proposal.get("publicationPermitted") is not False:
                            raise ValueError("Retained suggestions cannot become accepted work identities")
                        outcomes[proposal["outcome"]] += 1
                        if proposal["candidates"]:
                            self._song(db, proposal, artifact["path"], physical, proof_hash)
                    if len(seen) % 1000 == 0:
                        self._space()
            if physical != artifact["rows"]:
                raise ValueError("A composition chunk changes its physical row census")
        _artifact(self.manifest.parent, manifest["progress"])
        return counts, outcomes

    @staticmethod
    def _work(db, work, chunk, line, proof_hash):
        provider, kind, provider_key = _identity(work["identity"])
        titles = [item["value"] for item in work["titles"] if isinstance(item.get("value"), str)]
        aliases = [item["name"]["value"] for item in work["aliases"] if isinstance((item.get("name") or {}).get("value"), str)]
        writers = [((item.get("artist") or {}).get("name") or {}).get("value") for item in work["writerRoles"]]
        writers = list(dict.fromkeys(name for name in writers if isinstance(name, str)))
        iswcs = [item["original"]["value"] for item in work["iswcs"] if isinstance((item.get("original") or {}).get("value"), str)]
        title = next((value for value in titles if value), provider_key)
        search_text = "\n".join([work["entityKey"], provider_key, *titles, *aliases, *writers, *iswcs])
        db.execute("INSERT INTO works VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)", (work["entityKey"], provider, kind, provider_key,
            title, json.dumps(titles), json.dumps(aliases), json.dumps(writers), json.dumps(iswcs), search_text,
            chunk, line, proof_hash))

    @staticmethod
    def _song(db, proposal, chunk, line, proof_hash):
        seed = proposal["song"]
        song = seed["song"]
        scope, artist, key = seed["scope"], seed["artistUuid"], song["entityKey"]
        original = db.execute("SELECT artist_uuid,title FROM music.groups WHERE scope=? AND entity_key=?", (scope, key)).fetchone()
        proposed = db.execute("SELECT 1 FROM music.members INDEXED BY members_target "
                              "WHERE scope=? AND target_key=? AND disposition='proposed' LIMIT 1", (scope, key)).fetchone()
        if not original or original["artist_uuid"] != artist or original["title"] != song["name"] or not proposed:
            raise ValueError("A composition suggestion changes its normalized song group")
        if song["artistUuid"] != artist:
            raise ValueError("A composition song changes its artist namespace")
        candidates = proposal["candidates"]
        db.execute("INSERT INTO songs VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", (scope, key, artist, song["name"],
            seed.get("artistName"), song["basis"], song.get("legacySongUuid"), seed["originalMemberships"],
            len(candidates), proposal["outcome"], proposal["reason"], proposal["method"], chunk, line, proof_hash))
        for ordinal, candidate in enumerate(candidates):
            identity = _identity(candidate["workIdentity"])
            db.execute("INSERT INTO candidates VALUES(?,?,?,?,?,?,?,?,?)", (candidate["workEntityKey"], scope, key,
                ordinal, candidate["retrievalBasis"], json.dumps([item["value"] for item in candidate["matchingTitles"]]), *identity))

    @staticmethod
    def _require_population(db, manifest, counts, outcomes):
        if (counts["composition_hypotheses"] != manifest["songs"] or counts["retained_work_candidates"] != manifest["works"]
                or dict(outcomes) != manifest["outcomes"]):
            raise ValueError("The composition output does not cover its declared complete population")
        invalid = db.execute("""SELECT 1 FROM candidates c LEFT JOIN works w ON w.work_key=c.work_key
            WHERE w.work_key IS NULL OR w.provider!=c.work_provider OR w.resource_type!=c.work_type
                OR w.provider_key!=c.work_provider_key LIMIT 1""").fetchone()
        if invalid:
            raise ValueError("A composition candidate changes its qualified retained work")

    def _space(self):
        if shutil.disk_usage(self.database.parent).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Composition projection stopped to preserve free space")


class CompositionSelectionBuilder:
    """Keep authenticated model results separate from retained composition facts."""

    def __init__(self, compositions, manifest, database, *, manifest_sha256,
                 minimum_free_bytes=int(5.5 * 1024**3), maximum_bytes=16 * 1024**2):
        self.compositions = Path(compositions).resolve(strict=True)
        self.manifest = Path(manifest).resolve(strict=True)
        self.database = Path(database).resolve()
        self.manifest_sha = manifest_sha256
        self.minimum_free_bytes = minimum_free_bytes
        self.maximum_bytes = maximum_bytes

    def _read_plan(self):
        raw = self.manifest.read_bytes()
        if len(raw) > MAXIMUM_LINE_BYTES or hashlib.sha256(raw).hexdigest() != self.manifest_sha:
            raise ValueError("The selection completion manifest differs from its supplied pin")
        report = json.loads(raw)
        plan_path = self.manifest.parent / "approved-plan.json"
        plan_raw = plan_path.read_bytes()
        if len(plan_raw) > MAXIMUM_LINE_BYTES:
            raise ValueError("The selection plan exceeds its byte bound")
        plan = json.loads(plan_raw)
        plan_version = {"composition-selection-batch-v1": "composition-selection-plan-v1",
                        "composition-selection-batch-v2": "composition-selection-plan-v2"}.get(report.get("version"))
        if (plan_version is None or report.get("state") != "completed"
                or report.get("publicationPermitted") is not False or report.get("externalUniverseComplete") is not False
                or hashlib.sha256(plan_raw).hexdigest() != report["planSha256"]
                or plan.get("version") != plan_version or plan["jobUuid"] != report["jobUuid"]):
            raise ValueError("Selections require their exact completed, unconfirmed plan")
        context = plan.get("targetContextManifest")
        if (plan_version == "composition-selection-plan-v2") != (context is not None):
            raise ValueError("Context-enriched selections require their pinned target context")
        if context is not None:
            self._context_pin(context)
        prepared = report["preflight"]
        expected = {self._key(row["key"]): row for row in prepared["rows"]}
        keys = [self._key(key) for key in plan["songs"]]
        if (len(keys) > 100_000 or len(set(keys)) != len(keys) or set(keys) != set(expected)
                or len(expected) != len(prepared["rows"]) or report["processedInputs"] != len(keys)
                or prepared["selectedInputs"] != len(keys) or prepared["planSha256"] != report["planSha256"]):
            raise ValueError("The completed selection plan does not cover its exact scoped inputs")
        return report, plan, expected

    @staticmethod
    def _context_pin(artifact):
        # C# authenticated the complete context before making requests. Retain
        # that exact export beside the response; never substitute a later head.
        if not isinstance(artifact, dict) or not {"path", "bytes", "sha256"}.issubset(artifact):
            raise ValueError("Selection target context requires a complete artifact pin")
        path = Path(artifact["path"]).resolve(strict=True)
        if path.stat().st_size != artifact["bytes"] or sha256_file(path) != artifact["sha256"]:
            raise MusicNormalizationChanged("Selection target context differs from its pinned export")

    def build(self):
        report, plan, expected = self._read_plan()
        marker = _stamp(self.compositions)
        with closing(sqlite3.connect(self.compositions.as_uri() + "?mode=ro&immutable=1", uri=True)) as source:
            source.row_factory = sqlite3.Row
            metadata = _metadata(source)
            hypothesis = plan["hypothesisManifest"]
            hypothesis_path = Path(hypothesis["path"]).resolve(strict=True)
            if (metadata["manifestSha256"] != hypothesis["sha256"] or hypothesis_path.stat().st_size != hypothesis["bytes"]
                    or sha256_file(hypothesis_path) != hypothesis["sha256"]):
                raise MusicNormalizationChanged("Selections belong to another retained composition image")
            original = json.loads(hypothesis_path.read_bytes())
            self.database.parent.mkdir(parents=True, exist_ok=True)
            self._space()
            building = self.database.with_name(self.database.name + "." + uuid.uuid4().hex + ".building")
            try:
                with closing(sqlite3.connect(building)) as db:
                    db.execute("PRAGMA journal_mode=MEMORY")
                    db.execute("PRAGMA max_page_count=" + str(self.maximum_bytes // 4096))
                    db.executescript("CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);"
                        "CREATE TABLE selections(scope TEXT,artist_uuid TEXT,song_key TEXT,value TEXT,chunk TEXT,line INTEGER,proof_hash BLOB,"
                        "PRIMARY KEY(scope,artist_uuid,song_key));")
                    outcomes, actual_outcomes, originals = self._consume(db, source, report, expected, original)
                    result = {"version": "composition-selections-v1", "state": "completed", "manifest": str(self.manifest),
                        "manifestSha256": self.manifest_sha, "compositionManifestSha256": metadata["manifestSha256"],
                        "planSha256": report["planSha256"], "jobUuid": report["jobUuid"], "method": report["method"],
                        "coveredSongs": len(expected), "candidateSongs": metadata["candidateSongs"],
                        "outcomes": dict(actual_outcomes), "executionStates": dict(outcomes),
                        "publicationPermitted": False, "externalUniverseComplete": False}
                    if plan.get("targetContextManifest") is not None:
                        result["targetContextManifest"] = plan["targetContextManifest"]
                    db.executemany("INSERT INTO metadata VALUES(?,?)", ((key, json.dumps(value)) for key, value in result.items()))
                    db.commit()
                self._space()
                if (_stamp(self.compositions) != marker or sha256_file(self.manifest) != self.manifest_sha
                        or sha256_file(self.manifest.parent / "approved-plan.json") != report["planSha256"]
                        or sha256_file(hypothesis_path) != hypothesis["sha256"]):
                    raise MusicNormalizationChanged("Selection inputs changed before publication")
                for artifact in report["chunks"]:
                    _artifact(self.manifest.parent, artifact)
                for chunk in originals:
                    _artifact(hypothesis_path.parent, next(item for item in original["chunks"] if item["path"] == chunk))
                if plan.get("targetContextManifest") is not None:
                    self._context_pin(plan["targetContextManifest"])
                building.replace(self.database)
                return result
            finally:
                building.unlink(missing_ok=True)

    def _consume(self, db, source, report, expected, original):
        seen, originals = set(), set()
        for artifact in sorted(report["chunks"], key=lambda item: item["path"]):
            path = _artifact(self.manifest.parent, artifact)
            rows = 0
            with gzip.open(path, "rb") as stream:
                while line := stream.readline(MAXIMUM_LINE_BYTES + 1):
                    rows += 1
                    if len(line) > MAXIMUM_LINE_BYTES or not line.endswith(b"\n"):
                        raise ValueError("A selection row exceeds the complete-line bound")
                    record = json.loads(line)
                    row = record["proposal"]
                    key = self._key(row["input"]["key"])
                    digest = _hash(record)
                    if key not in expected or _hash(row["input"]) != _hash(expected[key]):
                        raise ValueError("Selection input differs from its saved preflight")
                    if record["domain"] != "composition_selections" or self._key(json.loads(record["inputKey"]), array=True) != key:
                        raise ValueError("Selection result changes its scoped input")
                    seen.add(key)
                    value = self._selection(source, original, row, originals)
                    # A completed resumed run can retain an earlier receipt and a
                    # later cache hit. Follow the producer's last-valid-row order.
                    db.execute("INSERT INTO selections VALUES(?,?,?,?,?,?,?) ON CONFLICT(scope,artist_uuid,song_key) "
                        "DO UPDATE SET value=excluded.value,chunk=excluded.chunk,line=excluded.line,proof_hash=excluded.proof_hash", (*key, json.dumps(value), artifact["path"], rows, digest))
                    self._space()
            if rows != artifact["rows"]:
                raise ValueError("Selection chunk row count differs from its manifest")
        if set(seen) != set(expected):
            raise ValueError("Selection chunks do not complete their selected plan")
        actual_outcomes, outcomes = Counter(), Counter()
        for value, in db.execute("SELECT value FROM selections"):
            value = json.loads(value)
            actual_outcomes[value["outcome"]] += 1
            outcomes[value["executionState"]] += 1
        if dict(actual_outcomes) != report["outcomes"]:
            raise ValueError("Selection outcomes differ from their completion manifest")
        return outcomes, actual_outcomes, originals

    @staticmethod
    def _key(value, array=False):
        scope, artist, song = value if array else (value["scope"], value["artistUuid"], value["songEntityKey"])
        return scope, str(uuid.UUID(artist)), song

    def _selection(self, source, original, row, originals):
        prepared, selection = row["input"], row["selection"]
        scope, artist, song = self._key(prepared["key"])
        parent = source.execute("SELECT chunk,line,artist_uuid FROM songs WHERE scope=? AND song_key=?", (scope, song)).fetchone()
        origin = prepared["hypothesisOrigin"]
        artifact = next((item for item in original["chunks"] if origin and item["path"] == origin["chunk"]), None)
        missing = (parent is None and origin is None and prepared["inputSha256"] is None
                   and prepared["request"] is None and selection is None
                   and prepared["skipReason"] == "missing_candidate_bearing_hypothesis")
        if not missing and (parent is None or parent["artist_uuid"] != artist or not artifact
                or (parent["chunk"], parent["line"], artifact["sha256"]) != (origin["chunk"], origin["line"], origin["sha256"])):
            raise ValueError("Selection does not reference its original scoped hypothesis")
        if origin:
            originals.add(origin["chunk"])
        selected, alternatives, request = None, [], prepared["request"]
        if selection:
            if prepared["skipReason"] is not None:
                raise ValueError("A skipped input cannot acquire a model selection")
            inference = selection.get("inference")
            if not request or selection["method"] != request["method"]:
                raise ValueError("Selection method differs from its saved request")
            if inference:
                if (inference.get("cacheKey") != request["cacheKey"] or inference["method"] != request["method"]
                        or inference.get("publicationPermitted") is not False or inference.get("externalUniverseComplete") is not False
                        or any(inference[name] != selection[name] for name in ("selectedWorkMbid", "alternativeWorkMbids", "rationale"))
                        or selection["outcome"] != (inference["outcome"] if inference["state"] == "proposed" else inference["state"])):
                    raise ValueError("Inference differs from its authenticated request or selection")
            selected = self._work_key(selection["selectedWorkMbid"])
            alternatives = [self._work_key(value) for value in selection["alternativeWorkMbids"]]
            candidates = {item[0] for item in source.execute("SELECT work_key FROM candidates WHERE scope=? AND song_key=?", (scope, song))}
            if any(key not in candidates for key in [selected, *alternatives] if key):
                raise ValueError("Model selection names a work outside its supplied candidate pool")
            state = inference["state"] if inference else "unavailable"
            outcome = selection["outcome"]
        else:
            if not prepared["skipReason"]:
                raise ValueError("A missing selection needs its saved skip reason")
            inference, state, outcome = None, "skipped", "skipped"
        return {"executionState": state, "outcome": outcome, "inferenceOutcome": inference["outcome"] if inference else None, "selectedWorkKey": selected,
            "alternativeWorkKeys": alternatives, "rationale": selection["rationale"] if selection else None,
            "method": selection["method"] if selection else None, "model": request["model"] if request else None,
            "reason": inference.get("reason") if inference else prepared["skipReason"],
            "inputSha256": prepared["inputSha256"], "cacheKey": request["cacheKey"] if request else None,
            "acceptedIdentity": False, "publicationPermitted": False, "externalUniverseComplete": False}

    @staticmethod
    def _work_key(value):
        return "composition:musicbrainz:" + str(uuid.UUID(value)) if value else None

    def _space(self):
        if shutil.disk_usage(self.database.parent).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Selection projection stopped to preserve free space")


class CompositionSelections:
    """Read a plan's model hypotheses without changing title-based membership."""

    def __init__(self, compositions, database):
        self.compositions = compositions
        self.database = Path(database).resolve(strict=True)
        self.marker = _stamp(self.database)
        with closing(sqlite3.connect(self.database.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
            self.metadata = _metadata(db)
        if (self.metadata.get("state") != "completed" or self.marker != _stamp(self.database)
                or self.metadata["compositionManifestSha256"] != compositions.metadata["manifestSha256"]):
            raise MusicNormalizationChanged("Model selections belong to another composition image")

    def _connect(self):
        self.compositions.stats()
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro&immutable=1", uri=True)
        try:
            db.row_factory = sqlite3.Row
            db.execute("BEGIN")
            actual = _metadata(db)
            self.compositions.stats()
            if actual != self.metadata or self.marker != _stamp(self.database):
                raise MusicNormalizationChanged("Model selections changed. Reload the reader")
            return db
        except BaseException:
            db.close()
            raise

    def stats(self):
        with closing(self._connect()):
            return dict(self.metadata)

    def song(self, scope, artist, key):
        with closing(self._connect()) as db:
            row = db.execute("SELECT value FROM selections WHERE scope=? AND artist_uuid=? AND song_key=?", (scope, artist, key)).fetchone()
            return json.loads(row[0]) if row else None

    def evidence(self, scope, artist, key):
        with closing(self._connect()) as db:
            row = db.execute("SELECT chunk,line,proof_hash FROM selections WHERE scope=? AND artist_uuid=? AND song_key=?", (scope, artist, key)).fetchone()
        if row is None:
            raise KeyError("No model selection covers this exact scoped song")
        path = Path(self.metadata["manifest"])
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != self.metadata["manifestSha256"]:
            raise MusicNormalizationChanged("The selection completion manifest changed")
        manifest = json.loads(raw)
        artifact = next(item for item in manifest["chunks"] if item["path"] == row["chunk"])
        chunk = _artifact(path.parent, artifact)
        with gzip.open(chunk, "rb") as stream:
            for number in range(1, row["line"] + 1):
                line = stream.readline(MAXIMUM_LINE_BYTES + 1)
                if not line or len(line) > MAXIMUM_LINE_BYTES:
                    raise MusicNormalizationChanged("The original selection evidence is unavailable")
                if number == row["line"]:
                    result = json.loads(line)
        if _hash(result) != row["proof_hash"]:
            raise MusicNormalizationChanged("The original model selection changed")
        return result


class CompositionCatalogue:
    """Read possible versions without promoting retrieval to accepted identity."""

    def __init__(self, groups, database, selections=None):
        self.groups = groups
        self.database = Path(database).resolve(strict=True)
        marker = _stamp(self.database)
        with closing(sqlite3.connect(self.database.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
            self.metadata = _metadata(db)
        if (self.metadata.get("state") != "completed"
                or self.metadata["normalizationOutputSha256"] != groups.metadata["outputManifestSha256"]
                or marker != _stamp(self.database)):
            raise MusicNormalizationChanged("Composition suggestions belong to another music image")
        self.marker = marker
        self.selections = CompositionSelections(self, selections) if selections else None

    def _connect(self):
        if _stamp(self.database) != self.marker:
            raise MusicNormalizationChanged("Composition suggestions changed. Reload the reader")
        # MusicGroups verifies both immutable music images through their actual
        # opened snapshots. Its metadata also fixes this projection's generation.
        self.groups.stats()
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro&immutable=1", uri=True)
        try:
            deadline = time.monotonic() + 10
            db.set_progress_handler(lambda: time.monotonic() > deadline, 1000)
            db.row_factory = sqlite3.Row
            db.create_function("casefold", 1, lambda value: (value or "").casefold(), deterministic=True)
            db.execute("BEGIN")
            current = json.loads(db.execute("SELECT value FROM metadata WHERE key='manifestSha256'").fetchone()[0])
            self.groups.stats()
            if current != self.metadata["manifestSha256"] or _stamp(self.database) != self.marker:
                raise MusicNormalizationChanged("Composition suggestions changed while opening the image")
            return db
        except BaseException:
            db.close()
            raise

    def stats(self):
        with closing(self._connect()):
            return dict(self.metadata)

    @staticmethod
    def _page(limit, offset):
        limit, offset = int(limit), int(offset)
        if not 1 <= limit <= 100 or offset < 0:
            raise ValueError("Use a limit of 1–100 and a nonnegative offset")
        return limit, offset

    @staticmethod
    def _filters(scope, artist, prefix="s"):
        conditions, args = [], []
        if scope:
            conditions.append(prefix + ".scope=?")
            args.append(scope)
        if artist:
            conditions.append(prefix + ".artist_uuid=?")
            args.append(str(uuid.UUID(artist)))
        return " AND ".join(conditions) or "1", args

    @staticmethod
    def _display(row):
        value = dict(row)
        for name in ("title_names", "aliases", "writer_names", "iswcs", "matching_titles"):
            if name in value:
                value[name] = json.loads(value[name])
        return value

    def works(self, q="", scope="", artist="", limit=40, offset=0):
        limit, offset = self._page(limit, offset)
        if len(q) > 256:
            raise ValueError("Use at most 256 characters in a composition search")
        conditions, args = [], []
        if q:
            conditions.append("instr(casefold(w.search_text),?)>0")
            args.append(q.casefold())
        if scope or artist:
            song_filter, song_args = self._filters(scope, artist)
            conditions.append("EXISTS(SELECT 1 FROM candidates c INDEXED BY candidates_work CROSS JOIN songs s "
                "ON s.scope=c.scope AND s.song_key=c.song_key WHERE c.work_key=w.work_key AND " + song_filter + ")")
            args.extend(song_args)
        where = " AND ".join(conditions) or "1"
        with closing(self._connect()) as db:
            total = db.execute("SELECT count(*) FROM works w WHERE " + where, args).fetchone()[0]
            rows = db.execute("SELECT w.work_key,w.provider_key,w.title,w.title_names,w.aliases,w.writer_names,w.iswcs "
                "FROM works w WHERE " + where + " ORDER BY casefold(w.title),w.work_key LIMIT ? OFFSET ?",
                (*args, limit, offset)).fetchall()
            items = []
            for row in rows:
                item = self._display(row)
                item["song_count"] = db.execute("SELECT count(*) FROM candidates INDEXED BY candidates_work WHERE work_key=?", (row["work_key"],)).fetchone()[0]
                items.append(item)
        return {"items": items, "total": total, "limit": limit, "offset": offset}

    def work(self, key, scope="", artist="", limit=40, offset=0):
        limit, offset = self._page(limit, offset)
        where, args = self._filters(scope, artist)
        with closing(self._connect()) as db:
            row = db.execute("SELECT work_key,provider_key,title,title_names,aliases,writer_names,iswcs FROM works WHERE work_key=?", (key,)).fetchone()
            if row is None:
                return None
            result = self._display(row)
            source = (" FROM candidates c INDEXED BY candidates_work CROSS JOIN songs s ON s.scope=c.scope AND s.song_key=c.song_key "
                      "WHERE c.work_key=? AND " + where)
            result["songs_total"] = db.execute("SELECT count(*)" + source, (key, *args)).fetchone()[0]
            result["songs"] = [self._display(row) for row in db.execute("SELECT s.scope,s.song_key entity_key,s.artist_uuid,s.title,s.artist_name," 
                "s.basis,s.legacy_uuid,s.original_memberships member_count,s.candidate_count,c.retrieval_basis,c.matching_titles," 
                "1 normalized_group" + source + " ORDER BY casefold(s.artist_name),s.scope,s.song_key LIMIT ? OFFSET ?", (key, *args, limit, offset))]
            result["artists"] = [dict(item) for item in db.execute("SELECT s.scope,s.artist_uuid,s.artist_name,count(*) song_count "
                "FROM candidates c INDEXED BY candidates_work CROSS JOIN songs s ON s.scope=c.scope AND s.song_key=c.song_key "
                "WHERE c.work_key=? GROUP BY s.scope,s.artist_uuid ORDER BY casefold(s.artist_name),s.scope,s.artist_uuid", (key,))]
            result["songs_limit"], result["songs_offset"] = limit, offset
            result["acceptedIdentity"], result["inference"] = False, None
            for song in result["songs"]:
                song["modelSelection"] = self.selections.song(song["scope"], song["artist_uuid"], song["entity_key"]) if self.selections else None
            return result

    def song(self, scope, artist, key):
        # Absence from this compact table means no retained candidates only for
        # a genuinely proposed target in the exact complete producer population.
        scope, artist, key = self.groups._coordinates(scope, artist, key)
        if not self.groups.member_keys(scope, artist, key):
            raise KeyError("This song is not a proposed target in the music image")
        with closing(self._connect()) as db:
            row = db.execute("SELECT scope,song_key,artist_uuid,title,candidate_count,outcome,reason,method FROM songs "
                             "WHERE scope=? AND song_key=? AND artist_uuid=?", (scope, key, artist)).fetchone()
            if row is None:
                return {"scope": scope, "song_key": key, "artist_uuid": artist, "candidates": [],
                    "outcome": "missing_retained_candidates", "reason": "no_literal_title_or_explicit_alias_in_the_retained_work_pool",
                    "acceptedIdentity": False, "inference": None,
                    "modelSelection": self.selections.song(scope, artist, key) if self.selections else None}
            result = dict(row)
            result["candidates"] = [self._display(item) for item in db.execute("SELECT w.work_key,w.provider_key,w.title," 
                "w.writer_names,c.retrieval_basis,c.matching_titles,(SELECT count(*) FROM candidates other INDEXED BY candidates_work "
                "WHERE other.work_key=w.work_key) song_count FROM candidates c CROSS JOIN works w ON w.work_key=c.work_key "
                "WHERE c.scope=? AND c.song_key=? ORDER BY c.ordinal", (scope, key))]
            result["acceptedIdentity"], result["inference"] = False, None
            result["modelSelection"] = self.selections.song(scope, artist, key) if self.selections else None
            return result

    def evidence(self, kind, key, scope="", artist=""):
        with closing(self._connect()) as db:
            if kind == "work":
                row = db.execute("SELECT chunk,line,proof_hash FROM works WHERE work_key=?", (key,)).fetchone()
            elif kind == "song":
                row = db.execute("SELECT chunk,line,proof_hash FROM songs WHERE scope=? AND song_key=? AND artist_uuid=?",
                                 (scope, key, str(uuid.UUID(artist)))).fetchone()
            else:
                raise ValueError("Choose work or song source evidence")
        if row is None:
            raise KeyError("No candidate-bearing source record is indexed for this key")
        manifest_path = Path(self.metadata["manifest"])
        raw = manifest_path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != self.metadata["manifestSha256"]:
            raise MusicNormalizationChanged("The original composition completion manifest changed")
        manifest = json.loads(raw)
        artifact = next(item for item in manifest["chunks"] if item["path"] == row["chunk"])
        path = _artifact(manifest_path.parent, artifact)
        with gzip.open(path, "rb") as stream:
            for number in range(1, row["line"] + 1):
                line = stream.readline(MAXIMUM_LINE_BYTES + 1)
                if not line or len(line) > MAXIMUM_LINE_BYTES:
                    raise MusicNormalizationChanged("The original composition evidence is unavailable")
                if number == row["line"]:
                    record = json.loads(line)
        if _hash(record) != row["proof_hash"]:
            raise MusicNormalizationChanged("The original composition evidence changed")
        return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--normalization")
    parser.add_argument("--composition-database", help="Existing composition projection; build only model selections")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--manifest-sha256", required=True)
    parser.add_argument("--database", required=True)
    parser.add_argument("--minimum-free-bytes", type=int, default=int(5.5 * 1024**3))
    parser.add_argument("--maximum-bytes", type=int)
    args = parser.parse_args()
    if not args.composition_database and not args.normalization:
        parser.error("Use --normalization for compositions or --composition-database for selections")
    builder = CompositionSelectionBuilder if args.composition_database else CompositionCatalogueBuilder
    metadata = builder(args.composition_database or args.normalization, args.manifest, args.database,
        manifest_sha256=args.manifest_sha256, minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes or (16 * 1024**2 if args.composition_database else 256 * 1024**2)).build()
    print(json.dumps(metadata, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
