"""Exercise capture completeness and population/sample reconciliation."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

_spec = importlib.util.spec_from_file_location("scale_summary", Path(__file__).with_name("summarize.py"))
_module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_module)
summarize = _module.summarize


class TestSummary(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        sql = self.directory / "executed.sql"
        sql.write_text("select 1;\n")
        (self.directory / "query.json").write_text(json.dumps({"sql_sha256": hashlib.sha256(sql.read_bytes()).hexdigest()}))
        self.records = [{"kind": "snapshot", "read_only": "on", "in_recovery": True}]
        for artist in range(1, 4):
            self.records.append({"kind": "artist", "row": {"id": artist}, "configured_provider_mappings": [{"provider_name": "archive.org"}]})
        for artist, count in ((1, 2), (2, 1)):
            for domain in ("source", "track", "venue", "song", "setlist"):
                row = {"artist_id": artist, "rows": count}
                if domain == "track":
                    row.update(active=count, orphaned=0, unknown_orphan_state=0)
                if domain == "setlist":
                    row.update(shows=count, memberships=count)
                self.records.append({"kind": domain + "_profile", "row": row})
                for rank in range(1, count + 1):
                    sample = {"id": artist * 10 + rank, "artist_id": artist, "name": "Example", "location": "Town"}
                    self.records.append({"kind": domain + "_sample", "rank": rank, "row": sample})
        self.records.extend([
            {"kind": "totals", "rows": {"artists": 3, "sources": 3, "tracks": 3, "venues": 3, "songs": 3, "setlist_shows": 3, "setlist_memberships": 3}},
            {"kind": "snapshot_complete", "completed_at": "2026-10-17T00:00:00Z"},
        ])

    def run_summary(self):
        (self.directory / "snapshot.jsonl").write_text("".join(json.dumps(record) + "\n" for record in self.records))
        return summarize(self.directory)

    def test_complete_capture_reconciles_samples_and_absent_artists(self):
        report = self.run_summary()
        for domain in report["coverage"].values():
            self.assertEqual(3, domain["sampled_rows"])
            self.assertEqual(2, domain["sampled_artists"])
            self.assertEqual(1, domain["artists_without_eligible_rows"])
            self.assertEqual(0, domain["missing_eligible_artists"])

    def test_missing_artist_sample_is_rejected(self):
        self.records = [r for r in self.records if not (r["kind"] == "source_sample" and r["row"]["artist_id"] == 2)]
        with self.assertRaisesRegex(ValueError, "Incomplete source"):
            self.run_summary()

    def test_wrong_full_population_total_is_rejected(self):
        self.records[-2]["rows"]["tracks"] += 1
        with self.assertRaisesRegex(ValueError, "population does not reconcile"):
            self.run_summary()

    def test_duplicate_sample_cannot_replace_missing_data(self):
        sample = next(r for r in self.records if r["kind"] == "venue_sample")
        self.records.insert(-1, sample)
        with self.assertRaisesRegex(ValueError, "Duplicate sampled"):
            self.run_summary()

    def test_missing_completion_is_rejected(self):
        self.records.pop()
        with self.assertRaisesRegex(ValueError, "complete snapshot"):
            self.run_summary()

    def test_writable_endpoint_is_rejected(self):
        self.records[0]["read_only"] = "off"
        with self.assertRaisesRegex(ValueError, "read-only replica"):
            self.run_summary()

    def test_changed_executed_query_is_rejected(self):
        (self.directory / "executed.sql").write_text("select 2;\n")
        with self.assertRaisesRegex(ValueError, "SQL does not match"):
            self.run_summary()

    def test_empty_capture_is_rejected(self):
        self.records = []
        with self.assertRaisesRegex(ValueError, "complete snapshot"):
            self.run_summary()

    def test_unattributed_population_cannot_count_as_artist_coverage(self):
        row = next(r["row"] for r in self.records if r["kind"] == "song_profile")
        row["artist_id"] = None
        with self.assertRaisesRegex(ValueError, "Unattributed song"):
            self.run_summary()

    def test_repeated_provider_mapping_counts_an_artist_once(self):
        for record in self.records:
            if record["kind"] == "artist":
                record["configured_provider_mappings"] *= 2
        report = self.run_summary()
        self.assertEqual(3, report["coverage"]["source"]["configured_provider_coverage"]["archive.org"]["configured_artists"])

    def test_membership_total_is_independently_reconciled(self):
        self.records[-2]["rows"]["setlist_memberships"] += 1
        with self.assertRaisesRegex(ValueError, "membership population"):
            self.run_summary()


if __name__ == "__main__":
    unittest.main()
