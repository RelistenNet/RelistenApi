"""Browse shared C# file-title replay without changing retained recording ownership.

The replay owns song interpretation; its separate file map owns original reader
coordinates. This module joins those contracts and applies C# review deltas. It
never matches titles, parses artist aliases or chooses an audio URL.
"""

from contextlib import closing
import json
from pathlib import Path
import sqlite3
import time
import uuid

from music_consolidation import sha256_file
from reviewed_knowledge import ReviewedKnowledgeUnavailable


class FileTitleReplayChanged(ValueError):
    """A replay no longer describes the configured frozen music/file population."""


class FileTitleMembership:
    def __init__(self, receipt, groups, review_client=None):
        self.receipt = Path(receipt).resolve(strict=True)
        value = json.loads(self.receipt.read_bytes())
        if value.get("version") != "retained-file-title-overlay-v1" or value.get("state") != "completed":
            raise ValueError("A completed file-title projection receipt is required")
        self.database = self._artifact(value["projection"])
        with closing(sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)) as db:
            self.metadata = {key: json.loads(raw) for key, raw in db.execute("SELECT key,value FROM metadata")}
        if (self.metadata.get("state") != "completed"
                or self.metadata.get("fileReplayVersion") != "retained-file-title-replay-v2"
                or self.metadata.get("musicInputManifest", {}).get("sha256") != groups.metadata["inputManifestSha256"]
                or self.metadata.get("resolverVersion") != groups.metadata["resolverVersion"]
                or self.metadata.get("extractorVersion") != groups.metadata["extractorVersion"]):
            raise FileTitleReplayChanged("The retained file replay belongs to another music generation")
        self.file_map = self._artifact(self.metadata["fileMap"])
        input_manifest = self._artifact(self.metadata["inputManifest"])
        output_manifest = self._artifact(self.metadata["outputManifest"])
        if self.metadata["outputManifest"]["sha256"] != value["manifestSha256"]:
            raise FileTitleReplayChanged("The replay receipt and projection have different output pins")
        self.review_client = review_client
        music_input = self._artifact(self.metadata["musicInputManifest"])
        self.sources = {row["id"]: row for row in json.loads(music_input.read_bytes())["sources"]}
        self.witness_stamps = {}
        self.stamps = {path: self._stamp(path) for path in (self.receipt, self.database, self.file_map, input_manifest, output_manifest)}
        with closing(self._connect(reviewed=False)) as db:
            self.readers = [dict(row) for row in db.execute("SELECT * FROM filemap.readers ORDER BY id")]

    @staticmethod
    def _artifact(pin):
        path = Path(pin["path"]).resolve(strict=True)
        if path.stat().st_size != pin["bytes"] or sha256_file(path) != pin["sha256"]:
            raise FileTitleReplayChanged("A retained file-title artifact changed")
        return path

    @staticmethod
    def _stamp(path):
        value = path.stat()
        return value.st_ino, value.st_size, value.st_mtime_ns

    def stats(self):
        self._require_unchanged()
        return dict(self.metadata)

    def _require_unchanged(self):
        if any(self._stamp(path) != stamp for path, stamp in self.stamps.items()):
            raise FileTitleReplayChanged("The retained file replay changed. Reload a matching completed image")

    def _connect(self, artist=None, reviewed=True, read_timeout=10):
        self._require_unchanged()
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)
        try:
            db.row_factory = sqlite3.Row
            deadline = time.monotonic() + read_timeout
            db.set_progress_handler(lambda: time.monotonic() > deadline, 1000)
            db.create_function("casefold", 1, lambda value: (value or "").casefold(), deterministic=True)
            db.execute("ATTACH DATABASE ? AS filemap", (self.file_map.as_uri() + "?mode=ro",))
            db.execute("BEGIN")
            if reviewed and self.review_client is not None:
                snapshot = self.review_client.snapshot()
                deadline = time.monotonic() + read_timeout
                if any(rule["definition"]["domain"] == "songs" for rule in snapshot["rules"]):
                    changes = self.review_client.file_title_changes(self.metadata, snapshot)
                    selected = [change for change in changes if artist is None or change["correspondence"]["artistUuid"] == artist]
                    # Authority refresh has its own HTTP timeout. Reserve the SQL
                    # work budget for projecting and reading the completed reply.
                    deadline = time.monotonic() + read_timeout
                    self._apply_changes(db, selected)
            return db
        except BaseException:
            db.close()
            raise

    @staticmethod
    def _apply_changes(db, changes, source="main", prefix="", filemap="filemap", include_rest=False):
        if not changes:
            return source + ".interpretations", source + ".targets"
        artists = sorted({change["correspondence"]["artistUuid"] for change in changes})
        placeholders = ",".join("?" for _ in artists)
        interpretations, targets = prefix + "interpretations", prefix + "targets"
        db.execute("CREATE TEMP TABLE " + interpretations + " AS SELECT * FROM " + source
                   + ".interpretations WHERE artist_uuid IN (" + placeholders + ")", artists)
        db.execute("CREATE UNIQUE INDEX " + prefix + "interpretations_id ON " + interpretations + "(title_id)")
        db.execute("CREATE TEMP TABLE " + targets + " AS SELECT * FROM " + source
                   + ".targets WHERE artist_uuid IN (" + placeholders + ")", artists)
        db.execute("CREATE UNIQUE INDEX " + prefix + "targets_ordinal ON " + targets + "(title_id,target_ordinal)")
        db.execute("CREATE INDEX " + prefix + "targets_song ON " + targets + "(scope,artist_uuid,target_key,disposition,title_id)")
        for change in changes:
            title_id, row = change["titleId"], change["correspondence"]
            original = db.execute("SELECT * FROM " + filemap + ".titles WHERE id=?", (title_id,)).fetchone()
            if original is None or (original["scope"], original["artist_uuid"]) != (row["scope"], row["artistUuid"]):
                raise ReviewedKnowledgeUnavailable("A reviewed replay title is absent from its original file map")
            db.execute("UPDATE " + interpretations + " SET disposition=?,reason=?,sequence_basis=? WHERE title_id=?",
                       (row["disposition"], row["reason"], row["sequenceBasis"], title_id))
            db.execute("DELETE FROM " + targets + " WHERE title_id=?", (title_id,))
            for ordinal, target in enumerate(row["targets"]):
                song = target["song"]
                if song["artistUuid"] != original["artist_uuid"]:
                    raise ReviewedKnowledgeUnavailable("A reviewed replay target crosses artist ownership")
                db.execute("INSERT INTO " + targets + " VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)", (title_id, ordinal, row["scope"],
                    row["artistUuid"], song["entityKey"], song["name"], song["basis"], song.get("legacySongUuid"),
                    target["relation"], target["disposition"], target["reason"],
                    json.dumps([dict(kind=kind) for kind in target["qualifiers"]]), "null"))
        if include_rest:
            changed = prefix + "artists"
            db.execute("CREATE TEMP TABLE " + changed + "(artist_uuid TEXT PRIMARY KEY)")
            db.executemany("INSERT INTO " + changed + " VALUES(?)", ((artist,) for artist in artists))
            for table in (interpretations, targets):
                original = "interpretations" if table == interpretations else "targets"
                db.execute("CREATE TEMP VIEW " + table + "_all AS SELECT * FROM " + table + " UNION ALL SELECT b.* FROM "
                    + source + "." + original + " b WHERE NOT EXISTS(SELECT 1 FROM " + changed + " a WHERE a.artist_uuid=b.artist_uuid)")
            return "temp." + interpretations + "_all", "temp." + targets + "_all"
        return "temp." + interpretations, "temp." + targets

    def prepare_browse(self, db, artist=None, reviewed=True):
        """Attach typed replay candidates to an existing normalized-song query."""
        self._require_unchanged()
        db.execute("ATTACH DATABASE ? AS replay_data", (self.database.as_uri() + "?mode=ro",))
        db.execute("ATTACH DATABASE ? AS replay_files", (self.file_map.as_uri() + "?mode=ro",))
        changes = []
        if reviewed and self.review_client is not None:
            snapshot = self.review_client.snapshot()
            if any(rule["definition"]["domain"] == "songs" for rule in snapshot["rules"]):
                changes = self.review_client.file_title_changes(self.metadata, snapshot)
                if artist:
                    changes = [change for change in changes if change["correspondence"]["artistUuid"] == artist]
        deadline = time.monotonic() + 10
        db.set_progress_handler(lambda: time.monotonic() > deadline, 1000)
        return self._apply_changes(db, changes, source="replay_data", prefix="replay_", filemap="replay_files", include_rest=not artist)

    @staticmethod
    def browse_sql(bindings, q="", artist="", scope="", excluded_artists=()):
        _, targets = bindings
        conditions, args = ["t.disposition='proposed'"], []
        for value, field in ((artist, "artist_uuid"), (scope, "scope")):
            if value:
                conditions.append("t." + field + "=?")
                args.append(value)
        source = targets + " t"
        if q:
            source += " JOIN replay_files.titles title ON title.id=t.title_id"
            conditions.append("(instr(casefold(t.title),?)>0 OR instr(casefold(title.original_title),?)>0)")
            args.extend((q.casefold(), q.casefold()))
        if excluded_artists:
            conditions.append("t.artist_uuid NOT IN (" + ",".join("?" for _ in excluded_artists) + ")")
            args.extend(excluded_artists)
        sql = ("SELECT t.scope,t.target_key entity_key,t.artist_uuid,t.title,t.basis,t.legacy_uuid,'proposed' status,1 normalized_group "
               "FROM " + source + " WHERE " + " AND ".join(conditions)
               + " GROUP BY t.scope,t.artist_uuid,t.target_key,t.title,t.basis,t.legacy_uuid")
        return sql, args

    @staticmethod
    def _coordinates(scope, artist, key):
        artist = str(uuid.UUID(artist))
        if not scope or not isinstance(key, str) or not key.startswith("repertoire:") or len(key) > 300:
            raise ValueError("Choose a scoped replay song and exact artist UUID")
        return scope, artist, key

    def group(self, scope, artist, key, reviewed=True):
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect(artist, reviewed=reviewed)) as db:
            row = db.execute("SELECT scope,target_key entity_key,artist_uuid,title,basis,legacy_uuid FROM targets "
                "INDEXED BY targets_song WHERE scope=? AND artist_uuid=? AND target_key=? AND disposition='proposed' LIMIT 1",
                (scope, artist, key)).fetchone()
            return dict(row, normalized_group=1, replay_group=True, title_forms=[], title_forms_truncated=False) if row else None

    def original_identity(self, scope, artist, key):
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect(reviewed=False)) as db:
            row = db.execute("SELECT i.scope,i.artist_uuid,i.original_key entity_key,t.original_title title "
                "FROM interpretations i JOIN filemap.titles t ON t.id=i.title_id "
                "WHERE i.scope=? AND i.artist_uuid=? AND i.original_key=? LIMIT 1", (scope, artist, key)).fetchone()
            return dict(row, basis="title_derived", legacy_uuid=None, normalized_group=0,
                        replay_only=True, title_forms=[], title_forms_truncated=False) if row else None

    def files(self, scope, artist, key, reader_id=None, home_key=None):
        """Return exact retained files; repeated mentions remain ordered relations."""
        scope, artist, key = self._coordinates(scope, artist, key)
        conditions, args = ["t.scope=?", "t.artist_uuid=?", "t.target_key=?", "t.disposition='proposed'"], [scope, artist, key]
        if reader_id is not None:
            conditions.append("f.reader_id=?")
            args.append(reader_id)
        with closing(self._connect(artist)) as db:
            rows = db.execute("""SELECT f.*,i.original_key,t.target_ordinal,t.relation,t.qualifiers,
                t.evidence,t.reason target_reason,i.disposition interpretation_disposition,
                names.original_title,n.home_key native_home_key,n.home_kind native_home_kind
                FROM targets t INDEXED BY targets_song CROSS JOIN filemap.files f INDEXED BY files_title ON f.title_id=t.title_id
                CROSS JOIN interpretations i ON i.title_id=t.title_id
                CROSS JOIN filemap.titles names ON names.id=f.title_id
                LEFT JOIN native_homes n ON n.reader_id=f.reader_id AND n.file_key=f.file_key
                WHERE """ + " AND ".join(conditions) + " ORDER BY f.reader_id,f.record_id,f.file_ordinal,t.target_ordinal", args)
            result = {}
            for row in rows:
                show = "show:" + row["show_uuid"] if row["show_uuid"] else None
                source = "source:" + row["source_uuid"] if row["source_uuid"] else None
                home = row["native_home_key"] if row["kind"] == "native" else show or source or row["file_key"]
                kind = row["native_home_kind"] if row["kind"] == "native" else "catalog_show" if show else "recording_account" if source else "file_account"
                if not home or not kind:
                    raise FileTitleReplayChanged("A replay file has no authenticated original home")
                if home_key is not None and home != home_key:
                    continue
                identity = row["reader_id"], row["file_key"]
                file = result.setdefault(identity, dict(readerId=row["reader_id"], fileKind=row["kind"],
                    recordId=row["record_id"], fileOrdinal=row["file_ordinal"],
                    nativeFileKey=row["file_key"] if row["kind"] == "native" else None,
                    fileKey="file:" + row["file_uuid"] if row["file_uuid"] else None,
                    sourceKey=source, showKey=show, label=row["original_title"], homeKey=home, homeKind=kind,
                    matchingKeys=[], normalizationRelations=[]))
                if row["original_key"] not in file["matchingKeys"]:
                    file["matchingKeys"].append(row["original_key"])
                file["normalizationRelations"].append(dict(originalKey=row["original_key"],
                    relation=row["relation"], targetOrdinal=row["target_ordinal"], titleId=row["title_id"],
                    qualifiers=[value["kind"] for value in json.loads(row["qualifiers"])],
                    partialSource=row["interpretation_disposition"] != "proposed"))
            files = list(result.values())
            for selected_reader in {file["readerId"] for file in files}:
                self._validate_coordinates(selected_reader, [file["nativeFileKey"] or file["fileKey"]
                    for file in files if file["readerId"] == selected_reader])
            return files

    def details(self, scope, artist, key, limit=40, offset=0, reviewed=True):
        result = self.group(scope, artist, key, reviewed=reviewed)
        if result is None:
            return None
        with closing(self._connect(artist, reviewed=reviewed)) as db:
            select = (" FROM targets t INDEXED BY targets_song CROSS JOIN interpretations i ON i.title_id=t.title_id "
                      "CROSS JOIN filemap.titles original ON original.id=t.title_id "
                      "WHERE t.scope=? AND t.artist_uuid=? AND t.target_key=? AND t.disposition='proposed'")
            args = (scope, artist, key)
            result["members_total"] = db.execute("SELECT count(*)" + select, args).fetchone()[0]
            rows = db.execute("SELECT i.original_key,original.original_title,i.disposition,i.reason,i.sequence_basis,"
                "t.relation,t.target_ordinal,t.qualifiers,t.disposition target_disposition,t.reason target_reason,t.title_id"
                + select + " ORDER BY i.original_key,t.target_ordinal LIMIT ? OFFSET ?", (*args, limit, offset))
            result["members"] = [dict(row, qualifiers=[value["kind"] for value in json.loads(row["qualifiers"])], replay_only=True) for row in rows]
        result.update(members_limit=limit, members_offset=offset, works=[], replay_group=True)
        return result

    def original(self, scope, artist, key, limit=40, offset=0):
        result = self.original_identity(scope, artist, key)
        if result is None:
            return self.details(scope, artist, key, limit, offset, reviewed=False)
        with closing(self._connect(reviewed=False)) as db:
            title = db.execute("SELECT t.id,t.witness FROM interpretations i JOIN filemap.titles t ON t.id=i.title_id "
                "WHERE i.scope=? AND i.artist_uuid=? AND i.original_key=? LIMIT 1", (scope, artist, key)).fetchone()
            result["sourceWitness"] = json.loads(title["witness"])
            result["files_total"] = db.execute("SELECT count(*) FROM filemap.files WHERE title_id=?", (title["id"],)).fetchone()[0]
            result["files"] = [dict(row) for row in db.execute("SELECT * FROM filemap.files WHERE title_id=? "
                "ORDER BY reader_id,record_id,file_ordinal LIMIT ? OFFSET ?", (title["id"], limit, offset))]
        result.update(files_limit=limit, files_offset=offset)
        return result

    def iter_memberships(self, scope, artist, source_run=None):
        """Stream the same proposed replay memberships used by search/playback.

        Offline comparisons can select one original capture. Native observations
        keep nullable catalog bindings; the caller must not manufacture file IDs.
        """
        artist = str(uuid.UUID(artist))
        conditions, args = ["t.scope=?", "t.artist_uuid=?", "t.disposition='proposed'"], [scope, artist]
        if source_run is not None:
            conditions.append("r.run=?")
            args.append(str(Path(source_run).resolve(strict=True)))
        with closing(self._connect(artist, read_timeout=60)) as db:
            rows = db.execute("""SELECT f.reader_id readerId,r.run,f.file_key fileKey,f.file_uuid fileUuid,
                f.source_uuid sourceUuid,f.show_uuid showUuid,f.record_id recordId,f.file_ordinal fileOrdinal,
                f.kind fileKind,i.original_key originalKey,t.target_key targetKey,t.legacy_uuid legacySongUuid,
                t.target_ordinal targetOrdinal,t.relation,i.disposition interpretationDisposition,
                f.title_id titleId,t.title targetTitle,t.basis targetBasis,names.original_title originalTitle
                FROM targets t CROSS JOIN filemap.files f INDEXED BY files_title ON f.title_id=t.title_id
                CROSS JOIN interpretations i ON i.title_id=t.title_id
                CROSS JOIN filemap.readers r ON r.id=f.reader_id
                CROSS JOIN filemap.titles names ON names.id=f.title_id WHERE """ + " AND ".join(conditions), args)
            for row in rows:
                yield dict(row)

    def covered_files(self, scope, artist, source_run=None):
        """Yield exact titled observations whose old membership the replay replaces."""
        artist = str(uuid.UUID(artist))
        conditions, args = ["names.scope=?", "names.artist_uuid=?"], [scope, artist]
        if source_run is not None:
            conditions.append("r.run=?")
            args.append(str(Path(source_run).resolve(strict=True)))
        with closing(self._connect(reviewed=False, read_timeout=60)) as db:
            rows = db.execute("""SELECT f.reader_id readerId,r.run,f.file_key fileKey,f.file_uuid fileUuid,
                f.source_uuid sourceUuid,f.show_uuid showUuid,f.record_id recordId,f.file_ordinal fileOrdinal,f.kind fileKind,
                f.title_id titleId,i.disposition interpretationDisposition
                FROM filemap.titles names CROSS JOIN filemap.files f INDEXED BY files_title ON f.title_id=names.id
                CROSS JOIN filemap.readers r ON r.id=f.reader_id
                CROSS JOIN interpretations i ON i.title_id=f.title_id WHERE """ + " AND ".join(conditions), args)
            for row in rows:
                yield dict(row)

    def reader_ids(self, source):
        run = str(Path(source["run"]).resolve())
        return [row["id"] for row in self.readers if row["scope"] == source["scope"] and row["run"] == run]

    def homes(self, scope, artist, key, reader_id):
        homes = {}
        for file in self.files(scope, artist, key, reader_id):
            home = homes.setdefault(file["homeKey"], dict(homeKey=file["homeKey"], kind=file["homeKind"],
                showUuid=file["showKey"][5:] if file["showKey"] else None, fileCount=0,
                representativeRecordId=file["recordId"], dateRecordId=file["recordId"] if file["fileKind"] == "legacy" else None,
                literalDate=None, performanceLabel=None))
            home["fileCount"] += 1
            home["representativeRecordId"] = min(home["representativeRecordId"], file["recordId"])
        return list(homes.values())

    def _validate_coordinates(self, reader_id, file_keys, verify_witness=False):
        """Authenticate selected coordinates while allowing unrelated reader appends.

        A rebuilt source index can reuse an ordinal record ID. The retained map
        therefore authenticates the current header and compressed witness before
        the reader exposes a saved coordinate or forwards its playback response.
        """
        from shadow_reader import _unpack_text
        configuration = self.sources[reader_id]
        index = Path(configuration["index"]).resolve(strict=True)
        with closing(sqlite3.connect(index.as_uri() + "?mode=ro", uri=True)) as db:
            db.row_factory = sqlite3.Row
            db.create_function("key_text", 1, _unpack_text, deterministic=True)
            db.execute("ATTACH DATABASE ? AS filemap", (self.file_map.as_uri() + "?mode=ro",))
            db.execute("BEGIN")
            for file_key in set(file_keys):
                row = db.execute("""SELECT f.*,w.path,w.bytes,w.sha256,c.path current_path,r.line_no,
                    a.uuid current_artist,d.name current_domain,
                    coalesce(h.input_uuid,substr(key_text(input.data),-36)) current_uuid
                    FROM filemap.files f JOIN filemap.chunks w ON w.id=f.chunk_id
                    LEFT JOIN records r ON r.id=f.record_id LEFT JOIN headers h ON h.id=r.header_id
                    LEFT JOIN input_keys input ON input.id=h.input_id LEFT JOIN artists a ON a.id=h.artist_id
                    LEFT JOIN domains d ON d.id=r.domain_id LEFT JOIN chunks c ON c.id=r.chunk_id
                    WHERE f.reader_id=? AND f.file_key=?""", (reader_id, file_key)).fetchone()
                if row is None:
                    continue
                if (row["current_path"] != row["path"] or row["line_no"] != row["line"]
                        or row["current_artist"] != row["artist_uuid"] or row["current_domain"] != "file_contents"
                        or row["kind"] == "legacy" and row["current_uuid"] != row["file_uuid"]):
                    raise FileTitleReplayChanged("An original file coordinate changed. Reload a matching retained replay")
                if row["kind"] == "native":
                    child = db.execute("""SELECT key_text(k.data) FROM native_files f
                        JOIN entities k ON k.id=f.native_key_id WHERE f.parent_id=? AND f.file_ordinal=?""",
                        (row["record_id"], row["file_ordinal"])).fetchone()
                    if child is None or child[0] != file_key:
                        raise FileTitleReplayChanged("An original native file coordinate changed")
                if not verify_witness:
                    continue
                path = (Path(configuration["run"]) / row["path"]).resolve(strict=True)
                stamp = self._stamp(path)
                if self.witness_stamps.get(path) != stamp:
                    if path.stat().st_size != row["bytes"] or sha256_file(path) != row["sha256"]:
                        raise FileTitleReplayChanged("An original file witness changed")
                    if self._stamp(path) != stamp:
                        raise FileTitleReplayChanged("An original file witness changed while reading")
                    self.witness_stamps[path] = stamp

    def validate_file(self, source, record_id, file_key):
        """Fence an exact playback click independently of its membership page."""
        if not isinstance(file_key, str) or not file_key.startswith(("file:", "native_file:")) or len(file_key) > 300:
            raise ValueError("Choose the exact original file key")
        for reader_id in self.reader_ids(source):
            self._validate_coordinates(reader_id, [file_key], verify_witness=True)
            with closing(self._connect(reviewed=False)) as db:
                row = db.execute("SELECT record_id FROM filemap.files WHERE reader_id=? AND file_key=?",
                                 (reader_id, file_key)).fetchone()
                if row is not None and row[0] != record_id:
                    raise FileTitleReplayChanged("The original playback coordinate changed its retained parent")

    def fallback_queries(self, source, reader_id, read_record):
        from shadow_music import ShadowMusicQueries
        index = source.get("index") or str(Path(source["run"]) / ".shadow-reader-v3.sqlite3")
        return ShadowMusicQueries(ReadonlyMusicIndex(index, read_record),
                                  excluded_titles=(self.file_map.as_uri() + "?mode=ro", reader_id))


class ReadonlyMusicIndex:
    """Reuse original membership queries without creating/upgrading a reader index."""
    def __init__(self, database, read_record):
        self.database = Path(database).resolve(strict=True)
        self.read_record = read_record

    def connect(self, read_timeout=None):
        from shadow_reader import _unpack_text, _has_domain, _summary
        db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)
        db.row_factory = sqlite3.Row
        if read_timeout is not None:
            deadline = time.monotonic() + read_timeout
            db.set_progress_handler(lambda: time.monotonic() > deadline, 1000)
        db.create_function("key_text", 1, _unpack_text, deterministic=True)
        db.create_function("has_domain", 2, _has_domain, deterministic=True)
        db.create_function("summary_label", 1, lambda value: _summary(value)[0], deterministic=True)
        return db

    def detail(self, record_id):
        return self.read_record(record_id)
