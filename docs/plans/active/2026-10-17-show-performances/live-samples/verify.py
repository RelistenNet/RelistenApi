#!/usr/bin/env python3
"""Verify actual listener HTTP responses and retain complete playback identities.

All requests are GETs. Identity manifests and response digests belong outside Git;
the summary omits media URLs and provider descriptions. Repeat comparison ignores
timestamps and ratings by recording only the stable listener contract.
"""

import argparse
import collections
import datetime
import hashlib
import json
import math
import pathlib
import urllib.parse
import urllib.request
import uuid


PERFORMANCE_TYPES = {"unknown", "afternoon", "early", "late", "other"}
PERFORMANCE_ORDER = {name: index for index, name in enumerate(("early", "afternoon", "late", "other", "unknown"))}
INTERNAL_FIELDS = {"performance_key", "is_legacy_survivor", "assignment_kind", "assignment_evidence", "basis_display_date"}


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def save(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def identifier(value):
    parsed = uuid.UUID(value)
    assert parsed.int != 0, "An empty UUID is not a published identity."
    return str(parsed)


def no_internal_fields(value):
    if isinstance(value, dict):
        assert not (INTERNAL_FIELDS & value.keys()), "Internal performance fields leaked into an HTTP response."
        for entry in value.values():
            no_internal_fields(entry)
    elif isinstance(value, list):
        for entry in value:
            no_internal_fields(entry)


class HttpApi:
    def __init__(self, base):
        self.base = base.rstrip("/")
        self.requests = []

    def get(self, path):
        request = urllib.request.Request(self.base + path, headers={"User-Agent": "Relisten/1.0"})
        receipt = {"path": path, "status": None}
        self.requests.append(receipt)
        with urllib.request.urlopen(request, timeout=90) as response:
            raw = response.read()
            receipt.update({"status": response.status, "sha256": hashlib.sha256(raw).hexdigest()})
            assert response.status == 200, f"Unexpected status for {path}."
        value = json.loads(raw)
        no_internal_fields(value)
        return value

    def pages(self, path, identity):
        rows, seen_cursors, cursor = [], set(), None
        while True:
            parameters = {"limit": 33}
            if cursor:
                parameters["cursor"] = cursor
            page = self.get(path + ("&" if "?" in path else "?") + urllib.parse.urlencode(parameters))
            assert isinstance(page, dict) and isinstance(page["items"], list), "Expected a collection page object."
            rows.extend(page["items"])
            cursor = page["next_cursor"]
            if cursor is None:
                break
            assert cursor not in seen_cursors, "Pagination repeated a cursor."
            seen_cursors.add(cursor)
        assert len(rows) == len({row[identity] for row in rows}), "Pagination returned a duplicate identity."
        return rows


class CatalogVerifier:
    def __init__(self, api):
        self.api = api
        default = api.get("/api/v3/artists")
        assert isinstance(default, list), "The default artist list must be a JSON array."
        self.default_artists = {artist["uuid"] for artist in default}
        self.manifest = {"format": 1, "artists": {}, "shows": {}, "date_destinations": {}}

    @staticmethod
    def show_identity(show):
        show_uuid, artist_uuid = identifier(show["uuid"]), identifier(show["artist_uuid"])
        kind, label, date = show["performance_type"], show["performance_label"], show["display_date"]
        assert kind in PERFORMANCE_TYPES, "Unknown public performance type."
        assert label is None or isinstance(label, str), "The performance label must be a string or null."
        expected_label = date
        if label and label.strip():
            expected_label += " · " + label
        elif kind != "unknown":
            expected_label += " · " + kind
        assert show["display_label"] == expected_label, "The display label disagrees with its structured fields."
        sources = [CatalogVerifier.source_identity(source, show_uuid, artist_uuid) for source in show["sources"]]
        assert len(sources) == show["source_count"] > 0, "Published show source count disagrees with its contents."
        assert len(sources) == len({source["uuid"] for source in sources}), "A show repeats a source."
        return {"uuid": show_uuid, "artist_uuid": artist_uuid, "display_date": date,
                "date": show["date"], "performance_type": kind, "performance_label": label,
                "display_label": show["display_label"], "venue_uuid": show.get("venue_uuid"),
                "sources": sorted(sources, key=lambda source: source["uuid"])}

    @staticmethod
    def source_identity(source, show_uuid, artist_uuid):
        source_uuid = identifier(source["uuid"])
        assert source["artist_uuid"] == artist_uuid and source["show_uuid"] == show_uuid, "A source points to another artist or show."
        sets = source["sets"]
        assert [entry["index"] for entry in sets] == sorted(entry["index"] for entry in sets), "Source sets are out of order."
        normalized_sets, track_uuids = [], set()
        for entry in sets:
            set_uuid = identifier(entry["uuid"])
            assert entry["source_uuid"] == source_uuid and entry["artist_uuid"] == artist_uuid, "A set belongs to another recording."
            tracks = entry["tracks"]
            assert [track["track_position"] for track in tracks] == sorted(track["track_position"] for track in tracks), "Playback tracks are out of order."
            normalized_tracks = []
            for track in tracks:
                track_uuid = identifier(track["uuid"])
                assert track_uuid not in track_uuids, "A source repeats a playback track."
                track_uuids.add(track_uuid)
                assert track["source_uuid"] == source_uuid and track["source_set_uuid"] == set_uuid, "A track points to another source or set."
                assert track["artist_uuid"] == artist_uuid and track["show_uuid"] == show_uuid, "A track retained an incorrect artist/show association."
                assert track.get("mp3_url") or track.get("flac_url"), "A published track has no playback URL."
                if track.get("duration") is not None:
                    assert math.isfinite(track["duration"]) and track["duration"] >= 0, "Invalid playback duration."
                normalized_tracks.append({key: track.get(key) for key in (
                    "uuid", "source_uuid", "source_set_uuid", "artist_uuid", "show_uuid",
                    "track_position", "duration", "title", "slug", "mp3_url", "flac_url", "mp3_md5", "flac_md5")})
            normalized_sets.append({"uuid": set_uuid, "source_uuid": source_uuid, "artist_uuid": artist_uuid,
                                    "index": entry["index"], "name": entry["name"], "is_encore": entry["is_encore"],
                                    "tracks": normalized_tracks})
        assert track_uuids, "A published recording has no playback tracks."
        assert len(normalized_sets) == len({entry["uuid"] for entry in normalized_sets}), "A source repeats a set."
        return {"uuid": source_uuid, "artist_uuid": artist_uuid, "show_uuid": show_uuid,
                "upstream_identifier": source["upstream_identifier"], "display_date": source["display_date"],
                "venue_uuid": source.get("venue_uuid"), "sets": normalized_sets}

    def artist(self, artist_uuid, selected_shows=None):
        artist_uuid = identifier(artist_uuid)
        assert artist_uuid in self.default_artists, "A playable artist is absent from the default artist list."
        artist = self.api.get("/api/v3/artists/" + artist_uuid)
        normalized = self.api.get("/api/v3/artists/" + artist_uuid + "/normalized")
        assert artist["uuid"] == normalized["artist"]["uuid"] == artist_uuid
        shows = normalized["shows"]
        assert len(shows) == len({show["uuid"] for show in shows}) == artist["show_count"], "Normalized show counts or identities disagree."
        assert sum(show["source_count"] for show in shows) == artist["source_count"], "Artist recording count disagrees with its shows."
        self.manifest["artists"][artist_uuid] = {"uuid": artist_uuid, "name": artist["name"], "slug": artist["slug"],
                                               "show_uuids": sorted(show["uuid"] for show in shows)}
        chosen = shows if selected_shows is None else [show for show in shows if show["uuid"] in selected_shows]
        if selected_shows is not None:
            assert {show["uuid"] for show in chosen} == set(selected_shows), "Collection refers to a show absent from normalized artist data."
        dates = collections.defaultdict(list)
        for slim in chosen:
            show = self.show_identity(self.api.get("/api/v3/shows/" + slim["uuid"]))
            assert show["artist_uuid"] == artist_uuid
            assert all(slim[key] == show[key] for key in ("uuid", "display_date", "performance_type", "performance_label", "display_label")), "Normalized and full show fields disagree."
            self.manifest["shows"][show["uuid"]] = show
            dates[show["display_date"]].append(show["uuid"])
        for date in dates:
            self.date(artist_uuid, date, shows)

    def date(self, artist_uuid, date, artist_shows):
        path = "/api/v3/artists/" + artist_uuid + "/shows/" + urllib.parse.quote(date, safe="") + "/performances"
        group = self.api.get(path)
        assert isinstance(group, list), "The date-group endpoint must return a direct JSON array, not a data envelope."
        identities = [self.show_identity(show) for show in group]
        expected = {show["uuid"] for show in artist_shows if show["display_date"] == date}
        assert {show["uuid"] for show in identities} == expected and len(identities) == len(expected), "Date browsing omitted or repeated a performance."
        order = [(PERFORMANCE_ORDER[show["performance_type"]], show["uuid"]) for show in identities]
        assert order == sorted(order), "Same-date performances are out of presentation order."
        sources = [source["uuid"] for show in identities for source in show["sources"]]
        assert len(sources) == len(set(sources)), "Date browsing repeats a canonical recording."
        for show in identities:
            if show["uuid"] in self.manifest["shows"]:
                assert self.manifest["shows"][show["uuid"]] == show, "Date browsing and UUID reads disagree."
            else:
                direct = self.show_identity(self.api.get("/api/v3/shows/" + show["uuid"]))
                assert direct == show, "An additional date performance disagrees with its UUID response."
                self.manifest["shows"][show["uuid"]] = direct
        legacy = self.api.get("/api/v2/artists/" + artist_uuid + "/shows/" + urllib.parse.quote(date, safe=""))
        assert isinstance(legacy, dict) and legacy["uuid"] in expected, "The v2 date destination stopped resolving to a current performance."
        assert self.show_identity(legacy) == next(show for show in identities if show["uuid"] == legacy["uuid"]), "The v2 date response disagrees with its UUID destination."
        self.manifest["date_destinations"][artist_uuid + "/" + date] = legacy["uuid"]

    def collection(self, slug):
        path = "/api/v3/collections/" + urllib.parse.quote(slug, safe="")
        collection = self.api.get(path)
        assert self.api.get("/api/v3/collections/" + collection["uuid"]) == collection, "Collection slug/UUID lookups disagree."
        listed = self.api.pages("/api/v3/collections", "uuid")
        assert collection["uuid"] in {row["uuid"] for row in listed}, "A published collection is absent from collection listings."
        facets = self.api.pages(path + "/artists", "uuid")
        recordings = self.api.pages(path + "/recordings", "source_uuid")
        assert len(recordings) == collection["source_count"]
        assert len(facets) == collection["artist_count"] == len({row["artist_uuid"] for row in recordings})
        assert collection["show_count"] == len({row["show_uuid"] for row in recordings})
        for facet in facets:
            artist_uuid = facet["uuid"]
            expected = [row for row in recordings if row["artist_uuid"] == artist_uuid]
            filtered = self.api.pages(path + "/recordings?artist_uuid=" + artist_uuid, "source_uuid")
            assert filtered == expected, "An artist filter expanded, omitted or reordered collection membership."
            assert facet["source_count"] == len(expected) and facet["show_count"] == len({row["show_uuid"] for row in expected})
            assert isinstance(facet["includes_all_recordings"], bool)
            self.artist(artist_uuid, {row["show_uuid"] for row in expected})
        sources = {source["uuid"]: source for show in self.manifest["shows"].values() for source in show["sources"]}
        for recording in recordings:
            source = sources[recording["source_uuid"]]
            assert source["artist_uuid"] == recording["artist_uuid"] and source["show_uuid"] == recording["show_uuid"]
            assert sum(len(entry["tracks"]) for entry in source["sets"]) == recording["track_count"], "Collection playback counts disagree with the recording."
        self.manifest["scope"] = {"collection_uuid": collection["uuid"], "collection_slug": collection["slug"],
                                  "source_uuids": sorted(row["source_uuid"] for row in recordings)}
        return len(recordings)


def compare(before, after, path="", differences=None):
    differences = [] if differences is None else differences
    if before == after or len(differences) >= 20:
        return differences
    if isinstance(before, dict) and isinstance(after, dict):
        for key in sorted(before.keys() | after.keys()):
            if key not in before or key not in after:
                differences.append(path + "/" + key + " added or missing")
            else:
                compare(before[key], after[key], path + "/" + key, differences)
    elif isinstance(before, list) and isinstance(after, list) and len(before) == len(after):
        for index, (left, right) in enumerate(zip(before, after)):
            compare(left, right, path + "/" + str(index), differences)
    else:
        differences.append(path + " changed")
    return differences


def media_range(manifest):
    tracks = (track for show in manifest["shows"].values() for source in show["sources"] for entry in source["sets"] for track in entry["tracks"])
    track = next(tracks)
    url = track["mp3_url"] or track["flac_url"]
    request = urllib.request.Request(url, headers={"User-Agent": "Relisten/1.0", "Range": "bytes=0-4095"})
    with urllib.request.urlopen(request, timeout=90) as response:
        assert response.status == 206, "Sampled media did not honor the bounded range request."
        assert response.headers.get("Content-Range", "").startswith("bytes 0-"), "Missing sampled media content range."
        body = response.read(4096)
        assert body, "Sampled media range was empty."
        return {"track_uuid": track["uuid"], "status": 206, "bytes_read": len(body),
                "content_type": response.headers.get("Content-Type"), "url_sha256": hashlib.sha256(url.encode()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--artist-uuid", "--artistUUID", dest="artist_uuid")
    target.add_argument("--collection-slug", "--collectionslug", dest="collection_slug")
    parser.add_argument("--minimum-recordings", "--minimumrecordings", dest="minimum", type=int, default=1)
    parser.add_argument("--output-directory", "--outputdirectory", dest="output", type=pathlib.Path, required=True)
    parser.add_argument("--compare", type=pathlib.Path)
    parser.add_argument("--media-range", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    api = HttpApi(args.base)
    summary = {"checked_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "passed": False}
    try:
        verifier = CatalogVerifier(api)
        if args.artist_uuid:
            verifier.artist(args.artist_uuid)
            source_uuids = {source["uuid"] for show in verifier.manifest["shows"].values() for source in show["sources"]}
            verifier.manifest["scope"] = {"artist_uuid": args.artist_uuid, "source_uuids": sorted(source_uuids)}
            count = len(source_uuids)
        else:
            count = verifier.collection(args.collection_slug)
        assert count >= args.minimum, f"Expected at least {args.minimum} playable recordings; observed {count}."
        manifest = verifier.manifest
        save(args.output / "identity-manifest.json", manifest)
        if args.compare:
            baseline = args.compare / "identity-manifest.json" if args.compare.is_dir() else args.compare
            differences = compare(json.loads(baseline.read_text()), manifest)
            save(args.output / "comparison.json", {"equal": not differences, "differences": differences})
            assert not differences, "Repeat import changed playback identities, associations, dates or URLs; inspect comparison.json."
        if args.media_range:
            summary["media_range"] = media_range(manifest)
        sources = [source for show in manifest["shows"].values() for source in show["sources"]]
        sets = [entry for source in sources for entry in source["sets"]]
        tracks = [track for entry in sets for track in entry["tracks"]]
        assert len(sources) == len({source["uuid"] for source in sources}), "A canonical source appears under several shows."
        assert len(sets) == len({entry["uuid"] for entry in sets}), "A set appears under several recordings."
        assert len(tracks) == len({track["uuid"] for track in tracks}), "A track appears under several recordings."
        date_counts = collections.Counter((show["artist_uuid"], show["display_date"]) for show in manifest["shows"].values())
        summary.update({"passed": True, "playable_recordings_in_scope": count, "artists": len(manifest["artists"]),
                        "shows_read": len(manifest["shows"]), "sources_read": len(sources),
                        "sets_read": len(sets), "tracks_read": len(tracks),
                        "dates_read": len(manifest["date_destinations"]),
                        "dates_with_multiple_performances": sum(count > 1 for count in date_counts.values()),
                        "partial_date_shows": sum("XX" in show["display_date"].upper() for show in manifest["shows"].values()),
                        "performance_types": dict(collections.Counter(show["performance_type"] for show in manifest["shows"].values())),
                        "identity_sha256": hashlib.sha256(canonical(manifest)).hexdigest(), "repeat_equal": bool(args.compare)})
    except Exception as error:
        summary["failure"] = type(error).__name__ + ": " + str(error)
        raise
    finally:
        save(args.output / "requests.json", api.requests)
        summary["http_requests"] = len(api.requests)
        save(args.output / "summary.json", summary)
        print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
