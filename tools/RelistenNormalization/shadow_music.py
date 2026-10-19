"""Bounded music membership queries over the reader's existing compact indexes.

Only literal entity memberships locate files. A retained show UUID groups a
performance; recordings without one keep their original account. Dates, song
titles, file positions and sets never allocate a performance here.
"""

from contextlib import closing
import hashlib
import json
import uuid


MAXIMUM_KEYS = 32768
MAXIMUM_REQUEST_BYTES = 8 * 1024**2


def recording_account_key(parent_uuid, child):
    """Identify an original recording observation, not a normalized identity."""
    origin = child.get("recordingOrigin")
    if not isinstance(origin, dict) or not origin:
        return child["entityKey"]
    literal = json.dumps([parent_uuid, origin], ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return "native_recording:" + hashlib.sha256(literal.encode()).hexdigest()


def uuid_key(value, prefix):
    if not value.startswith(prefix):
        return None
    try:
        return prefix + str(uuid.UUID(value[len(prefix):]))
    except ValueError:
        return None


class ShadowMusicQueries:
    """Use a small indexed key table, independent of SQLite's bind limit."""

    def __init__(self, index):
        self.index = index

    def _request(self, query):
        artist = str(uuid.UUID(query.get("artistUuid", "")))
        keys = query.get("entityKeys")
        if (not isinstance(keys, list) or not 1 <= len(keys) <= MAXIMUM_KEYS
                or any(not isinstance(key, str) or not key.startswith("repertoire:") or len(key) > 300 for key in keys)):
            raise ValueError("Choose 1–32768 exact repertoire keys and their artist UUID")
        limit, offset = int(query.get("limit", 40)), int(query.get("offset", 0))
        if not 1 <= limit <= 100 or offset < 0:
            raise ValueError("Use a limit of 1–100 and a nonnegative offset")
        return artist, sorted(set(keys)), limit, offset

    def _prepare(self, db, artist, keys):
        # This is connection-local scratch state. No source/cache tables change,
        # and one key per insert avoids SQLite's variable limit at full scope.
        db.execute("CREATE TEMP TABLE requested_music_keys(digest BLOB,key TEXT PRIMARY KEY) WITHOUT ROWID")
        db.executemany("INSERT INTO requested_music_keys VALUES(?,?)",
                       ((hashlib.blake2b(key.encode(), digest_size=16).digest(), key) for key in keys))
        db.create_function("music_uuid_key", 2, uuid_key, deterministic=True)
        db.execute("CREATE TEMP TABLE selected_music_files AS " + self._selection(), (artist, artist))
        db.execute("CREATE INDEX selected_music_home ON selected_music_files(home_key)")

    @staticmethod
    def _selection():
        # CROSS JOIN keeps lookup order selective: requested key -> membership ->
        # current file, then that selected file’s home memberships. Ordinary
        # aggregation joins otherwise let SQLite scan the full membership index.
        # Provider parents are not mistaken for individual files.
        return """WITH legacy_matches AS MATERIALIZED (
            SELECT DISTINCT r.id,r.input_id,r.domain_id FROM requested_music_keys wanted
            CROSS JOIN entities found ON found.digest=wanted.digest AND key_text(found.data)=wanted.key
            CROSS JOIN input_entities linked INDEXED BY entity_lookup ON linked.entity_id=found.id
            CROSS JOIN records r ON r.input_id=linked.input_id AND has_domain(linked.domains,r.domain_id)
            CROSS JOIN headers h ON h.id=r.header_id CROSS JOIN artists a ON a.id=h.artist_id
            CROSS JOIN chunks c ON c.id=r.chunk_id CROSS JOIN domains d ON d.id=r.domain_id
            CROSS JOIN terms k ON k.id=h.kind_id
            WHERE a.uuid=? AND c.state='indexed' AND d.name='file_contents' AND k.value!='provider_input'
        ), native_matches AS MATERIALIZED (
            SELECT DISTINCT f.id FROM requested_music_keys wanted
            CROSS JOIN entities found ON found.digest=wanted.digest AND key_text(found.data)=wanted.key
            CROSS JOIN native_file_entities linked INDEXED BY native_file_entity_lookup ON linked.entity_id=found.id
            CROSS JOIN native_files f ON f.id=linked.file_id CROSS JOIN records r ON r.id=f.parent_id
            CROSS JOIN headers h ON h.id=r.header_id CROSS JOIN artists a ON a.id=h.artist_id
            CROSS JOIN chunks c ON c.id=r.chunk_id WHERE a.uuid=? AND c.state='indexed'
        ), legacy_homes AS (
            SELECT m.id,min(music_uuid_key(key_text(e.data),'show:')) show_key,
                count(DISTINCT music_uuid_key(key_text(e.data),'show:')) show_keys,
                min(music_uuid_key(key_text(e.data),'source:')) source_key,
                count(DISTINCT music_uuid_key(key_text(e.data),'source:')) source_keys,
                min(music_uuid_key(key_text(e.data),'file:')) file_key
            FROM legacy_matches m CROSS JOIN input_entities linked ON linked.input_id=m.input_id
                AND has_domain(linked.domains,m.domain_id)
            CROSS JOIN entities e ON e.id=linked.entity_id GROUP BY m.id
        ), legacy_unique AS (
            SELECT *,row_number() OVER(PARTITION BY coalesce(file_key,'record:'||id) ORDER BY id DESC) choice
            FROM legacy_homes
        ), native_homes AS (
            SELECT f.id,f.parent_id,f.file_ordinal,f.recording_key,
                min(music_uuid_key(key_text(e.data),'show:')) show_key,
                count(DISTINCT music_uuid_key(key_text(e.data),'show:')) show_keys,
                min(music_uuid_key(key_text(e.data),'source:')) source_key,
                count(DISTINCT music_uuid_key(key_text(e.data),'source:')) source_keys,
                min(music_uuid_key(key_text(e.data),'file:')) file_key
            FROM native_matches m CROSS JOIN native_files f ON f.id=m.id
            CROSS JOIN native_file_entities linked ON linked.file_id=f.id CROSS JOIN entities e ON e.id=linked.entity_id GROUP BY f.id
        ), located AS (
            SELECT 'legacy' file_kind,h.id record_id,NULL native_id,NULL file_ordinal,NULL native_key,
                CASE WHEN h.show_keys=1 THEN h.show_key END show_key,
                CASE WHEN h.source_keys=1 THEN h.source_key END source_key,
                h.file_key,coalesce(h.file_key,'input:'||key_text(i.data)) account_key,
                h.show_keys>1 OR h.source_keys>1 home_conflict
            FROM legacy_unique h JOIN records r ON r.id=h.id JOIN input_keys i ON i.id=r.input_id WHERE h.choice=1
            UNION ALL
            SELECT 'native',h.parent_id,h.id,h.file_ordinal,key_text(k.data),
                CASE WHEN h.show_keys=1 THEN h.show_key END,
                CASE WHEN h.source_keys=1 THEN h.source_key END,h.file_key,h.recording_key,
                h.show_keys>1 OR h.source_keys>1
            FROM native_homes h JOIN native_files f ON f.id=h.id JOIN entities k ON k.id=f.native_key_id
        ) SELECT *,coalesce(show_key,source_key,account_key) home_key,
            CASE WHEN show_key IS NOT NULL THEN 'catalog_show'
                 WHEN source_key IS NOT NULL OR file_kind='native' THEN 'recording_account' ELSE 'file_account' END home_kind
        FROM located"""

    def homes(self, query):
        artist, keys, limit, offset = self._request(query)
        with closing(self.index.connect(read_timeout=10)) as db:
            self._prepare(db, artist, keys)
            total = db.execute("SELECT count(DISTINCT home_key) FROM selected_music_files").fetchone()[0]
            rows = db.execute("""SELECT home_key homeKey,home_kind kind,show_key showKey,
                count(*) fileCount,count(DISTINCT source_key) sourceCount,max(home_conflict) homeConflict,
                min(record_id) representativeRecordId,
                min(CASE WHEN file_kind='legacy' THEN record_id END) dateRecordId
                FROM selected_music_files GROUP BY home_key
                ORDER BY home_kind,home_key LIMIT ? OFFSET ?""", (limit, offset)).fetchall()
            items = [dict(row) for row in rows]
            for item in items:
                show_key = item.pop("showKey")
                item["showUuid"] = show_key[5:] if show_key else None
                item["homeConflict"] = bool(item["homeConflict"])
        # Only this bounded page reads a representative original file. Its typed
        # home supplies display facts; the date never participates in grouping.
        for item in items:
            date_record = item.pop("dateRecordId")
            record = self.index.detail(date_record)["record"] if date_record is not None else {}
            home = (record.get("proposal") or {}).get("home") or {}
            item.update(literalDate=home.get("literalDate"), performanceType=home.get("performanceType"),
                        performanceLabel=home.get("performanceLabel"))
        return {"items": items, "total": total, "limit": limit, "offset": offset}

    def files(self, query):
        artist, keys, limit, offset = self._request(query)
        home_key = query.get("homeKey")
        if not isinstance(home_key, str) or not home_key or len(home_key) > 400:
            raise ValueError("Choose an exact retained performance or recording account")
        with closing(self.index.connect(read_timeout=10)) as db:
            self._prepare(db, artist, keys)
            total = db.execute("SELECT count(*) FROM selected_music_files WHERE home_key=?", (home_key,)).fetchone()[0]
            rows = db.execute("""SELECT selected.file_kind fileKind,selected.record_id recordId,
                selected.file_ordinal fileOrdinal,selected.native_key nativeFileKey,
                selected.file_key fileKey,selected.source_key sourceKey,selected.show_key showKey,
                coalesce(f.label,summary_label(s.data)) label,selected.native_id nativeId
                FROM selected_music_files selected JOIN records r ON r.id=selected.record_id
                JOIN summaries s ON s.id=r.summary_id LEFT JOIN native_files f ON f.id=selected.native_id
                WHERE selected.home_key=? ORDER BY selected.file_kind,selected.record_id,selected.file_ordinal
                LIMIT ? OFFSET ?""", (home_key, limit, offset)).fetchall()
            items = []
            for row in rows:
                item = dict(row)
                native_id = item.pop("nativeId")
                if native_id is None:
                    matched = db.execute("""SELECT wanted.key FROM records r JOIN input_entities linked
                        ON linked.input_id=r.input_id AND has_domain(linked.domains,r.domain_id)
                        JOIN entities e ON e.id=linked.entity_id JOIN requested_music_keys wanted
                        ON wanted.digest=e.digest AND wanted.key=key_text(e.data) WHERE r.id=? ORDER BY wanted.key""",
                        (item["recordId"],))
                else:
                    matched = db.execute("""SELECT wanted.key FROM native_file_entities linked
                        JOIN entities e ON e.id=linked.entity_id JOIN requested_music_keys wanted
                        ON wanted.digest=e.digest AND wanted.key=key_text(e.data) WHERE linked.file_id=? ORDER BY wanted.key""",
                        (native_id,))
                item["matchingKeys"] = [key[0] for key in matched]
                items.append(item)
        return {"items": items, "total": total, "limit": limit, "offset": offset}
