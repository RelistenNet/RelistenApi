"""Behavior checks use captured-response fakes; no listening server is required."""

import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib import parse
import uuid

import verify_api


def identity(number):
    return str(uuid.UUID(int=number))


def catalog():
    artists = [
        {
            "id": 1, "uuid": identity(1), "name": "First artist", "slug": "first",
            "featured": 0, "show_count": 2, "source_count": 2,
        },
        {
            "id": 2, "uuid": identity(2), "name": "Imported artist", "slug": "imported",
            "featured": 2, "show_count": 1, "source_count": 1,
        },
    ]
    shows, sources, sets, tracks = [], [], [], []
    for index in range(3):
        artist = artists[0 if index < 2 else 1]
        show = {
            "id": 11 + index, "uuid": identity(11 + index),
            "artist_id": artist["id"], "artist_uuid": artist["uuid"],
            "display_date": "1975-01-01" if index < 2 else "1976-XX-XX",
            "date": "1975-01-01" if index < 2 else "1976-01-01",
            "performance_type": ["early", "late", "unknown"][index],
            "performance_label": None, "is_legacy_survivor": index != 1,
            "browsable": True, "source_count": 1,
        }
        source = {
            "id": 21 + index, "uuid": identity(21 + index),
            "artist_id": artist["id"], "artist_uuid": artist["uuid"],
            "show_id": show["id"], "show_uuid": show["uuid"],
            "display_date": show["display_date"], "upstream_identifier": "retained-" + str(index),
        }
        source_set = {
            "id": 31 + index, "uuid": identity(31 + index),
            "source_id": source["id"], "source_uuid": source["uuid"],
            "index": 0, "name": "Set", "is_encore": False,
        }
        track = {
            "id": 41 + index, "uuid": identity(41 + index),
            "source_id": source["id"], "source_uuid": source["uuid"],
            "source_set_id": source_set["id"], "source_set_uuid": source_set["uuid"],
            "artist_id": artist["id"], "artist_uuid": artist["uuid"],
            "set_source_id": source["id"], "is_orphaned": False,
            "track_position": 1, "title": "Retained title", "duration": 120,
            "mp3_url": f"https://media.example/{index}.mp3", "flac_url": None,
        }
        shows.append(show)
        sources.append(source)
        sets.append(source_set)
        tracks.append(track)
    orphan = {**tracks[0], "id": 50, "uuid": identity(50), "is_orphaned": True}
    no_media = {**tracks[2], "id": 51, "uuid": identity(51), "track_position": 2, "mp3_url": "", "flac_url": ""}
    tracks.extend([orphan, no_media])
    recordings = []
    facets = []
    # The collection selects one recording from each artist; the first artist's
    # late source belongs to its catalog but does not join the collection.
    for index in (2, 0):
        artist = artists[0 if index == 0 else 1]
        source, show = sources[index], shows[index]
        recordings.append({
            "source_uuid": source["uuid"], "artist_uuid": artist["uuid"],
            "artist_name": artist["name"], "artist_slug": artist["slug"],
            "show_uuid": show["uuid"], "date": show["date"],
            "display_date": source["display_date"], "track_count": 1, "duration": 120.0,
        })
        facets.append({
            "uuid": artist["uuid"], "name": artist["name"], "slug": artist["slug"],
            "sort_name": artist["name"], "includes_all_recordings": False,
            "source_count": 1, "show_count": 1,
        })
    facets.sort(key=lambda item: item["sort_name"])
    collection = {
        "uuid": identity(61), "slug": "selected", "name": "Selected recordings",
        "published": True, "source_count": 2, "show_count": 2, "artist_count": 2,
        "recordings": recordings, "artists": facets,
    }
    return {
        "schema_version": verify_api.INPUT_VERSION,
        "database": "relisten_normalization_fixture", "transaction_read_only": "on",
        "migration": 49,
        "normalization_state": dict.fromkeys(verify_api.NORMALIZATION_COUNTERS, 0),
        "artists": artists, "shows": shows, "sources": sources,
        "sets": sets, "tracks": tracks, "collections": [collection],
    }



class CapturedApi:
    def __init__(self, manifest):
        self.manifest = manifest
        self.calls = []
        self.transform = lambda path, value: value
        self.failure_path = None
        self.repeated_cursor = False

    def show(self, show):
        value = copy.deepcopy(show)
        label = show["performance_label"] or (None if show["performance_type"] == "unknown" else show["performance_type"])
        value["display_label"] = show["display_date"] + (" · " + label if label else "")
        value["sources"] = []
        for source in self.manifest["sources"]:
            if source["show_id"] != show["id"]:
                continue
            result = copy.deepcopy(source)
            result["sets"] = []
            for source_set in self.manifest["sets"]:
                if source_set["source_id"] != source["id"]:
                    continue
                nested = copy.deepcopy(source_set)
                nested["artist_uuid"] = source["artist_uuid"]
                nested["tracks"] = [{**track, "show_uuid": show["uuid"]} for track in self.manifest["tracks"] if track["source_set_id"] == source_set["id"] and not track["is_orphaned"]]
                if nested["tracks"]:
                    result["sets"].append(nested)
            value["sources"].append(result)
        return value

    def get(self, path):
        self.calls.append(path)
        if path == self.failure_path:
            return 503, b'{"error":"temporarily unavailable"}'
        parsed = parse.urlsplit(path)
        parts = parsed.path.strip("/").split("/")
        query = parse.parse_qs(parsed.query)
        m = self.manifest
        if parts == ["api", "v3", "collections"]:
            value = {"items": m["collections"], "next_cursor": None}
        elif parts[:3] == ["api", "v3", "collections"]:
            collection = next(item for item in m["collections"] if item["uuid"] == parts[3])
            if len(parts) == 4:
                value = collection
            elif parts[4] == "artists":
                value = {"items": collection["artists"], "next_cursor": None}
            else:
                rows = collection["recordings"]
                if "artist_uuid" in query:
                    rows = [row for row in rows if row["artist_uuid"] == query["artist_uuid"][0]]
                    value = {"items": rows, "next_cursor": None}
                elif "cursor" not in query:
                    value = {"items": rows[:1], "next_cursor": "next page & token"}
                else:
                    value = {"items": rows[1:], "next_cursor": "next page & token" if self.repeated_cursor else None}
        elif parts == ["api", "v3", "artists"]:
            if query.get("include_autocreated") != ["true"]:
                raise AssertionError("Auto-created artists must be included")
            value = m["artists"]
        elif parts[:3] in (["api", "v3", "artists"], ["api", "v2", "artists"]):
            artist = next(a for a in m["artists"] if a["uuid"] == parts[3])
            if len(parts) == 4:
                value = artist
            elif parts[4] == "normalized":
                value = {"artist": artist, "shows": [show for show in m["shows"] if show["artist_uuid"] == artist["uuid"]]}
            else:
                shows = [show for show in m["shows"] if show["artist_uuid"] == artist["uuid"] and show["display_date"] == parse.unquote(parts[5])]
                value = shows if parts[1] == "v3" else min(shows, key=lambda s: (not s["is_legacy_survivor"], s["id"]))
        elif parts[:3] == ["api", "v3", "shows"]:
            value = self.show(next(show for show in m["shows"] if show["uuid"] == parts[3]))
        else:
            raise AssertionError("Unexpected route")
        return 200, json.dumps(self.transform(path, copy.deepcopy(value))).encode()


class TestRehearsal(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.manifest = catalog()
        self.api = CapturedApi(self.manifest)
        self.input = self.root / "input.json"

    def run_check(self):
        self.input.write_text(json.dumps(self.manifest))
        return verify_api.run("http://127.0.0.1:18290", self.input, self.root / "run", self.api.get)

    def test_complete_walk_checks_selected_collection_and_all_recordings(self):
        result = self.run_check()
        self.assertEqual("completed", result["state"], result["errors"])
        self.assertEqual(3, result["counts"]["distinct_sources_checked"])
        self.assertEqual(4, result["counts"]["distinct_tracks_checked"])
        self.assertEqual(1, result["counts"]["orphan_tracks_excluded_from_playback"])
        self.assertEqual(1, result["counts"]["non_orphan_tracks_without_media"])
        self.assertEqual(1, result["counts"]["multiple_performance_date_cohorts"])
        self.assertEqual(1, result["counts"]["partial_date_cohorts"])
        self.assertTrue(any("cursor=next+page+%26+token" in path for path in self.api.calls))
        for response in result["responses"]:
            self.assertEqual(response["sha256"], verify_api.fingerprint((self.root / "run" / response["file"]).read_bytes())["sha256"])

    def test_changed_playback_url_fails_exact_contract(self):
        def change(path, value):
            if path.startswith("/api/v3/shows/"):
                value["sources"][0]["sets"][0]["tracks"][0]["mp3_url"] = "https://different.example/audio.mp3"
            return value
        self.api.transform = change
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertTrue(any(error["detail"] == "Property differs: mp3_url" for error in result["errors"]))

    def test_wrong_track_ownership_and_orphan_reappearance_fail(self):
        def change(path, value):
            if path.endswith(identity(11)):
                track = value["sources"][0]["sets"][0]["tracks"][0]
                track["source_uuid"] = identity(23)
                value["sources"][0]["sets"][0]["tracks"].append({**track, "uuid": identity(50)})
            return value
        self.api.transform = change
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertTrue(any(error["detail"] == "Property differs: source_uuid" for error in result["errors"]))
        self.assertTrue(any("Identity set differs" in error["detail"] for error in result["errors"]))

    def test_partial_http_failure_keeps_evidence_and_checks_other_shows(self):
        self.api.failure_path = "/api/v3/shows/" + identity(11)
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertEqual(2, result["counts"]["distinct_sources_checked"])
        self.assertEqual(1, result["counts"]["browsable_sources_not_reached"])
        self.assertTrue(any(row["status"] == 503 for row in result["responses"]))
        self.assertEqual("failed", json.loads((self.root / "run/result.json").read_text())["state"])

    def test_repeated_cursor_cannot_produce_success(self):
        self.api.repeated_cursor = True
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertTrue(any("pagination cursor" in item["detail"] for item in result["errors"]))
        self.assertLess(len(self.api.calls), 40)

    def test_collection_filter_cannot_expand_to_artist_catalog(self):
        def change(path, value):
            if "artist_uuid=" + identity(1) in path:
                extra = {**value["items"][0], "source_uuid": identity(22), "show_uuid": identity(12)}
                value["items"].append(extra)
            return value
        self.api.transform = change
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertTrue(any("artist_uuid=" in item["context"] for item in result["errors"]))

    def test_legacy_date_route_must_keep_original_uuid(self):
        def change(path, value):
            if path.startswith("/api/v2/") and path.endswith("1975-01-01"):
                value["uuid"] = identity(12)
            return value
        self.api.transform = change
        result = self.run_check()
        self.assertTrue(any(item["context"].startswith("/api/v2/") and item["detail"] == "Property differs: uuid" for item in result["errors"]))

    def test_existing_output_and_nonlocal_origin_fail_before_requests(self):
        self.input.write_text(json.dumps(self.manifest))
        output = self.root / "existing"
        output.mkdir()
        marker = output / "original"
        marker.write_text("preserve")
        with self.assertRaises(FileExistsError):
            verify_api.run("http://localhost:18290", self.input, output, self.api.get)
        with self.assertRaises(ValueError):
            verify_api.run("http://example.com:18290", self.input, self.root / "new", self.api.get)
        self.assertEqual([], self.api.calls)
        self.assertEqual("preserve", marker.read_text())

    def test_cancellation_retains_failed_receipt_without_completed_claim(self):
        self.input.write_text(json.dumps(self.manifest))
        def interrupted(path):
            raise KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt):
            verify_api.run("http://localhost:18290", self.input, self.root / "run", interrupted)
        self.assertEqual("failed", json.loads((self.root / "run/result.json").read_text())["state"])

    def test_input_change_before_completion_is_not_success(self):
        def change(path, value):
            self.input.write_text("{}")
            return value
        self.api.transform = change
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertTrue(any(item["context"] == "manifest" for item in result["errors"]))

    def test_failed_fetches_consume_request_budget(self):
        self.input.write_text(json.dumps(self.manifest))
        calls = []
        def unavailable(path):
            calls.append(path)
            raise OSError("unavailable")
        with patch.object(verify_api, "MAX_REQUESTS", 2):
            result = verify_api.run(
                "http://localhost:18290", self.input, self.root / "run", unavailable
            )
        self.assertEqual(2, len(calls))
        self.assertEqual(2, result["request_attempts"])
        self.assertEqual("failed", result["state"])

    def test_reversed_tracks_and_sets_fail(self):
        second_set = {**self.manifest["sets"][0], "id": 70, "uuid": identity(70), "index": 1}
        second_track = {
            **self.manifest["tracks"][0], "id": 71, "uuid": identity(71),
            "source_set_id": 70, "source_set_uuid": identity(70),
        }
        self.manifest["sets"].append(second_set)
        self.manifest["tracks"].append(second_track)
        def reorder(path, value):
            if path.endswith(identity(11)):
                value["sources"][0]["sets"].reverse()
            if path.endswith(identity(13)):
                value["sources"][0]["sets"][0]["tracks"].reverse()
            return value
        self.api.transform = reorder
        result = self.run_check()
        self.assertGreaterEqual(
            sum(error["detail"] == "Playback positions are out of order" for error in result["errors"]), 2
        )

    def test_equal_track_positions_do_not_require_a_uuid_tiebreaker(self):
        self.manifest["tracks"][-1]["track_position"] = 1
        def reorder(path, value):
            if path.endswith(identity(13)):
                value["sources"][0]["sets"][0]["tracks"].reverse()
            return value
        self.api.transform = reorder
        result = self.run_check()
        self.assertEqual("completed", result["state"], result["errors"])

    def test_unknown_music_content_or_active_composition_policy_rejects_before_http(self):
        for counter in ("track_contents", "setlist_editions", "active_composition_policies"):
            with self.subTest(counter=counter):
                data = copy.deepcopy(self.manifest)
                data["normalization_state"][counter] = 1
                self.input.write_text(json.dumps(data))
                with self.assertRaises(ValueError):
                    verify_api.run(
                        "http://localhost:18290", self.input, self.root / counter, self.api.get
                    )
        self.assertEqual([], self.api.calls)

    def test_null_detail_payload_cannot_pass_as_missing_optional_data(self):
        def null_detail(path, value):
            return None if path == "/api/v3/artists/" + identity(1) else value
        self.api.transform = null_detail
        result = self.run_check()
        self.assertEqual("failed", result["state"])
        self.assertTrue(any(item["detail"] == "Expected an object" for item in result["errors"]))


if __name__ == "__main__":
    unittest.main()
