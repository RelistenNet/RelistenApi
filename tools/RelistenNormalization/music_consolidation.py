"""Export literal music descriptors for the shared C# normalization policy.

The completed browse catalogue selects the population. Original retained bodies
authenticate every title and dictionary spelling. This module does not clean
titles, choose song identities, or copy recording evidence into another database.
"""

from contextlib import closing
from collections import Counter
import gzip
import hashlib
import itertools
import json
from pathlib import Path
import re
import shutil
import sqlite3
import sys
import uuid

from music_catalogue import CatalogueBuildPaused, MAXIMUM_LINE_BYTES, MusicCatalogueBuilder
from shadow_reader import _unpack_text


def sha256_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def title_witnesses(record):
    """Yield exact repertoire/title values and their JSON pointers in a result."""
    def visit(value, pointer):
        if isinstance(value, list):
            for index, child in enumerate(value):
                yield from visit(child, pointer + "/" + str(index))
        elif isinstance(value, dict):
            if str(value.get("entityKey", "")).startswith("repertoire:"):
                yield value["entityKey"], value.get("name"), pointer + "/name", value.get("legacySongUuid")
            if str(value.get("songEntityKey", "")).startswith("repertoire:"):
                yield value["songEntityKey"], value.get("title"), pointer + "/title", value.get("legacySongUuid")
            for key in value.get("repertoireEntityKeys", []):
                if isinstance(key, str) and key.startswith("repertoire:"):
                    yield key, value.get("title"), pointer + "/title", None
            for name, child in value.items():
                escaped = name.replace("~", "~0").replace("/", "~1")
                yield from visit(child, pointer + "/" + escaped)

    yield from visit(record.get("proposal"), "/proposal")


def dictionary_witnesses(record):
    """Only original catalog dictionary rows/memberships supply known names."""
    proposal = record.get("proposal")
    if record.get("inputKind") == "song_sample" and record.get("domain") == "repertoire":
        if isinstance(proposal, dict):
            yield proposal["entityKey"], proposal.get("name"), "/proposal/name", proposal.get("legacySongUuid")
    elif record.get("inputKind") == "setlist_sample" and record.get("domain") == "setlists":
        accounts = enumerate(proposal) if isinstance(proposal, list) else [(None, proposal)]
        for account_index, account in accounts:
            if not isinstance(account, dict):
                continue
            prefix = "/proposal" + ("/" + str(account_index) if account_index is not None else "")
            for index, member in enumerate(account.get("memberships", [])):
                yield member.get("songEntityKey"), member.get("title"), prefix + f"/memberships/{index}/title", member.get("legacySongUuid")


class MusicConsolidationExporter:
    """Write a complete, pinned metadata bundle; publish its manifest last.

    A failed export leaves an explicitly incomplete directory for inspection. It
    never replaces an existing bundle or changes the source catalogue/readers.
    """

    def __init__(self, catalogue, destination, minimum_free_bytes=int(4.5 * 1024**3),
                 maximum_bytes=512 * 1024**2):
        self.catalogue = Path(catalogue).resolve(strict=True)
        self.destination = Path(destination).resolve()
        self.minimum_free_bytes = minimum_free_bytes
        self.maximum_bytes = maximum_bytes
        self.chunk_pins = {}
        self.context_documents = []
        folder = Path(__file__).resolve().parent
        self.runtime = {"exporterVersion": "music-consolidation-export-v1", "pythonVersion": sys.version,
                        "sources": [{"path": str(folder / name), "sha256": sha256_file(folder / name)} for name in
                                    ("music_consolidation.py", "music_catalogue.py", "shadow_reader.py", "shadow_music.py")]}

    def export(self):
        self.destination.mkdir(parents=True, exist_ok=False)
        self._space()
        original_sha = sha256_file(self.catalogue)
        with closing(sqlite3.connect(self.catalogue.as_uri() + "?mode=ro", uri=True)) as db:
            db.row_factory = sqlite3.Row
            db.execute("BEGIN")
            metadata = {row[0]: json.loads(row[1]) for row in db.execute("SELECT key,value FROM metadata")}
            if metadata.get("status") != "completed_projection":
                raise ValueError("Music consolidation requires a completed browse catalogue")
            sources = {source["id"]: source for source in metadata["sources"]}
            work_keys = {}
            for row in db.execute("SELECT scope,song_key,work_key FROM work_songs ORDER BY work_key"):
                work_keys.setdefault((row[0], row[1]), []).append(row[2])
            context_work_keys = {}
            for row in db.execute("""SELECT s.scope,s.artist_uuid,s.legacy_uuid,link.work_key FROM songs s
                JOIN work_songs link ON link.scope=s.scope AND link.song_key=s.entity_key WHERE s.legacy_uuid IS NOT NULL"""):
                context_work_keys.setdefault(tuple(row[:3]), set()).add(row[3])
            descriptors = self._descriptors(db, sources, work_keys)
            dictionaries = self._dictionaries(sources, work_keys, context_work_keys)
            expected = db.execute("SELECT count(*) FROM songs").fetchone()[0]
            if descriptors["rows"] != expected:
                raise ValueError("The descriptor export did not cover the complete catalogue")
        if sha256_file(self.catalogue) != original_sha:
            raise ValueError("The completed browse catalogue changed during export")
        self._space()
        manifest = {"version": "music-consolidation-input-v1", "state": "completed",
                    "catalogue": {"path": str(self.catalogue), "sha256": original_sha,
                                  "bytes": self.catalogue.stat().st_size, "builtAt": metadata["built_at"]},
                    "sources": metadata["sources"], "descriptors": descriptors, "dictionaryNames": dictionaries,
                    "witnessChunks": list(self.chunk_pins.values()), "contextDocuments": self.context_documents,
                    "runtime": self.runtime}
        pending = self.destination / "manifest.json.pending"
        pending.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        self._space()
        pending.rename(self.destination / "manifest.json")
        return manifest

    def _space(self):
        if shutil.disk_usage(self.destination).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Music descriptor export paused to preserve free space")
        if sum(path.stat().st_size for path in self.destination.iterdir() if path.is_file()) > self.maximum_bytes:
            raise CatalogueBuildPaused("Music descriptor export reached its byte cap")

    def _descriptors(self, db, sources, work_keys):
        rows = db.execute("SELECT * FROM songs ORDER BY witness_run,witness_chunk,witness_line,scope,entity_key")
        path = self.destination / "descriptors.jsonl.gz"
        count = 0
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            for (source_id, chunk), chunk_rows in itertools.groupby(rows, lambda row: (row["witness_run"], row["witness_chunk"])):
                source = sources[source_id]
                targets = itertools.groupby(chunk_rows, lambda row: row["witness_line"])
                for record, line, selected in self._records(source, chunk, targets):
                    witnesses = list(title_witnesses(record))
                    for row in selected:
                        if row["scope"] != source["scope"] or row["artist_uuid"] != record.get("artistUuid"):
                            raise ValueError("A music descriptor disagrees with its original artist scope")
                        if row["entity_key"] not in record.get("entityKeys", []):
                            raise ValueError("A music descriptor disappeared from its original result")
                        matches = [pointer for key, title, pointer, _ in witnesses
                                   if key == row["entity_key"] and title == row["title"]]
                        if row["title"] is not None and not matches:
                            raise ValueError("A music descriptor title differs from its original witness")
                        witness = self._witness(source, chunk, line, row["witness_record"],
                                                matches[0] if row["title"] is not None else None)
                        self._write(stream, {"scope": row["scope"], "artistUuid": row["artist_uuid"],
                            "originalKey": row["entity_key"], "title": row["title"], "basis": row["basis"],
                            "status": row["status"], "legacySongUuid": row["legacy_uuid"],
                            "workKeys": work_keys.get((row["scope"], row["entity_key"]), []), "witness": witness})
                        count += 1
                        if count % 1000 == 0:
                            self._space()
        return self._file_summary(path, count)

    def _dictionaries(self, sources, work_keys, context_work_keys):
        path = self.destination / "dictionary-names.jsonl.gz"
        seen = set()
        contexts = self._context_sources(sources)
        with gzip.open(path, "wt", encoding="utf-8") as stream:
            # Original dictionary rows outrank membership duplicates regardless of
            # configured source order. Provider matching results never add names.
            for kind, domain in (("song_sample", "repertoire"), ("setlist_sample", "setlists")):
                for source in sources.values():
                    self._dictionary_source(source, kind, domain, seen, stream, work_keys)
                for source, context, artists in contexts:
                    self._context_dictionary(source, context, artists, kind, seen, stream, context_work_keys)
        for source, context, _ in contexts:
            if sha256_file(context) != self.chunk_pins[(source["run"], str(context))]["sha256"]:
                raise ValueError("The original native catalog context changed during export")
        return self._file_summary(path, len(seen))

    def _context_sources(self, sources):
        contexts = []
        for source in sources.values():
            manifest_path = Path(source["run"]) / "manifest.json"
            if not manifest_path.is_file():
                continue
            manifest = json.loads(manifest_path.read_text())
            population = manifest.get("population", {})
            if population.get("processingScope") != "provider_inputs_only":
                continue
            match = re.fullmatch(r"native-rows-inline/([a-f0-9]{64})/[^/]+", manifest.get("inputIdentity", ""))
            paths = population.get("snapshotPaths", [])
            if manifest.get("state") != "completed" or not match or paths != [population.get("snapshotPath")]:
                raise ValueError("A provider-only music context lacks an authenticated ordinary input")
            context = Path(paths[0]).resolve(strict=True)
            digest = sha256_file(context)
            if digest != match[1]:
                raise ValueError("The native catalog context differs from its producer input identity")
            artists, counts = {}, Counter()
            for _, record in self._context_rows(context):
                counts[record.get("kind")] += 1
                if record.get("kind") == "artist":
                    row = record["row"]
                    artists[row["id"]] = str(uuid.UUID(row["uuid"]))
            if dict(counts) != population["inputKinds"]:
                raise ValueError("The native catalog context differs from its producer population")
            self.chunk_pins[(source["run"], str(context))] = {"run": source["run"], "chunk": str(context),
                "bytes": context.stat().st_size, "sha256": digest}
            self.context_documents.append({"sourceRun": source["run"], "manifestSha256": sha256_file(manifest_path),
                "inputIdentity": manifest["inputIdentity"], "path": str(context), "sha256": digest,
                "bytes": context.stat().st_size, "inputKinds": dict(counts)})
            contexts.append((source, context, artists))
        return contexts

    @staticmethod
    def _context_rows(path):
        with gzip.open(path, "rb") as stream:
            line_number = 0
            while line := stream.readline(MAXIMUM_LINE_BYTES + 1):
                line_number += 1
                if len(line) > MAXIMUM_LINE_BYTES:
                    raise ValueError("A native catalog context frame exceeds the bounded line size")
                yield line_number, json.loads(line)

    def _context_dictionary(self, source, context, artists, kind, seen, stream, work_keys):
        for line, record in self._context_rows(context):
            if record.get("kind") != kind:
                continue
            row = record["row"]
            artist = artists[row["artist_id"]]
            input_key = kind + ":" + str(uuid.UUID(row["uuid"]))
            values = [(row, "/row/name")] if kind == "song_sample" else [
                (member["song"], f"/unordered_memberships/{index}/song/name")
                for index, member in enumerate(record.get("unordered_memberships", []))
                if isinstance(member.get("song"), dict)]
            for song, pointer in values:
                if song["artist_id"] != row["artist_id"]:
                    raise ValueError("A native context membership crosses its original artist")
                legacy = str(uuid.UUID(song["uuid"]))
                title = song.get("name")
                if title is None:
                    continue
                if not isinstance(title, str):
                    raise ValueError("A native context dictionary name is not a literal string")
                identity = (source["scope"], artist, legacy, title)
                if identity in seen:
                    continue
                seen.add(identity)
                self._write(stream, {"scope": source["scope"], "artistUuid": artist, "songKey": None,
                    "legacySongUuid": legacy, "title": title, "inputKey": input_key,
                    "workKeys": sorted(work_keys.get((source["scope"], artist, legacy), [])),
                    "witness": {"run": source["run"], "chunk": str(context), "line": line,
                        "recordId": 0, "valuePointer": pointer, "documentKind": "catalog_context",
                        "chunkSha256": self.chunk_pins[(source["run"], str(context))]["sha256"]}})
            self._space()

    def _dictionary_source(self, source, kind, domain, seen, stream, work_keys):
        with closing(sqlite3.connect(Path(source["index"]).as_uri() + "?mode=ro", uri=True)) as db:
            db.row_factory = sqlite3.Row
            db.execute("BEGIN")
            rows = db.execute("""SELECT r.id record_id,c.path chunk,r.line_no,c.bytes,c.mtime_ns,
                    a.uuid artist_uuid,i.data input_key,d.name domain
                FROM records r JOIN chunks c ON c.id=r.chunk_id JOIN domains d ON d.id=r.domain_id
                JOIN headers h ON h.id=r.header_id JOIN terms k ON k.id=h.kind_id
                JOIN artists a ON a.id=h.artist_id JOIN input_keys i ON i.id=r.input_id
                WHERE c.state='indexed' AND d.name=? AND k.value=?
                ORDER BY c.path,r.line_no""", (domain, kind))
            for chunk, chunk_rows in itertools.groupby(rows, lambda row: row["chunk"]):
                targets = itertools.groupby(chunk_rows, lambda row: row["line_no"])
                for record, line, selected in self._records(source, chunk, targets):
                    coordinate = next(iter(selected))
                    MusicCatalogueBuilder._check_chunk(Path(source["run"]) / chunk,
                                                       coordinate["bytes"], coordinate["mtime_ns"])
                    if (record.get("inputKey") != _unpack_text(coordinate["input_key"])
                            or record.get("artistUuid") != coordinate["artist_uuid"]
                            or record.get("domain") != coordinate["domain"]):
                        raise ValueError("A dictionary witness differs from its indexed original")
                    for key, title, pointer, legacy in dictionary_witnesses(record):
                        if not legacy or title is None or not key:
                            continue
                        identity = (source["scope"], record["artistUuid"], legacy, title)
                        if identity in seen:
                            continue
                        seen.add(identity)
                        self._write(stream, {"scope": source["scope"], "artistUuid": record["artistUuid"],
                            "songKey": key, "legacySongUuid": legacy, "title": title,
                            "inputKey": record["inputKey"],
                            "workKeys": work_keys.get((source["scope"], key), []),
                            "witness": self._witness(source, chunk, line, coordinate["record_id"], pointer)})
                    self._space()

    def _records(self, source, relative, targets):
        path = Path(source["run"]) / relative
        if (len(Path(relative).parts) != 2 or path.is_symlink() or path.parent.is_symlink()
                or path.resolve().parent.parent != Path(source["run"])):
            raise ValueError("A music witness must be an original direct domain chunk")
        before = path.stat()
        pin_key = (source["run"], relative)
        pin = {"run": source["run"], "chunk": relative,
               "sha256": sha256_file(path), "bytes": before.st_size}
        if pin_key in self.chunk_pins and self.chunk_pins[pin_key] != pin:
            raise ValueError("An original music chunk changed between export phases")
        self.chunk_pins[pin_key] = pin
        target = next(targets, None)
        with gzip.open(path, "rb") as stream:
            line_number = 0
            while target is not None:
                line_number += 1
                selected = line_number == target[0]
                line = stream.readline(MAXIMUM_LINE_BYTES + 1 if selected else 64 * 1024)
                if not line:
                    raise ValueError("An original music witness line disappeared")
                if not selected:
                    while not line.endswith(b"\n"):
                        line = stream.readline(64 * 1024)
                        if not line:
                            break
                    continue
                if len(line) > MAXIMUM_LINE_BYTES:
                    raise ValueError("An original music witness exceeds the bounded line size")
                yield json.loads(line), line_number, target[1]
                target = next(targets, None)
        MusicCatalogueBuilder._check_chunk(path, before.st_size, before.st_mtime_ns)

    def _witness(self, source, chunk, line, record_id, pointer):
        return {"run": source["run"], "chunk": chunk, "line": line, "recordId": record_id,
                "valuePointer": pointer, "chunkSha256": self.chunk_pins[(source["run"], chunk)]["sha256"],
                "documentKind": "retained_result"}

    @staticmethod
    def _write(stream, value):
        stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")

    @staticmethod
    def _file_summary(path, count):
        return {"path": path.name, "rows": count, "bytes": path.stat().st_size, "sha256": sha256_file(path)}


class MusicConsolidationProjection:
    """Project complete C# correspondences beside the unchanged browse catalogue.

    Only identities, relations and original output coordinates are stored. Exact
    checkpoint repeats are harmless; contradictory repeats and incomplete logical
    populations cannot replace a completed projection.
    """

    RELATIONS = {"identity_alias", "compound_member", "jam", "tease", "reprise", "practice"}
    DISPOSITIONS = {"proposed", "review", "unresolved", "not_applicable"}

    def __init__(self, catalogue, input_manifest, output_manifest, database,
                 minimum_free_bytes=int(4.5 * 1024**3), maximum_bytes=1024**3):
        self.catalogue = Path(catalogue).resolve(strict=True)
        self.input_manifest = Path(input_manifest).resolve(strict=True)
        self.output_manifest = Path(output_manifest).resolve(strict=True)
        self.database = Path(database).resolve()
        self.minimum_free_bytes = minimum_free_bytes
        self.maximum_bytes = maximum_bytes

    def build(self):
        input_bytes = self.input_manifest.read_bytes()
        output_bytes = self.output_manifest.read_bytes()
        inputs = json.loads(input_bytes)
        outputs = json.loads(output_bytes)
        input_sha = hashlib.sha256(input_bytes).hexdigest()
        output_sha = hashlib.sha256(output_bytes).hexdigest()
        if inputs.get("state") != "completed" or outputs.get("complete") is not True:
            raise ValueError("Music normalization needs completed input and output manifests")
        if outputs.get("inputManifestSha256") != input_sha:
            raise ValueError("Music normalization output belongs to another input bundle")
        catalogue_sha = sha256_file(self.catalogue)
        if catalogue_sha != inputs["catalogue"]["sha256"] or self.catalogue.stat().st_size != inputs["catalogue"]["bytes"]:
            raise ValueError("Music normalization belongs to another browse catalogue")
        self._artifact_path(self.output_manifest.parent, outputs["progress"])
        self._artifact_path(self.input_manifest.parent, inputs["dictionaryNames"])
        self.database.parent.mkdir(parents=True, exist_ok=True)
        self._space()
        building = self.database.with_name(self.database.name + "." + uuid.uuid4().hex + ".building")
        try:
            with closing(sqlite3.connect(building)) as db:
                db.row_factory = sqlite3.Row
                db.execute("PRAGMA journal_mode=MEMORY")
                db.execute("PRAGMA max_page_count=" + str(self.maximum_bytes // 4096))
                self._schema(db)
                db.execute("ATTACH DATABASE ? AS catalogue", (self.catalogue.as_uri() + "?mode=ro",))
                expected = self._inputs(db, inputs)
                mismatches = db.execute("""SELECT count(*) FROM originals o LEFT JOIN catalogue.songs s
                    ON s.scope=o.scope AND s.entity_key=o.original_key
                    WHERE s.entity_key IS NULL OR s.artist_uuid!=o.artist_uuid OR s.title IS NOT o.original_title""").fetchone()[0]
                if mismatches or expected != db.execute("SELECT count(*) FROM catalogue.songs").fetchone()[0]:
                    raise ValueError("The input descriptors do not cover the pinned browse catalogue")
                self._outputs(db, outputs)
                missing = db.execute("SELECT count(*) FROM originals WHERE output_hash IS NULL").fetchone()[0]
                if missing or expected != outputs["logicalRows"]:
                    raise ValueError("Music normalization output does not cover every original descriptor")
                metadata = {"version": "music-consolidation-projection-v1", "state": "completed",
                            "catalogueSha256": catalogue_sha, "catalogueBuiltAt": inputs["catalogue"]["builtAt"],
                            "inputManifest": str(self.input_manifest), "inputManifestSha256": input_sha,
                            "outputManifest": str(self.output_manifest), "outputManifestSha256": output_sha,
                            "outputRoot": str(self.output_manifest.parent), "extractorVersion": outputs["extractorVersion"],
                            "resolverVersion": outputs["resolverVersion"], "originals": expected,
                            "groups": db.execute("SELECT count(*) FROM groups").fetchone()[0],
                            "memberships": db.execute("SELECT count(*) FROM members").fetchone()[0]}
                db.executemany("INSERT INTO metadata VALUES(?,?)", ((key, json.dumps(value)) for key, value in metadata.items()))
                db.commit()
            self._space()
            if sha256_file(self.catalogue) != catalogue_sha:
                raise ValueError("The browse catalogue changed while projecting normalization")
            if sha256_file(self.input_manifest) != input_sha or sha256_file(self.output_manifest) != output_sha:
                raise ValueError("A music normalization manifest changed during projection")
            for artifact in (inputs["descriptors"], inputs["dictionaryNames"]):
                self._artifact_path(self.input_manifest.parent, artifact)
            for artifact in [*outputs["chunks"], outputs["progress"]]:
                self._artifact_path(self.output_manifest.parent, artifact)
            building.replace(self.database)
        finally:
            building.unlink(missing_ok=True)
        return metadata

    def _space(self):
        if shutil.disk_usage(self.database.parent).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Music normalization projection paused to preserve free space")

    @staticmethod
    def _schema(db):
        db.executescript("""
            CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE originals(scope TEXT,original_key TEXT,artist_uuid TEXT,original_title TEXT,expected_hash BLOB,
                output_hash BLOB,disposition TEXT,reason TEXT,sequence_basis TEXT,chunk TEXT,line INTEGER,
                PRIMARY KEY(scope,original_key)) WITHOUT ROWID;
            CREATE TABLE groups(scope TEXT,entity_key TEXT,artist_uuid TEXT,title TEXT,basis TEXT,legacy_uuid TEXT,
                PRIMARY KEY(scope,entity_key)) WITHOUT ROWID;
            CREATE TABLE members(scope TEXT,original_key TEXT,target_key TEXT,relation TEXT,
                target_ordinal INTEGER,qualifiers TEXT,disposition TEXT,reason TEXT,
                PRIMARY KEY(scope,original_key,target_ordinal)) WITHOUT ROWID;
            CREATE INDEX members_target ON members(scope,target_key,original_key);
            CREATE INDEX groups_artist ON groups(scope,artist_uuid);
            CREATE INDEX groups_literal ON groups(scope,artist_uuid,title);
            CREATE INDEX originals_literal ON originals(scope,artist_uuid,original_title);
        """)

    @staticmethod
    def _source_hash(artist, title, witness):
        return hashlib.sha256(json.dumps([artist, title, witness], sort_keys=True, ensure_ascii=False,
                                         separators=(",", ":")).encode()).digest()

    def _inputs(self, db, manifest):
        count = 0
        for _, row in self._artifact_rows(self.input_manifest.parent, manifest["descriptors"]):
            db.execute("INSERT INTO originals(scope,original_key,artist_uuid,original_title,expected_hash) VALUES(?,?,?,?,?)",
                       (row["scope"], row["originalKey"], row["artistUuid"], row["title"],
                        self._source_hash(row["artistUuid"], row["title"], row["witness"])))
            count += 1
            if count % 1000 == 0:
                self._space()
        if count != manifest["descriptors"]["rows"]:
            raise ValueError("The descriptor bundle row count changed")
        return count

    def _outputs(self, db, manifest):
        for artifact in manifest["chunks"]:
            for line, record in self._artifact_rows(self.output_manifest.parent, artifact):
                if record.get("domain") != "repertoire_correspondences":
                    raise ValueError("Unexpected domain in music normalization output")
                row = record["proposal"]
                if record.get("artistUuid") != row["artistUuid"] or record.get("inputKind") != "repertoire_descriptor":
                    raise ValueError("A music normalization result disagrees with its original header")
                previous = db.execute("SELECT * FROM originals WHERE scope=? AND original_key=?",
                                      (row["scope"], row["originalKey"])).fetchone()
                if previous is None or self._source_hash(row["artistUuid"], row["originalTitle"], row["sourceWitness"]) != previous["expected_hash"]:
                    raise ValueError("A normalized song does not match its original descriptor witness")
                output_hash = hashlib.sha256(json.dumps(row, sort_keys=True, ensure_ascii=False,
                                                       separators=(",", ":")).encode()).digest()
                if previous["output_hash"] is not None:
                    if previous["output_hash"] != output_hash:
                        raise ValueError("Conflicting normalization results repeat an original song")
                    continue
                if row["disposition"] not in self.DISPOSITIONS:
                    raise ValueError("Unknown music normalization disposition")
                for ordinal, target in enumerate(row["targets"]):
                    self._target(db, row, target, ordinal)
                db.execute("""UPDATE originals SET output_hash=?,disposition=?,reason=?,sequence_basis=?,chunk=?,line=?
                    WHERE scope=? AND original_key=?""", (output_hash, row["disposition"], row["reason"],
                        row["sequenceBasis"], artifact["path"], line, row["scope"], row["originalKey"]))
            self._space()

    def _target(self, db, row, target, ordinal):
        song = target["song"]
        if (song["artistUuid"] != row["artistUuid"] or not song["entityKey"].startswith("repertoire:")
                or target["relation"] not in self.RELATIONS or target["disposition"] not in self.DISPOSITIONS):
            raise ValueError("A normalized song target crosses its original artist or relation contract")
        values = (row["scope"], song["entityKey"], song["artistUuid"], song["name"], song["basis"], song.get("legacySongUuid"))
        existing = db.execute("SELECT * FROM groups WHERE scope=? AND entity_key=?", values[:2]).fetchone()
        if existing and tuple(existing) != values:
            raise ValueError("A shared normalized song has contradictory preferred display facts")
        db.execute("INSERT OR IGNORE INTO groups VALUES(?,?,?,?,?,?)", values)
        db.execute("INSERT INTO members VALUES(?,?,?,?,?,?,?,?)", (row["scope"], row["originalKey"], song["entityKey"],
                   target["relation"], ordinal, json.dumps([qualifier["kind"] for qualifier in target.get("qualifiers", [])],
                                                         ensure_ascii=False, separators=(",", ":")),
                   target["disposition"], target["reason"]))

    @staticmethod
    def _artifact_path(root, artifact):
        path = root / artifact["path"]
        if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError("A music normalization artifact is outside its retained bundle")
        if path.stat().st_size != artifact["bytes"] or sha256_file(path) != artifact["sha256"]:
            raise ValueError("A pinned music normalization artifact changed")
        return path

    @staticmethod
    def _artifact_rows(root, artifact):
        path = MusicConsolidationProjection._artifact_path(root, artifact)
        count = 0
        with gzip.open(path, "rb") as stream:
            while line := stream.readline(MAXIMUM_LINE_BYTES + 1):
                if len(line) > MAXIMUM_LINE_BYTES:
                    raise ValueError("A music normalization artifact exceeds the bounded line size")
                count += 1
                yield count, json.loads(line)
        if count != artifact["rows"]:
            raise ValueError("A music normalization artifact has a different physical row count")
