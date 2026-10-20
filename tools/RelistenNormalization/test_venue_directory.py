"""Venue lookup contracts over synthetic retained outcomes and native catalog homes."""
from contextlib import redirect_stdout
import gzip
import io
import json
import os
import sqlite3
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit
from urllib.request import urlopen
from urllib.error import HTTPError
from http.server import ThreadingHTTPServer
from shadow_reader import ShadowIndex

from venue_directory import VenueDirectory, VenueDirectoryBuilder, NativeVenueOverlayBuilder, VenueCorrectionsBuilder, file_hash, handler_for, retained_outcome


def uid(number):
    return f"00000000-0000-0000-0000-{number:012d}"


def write_json(path, value):
    path.write_text(json.dumps(value))


def write_gzip(path, rows):
    with gzip.open(path, "wt") as stream:
        for row in rows:
            stream.write(json.dumps(row) + "\n")


class TestVenueDirectory(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.run = self.root / "run"
        self.raw = self.root / "raw"
        self.run.mkdir()
        self.raw.mkdir()
        (self.run / "venues").mkdir()
        self.database = self.root / "venues.sqlite3"
        self.artists = [(10, "Artist One"), (20, "Other Artist"), (30, "Ärtist Drei")]
        self.rows = [self.venue(1, 10, "Greek Theatre", "Berkeley, CA", "one"),
                     self.venue(2, 20, "Greek Theatre", "Los Angeles, CA", "two"),
                     self.venue(3, 30, "Alte Bühne 100%_Hall", "Berlin", "room"),
                     self.venue(4, 20, "Greek Theatre", "Berkeley, CA", "one")]
        self.rows[0]["proposal"]["pastNames"] = [{"name": "Hearst Amphitheatre", "fromYear": 1900,
            "toYear": None, "scope": "unknown", "relationship": "history_unspecified",
            "evidenceField": "past_names", "evidenceQuote": "Hearst Amphitheatre", "evidenceOffset": 0, "evidenceLength": 18}]
        write_json(self.run / "manifest.json", {"state": "completed", "methods": {"ShadowVenueAdapter": "retained-v1"}})
        self.chunks()
        prefix = [{"kind": "artist", "row": {"id": number, "uuid": uid(number), "name": name}} for number, name in self.artists]
        sources = [self.source(101, 10, 1, 2, "unknown"), self.source(102, 10, 1, 1, "1999-02"), self.source(103, 20, 4, 4, None)]
        source = self.raw / "sources.jsonl.gz"
        write_gzip(source, prefix + sources)
        receipt = self.raw / "sources.receipt.json"
        write_json(receipt, {"state": "completed"})
        write_json(self.raw / "core-inputs-manifest-v1.json", {
            "plan": [{"id": "sources", "domain": "sources"}],
            "completedShards": [{"shardId": "sources", "gzipPath": source.name,
                "receipt": receipt.name, "receiptFingerprint": {"sha256": file_hash(receipt)},
                "compressed": {"sha256": file_hash(source)}, "actualRows": 3}]})

    def venue(self, number, artist, name, location, key):
        return {"domain": "venues", "inputUuid": uid(number), "inputKey": "venue_sample:" + uid(number),
            "inputKind": "venue_sample", "artistUuid": uid(artist), "artistName": dict(self.artists)[artist],
            "method": "retained-v1", "outcome": "unresolved", "reason": "premises_unresolved",
            "proposal": {"proposedPlaceKey": "venue:" + key, "displayName": name,
                "scope": "room" if number == 3 else "place", "groupingBasis": "retained_venue_row",
                "locality": {"originalWording": location, "city": location.split(",")[0], "region": None,
                    "countryCode": None, "basis": "parsed_catalog_location"},
                "pastNames": [], "protectedUnknowns": ["room_parent_unknown"], "enrichmentTasks": [],
                "alternatives": [], "alternativeCount": 97, "alternativesTruncated": True, "acceptedIdentity": False},
            "evidence": {"nativeVenue": {"id": number, "uuid": uid(number), "artist_id": artist,
                "name": name, "location": location}}}

    @staticmethod
    def source(number, artist, venue, show_venue, date):
        return {"kind": "source_sample", "row": {"uuid": uid(number), "artist_id": artist,
            "venue_id": venue, "show_id": number + 1000, "display_date": date},
            "show_context": {"id": number + 1000, "uuid": uid(number + 1000),
                "venue_id": show_venue, "display_date": "uncertain show date"}}

    def chunks(self):
        write_gzip(self.run / "venues" / "100-a.jsonl.gz", self.rows)

    def build(self, resume=False):
        with redirect_stdout(io.StringIO()):
            VenueDirectoryBuilder(self.run, self.raw, self.database, minimum_free=0).build(resume)
        return VenueDirectory(self.database, "http://files/", "http://core/")

    def test_cross_artist_search_keeps_namesakes_and_existing_hypotheses_separate(self):
        directory = self.build()
        result = directory.search({"q": "Greek"})
        self.assertEqual(result["total"], 3)
        self.assertEqual({r["place_key"] for r in result["rows"]}, {"venue:one", "venue:two"})
        detail = directory.detail(uid(1), {})
        self.assertEqual({r["uuid"] for r in detail["relatedRecords"]}, {uid(1), uid(4)})
        self.assertFalse(detail["venue"]["accepted_identity"])
        self.assertEqual(detail["venue"]["alternative_count"], 97)
        self.assertTrue(detail["venue"]["alternatives_truncated"])

    def test_review_http_uses_original_accounts_and_exact_aliases_across_artists(self):
        self.normalized_fixture()
        self.build()
        directory = VenueDirectory(self.database, "http://files/", "http://core/", review_url="https://reviews.test")
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        def get(route, **parameters):
            from urllib.parse import urlencode
            separator = "&" if "?" in route else "?"
            with urlopen(f"http://127.0.0.1:{server.server_port}" + route + separator + urlencode(parameters)) as response:
                return json.load(response)

        source = get("/api/review-source", key=uid(1))["items"][0]
        self.assertEqual((source["stableKey"], source["literal"], source["displayName"]),
                         (uid(1), "MSG", "Madison Square Garden"))
        self.assertEqual(source["artistUuid"], uid(10))
        self.assertEqual(get(source["evidenceUrl"])["evidence"]["nativeVenue"]["name"], "MSG")
        self.assertEqual(get("/api/review-source", key=uid(1), artist=uid(20))["items"], [])
        self.assertEqual(get("/api/review-source", key=uid(999))["total"], 0)
        candidates = get("/api/review-candidates", q="MSG")
        self.assertEqual(candidates["total"], 1)
        self.assertEqual(candidates["items"][0]["displayName"], "Madison Square Garden")
        exact = get("/api/review-observations", literal="Greek Theatre", limit=1)
        self.assertEqual(exact["total"], 1)
        self.assertTrue(exact["complete"])
        self.assertEqual(get("/api/review-observations", literal="msg")["total"], 0)
        self.assertEqual(get("/api/review-observations", literal="MSG", artist=uid(20))["total"], 0)
        self.assertEqual(get("/api/stats")["reviewUrl"], "https://reviews.test/")

    def test_review_exact_accounts_do_not_collapse_a_shared_spelling_or_hide_paging(self):
        directory = self.build()
        first = directory.review_observations(dict(literal="Greek Theatre", limit=1))
        self.assertEqual(first["total"], 3)
        self.assertFalse(first["complete"])
        second = directory.review_observations(dict(literal="Greek Theatre", limit=1, offset=1))
        self.assertNotEqual(first["items"][0]["stableKey"], second["items"][0]["stableKey"])
        self.assertFalse(directory.stats()["capabilities"]["normalizationReview"])
        for url in ("javascript:alert(1)", "https://name:password@reviews.test", "https://reviews.test/path", "https://reviews.test/?q=other"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                VenueDirectory(self.database, "http://files/", "http://core/", review_url=url)

    def test_reviewed_venue_http_changes_group_recordings_without_rewriting_source_references(self):
        source_path = self.raw / "sources.jsonl.gz"
        with gzip.open(source_path, "rt") as stream:
            raw = [json.loads(line) for line in stream]
        write_gzip(source_path, raw + [self.source(104, 20, 2, 2, "2000-01-01")])
        manifest_path = self.raw / "core-inputs-manifest-v1.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["completedShards"][0].update(actualRows=4, compressed=dict(sha256=file_hash(source_path)))
        write_json(manifest_path, manifest)
        directory = self.build()
        target = dict(domain="venues", namespace="legacy_venue", stableKey=uid(2))
        decision = dict(outcome="reviewed_correspondence", target=target, excludedTargets=[], appliedRules=[])
        class CoreReview:
            def snapshot(self):
                return dict(rules=[dict(state="accepted", definition=dict(domain="venues",
                    selector=dict(namespace="legacy_venue", accountStableKey=uid(1))))])

            def evaluate(self, references, snapshot):
                return [decision for _ in references]
        directory.review_client = CoreReview()
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        def get(route):
            with urlopen(f"http://127.0.0.1:{server.server_port}" + route) as response:
                return json.load(response)
        detail = get("/api/venue?id=" + uid(1))
        self.assertEqual(detail["venue"]["location"], "Berkeley, CA")
        self.assertEqual(detail["venue"]["normalized_location"], "Los Angeles, CA")
        self.assertEqual(detail["venue"]["place_key"], "venue:two")
        self.assertEqual(detail["venue"]["reviewedDecision"]["target"]["stableKey"], uid(2))
        self.assertEqual({row["source_uuid"] for row in detail["recordings"]}, {uid(101), uid(102), uid(104)})
        for recording in detail["recordings"]:
            self.assertEqual(parse_qs(urlsplit(recording["filesUrl"]).query)["entityKey"], ["source:" + recording["source_uuid"]])
        original = get("/api/review-source?key=" + uid(1))["items"][0]
        self.assertEqual((original["literal"], original["locality"]), ("Greek Theatre", "Berkeley, CA"))
        selected = get("/api/venue?id=" + uid(1) + "&recordings=selected")
        self.assertEqual({row["source_uuid"] for row in selected["recordings"]}, {uid(101), uid(102)})
        target_detail = get("/api/venue?id=" + uid(2))
        self.assertEqual(target_detail["recordingsScope"], "shared_normalization_hypothesis")
        self.assertEqual(target_detail["recordingsTotal"], 3)
        self.assertEqual({row["source_uuid"] for row in target_detail["recordings"]}, {uid(101), uid(102), uid(104)})
        grouped = next(row for row in get("/api/venues?q=Greek")["rows"] if row["place_key"] == "venue:two")
        self.assertEqual(grouped["source_count"], target_detail["recordingsTotal"])

    def normalized_fixture(self):
        """Supply C# decisions, not a Python implementation of its matching policy."""
        key = "venue_hypothesis:msg-new-york"
        names = {1: "MSG", 4: "Madison Square Garden"}
        for row in self.rows:
            number = row["evidence"]["nativeVenue"]["id"]
            if number not in names:
                continue
            original = names[number]
            row["evidence"]["nativeVenue"].update(name=original, location="NY, NY" if number == 1 else "New York, New York")
            row["proposal"].update(proposedPlaceKey=key, displayName="Madison Square Garden", pastNames=[])
            row["proposal"]["normalization"] = dict(status="proposed", membershipBasis="shared_csharp_fixture",
                originalName=original, displayLocation="New York City, New York, US", geographicKey="place:nyc",
                hypothesis=dict(key=key, anchorVenueUuid=uid(1), preferredName="Madison Square Garden",
                    scope="place", geographicKey="place:nyc", displayLocation="New York City, New York, US",
                    basis="corpus_alias_locality_context_hypothesis", memberCount=2, artistCount=2,
                    memberExamples=[uid(1)], membersTruncated=True, nameCount=2, names=["Madison Square Garden"],
                    namesTruncated=True, sourceObservationCount=3, sourceExamples=[], sourceExamplesTruncated=True),
                candidateCount=1, candidateKeys=[key], candidatesTruncated=False,
                uncertainties=["physical_identity_hypothesis_not_accepted"])
        self.chunks()
        return key

    def test_shared_normalization_searches_all_member_aliases_and_localities_without_python_matching(self):
        key = self.normalized_fixture()
        directory = self.build()

        for query in ({"q": "MSG"}, {"q": "Madison Square Garden"}, {"locality": "New York City"},
                      {"q": "NY, NY", "artist": "Other"}, {"q": "MSG", "artist": uid(20)}):
            with self.subTest(query=query):
                result = directory.search(query)
                self.assertEqual(result["total"], 1)
                row = result["rows"][0]
                self.assertEqual(row["normalization_key"], key)
                self.assertEqual(row["name"], "Madison Square Garden")
                self.assertEqual(row["normalized_location"], "New York City, New York, US")
                self.assertEqual((row["hypothesis_member_count"], row["hypothesis_artist_count"]), (2, 2))
                self.assertEqual((row["source_count"], row["show_count"]), (3, 3))
        self.assertEqual(directory.search({"q": "MSG", "artist": uid(20)})["rows"][0]["uuid"], uid(4))
        self.assertEqual(directory.search({"q": "Greek"})["total"], 1)
        self.assertEqual(directory.search({"scope": "room"})["rows"][0]["uuid"], uid(3))
        self.assertEqual(directory.stats()["normalizedHypotheses"], 1)
        self.assertEqual(directory.stats()["normalizedMembers"], 2)
        landing = directory.search({})
        pages = [directory.search({"limit": "1", "offset": str(offset)}) for offset in range(landing["total"])]
        self.assertEqual([page["rows"][0] for page in pages], landing["rows"])
        self.assertTrue(all(page["total"] == landing["total"] for page in pages))
        self.assertEqual(landing["rows"][0]["uuid"], uid(1))
        self.assertEqual(landing["rows"][0]["original_name"], "MSG")
        self.assertEqual(landing["rows"][0]["source_count"], 3)

    def test_group_search_words_can_have_different_members_before_selecting_filtered_original(self):
        key = self.normalized_fixture()
        for number, alias, city in ((1, "Eastalias", "Northbank"), (4, "Westalias", "Southbank")):
            proposal = self.rows[number - 1]["proposal"]
            proposal["pastNames"] = [dict(name=alias, fromYear=None, toYear=None, scope="unknown",
                relationship="history_unspecified", evidenceField="past_names", evidenceQuote=alias,
                evidenceOffset=0, evidenceLength=len(alias))]
            proposal["locality"]["city"] = city
        self.chunks()
        directory = self.build()

        for query in ({"q": "Eastalias Westalias", "artist": uid(20)},
                      {"locality": "Northbank Southbank", "artist": "Other"},
                      {"q": "Eastalias", "locality": "Southbank", "scope": "place", "kind": "legacy_catalog"}):
            with self.subTest(query=query):
                result = directory.search(query)
                self.assertEqual(result["total"], 1)
                self.assertEqual(result["rows"][0]["normalization_key"], key)
                self.assertEqual(result["rows"][0]["uuid"], uid(4) if "artist" in query else uid(1))
        for query in ({"q": "Eastalias Westalias", "artist": uid(30)},
                      {"q": "Eastalias absentword"}, {"q": "Eastalias", "scope": "room"}):
            with self.subTest(query=query):
                result = directory.search(query)
                self.assertEqual((result["total"], result["rows"]), (0, []))

    def test_original_uuid_detail_keeps_its_raw_evidence_and_selected_recording_home(self):
        key = self.normalized_fixture()
        directory = self.build()

        for number, original, expected in ((1, "MSG", {uid(101), uid(102)}), (4, "Madison Square Garden", {uid(103)})):
            with self.subTest(number=number):
                detail = directory.detail(uid(number), {"recordings": "selected"})
                self.assertEqual(detail["venue"]["original_name"], original)
                self.assertEqual(detail["venue"]["normalization"]["hypothesis"]["key"], key)
                self.assertEqual({row["uuid"] for row in detail["relatedRecords"]}, {uid(1), uid(4)})
                self.assertEqual(detail["recordingsScope"], "selected_legacy_venue_record")
                self.assertEqual({row["source_uuid"] for row in detail["recordings"]}, expected)
                self.assertEqual(directory.evidence(uid(number)), self.rows[number - 1])
                for row in detail["recordings"]:
                    self.assertEqual(parse_qs(urlsplit(row["filesUrl"]).query)["entityKey"], ["source:" + row["source_uuid"]])

    def test_http_normalized_group_pagination_aliases_and_original_evidence(self):
        self.normalized_fixture()
        directory = self.build()
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        base = "http://127.0.0.1:" + str(server.server_port)
        def get(path):
            with urlopen(base + path) as response:
                return json.load(response)

        result = get("/api/venues?q=MSG&limit=1")
        self.assertEqual(result["total"], 1)
        beyond_page = get("/api/venues?q=MSG&limit=1&offset=1")
        self.assertEqual((beyond_page["total"], beyond_page["rows"]), (1, []))
        missing = get("/api/venues?q=absentvenue")
        self.assertEqual((missing["total"], missing["rows"]), (0, []))
        detail = get("/api/venue?id=" + uid(1))
        self.assertEqual(detail["venue"]["name"], "Madison Square Garden")
        self.assertEqual(detail["venue"]["original_name"], "MSG")
        self.assertEqual(get(detail["evidenceUrl"])["evidence"]["nativeVenue"]["location"], "NY, NY")
        grouped = get("/api/venue?id=" + uid(1) + "&recordings=group")
        self.assertEqual(grouped["recordingsScope"], "shared_normalization_hypothesis")
        self.assertEqual({row["source_uuid"] for row in grouped["recordings"]}, {uid(101), uid(102), uid(103)})
        self.assertEqual(grouped["recordingsTotal"], 3)
        filtered = get("/api/venue?id=" + uid(1) + "&recordings=group&artist=" + uid(20))
        self.assertEqual([row["source_uuid"] for row in filtered["recordings"]], [uid(103)])
        for row in grouped["recordings"]:
            self.assertEqual(parse_qs(urlsplit(row["filesUrl"]).query)["entityKey"], ["source:" + row["source_uuid"]])
            self.assertEqual(row["show_uuid"], uid(int(row["source_uuid"].split("-")[-1]) + 1000))

    def corrections_fixture(self):
        key = self.normalized_fixture()
        self.build()
        folder = self.root / "corrections"
        folder.mkdir()
        artifact = lambda path, **extra: dict(path=str(path.resolve()), bytes=path.stat().st_size, sha256=file_hash(path), **extra)
        def state(row):
            proposal = row["proposal"]
            normalization = proposal.get("normalization") or {}
            return dict(placeKey=proposal["proposedPlaceKey"], normalizationKey=(normalization.get("hypothesis") or {}).get("key"),
                status=normalization.get("status"), basis=normalization.get("membershipBasis"), groupingBasis=proposal["groupingBasis"],
                name=proposal["displayName"], normalizedLocation=normalization.get("displayLocation"), geographicKey=normalization.get("geographicKey"),
                outcome=row["outcome"], reason=row["reason"], uncertainties=normalization.get("uncertainties", []))
        changes = []
        for number in (1, 2):
            previous = state(self.rows[number - 1])
            current = dict(previous)
            if number == 1:
                current.update(placeKey="venue:raw-msg", normalizationKey=None, name="MSG", status="ambiguous",
                    basis=None, normalizedLocation=None, geographicKey=None, uncertainties=["premises_conflict"])
            else:
                current.update(placeKey=key, normalizationKey=key, name="Madison Square Garden", status="proposed",
                    basis="corrected_shared_context", normalizedLocation="New York City, New York, US", geographicKey="place:nyc")
            changes.append(dict(venueUuid=uid(number), legacyId=number, artistId=self.rows[number - 1]["evidence"]["nativeVenue"]["artist_id"],
                previous=previous, current=current, originalOutcome=artifact(self.run / "venues/100-a.jsonl.gz", line=number, jsonPointer="/evidence/nativeVenue")))
        group = dict(self.rows[0]["proposal"]["normalization"]["hypothesis"])
        group.update(memberCount=2, artistCount=1, sourceObservationCount=2)
        write_gzip(folder / "changes.jsonl.gz", changes)
        write_gzip(folder / "groups.jsonl.gz", [group])
        write_json(folder / "geography.json", {"fixture": "pinned C# geography"})
        manifest = dict(version="venue-normalization-corrections-v1", state="completed", complete=True, method="corrected-csharp-fixture",
            baseDirectory=artifact(self.database), previousRunManifest=artifact(self.run / "manifest.json"),
            captureDescriptor=artifact(self.raw / "core-inputs-manifest-v1.json"), geographyManifest=artifact(folder / "geography.json"),
            changes=artifact(folder / "changes.jsonl.gz", rows=2), groups=artifact(folder / "groups.jsonl.gz", rows=1),
            totalVenues=4, changedVenues=2, unchangedVenues=2)
        manifest_path = folder / "manifest.json"
        write_json(manifest_path, manifest)
        sidecar = folder / "corrections.sqlite3"
        return manifest_path, sidecar, changes

    def test_http_corrections_replace_groups_but_preserve_original_ids_evidence_and_playback_homes(self):
        manifest, sidecar, changes = self.corrections_fixture()
        original = self.database.read_bytes()
        VenueCorrectionsBuilder(manifest, file_hash(manifest), self.database, sidecar, minimum_free=0).build()
        native_directory = self.native_overlay()
        native_id = native_directory.search({"kind": "native_observation"})["rows"][0]["uuid"]
        native_detail = native_directory.detail(native_id, {})
        directory = VenueDirectory(self.database, "http://files/", "http://core/", self.overlay, "http://native/", corrections_database=sidecar)
        server = ThreadingHTTPServer(("127.0.0.1", 0), handler_for(directory))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        def get(path):
            with urlopen("http://127.0.0.1:" + str(server.server_port) + path) as response:
                return json.load(response)
        grouped = get("/api/venue?id=" + uid(2))
        self.assertEqual(grouped["recordingsScope"], "shared_normalization_hypothesis")
        self.assertEqual({row["source_uuid"] for row in grouped["recordings"]}, {uid(101), uid(103)})
        self.assertEqual(grouped["venue"]["hypothesis_member_count"], 2)
        selected = get("/api/venue?id=" + uid(4) + "&recordings=selected")
        self.assertEqual([row["source_uuid"] for row in selected["recordings"]], [uid(103)])
        removed = get("/api/venue?id=" + uid(1))
        self.assertIsNone(removed["venue"]["normalization_key"])
        self.assertIsNone(removed["venue"]["normalization"]["hypothesis"])
        self.assertEqual(removed["recordingsScope"], "selected_legacy_venue_record")
        self.assertEqual(get(removed["venue"]["correctionEvidenceUrl"])["correction"], changes[0])
        self.assertEqual(get(removed["evidenceUrl"]), self.rows[0])
        result = get("/api/venues?q=Madison")
        self.assertEqual((result["total"], result["rows"][0]["source_count"]), (1, 2))
        self.assertEqual(result["rows"][0]["hypothesis_artist_count"], 1)
        self.assertEqual(get("/api/venues?q=Greek&artist=" + uid(20))["total"], 1)
        for row in grouped["recordings"]:
            self.assertEqual(parse_qs(urlsplit(row["filesUrl"]).query)["entityKey"], ["source:" + row["source_uuid"]])
            self.assertEqual(row["show_uuid"], uid(int(row["source_uuid"].split("-")[-1]) + 1000))
        self.assertTrue(get("/api/stats")["capabilities"]["venueCorrections"])
        self.assertEqual(get("/api/venue?id=" + native_id), native_detail)
        self.assertEqual(get("/api/evidence?id=" + uid(4)), self.rows[3])
        self.assertEqual(self.database.read_bytes(), original)
        changed = json.loads(json.dumps(self.rows))
        changed[0]["evidence"]["nativeVenue"]["name"] = "Changed after publication"
        pinned_chunk = (self.run / "venues/100-a.jsonl.gz").read_bytes()
        write_gzip(self.run / "venues/100-a.jsonl.gz", changed)
        for route in ("/api/venue?id=", "/api/evidence?id="):
            with self.assertRaises(HTTPError) as failure:
                get(route + uid(1))
            self.assertEqual(failure.exception.code, 400)
            failure.exception.close()
        retained_outcome.cache_clear()
        raw_directory = VenueDirectory(self.database, "", "")
        self.assertEqual(raw_directory.evidence(uid(1)), changed[0])
        (self.run / "venues/100-a.jsonl.gz").write_bytes(pinned_chunk)
        self.assertEqual(get("/api/evidence?id=" + uid(1)), self.rows[0])
        (self.run / "venues/100-a.jsonl.gz").unlink()
        with self.assertRaises(HTTPError) as failure:
            get("/api/evidence?id=" + uid(1))
        self.assertEqual(failure.exception.code, 400)
        failure.exception.close()

    def test_correction_startup_replacement_cannot_inherit_the_old_generation_provenance(self):
        manifest, sidecar, _ = self.corrections_fixture()
        VenueCorrectionsBuilder(manifest, file_hash(manifest), self.database, sidecar, minimum_free=0).build()
        replacement = sidecar.with_name("replacement.sqlite3")
        replacement.write_bytes(sidecar.read_bytes())
        original_stamps = VenueDirectory._correction_stamps
        calls = 0
        def replace_after_validation(directory):
            nonlocal calls
            stamps = original_stamps(directory)
            calls += 1
            if calls == 2:
                os.replace(replacement, sidecar)
            return stamps
        with patch.object(VenueDirectory, "_correction_stamps", replace_after_validation):
            directory = VenueDirectory(self.database, "", "", corrections_database=sidecar)
        with self.assertRaisesRegex(ValueError, "generation changed"):
            directory.search({"q": "Madison"})
        fresh = VenueDirectory(self.database, "", "", corrections_database=sidecar)
        self.assertEqual(fresh.search({"q": "Madison"})["total"], 1)

    def test_failed_correction_replacement_preserves_published_projection_and_original_base(self):
        manifest, sidecar, changes = self.corrections_fixture()
        base = self.database.read_bytes()
        with self.assertRaisesRegex(ValueError, "input artifact"):
            VenueCorrectionsBuilder(manifest, file_hash(manifest), self.database, self.database, minimum_free=0).build()
        self.assertEqual(self.database.read_bytes(), base)
        builder = lambda: VenueCorrectionsBuilder(manifest, file_hash(manifest), self.database, sidecar, minimum_free=0)
        builder().build()
        published = sidecar.read_bytes()
        changes[0]["previous"]["name"] = "Different prior generation"
        write_gzip(manifest.parent / "changes.jsonl.gz", changes)
        document = json.loads(manifest.read_text())
        document["changes"].update(bytes=(manifest.parent / "changes.jsonl.gz").stat().st_size,
                                   sha256=file_hash(manifest.parent / "changes.jsonl.gz"))
        write_json(manifest, document)
        with self.assertRaisesRegex(ValueError, "Previous venue tuple"):
            builder().build()
        self.assertEqual(sidecar.read_bytes(), published)
        self.assertFalse(sidecar.with_name(sidecar.name + ".building").exists())
        self.assertEqual(VenueDirectory(self.database, "", "").detail(uid(1), {})["venue"]["name"], "Madison Square Garden")

    def test_normalized_catalog_group_does_not_absorb_room_or_native_observations(self):
        self.normalized_fixture()
        self.rows[2]["proposal"]["displayName"] = "The Theater at MSG"
        self.rows[2]["evidence"]["nativeVenue"].update(name="The Theater at MSG", location="NY, NY")
        self.chunks()
        self.build()
        directory = self.native_overlay([self.native_fixture(name="MSG")])

        result = directory.search({"q": "MSG"})
        self.assertEqual(result["total"], 3)
        self.assertEqual(sum(bool(row["normalization_key"]) for row in result["rows"]), 1)
        room = next(row for row in result["rows"] if row["scope"] == "room")
        self.assertEqual(room["uuid"], uid(3))
        native = next(row for row in result["rows"] if row["record_kind"] == "native_observation")
        self.assertEqual(directory.detail(native["uuid"], {})["filesTotal"], 1)

    def test_old_projection_remains_readable_and_new_builder_refuses_to_upgrade_it_in_place(self):
        directory = self.build()
        with sqlite3.connect(self.database) as db:
            db.execute("DROP INDEX venues_normalization")
            columns = [row[1] for row in db.execute("PRAGMA table_info(venues)")]
            for column in columns[columns.index("normalization_key"):]:
                db.execute("ALTER TABLE venues DROP COLUMN " + column)
            db.execute("UPDATE metadata SET value=? WHERE key='version'", (json.dumps("retained-venue-directory-v1"),))
        before = file_hash(self.database)

        self.assertEqual(directory.search({"q": "Greek"})["total"], 3)
        self.assertEqual(directory.detail(uid(1), {})["recordingsTotal"], 2)
        with self.assertRaisesRegex(ValueError, "new database"):
            self.build(resume=True)
        self.assertEqual(file_hash(self.database), before)

    def test_history_locality_artist_and_literal_unicode_queries(self):
        directory = self.build()
        self.assertEqual(directory.search({"q": "Hearst"})["rows"][0]["uuid"], uid(1))
        self.assertEqual(directory.search({"q": "Greek", "locality": "Berkeley", "artist": "Other"})["rows"][0]["uuid"], uid(4))
        self.assertEqual(directory.search({"q": "BÜHNE 100%_", "artist": "ÄRTIST"})["rows"][0]["uuid"], uid(3))
        self.assertEqual(directory.search({"q": "100%X"})["total"], 0)
        self.assertEqual(directory.search({"scope": "room"})["total"], 1)

    def test_native_recording_and_show_venue_links_preserve_each_home(self):
        directory = self.build()
        first = directory.detail(uid(1), {})
        recordings = {r["source_uuid"]: r for r in first["recordings"]}
        self.assertEqual(first["recordingsTotal"], 2)
        self.assertEqual(recordings[uid(101)]["relationship"], "recording_venue")
        self.assertEqual(recordings[uid(102)]["relationship"], "recording_and_show_venue")
        self.assertEqual(recordings[uid(101)]["source_date"], "unknown")
        self.assertEqual(recordings[uid(101)]["show_date"], "uncertain show date")
        self.assertEqual(recordings[uid(102)]["source_date"], "1999-02")
        self.assertIn("source%3A" + uid(101), recordings[uid(101)]["filesUrl"])
        other = directory.detail(uid(2), {})["recordings"][0]
        self.assertEqual(other["source_uuid"], uid(101))
        self.assertEqual(other["relationship"], "show_venue")

    def test_artist_filter_pagination_and_evidence_use_exact_retained_row(self):
        directory = self.build()
        first = directory.search({"q": "Greek", "limit": "1"})
        second = directory.search({"q": "Greek", "limit": "1", "offset": "1"})
        self.assertEqual(first["total"], 3)
        self.assertNotEqual(first["rows"][0]["uuid"], second["rows"][0]["uuid"])
        detail = directory.detail(uid(1), {"artist": uid(20)})
        self.assertEqual(detail["recordingsTotal"], 0)
        self.assertEqual(directory.evidence(uid(1)), self.rows[0])
        self.assertEqual(directory.detail(uid(1), {})["venue"]["history"], self.rows[0]["proposal"]["pastNames"])
        self.assertIsNone(directory.detail(uid(999), {}))

    def test_later_result_retracts_historical_search_terms(self):
        changed = json.loads(json.dumps(self.rows[0]))
        changed["proposal"]["displayName"] = "New Hall"
        changed["proposal"]["pastNames"] = []
        write_gzip(self.run / "venues" / "200-b.jsonl.gz", [changed])
        directory = self.build()
        self.assertEqual(directory.search({"q": "Hearst"})["total"], 0)
        self.assertEqual(directory.search({"q": "New Hall"})["rows"][0]["uuid"], uid(1))
        self.assertEqual(directory.evidence(uid(1)), changed)

    def test_storage_pause_preserves_checkpoint_then_resumes(self):
        builder = VenueDirectoryBuilder(self.run, self.raw, self.database, minimum_free=0)
        checks = 0
        def space():
            nonlocal checks
            checks += 1
            if checks == 3:
                raise RuntimeError("disk reserve")
        with redirect_stdout(io.StringIO()), patch.object(builder, "_space", side_effect=space):
            with self.assertRaises(RuntimeError):
                builder.build()
        partial = VenueDirectory(self.database, "", "")
        self.assertEqual(partial.stats()["buildState"], "paused")
        self.assertEqual(partial.search({})["total"], 4)
        finished = self.build(resume=True)
        self.assertEqual(finished.stats()["buildState"], "completed")
        self.assertEqual(finished.detail(uid(1), {})["recordingsTotal"], 2)

    def test_population_search_is_not_limited_to_retained_twenty_alternatives(self):
        self.rows.extend(self.venue(number, 20, "Greek Theatre", "Another City", str(number))
                         for number in range(50, 75))
        self.chunks()
        directory = self.build()
        result = directory.search({"q": "Greek", "offset": "20", "limit": "100"})
        self.assertEqual(result["total"], 28)
        self.assertEqual(len(result["rows"]), 8)

    def test_contradictory_source_show_context_refuses_instead_of_moving_home(self):
        source = self.raw / "sources.jsonl.gz"
        with gzip.open(source, "rt") as stream:
            rows = [json.loads(line) for line in stream]
        rows[-1]["show_context"]["id"] = 999999
        write_gzip(source, rows)
        capture = self.raw / "core-inputs-manifest-v1.json"
        manifest = json.loads(capture.read_text())
        manifest["completedShards"][0]["compressed"]["sha256"] = file_hash(source)
        write_json(capture, manifest)
        with self.assertRaisesRegex(ValueError, "source/show context disagrees"):
            self.build()
        self.assertEqual(VenueDirectory(self.database, "", "").stats()["buildState"], "failed")

    def test_generated_links_filter_actual_reader_to_the_recording_home(self):
        directory = self.build()
        run = self.root / "file-reader"
        run.mkdir()
        for domain in ("file_contents", "performances"):
            (run / domain).mkdir()
            records = []
            for source in (101, 102):
                records.append({"domain": domain, "inputKey": "track_sample:" + uid(source + 2000),
                    "inputKind": "track_sample", "inputUuid": uid(source + 2000), "artistUuid": uid(10),
                    "artistName": "Artist", "provider": "catalog", "outcome": "proposed", "reason": None,
                    "method": "fixture", "entityKeys": ["source:" + uid(source)],
                    "proposal": {"sourceUuid": uid(source), "mp3Url": "https://audio.invalid/" + str(source)}, "evidence": {}})
            write_gzip(run / domain / "1.jsonl.gz", records)
        reader = ShadowIndex(run)
        reader.scan_once()
        recording = directory.detail(uid(1), {"limit": "1"})["recordings"][0]
        for url in (recording["filesUrl"], recording["coreUrl"]):
            filters = {key: values[-1] for key, values in parse_qs(urlsplit(url).query).items()}
            result = reader.search(filters)
            self.assertEqual(result["total"], 1)
            original = reader.detail(result["items"][0]["id"])["record"]
            self.assertEqual(original["proposal"]["sourceUuid"], recording["source_uuid"])

    def test_partial_source_checkpoint_does_not_report_uncomputed_counts_as_zero(self):
        builder = VenueDirectoryBuilder(self.run, self.raw, self.database, minimum_free=0)
        checks = 0
        def space():
            nonlocal checks
            checks += 1
            if checks == 4:
                raise RuntimeError("disk reserve after committed source shard")
        with redirect_stdout(io.StringIO()), patch.object(builder, "_space", side_effect=space):
            with self.assertRaises(RuntimeError):
                builder.build()
        directory = VenueDirectory(self.database, "", "")
        self.assertEqual(directory.stats()["recordings"], 3)
        self.assertFalse(directory.stats()["countsReady"])
        result = directory.detail(uid(1), {})
        self.assertFalse(result["recordingsComplete"])
        self.assertIsNone(result["venue"]["source_count"])
        self.assertEqual(result["recordingsTotal"], 2)
        self.assertTrue(all(r["source_count"] is None for r in directory.search({})["rows"]))

    def test_partial_show_context_cannot_supply_an_unproven_show_home(self):
        for case, (source_show_id, context_id) in enumerate(((None, 1103), (1103, None))):
            with self.subTest(source_show_id=source_show_id, context_id=context_id):
                source = self.source(103, 20, 4, 4, None)
                source["row"]["show_id"] = source_show_id
                source["show_context"]["id"] = context_id
                path = self.raw / "sources.jsonl.gz"
                write_gzip(path, [source])
                capture = self.raw / "core-inputs-manifest-v1.json"
                manifest = json.loads(capture.read_text())
                manifest["completedShards"][0]["compressed"]["sha256"] = file_hash(path)
                manifest["completedShards"][0]["actualRows"] = 1
                write_json(capture, manifest)
                database = self.root / ("partial-" + str(case) + ".sqlite3")
                with redirect_stdout(io.StringIO()), self.assertRaisesRegex(ValueError, "source/show context disagrees"):
                    VenueDirectoryBuilder(self.run, self.raw, database, 0).build()

    def test_changed_captured_source_bytes_are_refused(self):
        with (self.raw / "sources.jsonl.gz").open("ab") as stream:
            stream.write(b"changed")
        with self.assertRaisesRegex(ValueError, "source chunk changed"):
            self.build()


    def native_fixture(self, parent=801, name="Greek Theatre", anchor=None, root=False):
        origin = {"item": {"provider": "local", "resourceType": "directory_metadata", "providerKey": str(parent)},
                  "evidence": {"inputRevisionUuid": uid(parent), "jsonPointer": "/recording"}, "scope": "resource_item"}
        venue_origin = json.loads(json.dumps(origin))
        if root:
            venue_origin["evidence"] = {"inputRevisionUuid": uid(900), "jsonPointer": "/accounts/selected/venue"}
        key = "native_venue_observation:original-root" if root else "native_venue_observation:" + str(parent)
        account = {"observationKey": key, "displayName": name, "scope": "place", "outcome": "proposed",
            "acceptedIdentity": False, "protectedUnknowns": ["relocation_history_unknown"],
            "locality": {"originalWording": "Berkeley, CA", "city": None, "region": None, "countryCode": None},
            "localityAssertions": [{"value": "Berkeley, CA", "evidence": venue_origin["evidence"]},
                                   {"value": "Oakland?", "evidence": venue_origin["evidence"]}],
            "venue": {"origin": venue_origin, "names": [{"value": name, "evidence": venue_origin["evidence"]}],
                "identities": [{"identity": {"provider": "local", "providerKey": "1"}}],
                "text": [{"field": "past_names", "text": {"value": "Formerly the Lantern; dates unknown", "evidence": venue_origin["evidence"]}}]},
            "recordingLinks": [{"recording": origin, "catalogSourceUuid": anchor, "catalogShowUuid": None,
                "catalogBindingReason": None if anchor else "no_catalog_binding", "literalDate": {"value": "1990-??", "evidence": origin["evidence"]}}],
            "setlistLinks": []}
        record = {"domain": "venues", "inputKind": "provider_input", "inputUuid": uid(parent),
            "inputKey": "provider_input:" + uid(parent), "artistUuid": uid(10), "artistName": "Artist One",
            "provider": "local", "method": "native-fixture", "outcome": "proposed", "reason": "unaccepted",
            "proposal": {"venues": [account]}, "evidence": {}, "entityKeys": [key]}
        child = {"entityKey": "native_file:" + str(parent), "fileOrdinal": 0,
            "inputRevisionUuid": uid(parent), "recordingOrigin": origin,
            "file": {"title": {"value": "Late song"}, "filename": {"value": "recording.mp3"}},
            "outcome": "unresolved", "entityKeys": ["native_file:" + str(parent)]}
        if anchor:
            child["entityKeys"].append("source:" + anchor)
        file_record = {**record, "domain": "file_contents", "proposal": {"files": [child]},
                       "entityKeys": child["entityKeys"]}
        return record, file_record

    def native_overlay(self, records=None):
        self.native_run = self.root / "native"
        self.native_run.mkdir()
        for domain in ("venues", "file_contents"):
            (self.native_run / domain).mkdir()
        records = records or [self.native_fixture()]
        write_json(self.native_run / "manifest.json", {"state": "completed", "counts": [
            {"domain": domain, "provider": "local", "processed": len(records)}
            for domain in ("venues", "file_contents")]})
        for domain, position in (("venues", 0), ("file_contents", 1)):
            write_gzip(self.native_run / domain / "100.jsonl.gz", [r[position] for r in records])
        self.overlay = self.root / "native.sqlite3"
        NativeVenueOverlayBuilder(self.native_run, self.overlay, minimum_free=0).build()
        return VenueDirectory(self.database, "http://files/", "http://core/", self.overlay, "http://native/")

    def test_native_shared_search_keeps_legacy_and_native_namesakes_without_numeric_joins(self):
        self.build()
        before = file_hash(self.database)
        directory = self.native_overlay()
        self.assertEqual(file_hash(self.database), before)
        result = directory.search({"q": "Greek"})
        self.assertEqual(result["total"], 4)
        self.assertEqual(directory.search({"kind": "native_observation"})["total"], 1)
        self.assertEqual(directory.search({"kind": "legacy_catalog"})["total"], 4)
        native = next(row for row in result["rows"] if row["record_kind"] == "native_observation")
        self.assertIsNone(native["legacy_id"])
        detail = directory.detail(native["uuid"], {})
        self.assertEqual(detail["recordingsTotal"], 1)
        self.assertIsNone(detail["recordings"][0]["source_uuid"])
        self.assertIsNone(detail["recordings"][0]["filesUrl"])
        self.assertEqual(detail["filesTotal"], 1)
        self.assertEqual(directory.detail(uid(1), {})["recordingsTotal"], 2)
        self.assertEqual(directory.stats()["venueRecords"], 4)
        self.assertEqual(directory.stats()["recordings"], 3)
        self.assertEqual(directory.stats()["nativeCoverage"]["observations"], 1)

    def test_native_history_alternative_localities_and_root_citations_stay_original(self):
        self.build()
        record, files = self.native_fixture(root=True)
        directory = self.native_overlay([(record, files)])
        row = directory.search({"q": "Lantern", "locality": "Oakland"})["rows"][0]
        detail = directory.detail(row["uuid"], {})
        self.assertEqual(detail["venue"]["history"], [])
        self.assertEqual(detail["venue"]["history_prose"], record["proposal"]["venues"][0]["venue"]["text"])
        self.assertEqual(detail["venue"]["original_origin"]["evidence"]["inputRevisionUuid"], uid(900))
        self.assertEqual(detail["venue"]["locality_assertions"], record["proposal"]["venues"][0]["localityAssertions"])
        self.assertEqual(directory.evidence(row["uuid"]), record)
        self.assertEqual(detail["recordings"][0]["recording_origin"]["evidence"]["inputRevisionUuid"], uid(801))
        self.assertFalse(detail["venue"]["accepted_identity"])

    def test_native_exact_file_links_follow_original_recording_not_shared_root_or_name(self):
        self.build()
        first = self.native_fixture(801, root=True)
        second = self.native_fixture(802, root=True)
        unrelated = json.loads(json.dumps(first[1]["proposal"]["files"][0]))
        unrelated["entityKey"] = "native_file:unrelated"
        unrelated["fileOrdinal"] = 1
        unrelated["entityKeys"] = ["native_file:unrelated"]
        unrelated["recordingOrigin"]["evidence"]["jsonPointer"] = "/another-recording"
        first[1]["proposal"]["files"].append(unrelated)
        directory = self.native_overlay([first, second])
        rows = directory.search({"kind": "native_observation"})["rows"]
        self.assertEqual(len(rows), 2)
        first_id = next(row["uuid"] for row in rows if uid(801) in row["uuid"])
        detail = directory.detail(first_id, {})
        self.assertEqual(detail["filesTotal"], 1)
        link = detail["files"][0]["url"]
        filters = {key: values[-1] for key, values in parse_qs(urlsplit(link).query).items()}
        self.assertEqual(filters, {"view": "files", "entityKey": "native_file:801"})
        reader = ShadowIndex(self.native_run)
        reader.scan_once()
        found = reader.search_files(filters)
        self.assertEqual(found["total"], 1)
        child = reader.file_detail(found["items"][0]["parentId"], found["items"][0]["nativeFileKey"])
        self.assertEqual(child["parent"]["inputUuid"], uid(801))

    def test_native_latest_result_retracts_accounts_atomically_and_preserves_old_on_budget_failure(self):
        self.build()
        directory = self.native_overlay()
        before = file_hash(self.overlay)
        with self.assertRaisesRegex(RuntimeError, "storage bound"):
            NativeVenueOverlayBuilder(self.native_run, self.overlay, 0, maximum_bytes=1).build()
        self.assertEqual(file_hash(self.overlay), before)
        self.assertFalse(self.overlay.with_name(self.overlay.name + ".building").exists())
        builder = NativeVenueOverlayBuilder(self.native_run, self.overlay, 0)
        checks = 0
        def pause_after_venues(path):
            nonlocal checks
            checks += 1
            if checks == 3:
                raise RuntimeError("reserve after partial projection")
        with patch.object(builder, "_budget", side_effect=pause_after_venues):
            with self.assertRaisesRegex(RuntimeError, "partial projection"):
                builder.build()
        self.assertEqual(file_hash(self.overlay), before)
        record, _ = self.native_fixture()
        record["proposal"]["venues"] = []
        write_gzip(self.native_run / "venues" / "200.jsonl.gz", [record])
        NativeVenueOverlayBuilder(self.native_run, self.overlay, 0).build()
        self.assertEqual(directory.search({"kind": "native_observation"})["total"], 0)
        self.assertEqual(directory.stats()["nativeCoverage"]["parents"], 1)

    def test_native_root_account_without_recording_link_cannot_acquire_files(self):
        self.build()
        record, files = self.native_fixture(root=True)
        record["proposal"]["venues"][0]["recordingLinks"] = []
        directory = self.native_overlay([(record, files)])
        row = directory.search({"kind": "native_observation"})["rows"][0]
        detail = directory.detail(row["uuid"], {})
        self.assertEqual(detail["recordingsTotal"], 0)
        self.assertEqual(detail["filesTotal"], 0)
        self.assertEqual(directory.stats()["nativeCoverage"]["files"], 1)

    def test_native_missing_domain_or_chunk_does_not_retract_published_observations(self):
        self.build()
        directory = self.native_overlay()
        before = file_hash(self.overlay)
        for domain in ("venues", "file_contents"):
            with self.subTest(domain=domain):
                path = self.native_run / domain / "100.jsonl.gz"
                hidden = path.with_suffix(".retained")
                path.rename(hidden)
                with self.assertRaisesRegex(ValueError, "coverage differs"):
                    NativeVenueOverlayBuilder(self.native_run, self.overlay, 0).build()
                self.assertEqual(file_hash(self.overlay), before)
                self.assertEqual(directory.search({"kind": "native_observation"})["total"], 1)
                hidden.rename(path)
        path = self.native_run / "venues" / "100.jsonl.gz"
        write_gzip(path, [])
        with self.assertRaisesRegex(ValueError, "coverage differs"):
            NativeVenueOverlayBuilder(self.native_run, self.overlay, 0).build()
        self.assertEqual(file_hash(self.overlay), before)

    def test_native_overlay_cannot_replace_the_legacy_projection(self):
        self.build()
        self.native_overlay()
        before = file_hash(self.database)
        with self.assertRaisesRegex(ValueError, "not a native overlay"):
            NativeVenueOverlayBuilder(self.native_run, self.database, 0).build()
        self.assertEqual(file_hash(self.database), before)

    def test_native_many_files_share_one_exact_recording_origin_within_storage_bound(self):
        self.build()
        record, file_record = self.native_fixture()
        origin = record["proposal"]["venues"][0]["recordingLinks"][0]["recording"]
        origin["evidence"]["quote"] = "original recording evidence " * 180
        template = file_record["proposal"]["files"][0]
        template["recordingOrigin"] = origin
        file_record["proposal"]["files"] = [
            {**template, "entityKey": "native_file:bulk:" + str(index), "fileOrdinal": index}
            for index in range(5000)]
        directory = self.native_overlay([(record, file_record)])
        self.assertLess(self.overlay.stat().st_size, 32 * 1024**2)
        row = directory.search({"kind": "native_observation"})["rows"][0]
        page = directory.detail(row["uuid"], {"fileOffset": "4999", "fileLimit": "1"})
        self.assertEqual(page["filesTotal"], 5000)
        self.assertEqual(page["files"][0]["native_key"], "native_file:bulk:4999")
        self.assertEqual(json.loads(page["files"][0]["recording_origin"]), origin)
        self.assertIn("native_file%3Abulk%3A4999", page["files"][0]["url"])
        # A different original quote is a different account, even when all
        # resource fields and the provider input are identical.
        changed = json.loads(json.dumps(record))
        changed["proposal"]["venues"][0]["recordingLinks"][0]["recording"]["evidence"]["quote"] += "?"
        write_gzip(self.native_run / "venues" / "200.jsonl.gz", [changed])
        NativeVenueOverlayBuilder(self.native_run, self.overlay, 0).build()
        self.assertEqual(directory.detail(row["uuid"], {})["filesTotal"], 0)

    def test_native_uuid_anchors_and_file_pagination_do_not_expand_to_other_catalog_sources(self):
        self.build()
        record, files = self.native_fixture(anchor=uid(101))
        directory = self.native_overlay([(record, files), self.native_fixture(802, anchor=uid(101))])
        row = next(row for row in directory.search({"kind": "native_observation"})["rows"] if uid(801) in row["uuid"])
        detail = directory.detail(row["uuid"], {"fileOffset": "1", "fileLimit": "1"})
        self.assertEqual(detail["filesTotal"], 1)
        self.assertEqual(detail["files"], [])
        self.assertEqual(detail["recordingsTotal"], 1)
        native_link = urlsplit(detail["recordings"][0]["filesUrl"])
        self.assertEqual(native_link.netloc, "native")
        filters = {key: values[-1] for key, values in parse_qs(native_link.query).items()}
        self.assertEqual(filters, {"domain": "file_contents", "q": uid(801), "entityKey": "source:" + uid(101)})
        reader = ShadowIndex(self.native_run)
        reader.scan_once()
        found = reader.search(filters)
        self.assertEqual(found["total"], 1)
        self.assertEqual(found["items"][0]["input_uuid"], uid(801))
        # The coincident UUID belongs to a different namespace in the legacy DB.
        legacy = next(item for item in directory.detail(uid(1), {})["recordings"] if item["source_uuid"] == uid(101))
        self.assertEqual(urlsplit(legacy["filesUrl"]).netloc, "files")
        self.assertEqual(detail["filesUrl"], "http://native/")
        self.assertEqual(directory.detail(row["uuid"], {"artist": "Other Artist"})["filesTotal"], 0)


if __name__ == "__main__":
    unittest.main()
