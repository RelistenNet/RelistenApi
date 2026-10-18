#!/usr/bin/env python3
"""Compare a local catalog API with a retained, read-only SQL snapshot.

Only catalog GETs are issued. Media URLs are compared as data, never fetched.
Raw responses can contain provider prose and belong in ignored local storage.
"""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
from urllib import error, parse, request
import uuid


INPUT_VERSION = "normalization-api-rehearsal-input-v1"
MAX_RESPONSE_BYTES = 8 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_REQUESTS = 10000
MAX_PAGES = 1000
NORMALIZATION_COUNTERS = {
    "people", "venues", "compositions", "repertoire_songs", "recording_credits",
    "imported_venue_assignments", "show_venue_assignments", "track_contents",
    "setlist_editions", "song_occurrences", "setlist_structures", "setlist_sets",
    "music_exclusions", "active_people_policies", "active_composition_policies", "active_venue_policies",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fingerprint(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def read_json(data):
    def invalid_constant(value):
        raise ValueError("Non-finite JSON number")

    return json.loads(data, parse_constant=invalid_constant)


def local_base_url(value):
    url = parse.urlsplit(value)
    require(
        url.scheme == "http"
        and url.hostname in ("localhost", "127.0.0.1")
        and url.username is None
        and url.password is None
        and url.path in ("", "/")
        and not url.query
        and not url.fragment,
        "--base-url must be a loopback HTTP origin without credentials or a path",
    )
    require(url.port is not None, "--base-url needs an explicit local port")
    return value.rstrip("/")


class NoRedirects(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class LocalHttp:
    def __init__(self, base_url):
        self.base_url = local_base_url(base_url)
        # Ignore ambient proxies. A redirect must never turn a catalog check into
        # a request to a provider, authentication service, or media URL.
        self.opener = request.build_opener(request.ProxyHandler({}), NoRedirects())

    def get(self, path):
        req = request.Request(self.base_url + path, headers={"Accept": "application/json"})
        try:
            response = self.opener.open(req, timeout=30)
        except error.HTTPError as failure:
            response = failure
        with response:
            body = response.read(MAX_RESPONSE_BYTES + 1)
            require(len(body) <= MAX_RESPONSE_BYTES, "Response exceeds the 8 MiB limit")
            return response.status, body


class CatalogSnapshot:
    def __init__(self, data):
        try:
            require(data["schema_version"] == INPUT_VERSION, "Unsupported manifest version")
            require(
                data["database"].startswith("relisten_normalization_"),
                "Manifest must describe a dedicated local normalization copy",
            )
            require(data["transaction_read_only"] == "on", "SQL snapshot was not read-only")
            require(
                type(data["migration"]) is int and data["migration"] >= 50,
                "Manifest needs migration50 or later, including all three acceptance-policy counters",
            )
            self.data = data
            self.by_uuid = {}
            self.by_id = {}
            for kind in ("artists", "shows", "sources", "sets", "tracks", "collections"):
                rows = data[kind]
                require(isinstance(rows, list), f"{kind} must be an array")
                for row in rows:
                    require(str(uuid.UUID(row["uuid"])) == row["uuid"], "Invalid catalog UUID")
                self.by_uuid[kind] = {row["uuid"]: row for row in rows}
                require(len(self.by_uuid[kind]) == len(rows), f"Duplicate {kind} UUID")
                if kind != "collections":
                    self.by_id[kind] = {row["id"]: row for row in rows}
                    require(len(self.by_id[kind]) == len(rows), f"Duplicate {kind} ID")
            require(
                NORMALIZATION_COUNTERS <= data["normalization_state"].keys()
                and all(value == 0 for value in data["normalization_state"].values()),
                "Accepted normalization state or active policy is not empty",
            )
        except (KeyError, TypeError, AttributeError) as failure:
            raise ValueError("Malformed SQL manifest") from failure

    def rows(self, kind):
        return self.data[kind]


class ApiVerifier:
    def __init__(self, snapshot, fetch, output):
        self.snapshot = snapshot
        self.fetch = fetch
        self.output = output
        self.errors = []
        self.error_count = 0
        self.responses = []
        self.total_bytes = 0
        self.attempts = 0
        self.counts = {}
        self.checked_sources = set()
        self.checked_tracks = set()

    def problem(self, context, detail):
        self.error_count += 1
        if len(self.errors) < 1000:
            self.errors.append({"context": context, "detail": detail})

    def count(self, key, amount=1):
        self.counts[key] = self.counts.get(key, 0) + amount

    def get(self, path):
        try:
            require(self.attempts < MAX_REQUESTS, "Request limit exceeded")
            self.attempts += 1
            status, body = self.fetch(path)
            require(isinstance(body, bytes), "HTTP body must be bytes")
            require(len(body) <= MAX_RESPONSE_BYTES, "Response exceeds the 8 MiB limit")
            require(self.total_bytes + len(body) <= MAX_TOTAL_BYTES, "Total response limit exceeded")
            self.total_bytes += len(body)
            name = f"responses/{self.attempts:05d}.json"
            with (self.output / name).open("xb") as stream:
                stream.write(body)
            self.responses.append({"path": path, "status": status, "file": name, **fingerprint(body)})
            if status != 200:
                self.problem(path, f"HTTP {status}")
                return None
            return read_json(body)
        except Exception as failure:
            # Bodies and exception messages may contain upstream prose. Keep them
            # out of the compact report; successful bounded reads remain on disk.
            self.problem(path, f"Request failed: {type(failure).__name__}")
            return None

    def properties(self, actual, expected, fields, context):
        if not isinstance(actual, dict):
            self.problem(context, "Expected an object")
            return
        for field in fields:
            if field not in actual:
                self.problem(context, f"Missing property {field}")
                continue
            left, right = actual[field], expected[field]
            equal = left == right
            if (
                field == "duration"
                and isinstance(left, (int, float))
                and isinstance(right, (int, float))
            ):
                equal = math.isclose(left, right, rel_tol=1e-9, abs_tol=1e-6)
            if not equal:
                self.problem(context, f"Property differs: {field}")

    def rows(self, actual, expected, key, fields, context, ordered=False):
        if not isinstance(actual, list) or any(
            not isinstance(row, dict) or not isinstance(row.get(key), str)
            for row in actual
        ):
            self.problem(context, "Expected an array of identified objects")
            return {}
        found = {row[key]: row for row in actual}
        wanted = {row[key]: row for row in expected}
        if len(found) != len(actual):
            self.problem(context, "Duplicate identities")
        if found.keys() != wanted.keys():
            missing = len(wanted.keys() - found.keys())
            extra = len(found.keys() - wanted.keys())
            self.problem(context, f"Identity set differs: missing={missing}, extra={extra}")
        if ordered and [row[key] for row in actual] != [row[key] for row in expected]:
            self.problem(context, "Result order differs")
        for identity in found.keys() & wanted.keys():
            self.properties(found[identity], wanted[identity], fields, f"{context}/{identity}")
        return found

    def page(self, path):
        items, cursor, seen = [], None, set()
        for _ in range(MAX_PAGES):
            query = {"limit": 200}
            if cursor is not None:
                query["cursor"] = cursor
            separator = "&" if "?" in path else "?"
            response = self.get(path + separator + parse.urlencode(query))
            if response is None:
                return items
            if (
                not isinstance(response, dict)
                or not isinstance(response.get("items"), list)
                or "next_cursor" not in response
            ):
                self.problem(path, "Malformed collection page")
                return items
            items.extend(response["items"])
            cursor = response["next_cursor"]
            if cursor is None:
                return items
            if not isinstance(cursor, str) or not cursor or len(cursor) > 4096 or cursor in seen:
                self.problem(path, "Invalid or repeated pagination cursor")
                return items
            seen.add(cursor)
        self.problem(path, "Pagination limit exceeded")
        return items

    def collections(self):
        expected = self.snapshot.rows("collections")
        fields = ("uuid", "slug", "name", "published", "source_count", "show_count", "artist_count")
        self.rows(self.page("/api/v3/collections"), expected, "uuid", fields, "collections", ordered=True)
        for collection in expected:
            path = "/api/v3/collections/" + collection["uuid"]
            response = self.get(path)
            self.properties(response, collection, fields, path)
            self.rows(
                self.page(path + "/artists"), collection["artists"], "uuid",
                ("uuid", "name", "slug", "sort_name", "source_count", "show_count", "includes_all_recordings"),
                path + "/artists", ordered=True,
            )
            recording_fields = (
                "source_uuid", "artist_uuid", "artist_name", "artist_slug", "show_uuid",
                "date", "display_date", "track_count", "duration",
            )
            actual = self.page(path + "/recordings")
            self.rows(
                actual, collection["recordings"], "source_uuid", recording_fields,
                path + "/recordings", ordered=True,
            )
            self.count("collection_recording_memberships_expected", len(collection["recordings"]))
            # Filtering every represented artist checks item-scoped collections
            # without accidentally expanding a performer's other recordings.
            for artist in collection["artists"]:
                filtered = [
                    row for row in collection["recordings"]
                    if row["artist_uuid"] == artist["uuid"]
                ]
                filter_path = path + "/recordings?artist_uuid=" + artist["uuid"]
                self.rows(
                    self.page(filter_path), filtered, "source_uuid", recording_fields,
                    filter_path, ordered=True,
                )

    def artists(self):
        visible = [
            artist for artist in self.snapshot.rows("artists")
            if artist["source_count"] > 0 or artist["show_count"] > 0
        ]
        fields = ("uuid", "name", "slug", "featured", "source_count", "show_count")
        actual = self.get("/api/v3/artists?include_autocreated=true")
        self.rows(actual, visible, "uuid", fields, "artists")
        self.count("artists_expected", len(visible))
        for artist in visible:
            path = "/api/v3/artists/" + artist["uuid"]
            response = self.get(path)
            self.properties(response, artist, fields, path)
            response = self.get(path + "/normalized")
            if not isinstance(response, dict):
                self.problem(path + "/normalized", "Missing full artist object")
                continue
            self.properties(response.get("artist"), artist, fields, path + "/normalized/artist")
            shows = [
                show for show in self.snapshot.rows("shows")
                if show["artist_id"] == artist["id"] and show["browsable"]
            ]
            self.rows(response.get("shows"), shows, "uuid", self.show_fields(), path + "/normalized/shows")

    @staticmethod
    def show_fields():
        return (
            "uuid", "artist_uuid", "display_date", "performance_type",
            "performance_label", "source_count",
        )

    def shows(self):
        shows = [s for s in self.snapshot.rows("shows") if s["browsable"]]
        self.count("shows_expected", len(shows))
        for show in shows:
            response = self.get("/api/v3/shows/" + show["uuid"])
            if not isinstance(response, dict):
                self.problem(show["uuid"], "Missing full show object")
                continue
            self.full_show(response, show)
            self.count("full_show_responses", 1)

    def full_show(self, actual, show):
        context = "show/" + show["uuid"]
        self.properties(actual, show, self.show_fields(), context)
        label = show["performance_label"]
        if not label or not label.strip():
            label = None if show["performance_type"] == "unknown" else show["performance_type"]
        display_label = show["display_date"] + (" · " + label if label else "")
        self.properties(actual, {"display_label": display_label}, ("display_label",), context)

        sources = [
            source for source in self.snapshot.rows("sources")
            if source["show_id"] == show["id"] and source["artist_id"] == show["artist_id"]
        ]
        found = self.rows(
            actual.get("sources"), sources, "uuid",
            ("uuid", "artist_uuid", "show_uuid", "display_date", "upstream_identifier"),
            context + "/sources",
        )
        for source in sources:
            if source["uuid"] in found:
                self.source(found[source["uuid"]], source)

    def source(self, actual, source):
        self.checked_sources.add(source["uuid"])
        context = "source/" + source["uuid"]
        sets = [s for s in self.snapshot.rows("sets") if s["source_id"] == source["id"]]
        tracks_by_set = {
            source_set["id"]: [
                track for track in self.snapshot.rows("tracks")
                if track["source_set_id"] == source_set["id"] and not track["is_orphaned"]
            ]
            for source_set in sets
        }
        sets = [s for s in sets if tracks_by_set[s["id"]]]
        expected_sets = [{**s, "artist_uuid": source["artist_uuid"]} for s in sets]
        found = self.rows(
            actual.get("sets"), expected_sets, "uuid",
            ("uuid", "source_uuid", "artist_uuid", "index", "name", "is_encore"),
            context + "/sets",
        )
        self.playback_order(actual.get("sets"), "index", context + "/sets")
        for source_set in sets:
            if source_set["uuid"] not in found:
                continue
            expected_tracks = [
                {**track, "show_uuid": source["show_uuid"]}
                for track in tracks_by_set[source_set["id"]]
            ]
            actual_tracks = found[source_set["uuid"]].get("tracks")
            actual_by_uuid = self.rows(
                actual_tracks, expected_tracks, "uuid",
                ("uuid", "source_uuid", "source_set_uuid", "artist_uuid", "show_uuid",
                 "track_position", "title", "duration", "mp3_url", "flac_url"),
                context + "/tracks",
            )
            self.playback_order(actual_tracks, "track_position", context + "/tracks")
            self.checked_tracks.update(actual_by_uuid.keys() & {t["uuid"] for t in expected_tracks})

    def playback_order(self, rows, field, context):
        if not isinstance(rows, list):
            return
        positions = [row.get(field) for row in rows if isinstance(row, dict)]
        if len(positions) != len(rows) or any(type(value) is not int for value in positions):
            self.problem(context, "Missing playback position")
        elif positions != sorted(positions):
            # The public query orders positions only. Equal positions have no
            # promised UUID tie-breaker, so either identity order is acceptable.
            self.problem(context, "Playback positions are out of order")

    def performance_cohorts(self):
        groups = {}
        for show in self.snapshot.rows("shows"):
            if show["browsable"]:
                groups.setdefault((show["artist_uuid"], show["display_date"]), []).append(show)
        partial, multiple = 0, 0
        for (artist, date), shows in groups.items():
            is_partial = re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", date) is None
            if len(shows) == 1 and not is_partial:
                continue
            partial += int(is_partial)
            multiple += int(len(shows) > 1)
            date_path = artist + "/shows/" + parse.quote(date, safe="")
            path = "/api/v3/artists/" + date_path + "/performances"
            response = self.get(path)
            self.rows(response, shows, "uuid", self.show_fields(), path)
            expected_survivor = min(
                shows, key=lambda show: (not show["is_legacy_survivor"], show["id"])
            )
            legacy_path = "/api/v2/artists/" + date_path
            legacy = self.get(legacy_path)
            self.properties(legacy, expected_survivor, ("uuid", "display_date"), legacy_path)
        self.count("partial_date_cohorts", partial)
        self.count("multiple_performance_date_cohorts", multiple)

    def inventory(self):
        for key in (
            "orphan_tracks_excluded_from_playback", "tracks_with_inconsistent_context",
            "non_orphan_tracks_without_media", "sources_with_missing_or_inconsistent_context",
            "sources_outside_browsable_show_view",
        ):
            self.count(key, 0)
        for kind in ("artists", "shows", "sources", "sets", "tracks", "collections"):
            self.count("catalog_" + kind, len(self.snapshot.rows(kind)))
        mp3_sources, flac_only_sources = set(), set()
        for track in self.snapshot.rows("tracks"):
            if track["is_orphaned"]:
                self.count("orphan_tracks_excluded_from_playback")
                continue
            source = self.snapshot.by_id["sources"].get(track["source_id"])
            source_set = self.snapshot.by_id["sets"].get(track["source_set_id"])
            if (
                source is None or source_set is None
                or source_set["source_id"] != track["source_id"]
                or source["artist_id"] != track["artist_id"]
            ):
                self.count("tracks_with_inconsistent_context")
                self.problem("track/" + track["uuid"], "Stored source, set or artist ownership is inconsistent")
            if track["mp3_url"] and track["mp3_url"].strip():
                mp3_sources.add(track["source_uuid"])
            elif track["flac_url"] and track["flac_url"].strip():
                flac_only_sources.add(track["source_uuid"])
            else:
                self.count("non_orphan_tracks_without_media")
        self.count("mp3_bearing_recordings", len(mp3_sources))
        self.count("flac_only_recordings", len(flac_only_sources - mp3_sources))
        for source in self.snapshot.rows("sources"):
            show = self.snapshot.by_id["shows"].get(source["show_id"])
            if (
                show is None or source["artist_uuid"] is None
                or show["artist_id"] != source["artist_id"]
            ):
                self.count("sources_with_missing_or_inconsistent_context")
                self.problem("source/" + source["uuid"], "Stored artist/show ownership is inconsistent or missing")
            elif not show["browsable"]:
                self.count("sources_outside_browsable_show_view")

    def verify(self):
        self.inventory()
        self.collections()
        self.artists()
        self.shows()
        self.performance_cohorts()
        self.count("distinct_sources_checked", len(self.checked_sources))
        self.count("distinct_tracks_checked", len(self.checked_tracks))
        expected_sources = {
            source["uuid"] for source in self.snapshot.rows("sources")
            if source["show_id"] in self.snapshot.by_id["shows"]
            and self.snapshot.by_id["shows"][source["show_id"]]["browsable"]
        }
        self.count("browsable_sources_not_reached", len(expected_sources - self.checked_sources))


def run(base_url, manifest_path, output, fetch=None):
    base_url = local_base_url(base_url)
    manifest_path, output = Path(manifest_path), Path(output)
    original = manifest_path.read_bytes()
    snapshot = CatalogSnapshot(read_json(original))
    # Atomic directory ownership precedes all output and HTTP activity.
    output.mkdir(parents=True, exist_ok=False)
    (output / "responses").mkdir()
    with (output / "input.json").open("xb") as stream:
        stream.write(original)
    verifier = ApiVerifier(snapshot, fetch or LocalHttp(base_url).get, output)
    interrupted = None
    try:
        verifier.verify()
        if manifest_path.read_bytes() != original:
            verifier.problem("manifest", "Input changed during verification")
    except BaseException as failure:
        verifier.problem("run", f"Interrupted: {type(failure).__name__}")
        interrupted = failure
    receipt = {
        "schema_version": "normalization-api-rehearsal-result-v1",
        "state": "completed" if verifier.error_count == 0 else "failed",
        "base_url": base_url,
        "database": snapshot.data["database"],
        "migration": snapshot.data["migration"],
        "input": {"file": "input.json", **fingerprint(original)},
        "counts": verifier.counts,
        "request_attempts": verifier.attempts,
        "error_count": verifier.error_count,
        "errors": verifier.errors,
        "errors_omitted": verifier.error_count - len(verifier.errors),
        "responses": verifier.responses,
        "limits": [
            "Catalog responses and stored media links checked; no audio or provider requests.",
            "SQL input is a saved snapshot, not an independently queried live database.",
            "Catalog stability and empty accepted normalization state require a separate post-run SQL snapshot.",
            "No partial-date or multiple-performance claim when its cohort count is zero.",
        ],
    }
    pending = output / "result.pending.json"
    with pending.open("x", encoding="utf-8") as stream:
        json.dump(receipt, stream, indent=2, sort_keys=True)
        stream.write("\n")
    os.link(pending, output / "result.json")
    if interrupted is not None:
        raise interrupted
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        receipt = run(args.base_url, args.manifest, args.output)
    except (ValueError, OSError) as failure:
        print(f"Rehearsal failed: {type(failure).__name__}: {failure}", file=sys.stderr)
        return 2
    print(json.dumps({"state": receipt["state"], "counts": receipt["counts"], "error_count": receipt["error_count"]}, sort_keys=True))
    return 0 if receipt["state"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
