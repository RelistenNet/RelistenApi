#!/usr/bin/env python3
"""Browse retained music identities and follow exact keys to existing result readers.

The small catalogue owns discovery, not file membership or accepted identities.
Readers remain responsible for current logical results and immutable body checks.
"""

import argparse
import copy
from contextlib import closing
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sqlite3
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import Request, urlopen

from composition_catalogue import CompositionCatalogue
from music_catalogue import MusicCatalogue, MusicCatalogueBuilder, Source
from music_consolidation import MusicConsolidationExporter, MusicConsolidationProjection
from music_groups import MusicGroups, MusicNormalizationChanged
from shadow_music import MAXIMUM_KEYS, MAXIMUM_REQUEST_BYTES
from reviewed_knowledge import ReviewedKnowledgeClient, ReviewedKnowledgeUnavailable, apply_composition_review

MAXIMUM_RESPONSE_BYTES = 8 * 1024 * 1024
RELATION_DOMAINS = {"file_contents", "setlists", "compositions", "repertoire"}


class CatalogueChanged(ValueError):
    """A browser must reload before using ordinal source/record coordinates."""


class MusicQueryUnavailable(ValueError):
    """A complete source union could not be obtained; no partial total is published."""


class MusicDiscovery:
    """Read the compact catalogue and proxy bounded, selective source lookups."""

    def __init__(self, catalogue, readers=(), normalization=None, compositions=None, people_url=None, venue_url=None, composition_selections=None, review_url=None):
        self.catalogue = catalogue
        self._catalogue_marker = None
        self._catalogue_stats = None
        self.groups = MusicGroups(catalogue.database, normalization) if normalization else None
        if compositions and not self.groups:
            raise ValueError("Composition suggestions require the matching normalized music image")
        if composition_selections and not compositions:
            raise ValueError("Model selections require the matching composition projection")
        self.compositions = CompositionCatalogue(self.groups, compositions, composition_selections) if compositions else None
        self.navigation = {"songs": "#songs", "compositions": "#compositions"}
        self.review_url = None
        if review_url:
            origin = urlsplit(review_url)
            if (origin.scheme not in {"http", "https"} or not origin.hostname or origin.username or origin.password
                    or origin.query or origin.fragment or origin.path not in {"", "/"}):
                raise ValueError("Review URL must be an HTTP(S) origin without credentials")
            self.review_url = review_url.rstrip("/") + "/"
        self.review_client = ReviewedKnowledgeClient(self.review_url) if self.review_url else None
        for name, url in (("tapers", people_url), ("venues", venue_url)):
            if url:
                origin = urlsplit(url)
                if (origin.scheme not in {"http", "https"} or not origin.hostname or origin.username or origin.password
                        or origin.query or origin.fragment or origin.path not in {"", "/"}):
                    raise ValueError("Directory navigation needs an HTTP(S) origin without credentials")
                self.navigation[name] = url.rstrip("/") + "/"
        self.readers = []
        for reader in readers:
            run = str(Path(reader.run).resolve(strict=True))
            identity = json.dumps([reader.scope, run, reader.reader_url], separators=(",", ":"))
            self.readers.append({"id": "reader:" + hashlib.sha256(identity.encode()).hexdigest()[:16],
                                 "scope": reader.scope, "run": run,
                                 "reader_url": reader.reader_url.rstrip("/"),
                                 "visibility": "relationship_queries_only",
                                 "title_discovery_projected": False})
        for source in self.stats()["sources"]:
            url = urlsplit(source["reader_url"])
            if url.scheme not in {"http", "https"} or not url.hostname or url.username or url.password:
                raise ValueError("Each configured reader must have an HTTP(S) origin without credentials.")
            if url.query or url.fragment or url.path not in {"", "/"}:
                raise ValueError("A configured reader URL must be an origin.")

    def stats(self):
        stat = self.catalogue.database.stat()
        marker = (stat.st_ino, stat.st_size, stat.st_mtime_ns)
        if marker != self._catalogue_marker:
            self._catalogue_stats = self.catalogue.stats()
            self._catalogue_marker = marker
        # The image is immutable between atomic replacements. Recounting its
        # million descriptors on every selective file query only adds latency.
        result = copy.deepcopy(self._catalogue_stats)
        # Old processes also serve this HTML from disk. Advertise child routes
        # independently of projection metadata during a controlled cutover.
        result["native_file_queries"] = True
        result["navigation"] = dict(self.navigation)
        result["reviewUrl"] = self.review_url
        result["normalization_review"] = bool(self.review_url)
        result["song_identity_review"] = bool(self.review_url and self.groups)
        if self.groups:
            result["normalization"] = self.groups.stats()
            result["normalized_song_queries"] = True
        if self.compositions:
            result["composition_queries"] = True
            result["compositionSuggestions"] = self.compositions.stats()
            if self.compositions.selections:
                result["composition_selection_queries"] = True
                result["compositionSelections"] = self.compositions.selections.stats()
        sources = result["sources"]
        for reader in self.readers:
            if not any((source["scope"], source["run"]) == (reader["scope"], reader["run"]) for source in sources):
                sources.append(dict(reader))
        # Ordinal catalogue IDs and configured relationship-only IDs are both tied
        # to this image. Open pages must reload after either mapping changes.
        generation = [result["built_at"], [(s["id"], s["scope"], s["run"], s["reader_url"]) for s in sources],
                      (result.get("normalization") or {}).get("outputManifestSha256"),
                      (result.get("compositionSuggestions") or {}).get("manifestSha256"),
                      (result.get("compositionSelections") or {}).get("manifestSha256")]
        result["generation"] = hashlib.sha256(json.dumps(generation, separators=(",", ":")).encode()).hexdigest()[:24]
        return result

    def _review_song_ref(self, row, literal=None, domain="compositions"):
        coordinates = dict(scope=row["scope"], artist=row["artist_uuid"], key=row["entity_key"])
        route = "/api/song" if row.get("normalized_group") == 0 or row.get("witness_record") is not None else "/api/song-group"
        description = row.get("artist_name") or row["scope"]
        if domain == "songs":
            if row.get("legacy_uuid"):
                description += " · Imported song · ID …" + row["legacy_uuid"][-8:]
            elif row.get("basis") == "title_derived":
                description += " · Title-derived"
            else:
                description += " · Captured song entry"
            forms = row.get("title_forms")
            if forms:
                description += " · Titles: " + " / ".join(value[:100] + ("…" if len(value) > 100 else "") for value in forms)
                if row.get("title_forms_truncated"):
                    description += " / …"
        return dict(domain=domain, namespace="repertoire",
                    stableKey=json.dumps([row["scope"], row["entity_key"]], separators=(",", ":")),
                    literal=row["title"] if literal is None else literal, artistUuid=row["artist_uuid"],
                    provider=None, locality=None, date=None, displayName=row["title"],
                    description=description,
                    evidenceUrl=route + "?" + urlencode(coordinates))

    def _review_work_ref(self, row):
        return dict(domain="compositions", namespace="composition_work", stableKey=row["work_key"],
                    literal=row["title"], artistUuid=None, provider="musicbrainz", locality=None, date=None,
                    displayName=row["title"], description=", ".join(row.get("writer_names") or []),
                    evidenceUrl="/api/composition-work?" + urlencode({"key": row["work_key"], "generation": self.stats()["generation"]}))

    def review_source(self, parameters):
        key, artist = parameters.get("key", ""), parameters.get("artist", "")
        domain = parameters.get("domain", "compositions")
        if domain not in {"compositions", "songs"}:
            raise ValueError("Choose a song or composition review")
        if key.startswith("composition:musicbrainz:"):
            if domain == "songs":
                raise ValueError("Song correspondence targets must be existing songs by this artist")
            row = self.compositions.work(key, limit=1) if self.compositions else None
            items = [self._review_work_ref(row)] if row else []
        else:
            try:
                coordinates = json.loads(key)
                if not isinstance(coordinates, list) or len(coordinates) != 2 or not all(isinstance(value, str) for value in coordinates):
                    raise ValueError()
                scope, song_key = coordinates
            except (ValueError, TypeError):
                raise ValueError("Choose an exact scoped song key") from None
            row = self.groups.review_identity(scope, artist, song_key) if self.groups else None
            if row is not None and domain == "compositions" and not row["normalized_group"]:
                row = None
            items = [self._review_song_ref(row, domain=domain)] if row else []
        return dict(items=items, total=len(items), complete=True)

    def review_candidates(self, parameters):
        if parameters.get("domain", "compositions") not in {"compositions", "songs"}:
            raise ValueError("Choose a song or composition review")
        if parameters.get("domain") == "songs":
            if not self.groups or not parameters.get("artist") or not parameters.get("scope"):
                raise ValueError("Song correspondence needs this artist and source scope")
            if len(parameters.get("q", "")) > 256:
                raise ValueError("Use at most 256 characters in a song search")
            result = self.groups.songs(q=parameters.get("q", ""), artist=parameters["artist"], scope=parameters["scope"],
                                       review_order=True, **page(parameters))
            return dict(items=[self._review_song_ref(row, domain="songs") for row in result["items"]], total=result["total"],
                        complete=result["offset"] == 0 and len(result["items"]) == result["total"],
                        offset=result["offset"], limit=result["limit"])
        if not self.compositions:
            raise ValueError("No completed composition suggestions are configured")
        result = self.compositions.works(q=parameters.get("q", ""), **page(parameters))
        return dict(items=[self._review_work_ref(row) for row in result["items"]], total=result["total"],
                    complete=result["offset"] == 0 and len(result["items"]) == result["total"],
                    offset=result["offset"], limit=result["limit"])

    def review_observations(self, parameters):
        """Retrieve literal memberships; the operator host alone decides reusable scope."""
        if not self.groups:
            raise ValueError("No completed music normalization is configured")
        domain = parameters.get("domain", "compositions")
        if domain not in {"compositions", "songs"}:
            raise ValueError("Choose a song or composition review")
        literal = parameters.get("literal", "")
        if not literal or len(literal) > 4096:
            raise ValueError("Choose an original song spelling of 1–4096 characters")
        bounds = page(parameters)
        artist = parameters.get("artist", "")
        artist_filter = " AND g.artist_uuid=?" if artist else ""
        values = [literal, literal] + ([artist] if artist else [])
        # The shared C# correspondence owns the original-to-group relationship.
        # Text equality retrieves observations and never creates a new identity link.
        selection = ("WITH matched AS (SELECT scope,entity_key FROM groups WHERE title=? UNION "
                     "SELECT m.scope,m.target_key FROM originals o JOIN members m "
                     "ON m.scope=o.scope AND m.original_key=o.original_key "
                     "WHERE o.original_title=? AND m.disposition='proposed') "
                     "SELECT g.scope,g.entity_key,g.artist_uuid,g.title,g.basis,g.legacy_uuid,1 normalized_group,a.name artist_name FROM matched k JOIN groups g "
                     "ON g.scope=k.scope AND g.entity_key=k.entity_key LEFT JOIN catalogue.artists a "
                     "ON a.scope=g.scope AND a.uuid=g.artist_uuid WHERE 1" + artist_filter)
        if domain == "songs":
            # Benchmark IDs may name an original descriptor that has no proposed
            # group. Keep that original account addressable without inventing a link.
            selection += (" UNION ALL SELECT s.scope,s.entity_key,s.artist_uuid,s.title,s.basis,s.legacy_uuid,0 normalized_group,a.name artist_name "
                          "FROM originals o JOIN catalogue.songs s ON s.scope=o.scope AND s.entity_key=o.original_key "
                          "LEFT JOIN catalogue.artists a ON a.scope=s.scope AND a.uuid=s.artist_uuid "
                          "WHERE o.original_title=? AND NOT EXISTS(SELECT 1 FROM groups g "
                          "WHERE g.scope=o.scope AND g.entity_key=o.original_key)" + (" AND s.artist_uuid=?" if artist else ""))
            values.extend([literal] + ([artist] if artist else []))
        with closing(self.groups._connect()) as db:
            total = db.execute("SELECT count(*) FROM (" + selection + ")", values).fetchone()[0]
            rows = db.execute("SELECT * FROM (" + selection + ") ORDER BY scope,entity_key LIMIT ? OFFSET ?",
                              [*values, bounds["limit"], bounds["offset"]]).fetchall()
            items = [self._review_song_ref(dict(row), literal, domain) for row in rows]
        return dict(items=items, total=total, complete=bounds["offset"] == 0 and len(items) == total, **bounds)

    def _reviewed_work_references(self, work_key, snapshot):
        """Retrieve exact rule subjects; C# still decides which rules actually apply."""
        references, missing = {}, 0
        for rule in snapshot["rules"]:
            definition = rule["definition"]
            target = definition["target"]
            if (rule["state"] != "accepted" or definition["domain"] != "compositions"
                    or target["namespace"] != "composition_work" or target["stableKey"] != work_key):
                continue
            selector = definition["selector"]
            if selector["namespace"] != "repertoire":
                continue
            parameters = dict(artist=selector.get("artistUuid") or "")
            if selector.get("accountStableKey") is not None:
                page_result = self.review_source({**parameters, "key": selector["accountStableKey"]})
                missing += not page_result["items"]
                pages = [page_result]
            else:
                pages, offset = [], 0
                while True:
                    result = self.review_observations({**parameters, "literal": selector["literal"], "offset": offset, "limit": 100})
                    if result["total"] > 1000 or (not result["items"] and offset < result["total"]):
                        raise MusicQueryUnavailable("The complete reviewed composition exceeds its 1,000-observation lookup bound")
                    pages.append(result)
                    offset += len(result["items"])
                    if offset >= result["total"]:
                        break
            for result in pages:
                for reference in result["items"]:
                    references[json.dumps(reference, sort_keys=True)] = reference
                    if len(references) > 1000:
                        raise MusicQueryUnavailable("The complete reviewed composition exceeds its 1,000-observation lookup bound")
        return list(references.values()), int(missing)

    def reviewed_work_songs(self, work_key, parameters):
        """Add reviewed membership without rewriting the frozen source candidate pool."""
        result = dict(reviewedSongs=[], reviewedSongsTotal=0, reviewedSongsOffset=0, reviewedSongsLimit=100,
                      reviewedSongsComplete=True, reviewedArtists=[], reviewedMissingSources=0)
        if self.review_client is None:
            return result
        snapshot = self.review_client.snapshot()
        references, missing = self._reviewed_work_references(work_key, snapshot)
        decisions = self.review_client.evaluate(references, snapshot) if references else []
        groups = {}
        for reference, decision in zip(references, decisions):
            target = decision.get("target")
            if not target or target["namespace"] != "composition_work" or target["stableKey"] != work_key:
                continue
            scope, key = json.loads(reference["stableKey"])
            identity = (scope, reference["artistUuid"], key)
            if identity not in groups:
                row = self.groups.group(*identity, limit=1)
                if row is None:
                    raise CatalogueChanged("A reviewed song changed during lookup. Reload this composition")
                row.update(member_count=row.pop("members_total"), normalized_group=True,
                           reviewedDecision=decision, preferredWorkKey=work_key, preferredBasis="reviewed", reviewedContexts=[])
                row.pop("members", None)
                groups[identity] = row
            groups[identity]["reviewedContexts"].append(dict(sourceRef=reference, decision=decision))
        artists = {}
        for row in groups.values():
            artist = artists.setdefault((row["scope"], row["artist_uuid"]), dict(scope=row["scope"], artist_uuid=row["artist_uuid"],
                artist_name=row.get("artist_name"), song_count=0))
            artist["song_count"] += 1
        rows = [row for row in groups.values() if (not parameters.get("scope") or row["scope"] == parameters["scope"])
                and (not parameters.get("artist") or row["artist_uuid"] == parameters["artist"])]
        rows.sort(key=lambda row: ((row.get("artist_name") or "").casefold(), row["scope"], row["entity_key"]))
        offset = max(0, int(parameters.get("reviewedOffset", 0)))
        return dict(reviewedSongs=rows[offset:offset + 100], reviewedSongsTotal=len(rows), reviewedSongsOffset=offset,
                    reviewedSongsLimit=100, reviewedSongsComplete=offset == 0 and len(rows) <= 100,
                    reviewedArtists=sorted(artists.values(), key=lambda row: ((row["artist_name"] or "").casefold(), row["scope"], row["artist_uuid"])),
                    reviewedMissingSources=missing)

    def _group_query(self, query):
        metadata = self.stats()
        if query.get("generation") != metadata["generation"]:
            raise CatalogueChanged("The discovery catalogue changed. Reload before opening song performances")
        if not self.groups:
            raise ValueError("No completed music normalization is configured")
        scope, artist, key = query.get("scope", ""), query.get("artist", ""), query.get("key", "")
        keys = self.groups.member_keys(scope, artist, key)
        if not keys:
            return [], {"artistUuid": artist, "entityKeys": []}
        if len(keys) > MAXIMUM_KEYS:
            raise MusicQueryUnavailable("This song group exceeds the complete union-query key bound")
        sources = [source for source in metadata["sources"] if source["scope"] == scope and self._has_files(source)]
        return sources, {"artistUuid": artist, "entityKeys": keys}

    @staticmethod
    def _has_files(source):
        """Keep proof-only readers without calling file routes they do not need."""
        if not source.get("index"):
            return True  # Explicit relationship-only readers may have newer live files.
        with closing(sqlite3.connect(Path(source["index"]).as_uri() + "?mode=ro", uri=True)) as db:
            if db.execute("SELECT 1 FROM sqlite_master WHERE name='native_files'").fetchone():
                native = db.execute("SELECT 1 FROM native_files f JOIN records r ON r.id=f.parent_id "
                    "JOIN chunks c ON c.id=r.chunk_id WHERE c.state='indexed' LIMIT 1").fetchone()
                if native:
                    return True
            return db.execute("SELECT 1 FROM records r JOIN chunks c ON c.id=r.chunk_id "
                "JOIN headers h ON h.id=r.header_id JOIN terms kind ON kind.id=h.kind_id "
                "WHERE r.domain_id=(SELECT id FROM domains WHERE name='file_contents') "
                "AND c.state='indexed' AND kind.value!='provider_input' LIMIT 1").fetchone() is not None

    @staticmethod
    def _music_pages(source, route, payload):
        """Read the complete selective union before deriving a cross-reader total."""
        items, expected, offset, total_bytes = [], None, 0, 0
        while expected is None or offset < expected:
            body = json.dumps({**payload, "limit": 100, "offset": offset}, separators=(",", ":")).encode()
            if len(body) > MAXIMUM_REQUEST_BYTES:
                raise MusicQueryUnavailable("This complete song group exceeds the reader request bound")
            request = Request(source["reader_url"].rstrip("/") + route, data=body,
                              headers={"Content-Type": "application/json"})
            with urlopen(request, timeout=20) as response:
                result_bytes = response.read(MAXIMUM_RESPONSE_BYTES + 1)
            total_bytes += len(result_bytes)
            if len(result_bytes) > MAXIMUM_RESPONSE_BYTES or total_bytes > 64 * 1024**2:
                raise MusicQueryUnavailable("This complete song query exceeds the bounded response size")
            result = json.loads(result_bytes)
            if expected is not None and result["total"] != expected:
                raise MusicQueryUnavailable("The original reader changed during this query. Try again")
            expected = result["total"]
            if not result["items"] and offset < expected:
                raise MusicQueryUnavailable("The original reader returned incomplete music membership")
            items.extend(result["items"])
            offset += len(result["items"])
        return items

    @staticmethod
    def _home_identity(source, home):
        if home["homeKey"].startswith(("show:", "source:")):
            return home["homeKey"]
        return "reader:" + str(source["id"]) + ":" + home["homeKey"]

    def performances(self, query):
        sources, payload = self._group_query(query)
        homes = {}
        for source in sources:
            for home in self._music_pages(source, "/api/music/homes", payload):
                identity = self._home_identity(source, home)
                combined = homes.setdefault(identity, {"homeKey": identity, "kind": home["kind"],
                    "showUuid": home["showUuid"], "sources": [], "fileCountUpperBound": 0,
                    "literalDate": home.get("literalDate"), "performanceLabel": home.get("performanceLabel")})
                combined["fileCountUpperBound"] += home["fileCount"]
                combined["sources"].append({"source": source["id"], "homeKey": home["homeKey"],
                    "fileCount": home["fileCount"], "representativeRecordId": home["representativeRecordId"]})
        ordered = sorted(homes.values(), key=lambda home: (home["kind"], home["literalDate"] or "", home["homeKey"]))
        bounds = page(query)
        offset, limit = bounds["offset"], bounds["limit"]
        return {"items": ordered[offset:offset + limit], "total": len(ordered), **bounds,
                "performancesTotal": sum(home["kind"] == "catalog_show" for home in ordered),
                "unboundAccountsTotal": sum(home["kind"] != "catalog_show" for home in ordered)}

    def performance_files(self, query):
        sources, payload = self._group_query(query)
        relations = self.groups.member_relations(query.get("scope", ""), query.get("artist", ""), query.get("key", ""))
        requested_home = query.get("homeKey", "")
        if not requested_home or len(requested_home) > 500:
            raise ValueError("Choose an exact retained performance or recording account")
        items = {}
        for source in sources:
            prefix = "reader:" + str(source["id"]) + ":"
            if requested_home.startswith("reader:") and not requested_home.startswith(prefix):
                continue
            local_home = requested_home[len(prefix):] if requested_home.startswith(prefix) else requested_home
            for file in self._music_pages(source, "/api/music/files", {**payload, "homeKey": local_home}):
                identity = (("native", source["run"], file["nativeFileKey"]) if file["fileKind"] == "native"
                            else ("legacy", file["fileKey"]) if file["fileKey"]
                            else ("legacy", source["run"], file["recordId"]))
                # Later configured captures have the same display precedence as
                # the original catalogue. The selected file's exact home survives.
                interpretations = [relation for key in file["matchingKeys"] for relation in relations.get(key, [])]
                items[identity] = {**file, "source": source["id"], "normalizationRelations": interpretations}
        ordered = sorted(items.values(), key=lambda item: (str(item["source"]), item["recordId"], item["fileOrdinal"] or -1))
        bounds = page(query)
        offset, limit = bounds["offset"], bounds["limit"]
        return {"items": ordered[offset:offset + limit], "total": len(ordered), **bounds}

    def related(self, query):
        source = self._source(query)
        key = self._music_key(query)
        domain = query.get("domain", "file_contents")
        if domain not in RELATION_DOMAINS:
            raise ValueError("Unknown music relationship domain.")
        filters = {"entityKey": key, "domain": domain, **page(query)}
        return self._read(source, "/api/records?" + urlencode(filters))

    def files(self, query):
        source = self._source(query)
        key = self._music_key(query)
        return self._read(source, "/api/files?" + urlencode({"entityKey": key, **page(query)}))

    def file(self, query):
        source = self._source(query)
        parent = int(query.get("parentId", "0"))
        key = query.get("nativeFileKey", "")
        if parent < 1 or not key.startswith("native_file:") or len(key) > 300:
            raise ValueError("Choose an exact native file and its retained parent.")
        return self._read(source, "/api/file?" + urlencode({"parentId": parent, "nativeFileKey": key}))

    @staticmethod
    def _music_key(query):
        key = query.get("key", "")
        if not key.startswith(("repertoire:", "composition:", "show:", "source:", "file:", "native_file:", "setlist:", "setlist_account:")) or len(key) > 300:
            raise ValueError("Choose an exact music entity key.")
        return key

    def record(self, query):
        source = self._source(query)
        identifier = int(query.get("id", "0"))
        if identifier < 1:
            raise ValueError("Choose an indexed record.")
        return self._read(source, "/api/record?" + urlencode({"id": identifier}))

    def _source(self, query):
        # Resolve source IDs from the same published catalogue generation as search.
        # A completed rebuild may atomically add a source while this server stays up.
        metadata = self.stats()
        if query.get("generation") != metadata["generation"]:
            raise CatalogueChanged("The discovery catalogue changed. Reload this page before opening source results.")
        sources = {str(item["id"]): item for item in metadata["sources"]}
        try:
            source = sources[query.get("source", "")]
            return source
        except KeyError:
            raise ValueError("Choose a configured result reader.") from None

    @staticmethod
    def _read(source, path):
        # The browser supplies coordinates, never a destination URL. Playback and
        # original evidence are still authenticated by the existing reader.
        with urlopen(source["reader_url"].rstrip("/") + path, timeout=20) as response:
            body = response.read(MAXIMUM_RESPONSE_BYTES + 1)
        if len(body) > MAXIMUM_RESPONSE_BYTES:
            raise ValueError("The original result exceeds the 8 MiB response limit; open its source reader.")
        return json.loads(body)


def page(query):
    return {"limit": max(1, min(100, int(query.get("limit", "40")))),
            "offset": max(0, int(query.get("offset", "0")))}


def handler_for(discovery):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            route = urlsplit(self.path)
            query = {key: values[-1] for key, values in parse_qs(route.query).items()}
            try:
                if route.path in {"/", "/music-discovery.html"}:
                    return self.send_body(200, Path(__file__).with_name("music-discovery.html").read_bytes(), "text/html; charset=utf-8")
                catalogue = discovery.catalogue
                if route.path in {"/api/composition-works", "/api/composition-work", "/api/song-composition-hypotheses", "/api/composition-evidence", "/api/composition-selection-evidence"}:
                    if not discovery.compositions:
                        raise ValueError("No completed composition suggestions are configured")
                    if route.path != "/api/composition-works" and query.get("generation") != discovery.stats()["generation"]:
                        raise CatalogueChanged("Composition suggestions changed. Reload this page")
                if route.path == "/api/stats":
                    result = discovery.stats()
                elif route.path == "/api/artists":
                    result = catalogue.artists(q=query.get("q", ""), scope=query.get("scope", ""), **page(query))
                elif route.path == "/api/review-source":
                    result = discovery.review_source(query)
                elif route.path == "/api/review-candidates":
                    result = discovery.review_candidates(query)
                elif route.path == "/api/review-observations":
                    result = discovery.review_observations(query)
                elif route.path == "/api/songs":
                    result = catalogue.songs(q=query.get("q", ""), artist=query.get("artist", ""), scope=query.get("scope", ""), **page(query))
                elif route.path == "/api/normalized-songs":
                    if not discovery.groups:
                        raise ValueError("No completed music normalization is configured")
                    result = discovery.groups.songs(q=query.get("q", ""), artist=query.get("artist", ""), scope=query.get("scope", ""), **page(query))
                elif route.path == "/api/song-group":
                    if query.get("generation") and query["generation"] != discovery.stats()["generation"]:
                        raise CatalogueChanged("The music normalization changed. Reload this song page")
                    if not discovery.groups:
                        raise ValueError("No completed music normalization is configured")
                    result = discovery.groups.group(query.get("scope", ""), query.get("artist", ""), query.get("key", ""), **page(query))
                elif route.path == "/api/correspondence":
                    if not discovery.groups:
                        raise ValueError("No completed music normalization is configured")
                    result = discovery.groups.correspondence(query.get("scope", ""), query.get("artist", ""), query.get("key", ""))
                elif route.path == "/api/original-song-groups":
                    if not discovery.groups:
                        raise ValueError("No completed music normalization is configured")
                    result = discovery.groups.original_groups(query.get("scope", ""), query.get("artist", ""), query.get("key", ""))
                elif route.path == "/api/song-performances":
                    result = discovery.performances(query)
                elif route.path == "/api/performance-files":
                    result = discovery.performance_files(query)
                elif route.path == "/api/composition-works":
                    result = discovery.compositions.works(q=query.get("q", ""), scope=query.get("scope", ""), artist=query.get("artist", ""), **page(query))
                elif route.path == "/api/composition-work":
                    result = discovery.compositions.work(query.get("key", ""), scope=query.get("scope", ""), artist=query.get("artist", ""), **page(query))
                    if result is not None and discovery.review_client is not None:
                        result["songs"] = [apply_composition_review(song, discovery.review_client, discovery._review_song_ref(song))
                                           for song in result["songs"]]
                        result.update(discovery.reviewed_work_songs(result["work_key"], query))
                elif route.path == "/api/song-composition-hypotheses":
                    result = discovery.compositions.song(query.get("scope", ""), query.get("artist", ""), query.get("key", ""))
                    if result is not None and discovery.review_client is not None:
                        source = discovery.review_source(dict(key=json.dumps([query.get("scope", ""), query.get("key", "")], separators=(",", ":")),
                                                             artist=query.get("artist", "")))
                        if source["items"]:
                            result = apply_composition_review(result, discovery.review_client, source["items"][0])
                elif route.path == "/api/composition-evidence":
                    result = discovery.compositions.evidence(query.get("kind", ""), query.get("key", ""), query.get("scope", ""), query.get("artist", ""))
                elif route.path == "/api/composition-selection-evidence":
                    if not discovery.compositions.selections:
                        raise ValueError("No completed model selections are configured")
                    result = discovery.compositions.selections.evidence(query.get("scope", ""), query.get("artist", ""), query.get("key", ""))
                elif route.path == "/api/works":
                    result = catalogue.works(q=query.get("q", ""), **page(query))
                elif route.path == "/api/setlists":
                    result = catalogue.setlists(q=query.get("q", ""), artist=query.get("artist", ""), scope=query.get("scope", ""), **page(query))
                elif route.path == "/api/song":
                    result = catalogue.song(query.get("scope", ""), query.get("key", ""), **page(query))
                elif route.path == "/api/work":
                    result = catalogue.work(query.get("key", ""), **page(query))
                elif route.path == "/api/setlist":
                    result = catalogue.setlist(query.get("scope", ""), query.get("key", ""))
                elif route.path == "/api/related":
                    result = discovery.related(query)
                elif route.path == "/api/record":
                    result = discovery.record(query)
                elif route.path == "/api/files":
                    result = discovery.files(query)
                elif route.path == "/api/file":
                    result = discovery.file(query)
                else:
                    return self.send_json(404, {"error": "Unknown music discovery route."})
                if result is None:
                    return self.send_json(404, {"error": "This music identity is not in the current catalogue snapshot."})
                return self.send_json(200, result)
            except (CatalogueChanged, MusicNormalizationChanged) as error:
                return self.send_json(409, {"error": str(error)})
            except MusicQueryUnavailable as error:
                return self.send_json(503, {"error": str(error)})
            except ReviewedKnowledgeUnavailable as error:
                return self.send_json(503, {"error": str(error)})
            except KeyError:
                return self.send_json(404, {"error": "This music identity is not in the catalogue snapshot."})
            except HTTPError as error:
                if route.path == "/api/file" and error.code == 404:
                    return self.send_json(404, {"error": "This exact native child is not in the current reader index."})
                # Do not log or copy original provider prose from an error body.
                return self.send_json(503, {"error": f"The original reader returned HTTP {error.code}; its index may be updating."})
            except (URLError, TimeoutError, OSError):
                return self.send_json(503, {"error": "The original reader is temporarily unavailable. The catalogue remains available."})
            except (ValueError, TypeError) as error:
                return self.send_json(400, {"error": str(error)})
            except sqlite3.Error:
                return self.send_json(503, {"error": "The music catalogue is temporarily unavailable."})

        def send_json(self, status, value):
            return self.send_body(status, json.dumps(value, ensure_ascii=False).encode(), "application/json; charset=utf-8")

        def send_body(self, status, body, content_type):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; media-src https: http:; frame-ancestors 'none'; base-uri 'none'")
            self.end_headers()
            self.wfile.write(body)

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("build", help="Create a bounded discovery snapshot without changing reader indexes")
    build.add_argument("--source", nargs=3, action="append", metavar=("SCOPE", "RUN", "READER"), required=True)
    build.add_argument("--database", required=True)
    build.add_argument("--minimum-free-bytes", type=int, default=int(4.5 * 1024**3))
    build.add_argument("--maximum-bytes", type=int, default=256 * 1024**2)
    export = commands.add_parser("export-normalization", help="Export every literal descriptor and dictionary spelling for C# normalization")
    export.add_argument("--database", required=True)
    export.add_argument("--output", required=True)
    export.add_argument("--minimum-free-bytes", type=int, default=int(4.5 * 1024**3))
    export.add_argument("--maximum-bytes", type=int, default=512 * 1024**2)
    project = commands.add_parser("project-normalization", help="Build a complete normalized music sidecar without copying the browse catalogue")
    project.add_argument("--database", required=True, help="Original completed browse catalogue")
    project.add_argument("--input-manifest", required=True)
    project.add_argument("--output-manifest", required=True)
    project.add_argument("--normalization", required=True)
    project.add_argument("--minimum-free-bytes", type=int, default=int(4.5 * 1024**3))
    project.add_argument("--maximum-bytes", type=int, default=1024**3)
    serve = commands.add_parser("serve", help="Serve the private music browser")
    serve.add_argument("--database", required=True)
    serve.add_argument("--composition-selections", help="Optional completed unconfirmed model selection projection")
    serve.add_argument("--compositions", help="Completed composition suggestion projection for this exact music image")
    serve.add_argument("--people-url", help="Optional taper directory HTTP(S) origin")
    serve.add_argument("--venue-url", help="Optional venue directory HTTP(S) origin")
    serve.add_argument("--review-url", help="Optional authenticated normalization review HTTP(S) origin")
    serve.add_argument("--normalization", help="Completed C# correspondence sidecar for this exact catalogue")
    serve.add_argument("--reader", nargs=3, action="append", metavar=("SCOPE", "RUN", "READER"), default=[], help="Additional relationship-only reader; does not claim title discovery coverage")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=18769)
    args = parser.parse_args()
    if args.command == "build":
        sources = [Source(scope, Path(run), reader) for scope, run, reader in args.source]
        result = MusicCatalogueBuilder(sources, args.database, minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes).build()
        print(json.dumps(result, ensure_ascii=False), flush=True)
    elif args.command == "export-normalization":
        result = MusicConsolidationExporter(args.database, args.output,
            minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes).export()
        print(json.dumps({"manifest": str(Path(args.output).resolve() / "manifest.json"),
            "descriptors": result["descriptors"], "dictionaryNames": result["dictionaryNames"],
            "catalogueSha256": result["catalogue"]["sha256"]}, ensure_ascii=False), flush=True)
    elif args.command == "project-normalization":
        result = MusicConsolidationProjection(args.database, args.input_manifest, args.output_manifest,
            args.normalization, minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes).build()
        print(json.dumps(result, ensure_ascii=False), flush=True)
    else:
        readers = [Source(scope, Path(run), reader) for scope, run, reader in args.reader]
        discovery = MusicDiscovery(MusicCatalogue(args.database), readers, args.normalization, args.compositions, args.people_url, args.venue_url, args.composition_selections, args.review_url)
        server = ThreadingHTTPServer((args.host, args.port), handler_for(discovery))
        print(json.dumps({"url": f"http://{args.host}:{args.port}/", "database": str(Path(args.database).resolve())}), flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
