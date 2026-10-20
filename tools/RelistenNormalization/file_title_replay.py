"""Export retained file titles and project the shared C# replay beside original readers.

The SQLite map stores original file coordinates and ownership. Literal title IDs
deduplicate work inside this artifact only; C# owns every song interpretation.
Original reader indexes and compressed result bodies remain read-only.
"""

import argparse
from contextlib import closing
import gzip
import hashlib
import itertools
import json
from pathlib import Path
import shutil
import sqlite3
import time

from shadow_music import recording_account_key
from shadow_reader import _unpack_text


MAX_LINE = 32 * 1024**2


def digest(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1024**2), b""):
            h.update(block)
    return h.hexdigest()


def pin(path, rows):
    path = Path(path).resolve(strict=True)
    return {"path": str(path), "bytes": path.stat().st_size, "sha256": digest(path), "rows": rows}


def readonly(path):
    db = sqlite3.connect(Path(path).resolve(strict=True).as_uri() + "?mode=ro", uri=True)
    db.row_factory = sqlite3.Row
    db.execute("PRAGMA query_only=ON")
    return db


class FileTitleReplayExporter:
    """Select the complete current file population, not only old successful song links."""

    def __init__(self, music_manifest, destination, manifest_sha256, minimum_free_bytes=4 * 1024**3,
                 maximum_bytes=6 * 1024**3, maximum_seconds=7200):
        self.music_manifest = Path(music_manifest).resolve(strict=True)
        self.destination = Path(destination).resolve()
        self.manifest_sha256 = manifest_sha256
        self.minimum_free_bytes = minimum_free_bytes
        self.maximum_bytes = maximum_bytes
        self.maximum_seconds = maximum_seconds
        self.started = time.monotonic()
        self.chunks = []

    def check(self):
        if time.monotonic() - self.started > self.maximum_seconds:
            raise ValueError("File title export exceeded its time bound; no completed manifest was published")
        if shutil.disk_usage(self.destination).free < self.minimum_free_bytes:
            raise ValueError("File title export reached its free-space reserve")
        if sum(p.stat().st_size for p in self.destination.iterdir() if p.is_file()) > self.maximum_bytes:
            raise ValueError("File title export exceeded its storage bound")

    def export(self):
        if digest(self.music_manifest) != self.manifest_sha256:
            raise ValueError("The music input manifest differs from its external pin")
        music = json.loads(self.music_manifest.read_bytes())
        if music.get("version") != "music-consolidation-input-v1" or music.get("state") != "completed":
            raise ValueError("A completed full music input manifest is required")
        self.destination.mkdir(parents=True, exist_ok=False)
        self.check()
        database = self.destination / "files.sqlite3"
        with closing(sqlite3.connect(database)) as output:
            output.executescript("""
                PRAGMA journal_mode=DELETE;
                CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE readers(id INTEGER PRIMARY KEY,scope TEXT,run TEXT,reader_url TEXT);
                CREATE TABLE chunks(id INTEGER PRIMARY KEY,reader_id INTEGER,path TEXT,bytes INTEGER,sha256 TEXT);
                CREATE TABLE titles(id INTEGER PRIMARY KEY,scope TEXT,artist_uuid TEXT,original_title TEXT,witness TEXT,
                    UNIQUE(scope,artist_uuid,original_title));
                CREATE TABLE files(reader_id INTEGER,file_key TEXT,file_uuid TEXT,artist_uuid TEXT,
                    source_uuid TEXT,show_uuid TEXT,title_id INTEGER,record_id INTEGER,file_ordinal INTEGER,
                    chunk_id INTEGER,line INTEGER,kind TEXT,
                    PRIMARY KEY(reader_id,file_key)) WITHOUT ROWID;
            """)
            self.output = output
            readers = []
            for source in music["sources"]:
                readers.append(self.export_reader(source))
                output.commit()
                self.check()
            files = output.execute("SELECT count(*) FROM files").fetchone()[0]
            titles = output.execute("SELECT count(*) FROM titles").fetchone()[0]
            missing = output.execute("SELECT count(*) FROM files WHERE title_id IS NULL").fetchone()[0]
            output.execute("CREATE INDEX files_title ON files(title_id,reader_id)")
            output.execute("CREATE INDEX files_uuid ON files(reader_id,file_uuid)")
            metadata = {"version": "retained-file-title-map-v1", "state": "completed", "files": files,
                        "titles": titles, "filesWithoutTitle": missing, "musicInputManifestSha256": self.manifest_sha256}
            output.executemany("INSERT INTO metadata VALUES(?,?)", ((k, json.dumps(v)) for k, v in metadata.items()))
            output.commit()
            titles_path = self.destination / "titles.jsonl.gz"
            with gzip.open(titles_path, "wt", encoding="utf-8") as stream:
                for title_id, scope, artist, title, witness in output.execute("SELECT * FROM titles ORDER BY id"):
                    stream.write(json.dumps({"titleId": title_id, "scope": scope, "artistUuid": artist,
                                             "originalTitle": title, "witness": json.loads(witness)},
                                            ensure_ascii=False, separators=(",", ":")) + "\n")
        self.check()
        request = {"version": "retained-file-title-replay-input-v1", "state": "completed",
                   "musicInputManifest": pin(self.music_manifest, 1), "fileMap": pin(database, files),
                   "titles": pin(titles_path, titles), "readers": readers,
                   "witnessChunks": self.chunks, "filesWithoutTitle": missing,
                   "maximumSeconds": 7200, "maximumOutputBytes": 2 * 1024**3,
                   "minimumFreeBytes": self.minimum_free_bytes}
        (self.destination / "manifest.json").write_text(json.dumps(request, indent=2) + "\n")
        return request

    def export_reader(self, source):
        run = Path(source["run"]).resolve(strict=True)
        index = Path(source["index"]).resolve(strict=True)
        source_id = source["id"]
        self.output.execute("INSERT INTO readers VALUES(?,?,?,?)",
                            (source_id, source["scope"], str(run), source["reader_url"]))
        counts = {"selectedParents": 0, "legacyFiles": 0, "nativeFiles": 0, "filesWithoutTitle": 0}
        with closing(readonly(index)) as db:
            db.create_function("key_text", 1, _unpack_text, deterministic=True)
            db.execute("BEGIN")
            rows = db.execute("""SELECT r.id,r.line_no,c.path,c.bytes,a.uuid artist_uuid,
                coalesce(h.input_uuid,substr(key_text(i.data),-36)) input_uuid,k.value input_kind
                FROM records r JOIN headers h ON h.id=r.header_id JOIN artists a ON a.id=h.artist_id
                JOIN input_keys i ON i.id=h.input_id
                JOIN chunks c ON c.id=r.chunk_id JOIN domains d ON d.id=r.domain_id JOIN terms k ON k.id=h.kind_id
                WHERE d.name='file_contents' AND c.state='indexed' ORDER BY c.path,r.line_no""")
            for chunk, selected_rows in itertools.groupby(rows, lambda row: row["path"]):
                self.check()
                path = (run / chunk).resolve(strict=True)
                if not path.is_relative_to(run):
                    raise ValueError("A selected file witness escapes its reader")
                chunk_pin = pin(path, 0)
                chunk_id = self.output.execute("INSERT INTO chunks(reader_id,path,bytes,sha256) VALUES(?,?,?,?) RETURNING id",
                    (source_id, chunk, chunk_pin["bytes"], chunk_pin["sha256"])).fetchone()[0]
                self.chunks.append({"run": str(run), "chunk": chunk, "bytes": chunk_pin["bytes"], "sha256": chunk_pin["sha256"]})
                line = 0
                with gzip.open(path, "rb") as stream:
                    for selected in selected_rows:
                        while line < selected["line_no"]:
                            body = stream.readline(MAX_LINE + 1)
                            if not body:
                                raise ValueError("A selected file witness is missing")
                            line += 1
                            if len(body) > MAX_LINE:
                                raise ValueError("A selected file witness exceeds the line bound")
                        record = json.loads(body)
                        if (selected["bytes"] != chunk_pin["bytes"] or record.get("domain") != "file_contents"
                                or record.get("artistUuid") != selected["artist_uuid"]
                                or record.get("inputKind") != selected["input_kind"]
                                or selected["input_uuid"] is not None and record.get("inputUuid") != selected["input_uuid"]):
                            raise ValueError("A selected file witness disagrees with its reader header")
                        counts["selectedParents"] += 1
                        witness = {"run": str(run), "chunk": chunk, "line": line, "recordId": selected["id"],
                                   "chunkSha256": chunk_pin["sha256"], "documentKind": "retained_result"}
                        self.export_parent(source, selected, record, chunk_id, witness, counts)
                        if counts["selectedParents"] % 2000 == 0:
                            self.output.commit()
                            self.check()
                if path.stat().st_size != chunk_pin["bytes"]:
                    raise ValueError("A retained file chunk changed during export")
        return {"id": source_id, "scope": source["scope"], "run": str(run), **counts}

    def export_parent(self, source, selected, record, chunk_id, witness, counts):
        proposal = record.get("proposal") or {}
        if record["inputKind"] == "provider_input":
            for ordinal, file in enumerate(proposal.get("files", [])):
                if file.get("fileOrdinal") != ordinal:
                    raise ValueError("A native file ordinal disagrees with its retained array")
                extraction = file.get("titleInterpretation") or {}
                observed = (file.get("file") or {}).get("title") or {}
                playback = file.get("catalogPlayback")
                if playback is not None and (playback.get("fileUuid") != file.get("catalogTrackUuid")
                        or playback.get("sourceUuid") != file.get("catalogSourceUuid")
                        or playback.get("showUuid") != file.get("catalogShowUuid")):
                    raise ValueError("Native playback disagrees with its retained catalog binding")
                if extraction and extraction.get("artistUuid") != selected["artist_uuid"]:
                    raise ValueError("A native title extraction belongs to another artist")
                title = extraction.get("originalTitle", observed.get("value"))
                pointer = f"/proposal/files/{ordinal}/" + ("titleInterpretation/originalTitle" if "originalTitle" in extraction else "file/title/value")
                self.add_file(source, selected, file["entityKey"], file.get("catalogTrackUuid"),
                    file.get("catalogSourceUuid"), file.get("catalogShowUuid"), title,
                    {**witness, "valuePointer": pointer}, chunk_id, ordinal, "native", counts)
                counts["nativeFiles"] += 1
        else:
            home, playback = proposal.get("home") or {}, proposal.get("playback") or {}
            file_uuid = record["inputUuid"]
            if (home.get("fileUuid") != file_uuid or playback.get("fileUuid") != file_uuid
                    or home.get("sourceUuid") != playback.get("sourceUuid") or home.get("showUuid") != playback.get("showUuid")):
                raise ValueError("Retained file playback disagrees with its original catalog home")
            interpretation = proposal.get("titleInterpretation") or {}
            if interpretation and (interpretation.get("fileUuid") != file_uuid
                                    or interpretation.get("sourceUuid") != home.get("sourceUuid")):
                raise ValueError("Retained title interpretation belongs to another file")
            extraction = interpretation.get("extraction") or {}
            if extraction and extraction.get("artistUuid") != selected["artist_uuid"]:
                raise ValueError("A retained title extraction belongs to another artist")
            self.add_file(source, selected, "file:" + file_uuid, file_uuid, home.get("sourceUuid"), home.get("showUuid"),
                interpretation.get("originalTitle"), {**witness, "valuePointer": "/proposal/titleInterpretation/originalTitle"},
                chunk_id, None, "legacy", counts)
            counts["legacyFiles"] += 1

    def add_file(self, source, selected, file_key, file_uuid, source_uuid, show_uuid, title, witness, chunk_id, ordinal, kind, counts):
        if title is not None and not isinstance(title, str):
            raise ValueError("A retained original title must be text or missing")
        title_id = None
        if title is not None:
            row = self.output.execute("SELECT id FROM titles WHERE scope=? AND artist_uuid=? AND original_title=?",
                                      (source["scope"], selected["artist_uuid"], title)).fetchone()
            if row is None:
                title_id = self.output.execute("INSERT INTO titles(scope,artist_uuid,original_title,witness) VALUES(?,?,?,?) RETURNING id",
                    (source["scope"], selected["artist_uuid"], title, json.dumps(witness, separators=(",", ":")))).fetchone()[0]
            else:
                title_id = row[0]
        else:
            counts["filesWithoutTitle"] += 1
        self.output.execute("INSERT INTO files VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
            (source["id"], file_key, file_uuid, selected["artist_uuid"], source_uuid, show_uuid, title_id,
             selected["id"], ordinal, chunk_id, selected["line_no"], kind))


class FileTitleReplayProjection:
    """Index C# title results without copying the complete file ownership map."""

    def __init__(self, manifest, destination, manifest_sha256):
        self.manifest = Path(manifest).resolve(strict=True)
        self.destination = Path(destination).resolve()
        self.manifest_sha256 = manifest_sha256

    def project(self):
        if digest(self.manifest) != self.manifest_sha256:
            raise ValueError("The title replay output manifest differs from its external pin")
        output = json.loads(self.manifest.read_bytes())
        if output.get("version") != "retained-file-title-replay-output-v1" or output.get("state") != "completed":
            raise ValueError("A completed shared C# title replay is required")
        if output["inputManifestSha256"] != output["inputManifest"]["sha256"]:
            raise ValueError("The replay input manifest pins disagree")
        artifacts = [output["inputManifest"], output["musicInputManifest"], output["fileMap"], output["titles"]]
        if output.get("knowledgeSnapshot") is not None:
            artifacts.append(output["knowledgeSnapshot"])
        for artifact in artifacts:
            path = Path(artifact["path"]).resolve(strict=True)
            if path.stat().st_size != artifact["bytes"] or digest(path) != artifact["sha256"]:
                raise ValueError("A completed title replay artifact changed")
        self.destination.mkdir(parents=True, exist_ok=False)
        database = self.destination / "replay.sqlite3"
        with closing(sqlite3.connect(database)) as target, closing(readonly(output["fileMap"]["path"])) as source:
            configurations = {reader["id"]: reader for reader in json.loads(Path(output["musicInputManifest"]["path"]).read_bytes())["sources"]}
            self.validate_file_headers(source, output["fileMap"]["path"], configurations)
            target.executescript("""
                CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                CREATE TABLE interpretations(title_id INTEGER PRIMARY KEY,scope TEXT,artist_uuid TEXT,
                    original_key TEXT,disposition TEXT,reason TEXT,sequence_basis TEXT);
                CREATE TABLE targets(title_id INTEGER,target_ordinal INTEGER,scope TEXT,artist_uuid TEXT,
                    target_key TEXT,title TEXT,basis TEXT,legacy_uuid TEXT,relation TEXT,disposition TEXT,reason TEXT,
                    qualifiers TEXT,evidence TEXT,PRIMARY KEY(title_id,target_ordinal)) WITHOUT ROWID;
                CREATE TABLE native_homes(reader_id INTEGER,file_key TEXT,account_key TEXT,home_key TEXT,home_kind TEXT,
                    PRIMARY KEY(reader_id,file_key)) WITHOUT ROWID;
            """)
            count = 0
            with gzip.open(output["titles"]["path"], "rb") as stream:
                for encoded in stream:
                    if len(encoded) > MAX_LINE:
                        raise ValueError("A replayed title exceeds its retained line bound")
                    row = json.loads(encoded)
                    count += 1
                    if row["titleId"] != count:
                        raise ValueError("The replayed title sequence is incomplete or duplicated")
                    actual = row["correspondence"]
                    original = source.execute("SELECT * FROM titles WHERE id=?", (count,)).fetchone()
                    if (original is None or actual["scope"] != original["scope"]
                            or actual["artistUuid"] != original["artist_uuid"]
                            or actual["originalTitle"] != original["original_title"]
                            or actual["sourceWitness"] != json.loads(original["witness"])):
                        raise ValueError("A replayed title lost its original title, scope, artist or witness")
                    target.execute("INSERT INTO interpretations VALUES(?,?,?,?,?,?,?)", (count, actual["scope"], actual["artistUuid"],
                        actual["originalKey"], actual["disposition"], actual["reason"], actual["sequenceBasis"]))
                    for ordinal, member in enumerate(actual["targets"]):
                        song = member["song"]
                        if song["artistUuid"] != original["artist_uuid"]:
                            raise ValueError("A file title target crosses artist ownership")
                        target.execute("INSERT INTO targets VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)", (count, ordinal, actual["scope"], actual["artistUuid"],
                            song["entityKey"], song["name"], song["basis"], song.get("legacySongUuid"), member["relation"], member["disposition"],
                            member["reason"], json.dumps(member["qualifiers"], separators=(",", ":")), json.dumps(member["evidence"], separators=(",", ":"))))
                    if count % 2000 == 0:
                        target.commit()
            if count != output["titles"]["rows"] or count != source.execute("SELECT count(*) FROM titles").fetchone()[0]:
                raise ValueError("The replay does not cover every exported file title")
            target.execute("CREATE INDEX targets_song ON targets(scope,artist_uuid,target_key,disposition,title_id)")
            target.execute("CREATE INDEX interpretations_origin ON interpretations(scope,artist_uuid,original_key)")
            self.native_homes(source, target, configurations)
            metadata = {"version": "retained-file-title-overlay-v1", "state": "completed",
                        "manifestSha256": self.manifest_sha256, "outputManifest": pin(self.manifest, 1),
                        "inputManifestSha256": output["inputManifestSha256"],
                        "inputManifest": output["inputManifest"],
                        "musicInputManifest": output["musicInputManifest"], "fileMap": output["fileMap"],
                        "extractorVersion": output["extractorVersion"], "resolverVersion": output["resolverVersion"],
                        "fileReplayVersion": output["fileReplayVersion"],
                        "knowledgeSha256": output.get("knowledgeSha256"), "knowledgeSnapshot": output.get("knowledgeSnapshot"),
                        "titles": count}
            target.executemany("INSERT INTO metadata VALUES(?,?)", ((k, json.dumps(v)) for k, v in metadata.items()))
            target.commit()
        receipt = {"version": "retained-file-title-overlay-v1", "state": "completed",
                   "projection": pin(database, count), "fileMap": output["fileMap"], "manifestSha256": self.manifest_sha256,
                   "outputManifest": pin(self.manifest, 1), "inputManifest": output["inputManifest"]}
        (self.destination / "manifest.json").write_text(json.dumps(receipt, indent=2) + "\n")
        return receipt

    @staticmethod
    def validate_file_headers(source, file_map, configurations):
        """Bind exported legacy UUIDs to compact old indexes with nullable UUID headers."""
        for reader in source.execute("SELECT * FROM readers"):
            configuration = configurations[reader["id"]]
            if reader["scope"] != configuration["scope"] or reader["run"] != str(Path(configuration["run"]).resolve(strict=True)):
                raise ValueError("The replay file map refers to another reader scope")
            with closing(readonly(configuration["index"])) as index:
                index.create_function("key_text", 1, lambda value: _unpack_text(value) if value is not None else None, deterministic=True)
                index.execute("ATTACH DATABASE ? AS filemap", (Path(file_map).resolve(strict=True).as_uri() + "?mode=ro",))
                mismatch = index.execute("""SELECT f.file_uuid FROM filemap.files f
                    JOIN filemap.chunks original_chunk ON original_chunk.id=f.chunk_id
                    LEFT JOIN records r ON r.id=f.record_id LEFT JOIN headers h ON h.id=r.header_id
                    LEFT JOIN input_keys i ON i.id=h.input_id LEFT JOIN chunks c ON c.id=r.chunk_id
                    LEFT JOIN artists a ON a.id=h.artist_id LEFT JOIN domains d ON d.id=r.domain_id
                    WHERE f.reader_id=? AND f.kind='legacy' AND (
                        coalesce(h.input_uuid,substr(key_text(i.data),-36)) IS NOT f.file_uuid
                        OR a.uuid IS NOT f.artist_uuid OR d.name IS NOT 'file_contents'
                        OR c.path IS NOT original_chunk.path OR r.line_no IS NOT f.line) LIMIT 1""", (reader["id"],)).fetchone()
                if mismatch is not None:
                    raise ValueError("A replayed file UUID or coordinate differs from its original reader header")

    @staticmethod
    def native_homes(source, target, configurations):
        """Keep recording-account grouping without rereading all legacy file bodies."""
        rows = source.execute("""SELECT f.*,c.path,c.bytes,c.sha256,r.run FROM files f
            JOIN chunks c ON c.id=f.chunk_id JOIN readers r ON r.id=f.reader_id
            WHERE f.kind='native' ORDER BY f.chunk_id,f.line,f.file_ordinal""")
        for _, chunk_rows in itertools.groupby(rows, lambda row: row["chunk_id"]):
            chunk_rows = iter(chunk_rows)
            first = next(chunk_rows)
            path = (Path(first["run"]) / first["path"]).resolve(strict=True)
            if not path.is_relative_to(Path(first["run"]).resolve(strict=True)):
                raise ValueError("A native file witness escapes its retained reader")
            if path.stat().st_size != first["bytes"] or digest(path) != first["sha256"]:
                raise ValueError("A retained native file witness changed")
            line = 0
            with gzip.open(path, "rb") as stream:
                for selected_line, selected_files in itertools.groupby(itertools.chain([first], chunk_rows), lambda row: row["line"]):
                    while line < selected_line:
                        encoded = stream.readline(MAX_LINE + 1)
                        if not encoded or len(encoded) > MAX_LINE:
                            raise ValueError("A selected native file witness is missing or exceeds its bound")
                        line += 1
                    parent = json.loads(encoded)
                    selected_files = iter(selected_files)
                    first_file = next(selected_files)
                    with closing(readonly(configurations[first_file["reader_id"]]["index"])) as index:
                        index.create_function("key_text", 1, _unpack_text, deterministic=True)
                        header = index.execute("""SELECT coalesce(h.input_uuid,substr(key_text(i.data),-36))
                            FROM records r JOIN headers h ON h.id=r.header_id JOIN input_keys i ON i.id=h.input_id
                            WHERE r.id=?""", (first_file["record_id"],)).fetchone()
                    if header is None or header[0] != parent["inputUuid"]:
                        raise ValueError("A native parent differs from its original reader header")
                    children = parent["proposal"]["files"]
                    for file in itertools.chain([first_file], selected_files):
                        child = children[file["file_ordinal"]]
                        if (child["entityKey"] != file["file_key"] or parent["artistUuid"] != file["artist_uuid"]
                                or child.get("catalogTrackUuid") != file["file_uuid"]
                                or child.get("catalogSourceUuid") != file["source_uuid"]
                                or child.get("catalogShowUuid") != file["show_uuid"]):
                            raise ValueError("A retained native file changed its original identity or home")
                        account = recording_account_key(parent["inputUuid"], child)
                        home = ("show:" + file["show_uuid"] if file["show_uuid"] else
                                "source:" + file["source_uuid"] if file["source_uuid"] else account)
                        target.execute("INSERT INTO native_homes VALUES(?,?,?,?,?)", (file["reader_id"], file["file_key"], account,
                            home, "catalog_show" if file["show_uuid"] else "recording_account"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("export", "project"))
    parser.add_argument("manifest")
    parser.add_argument("destination")
    parser.add_argument("manifest_sha256")
    args = parser.parse_args()
    if args.phase == "export":
        result = FileTitleReplayExporter(args.manifest, args.destination, args.manifest_sha256).export()
        print(json.dumps({"files": result["fileMap"]["rows"], "titles": result["titles"]["rows"],
                          "filesWithoutTitle": result["filesWithoutTitle"]}))
    else:
        print(json.dumps(FileTitleReplayProjection(args.manifest, args.destination, args.manifest_sha256).project()))
