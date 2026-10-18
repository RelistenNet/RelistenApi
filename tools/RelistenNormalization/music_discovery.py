#!/usr/bin/env python3
"""Browse retained music identities and follow exact keys to existing result readers.

The small catalogue owns discovery, not file membership or accepted identities.
Readers remain responsible for current logical results and immutable body checks.
"""

import argparse
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sqlite3
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import urlopen

from music_catalogue import MusicCatalogue, MusicCatalogueBuilder, Source

MAXIMUM_RESPONSE_BYTES = 8 * 1024 * 1024
RELATION_DOMAINS = {"file_contents", "setlists", "compositions", "repertoire"}


class CatalogueChanged(ValueError):
    """A browser must reload before using ordinal source/record coordinates."""


class MusicDiscovery:
    """Read the compact catalogue and proxy bounded, selective source lookups."""

    def __init__(self, catalogue, readers=()):
        self.catalogue = catalogue
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
        result = self.catalogue.stats()
        sources = result["sources"]
        for reader in self.readers:
            if not any((source["scope"], source["run"]) == (reader["scope"], reader["run"]) for source in sources):
                sources.append(dict(reader))
        # Ordinal catalogue IDs and configured relationship-only IDs are both tied
        # to this image. Open pages must reload after either mapping changes.
        generation = [result["built_at"], [(s["id"], s["scope"], s["run"], s["reader_url"]) for s in sources]]
        result["generation"] = hashlib.sha256(json.dumps(generation, separators=(",", ":")).encode()).hexdigest()[:24]
        return result

    def related(self, query):
        source = self._source(query)
        key = query.get("key", "")
        if not key.startswith(("repertoire:", "composition:", "show:", "source:", "file:", "setlist:", "setlist_account:")) or len(key) > 300:
            raise ValueError("Choose an exact music entity key.")
        domain = query.get("domain", "file_contents")
        if domain not in RELATION_DOMAINS:
            raise ValueError("Unknown music relationship domain.")
        filters = {"entityKey": key, "domain": domain, **page(query)}
        return self._read(source, "/api/records?" + urlencode(filters))

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
                if route.path == "/api/stats":
                    result = discovery.stats()
                elif route.path == "/api/artists":
                    result = catalogue.artists(q=query.get("q", ""), scope=query.get("scope", ""), **page(query))
                elif route.path == "/api/songs":
                    result = catalogue.songs(q=query.get("q", ""), artist=query.get("artist", ""), scope=query.get("scope", ""), **page(query))
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
                else:
                    return self.send_json(404, {"error": "Unknown music discovery route."})
                if result is None:
                    return self.send_json(404, {"error": "This music identity is not in the current catalogue snapshot."})
                return self.send_json(200, result)
            except CatalogueChanged as error:
                return self.send_json(409, {"error": str(error)})
            except KeyError:
                return self.send_json(404, {"error": "This music identity is not in the catalogue snapshot."})
            except HTTPError as error:
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
    serve = commands.add_parser("serve", help="Serve the private music browser")
    serve.add_argument("--database", required=True)
    serve.add_argument("--reader", nargs=3, action="append", metavar=("SCOPE", "RUN", "READER"), default=[], help="Additional relationship-only reader; does not claim title discovery coverage")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=18769)
    args = parser.parse_args()
    if args.command == "build":
        sources = [Source(scope, Path(run), reader) for scope, run, reader in args.source]
        result = MusicCatalogueBuilder(sources, args.database, minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes).build()
        print(json.dumps(result, ensure_ascii=False), flush=True)
    else:
        readers = [Source(scope, Path(run), reader) for scope, run, reader in args.reader]
        discovery = MusicDiscovery(MusicCatalogue(args.database), readers)
        server = ThreadingHTTPServer((args.host, args.port), handler_for(discovery))
        print(json.dumps({"url": f"http://{args.host}:{args.port}/", "database": str(Path(args.database).resolve())}), flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
