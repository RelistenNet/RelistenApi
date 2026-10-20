"""Browse C# music groups beside their immutable original descriptor catalogue.

Only proposed correspondences supply default file membership. Review candidates
and unresolved originals remain searchable as their original scoped accounts.
All identity and display decisions come from the pinned C# output.
"""

from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import time
import uuid

from music_catalogue import MAXIMUM_LINE_BYTES
from music_consolidation import sha256_file


class MusicNormalizationChanged(ValueError):
    """The derived groups no longer describe the configured catalogue image."""


class MusicGroups:
    def __init__(self, catalogue, normalization):
        self.catalogue = Path(catalogue).resolve(strict=True)
        self.normalization = Path(normalization).resolve(strict=True)
        catalogue_stamp = self._stat(self.catalogue)
        normalization_stamp = self._stat(self.normalization)
        with closing(sqlite3.connect(self.normalization.as_uri() + "?mode=ro", uri=True)) as db:
            db.execute("BEGIN")
            self.metadata = {key: json.loads(value) for key, value in db.execute("SELECT key,value FROM metadata")}
            self.outcomes = dict(db.execute("SELECT disposition,count(*) FROM originals GROUP BY disposition"))
        if self.metadata.get("state") != "completed" or sha256_file(self.catalogue) != self.metadata["catalogueSha256"]:
            raise MusicNormalizationChanged("The normalized songs belong to another catalogue image")
        if catalogue_stamp != self._stat(self.catalogue) or normalization_stamp != self._stat(self.normalization):
            raise MusicNormalizationChanged("The music images changed while opening the normalized catalogue")
        self._catalogue_stat = catalogue_stamp
        self._normalization_stat = normalization_stamp

    @staticmethod
    def _stat(path):
        value = path.stat()
        return value.st_ino, value.st_size, value.st_mtime_ns

    def _connect(self):
        if self._stat(self.catalogue) != self._catalogue_stat or self._stat(self.normalization) != self._normalization_stat:
            raise MusicNormalizationChanged("The music images changed. Restart discovery with a matching completed projection")
        db = sqlite3.connect(self.normalization.as_uri() + "?mode=ro", uri=True)
        try:
            deadline = time.monotonic() + 10
            db.set_progress_handler(lambda: time.monotonic() > deadline, 1000)
            db.row_factory = sqlite3.Row
            db.create_function("casefold", 1, lambda value: (value or "").casefold(), deterministic=True)
            db.execute("ATTACH DATABASE ? AS catalogue", (self.catalogue.as_uri() + "?mode=ro",))
            db.execute("BEGIN")
            # Read both generations through the actual opened snapshots. A path can
            # be atomically replaced after the pre-open stat, while an older open
            # SQLite connection still correctly owns its previous image.
            sidecar = db.execute("SELECT value FROM metadata WHERE key='outputManifestSha256'").fetchone()
            catalogue = db.execute("SELECT value FROM catalogue.metadata WHERE key='built_at'").fetchone()
            if (sidecar is None or json.loads(sidecar[0]) != self.metadata["outputManifestSha256"]
                    or catalogue is None or json.loads(catalogue[0]) != self.metadata["catalogueBuiltAt"]
                    or self._stat(self.catalogue) != self._catalogue_stat
                    or self._stat(self.normalization) != self._normalization_stat):
                raise MusicNormalizationChanged("The music images changed while opening the normalized catalogue")
            return db
        except BaseException:
            db.close()
            raise

    @staticmethod
    def _coordinates(scope, artist, key):
        artist = str(uuid.UUID(artist))
        if not scope or not isinstance(key, str) or not key.startswith("repertoire:") or len(key) > 300:
            raise ValueError("Choose a scoped song group and its exact artist UUID")
        return scope, artist, key

    @staticmethod
    def _page(limit, offset):
        limit, offset = int(limit), int(offset)
        if not 1 <= limit <= 100 or offset < 0:
            raise ValueError("Use a limit of 1–100 and a nonnegative offset")
        return limit, offset

    def stats(self):
        with closing(self._connect()):
            return {**self.metadata, "outcomes": self.outcomes}

    def songs(self, q="", artist="", scope="", limit=40, offset=0, review_order=False):
        limit, offset = self._page(limit, offset)
        artist = str(uuid.UUID(artist)) if artist else ""
        group_where, group_args = self._search_filter("g", "title", q, artist, scope)
        original_where, original_args = self._search_filter("o", "original_title", q, artist, scope)
        # Resolve literal title/alias hits first. Correlated alias checks across
        # every group otherwise exhaust the deadline even for a small artist.
        # CROSS JOIN keeps literal candidates ahead of membership/body lookups;
        # SQLite otherwise prefers a broad scope scan on the full catalogue.
        group_source = "groups g INDEXED BY groups_artist" if artist and scope else "groups g NOT INDEXED"
        candidates = "SELECT g.scope,g.entity_key FROM " + group_source + " WHERE " + group_where
        arguments = list(group_args)
        original_source = " FROM originals o"
        if artist and scope:
            original_source = (" FROM catalogue.songs s INDEXED BY songs_artist CROSS JOIN originals o "
                               "ON o.scope=s.scope AND o.original_key=s.entity_key")
            original_where += " AND s.scope=? AND s.artist_uuid=?"
            original_args.extend((scope, artist))
        if q:
            candidates += (" UNION SELECT m.scope,m.target_key" + original_source
                + " CROSS JOIN members m ON m.scope=o.scope AND m.original_key=o.original_key"
                + " WHERE " + original_where + " AND m.disposition='proposed'")
            arguments.extend(original_args)
        # Select unresolved originals before their catalogue join. The source
        # catalogue is needed for display facts, not to decide correspondence.
        raw_condition = (original_where + " AND (o.disposition!='proposed' OR NOT EXISTS(SELECT 1 FROM members m "
            "WHERE m.scope=o.scope AND m.original_key=o.original_key AND m.disposition='proposed'))")
        raw = "SELECT o.*" + original_source + " WHERE " + raw_condition
        if q:
            group_select = ("SELECT g.* FROM candidates c CROSS JOIN groups g "
                            "ON g.scope=c.scope AND g.entity_key=c.entity_key")
            prefix = "WITH candidates AS MATERIALIZED (" + candidates + "), "
        else:
            group_select = "SELECT g.* FROM " + group_source + " WHERE " + group_where
            prefix = "WITH "
            arguments = list(group_args)
        visible = (prefix + "raw AS MATERIALIZED (" + raw + "), visible AS ("
            + "SELECT g.scope,g.entity_key,g.artist_uuid,g.title,g.basis,g.legacy_uuid,'proposed' status,1 normalized_group "
            + "FROM (" + group_select + ") g WHERE EXISTS(SELECT 1 FROM members m INDEXED BY members_target "
            + "WHERE m.scope=g.scope AND m.target_key=g.entity_key AND m.disposition='proposed') "
            + "UNION ALL SELECT o.scope,o.original_key,o.artist_uuid,o.original_title,s.basis,s.legacy_uuid,o.disposition,0 "
            + "FROM raw o CROSS JOIN catalogue.songs s ON s.scope=o.scope AND s.entity_key=o.original_key) ")
        arguments = [*arguments, *original_args] if q else [*original_args, *group_args]
        order = "casefold(v.title),v.scope,v.entity_key,v.normalized_group DESC"
        order_args = []
        if review_order and q:
            # A reviewer choosing an existing song needs exact imported entries
            # before numbered file titles. This is retrieval order, not a match.
            order = "(casefold(v.title)=?) DESC,(v.legacy_uuid IS NOT NULL) DESC," + order
            order_args = [q.casefold()]
        with closing(self._connect()) as db:
            # Count and page the same stream once. A second full count repeats
            # all correspondence lookups before the first unfiltered page.
            rows = db.execute(visible + ", selected AS (SELECT v.*,count(*) OVER() total_rows FROM visible v "
                "ORDER BY " + order + " LIMIT ? OFFSET ?) "
                "SELECT v.*,a.name artist_name FROM selected v LEFT JOIN catalogue.artists a "
                "ON a.scope=v.scope AND a.uuid=v.artist_uuid",
                (*arguments, *order_args, limit, offset)).fetchall()
            total = rows[0]["total_rows"] if rows else db.execute(visible + "SELECT count(*) FROM visible", arguments).fetchone()[0]
            items = []
            for row in rows:
                item = dict(row)
                item.pop("total_rows")
                item["member_count"] = db.execute("SELECT count(DISTINCT original_key) FROM members INDEXED BY members_target "
                    "WHERE scope=? AND target_key=? AND disposition='proposed'", (row["scope"], row["entity_key"])).fetchone()[0] if row["normalized_group"] else 1
                if review_order:
                    item.update(self._review_title_forms(db, row))
                items.append(item)
        return {"items": items, "total": total, "limit": limit, "offset": offset}

    @staticmethod
    def _search_filter(alias, title_field, q, artist, scope):
        conditions, arguments = [], []
        for value, field in ((artist, "artist_uuid"), (scope, "scope")):
            if value:
                conditions.append(alias + "." + field + "=?")
                arguments.append(value)
        if q:
            conditions.append("instr(casefold(" + alias + "." + title_field + "),?)>0")
            arguments.append(q.casefold())
        return " AND ".join(conditions) or "1", arguments

    @staticmethod
    def _review_title_forms(db, row):
        forms = [value[0] for value in db.execute("SELECT DISTINCT o.original_title FROM members m INDEXED BY members_target "
            "JOIN originals o ON o.scope=m.scope AND o.original_key=m.original_key "
            "WHERE m.scope=? AND m.target_key=? AND m.disposition='proposed' AND o.original_title IS NOT NULL "
            "ORDER BY o.original_title LIMIT 4", (row["scope"], row["entity_key"]))] if row["normalized_group"] else []
        return dict(title_forms=forms[:3], title_forms_truncated=len(forms) > 3)

    def review_identity(self, scope, artist, key):
        """Authenticate a review account without loading its work or file graph."""
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect()) as db:
            row = db.execute("SELECT g.*,1 normalized_group,a.name artist_name FROM groups g "
                "LEFT JOIN catalogue.artists a ON a.scope=g.scope AND a.uuid=g.artist_uuid "
                "WHERE g.scope=? AND g.artist_uuid=? AND g.entity_key=?", (scope, artist, key)).fetchone()
            if row is None:
                # An unresolved original is still a real imported account. It
                # remains separate from any proposed group and keeps its raw link.
                row = db.execute("SELECT s.*,0 normalized_group,a.name artist_name FROM originals o "
                    "JOIN catalogue.songs s ON s.scope=o.scope AND s.entity_key=o.original_key "
                    "LEFT JOIN catalogue.artists a ON a.scope=s.scope AND a.uuid=s.artist_uuid "
                    "WHERE o.scope=? AND o.artist_uuid=? AND o.original_key=? AND s.artist_uuid=o.artist_uuid",
                    (scope, artist, key)).fetchone()
            if row is None:
                return None
            result = dict(row)
            result.update(self._review_title_forms(db, row))
            return result

    def group(self, scope, artist, key, limit=40, offset=0):
        scope, artist, key = self._coordinates(scope, artist, key)
        limit, offset = self._page(limit, offset)
        with closing(self._connect()) as db:
            row = db.execute("SELECT g.*,a.name artist_name FROM groups g LEFT JOIN catalogue.artists a "
                "ON a.scope=g.scope AND a.uuid=g.artist_uuid WHERE g.scope=? AND g.artist_uuid=? AND g.entity_key=?",
                (scope, artist, key)).fetchone()
            if row is None:
                return None
            result = dict(row)
            result["members_total"] = db.execute("SELECT count(*) FROM members m JOIN originals o "
                "ON o.scope=m.scope AND o.original_key=m.original_key WHERE m.scope=? AND m.target_key=? "
                "AND m.disposition='proposed'", (scope, key)).fetchone()[0]
            members = db.execute("""SELECT o.original_key,o.original_title,o.disposition,o.reason,o.sequence_basis,
                m.relation,m.target_ordinal,m.qualifiers,m.disposition target_disposition,m.reason target_reason FROM members m JOIN originals o
                ON o.scope=m.scope AND o.original_key=m.original_key
                WHERE m.scope=? AND m.target_key=? AND m.disposition='proposed'
                ORDER BY o.original_key,m.target_ordinal LIMIT ? OFFSET ?""", (scope, key, limit, offset)).fetchall()
            result["members"] = [{**dict(member), "qualifiers": json.loads(member["qualifiers"])} for member in members]
            result["members_limit"], result["members_offset"] = limit, offset
            result["works"] = [dict(work) for work in db.execute("""SELECT DISTINCT w.* FROM catalogue.works w
                JOIN catalogue.work_songs link ON link.work_key=w.entity_key
                WHERE link.scope=? AND (link.song_key=? OR EXISTS(SELECT 1 FROM members m
                    WHERE m.scope=link.scope AND m.original_key=link.song_key AND m.target_key=?
                    AND m.disposition='proposed' AND m.relation='identity_alias')) ORDER BY w.entity_key""", (scope, key, key))]
            return result

    def member_keys(self, scope, artist, key):
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect()) as db:
            group = db.execute("SELECT 1 FROM groups WHERE scope=? AND artist_uuid=? AND entity_key=?", (scope, artist, key)).fetchone()
            if not group:
                raise KeyError("This normalized song group is not in the current image")
            return [row[0] for row in db.execute("SELECT DISTINCT m.original_key FROM members m JOIN originals o "
                "ON o.scope=m.scope AND o.original_key=m.original_key WHERE m.scope=? AND m.target_key=? "
                "AND m.disposition='proposed' ORDER BY m.original_key", (scope, key))]

    def original_groups(self, scope, artist, key):
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect()) as db:
            rows = db.execute("SELECT DISTINCT g.* FROM groups g JOIN members m "
                "ON m.scope=g.scope AND m.target_key=g.entity_key "
                "WHERE m.scope=? AND g.artist_uuid=? AND m.original_key=? AND m.disposition='proposed' "
                "ORDER BY g.entity_key", (scope, artist, key)).fetchall()
            return {"items": [dict(row) for row in rows]}

    def member_relations(self, scope, artist, key):
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect()) as db:
            rows = db.execute("""SELECT m.original_key,m.relation,m.qualifiers,o.disposition FROM members m
                JOIN originals o ON o.scope=m.scope AND o.original_key=m.original_key
                WHERE m.scope=? AND o.artist_uuid=? AND m.target_key=? AND m.disposition='proposed'
                ORDER BY m.original_key,m.target_ordinal""", (scope, artist, key))
            relations = {}
            for row in rows:
                relations.setdefault(row["original_key"], []).append({"originalKey": row["original_key"],
                    "relation": row["relation"], "qualifiers": json.loads(row["qualifiers"]),
                    "partialSource": row["disposition"] != "proposed"})
            return relations

    def correspondence(self, scope, artist, key):
        scope, artist, key = self._coordinates(scope, artist, key)
        with closing(self._connect()) as db:
            row = db.execute("SELECT chunk,line,output_hash FROM originals WHERE scope=? AND artist_uuid=? AND original_key=?",
                             (scope, artist, key)).fetchone()
        if row is None:
            raise KeyError("This original song is not in the normalization image")
        manifest_path = Path(self.metadata["outputManifest"])
        if sha256_file(manifest_path) != self.metadata["outputManifestSha256"]:
            raise MusicNormalizationChanged("The original normalization output manifest changed")
        manifest = json.loads(manifest_path.read_text())
        artifact = next(item for item in manifest["chunks"] if item["path"] == row["chunk"])
        path = manifest_path.parent / artifact["path"]
        if sha256_file(path) != artifact["sha256"]:
            raise MusicNormalizationChanged("The original normalization output chunk changed")
        with gzip.open(path, "rb") as stream:
            for number in range(1, row["line"] + 1):
                line = stream.readline(MAXIMUM_LINE_BYTES + 1)
                if not line or len(line) > MAXIMUM_LINE_BYTES:
                    raise MusicNormalizationChanged("The original normalization output line is unavailable")
                if number == row["line"]:
                    record = json.loads(line)
        output_hash = hashlib.sha256(json.dumps(record["proposal"], sort_keys=True, ensure_ascii=False,
                                               separators=(",", ":")).encode()).digest()
        if output_hash != row["output_hash"]:
            raise MusicNormalizationChanged("The original normalization result changed")
        return record
