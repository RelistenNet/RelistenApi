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

MAXIMUM_RESPONSE_BYTES = 8 * 1024 * 1024
RELATION_DOMAINS = {"file_contents", "setlists", "compositions", "repertoire"}


class CatalogueChanged(ValueError):
    """A browser must reload before using ordinal source/record coordinates."""


class MusicQueryUnavailable(ValueError):
    """A complete source union could not be obtained; no partial total is published."""


class MusicDiscovery:
    """Read the compact catalogue and proxy bounded, selective source lookups."""

    def __init__(self, catalogue, readers=(), normalization=None, compositions=None, people_url=None, venue_url=None):
        self.catalogue = catalogue
        self._catalogue_marker = None
        self._catalogue_stats = None
        self.groups = MusicGroups(catalogue.database, normalization) if normalization else None
        if compositions and not self.groups:
            raise ValueError("Composition suggestions require the matching normalized music image")
        self.compositions = CompositionCatalogue(self.groups, compositions) if compositions else None
        self.navigation = {"songs": "#songs", "compositions": "#compositions"}
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
        if self.groups:
            result["normalization"] = self.groups.stats()
            result["normalized_song_queries"] = True
        if self.compositions:
            result["composition_queries"] = True
            result["compositionSuggestions"] = self.compositions.stats()
        sources = result["sources"]
        for reader in self.readers:
            if not any((source["scope"], source["run"]) == (reader["scope"], reader["run"]) for source in sources):
                sources.append(dict(reader))
        # Ordinal catalogue IDs and configured relationship-only IDs are both tied
        # to this image. Open pages must reload after either mapping changes.
        generation = [result["built_at"], [(s["id"], s["scope"], s["run"], s["reader_url"]) for s in sources],
                      (result.get("normalization") or {}).get("outputManifestSha256"),
                      (result.get("compositionSuggestions") or {}).get("manifestSha256")]
        result["generation"] = hashlib.sha256(json.dumps(generation, separators=(",", ":")).encode()).hexdigest()[:24]
        return result

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
                if route.path in {"/api/composition-works", "/api/composition-work", "/api/song-composition-hypotheses", "/api/composition-evidence"}:
                    if not discovery.compositions:
                        raise ValueError("No completed composition suggestions are configured")
                    if route.path != "/api/composition-works" and query.get("generation") != discovery.stats()["generation"]:
                        raise CatalogueChanged("Composition suggestions changed. Reload this page")
                if route.path == "/api/stats":
                    result = discovery.stats()
                elif route.path == "/api/artists":
                    result = catalogue.artists(q=query.get("q", ""), scope=query.get("scope", ""), **page(query))
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
                elif route.path == "/api/song-composition-hypotheses":
                    result = discovery.compositions.song(query.get("scope", ""), query.get("artist", ""), query.get("key", ""))
                elif route.path == "/api/composition-evidence":
                    result = discovery.compositions.evidence(query.get("kind", ""), query.get("key", ""), query.get("scope", ""), query.get("artist", ""))
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
    serve.add_argument("--compositions", help="Completed composition suggestion projection for this exact music image")
    serve.add_argument("--people-url", help="Optional taper directory HTTP(S) origin")
    serve.add_argument("--venue-url", help="Optional venue directory HTTP(S) origin")
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
        discovery = MusicDiscovery(MusicCatalogue(args.database), readers, args.normalization, args.compositions, args.people_url, args.venue_url)
        server = ThreadingHTTPServer((args.host, args.port), handler_for(discovery))
        print(json.dumps({"url": f"http://{args.host}:{args.port}/", "database": str(Path(args.database).resolve())}), flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
