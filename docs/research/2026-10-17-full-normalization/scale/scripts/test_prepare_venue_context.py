"""Exercise eligibility, identity/provenance and SQL parameter boundaries offline."""

import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid


SPEC = importlib.util.spec_from_file_location(
    "prepare_venue_context", Path(__file__).with_name("prepare_venue_context.py")
)
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)


def identity(value):
    return str(uuid.UUID(int=value))


def frame():
    evidence, originals, sources = [], [], []
    for index in range(67):
        artist_id = index % 66 + 1
        artist = {"uuid": identity(5000 + artist_id), "name": "Artist " + str(artist_id)}
        venue_uuid = identity(1000 + index)
        row = {
            "id": 2000 + index, "uuid": venue_uuid, "artist_id": artist_id,
            "name": "O'Brien \u2028 room" if index == 0 else "Venue " + str(index),
            "location": None, "past_names": "", "latitude": None, "longitude": None,
            "slug": "venue", "upstream_identifier": None,
        }
        common = {
            "evidence_id": "venue_sample:" + venue_uuid,
            "artist_partition": "development", "family_partition": "development",
            "artist": artist,
        }
        evidence.append({**common, "record_uuid": venue_uuid, "resource": {
            "provider": "relisten_catalog_snapshot", "resource_type": "venue",
            "resource_uuid": venue_uuid,
        }})
        originals.append({**common, "raw_record": {"kind": "venue_sample", "row": row}})
    for index in range(51):
        artist_id = index % 44 + 1
        sources.append({
            "evidence_id": "source_sample:" + identity(10000 + index),
            "reference": "source:" + str(20000 + index),
            "artist_uuid": identity(5000 + artist_id),
            "artist_partition": "development", "family_partition": "development",
            "family": f"artist:{artist_id}:show:{30000 + index}",
            "family_group": "frozen-group-" + str(index), "source_line": 40000 + index,
        })
    reservation = {
        "metadata_matches": [], "excluded_annotation_metadata": [],
        "reserved_references": [], "reserved_known_families": [],
        "reserved_annotation_family_groups": [], "eligible_source_metadata": sources,
    }
    return evidence, originals, reservation


class TestVenueContextPreparation(unittest.TestCase):
    def setUp(self):
        self.evidence, self.originals, self.reservation = frame()

    def select(self):
        metadata = PREPARE.eligible_metadata(self.evidence, self.reservation)
        return PREPARE.build_allowlists(self.evidence, self.originals, metadata, self.reservation)

    def test_frozen_intersection_keeps_exact_resource_and_show_keys(self):
        venues, sources = self.select()
        self.assertEqual((67, 51), (len(venues), len(sources)))
        source = next(row for row in sources if row["source_id"] == 20000)
        self.assertEqual(identity(10000), source["source_uuid"])
        self.assertEqual(30000, source["expected_show_id"])
        self.assertEqual(identity(5001), source["artist_uuid"])
        self.assertEqual("O'Brien \u2028 room", venues[0]["expected_metadata"]["name"])

    def test_reserved_source_group_cannot_enter_the_intersection(self):
        self.reservation["reserved_annotation_family_groups"] = ["frozen-group-1"]
        with self.assertRaisesRegex(ValueError, "Reserved source"):
            self.select()

    def test_held_out_artist_or_family_cannot_supply_dates(self):
        for field in ("artist_partition", "family_partition"):
            with self.subTest(field=field):
                evidence = copy.deepcopy(self.evidence)
                evidence[0][field] = "validation"
                with self.assertRaisesRegex(ValueError, "strict development"):
                    PREPARE.eligible_metadata(evidence, self.reservation)

    def test_reserved_venue_uuid_is_rejected_before_original_fields_are_needed(self):
        self.reservation["metadata_matches"] = [{"kind": "venue", "uuid": identity(1000)}]
        with self.assertRaisesRegex(ValueError, "Reserved venue UUID"):
            PREPARE.eligible_metadata(self.evidence, self.reservation)

    def test_source_outside_the_frozen_intersection_is_not_assigned_development(self):
        self.reservation["eligible_source_metadata"].pop()
        with self.assertRaisesRegex(ValueError, "intersection changed"):
            self.select()

    def test_artist_id_drift_and_reused_venue_id_are_rejected(self):
        sources = PREPARE.eligible_metadata(self.evidence, self.reservation)
        changed = copy.deepcopy(self.originals)
        changed[0]["raw_record"]["row"]["artist_id"] = 99
        with self.assertRaisesRegex(ValueError, "artist UUID maps to different IDs"):
            PREPARE.build_allowlists(self.evidence, changed, sources, self.reservation)
        changed = copy.deepcopy(self.originals)
        changed[1]["raw_record"]["row"]["id"] = 2000
        with self.assertRaisesRegex(ValueError, "Duplicate original venue ID"):
            PREPARE.build_allowlists(self.evidence, changed, sources, self.reservation)

    def test_source_family_artist_must_match_its_frozen_uuid(self):
        sources = PREPARE.eligible_metadata(self.evidence, self.reservation)
        sources[0] = {**sources[0], "family": "artist:999:show:30000"}
        with self.assertRaisesRegex(ValueError, "artist ID/UUID mismatch"):
            PREPARE.build_allowlists(self.evidence, self.originals, sources, self.reservation)

    def test_discovery_parameters_omit_all_wording_and_geography(self):
        venues, sources = self.select()
        strict, discovery = PREPARE.render_queries(
            "__VENUE_ALLOWLIST__\n__SOURCE_ALLOWLIST__", "__VENUE_ALLOWLIST__", venues, sources
        )
        # Decode SQL string parameters rather than infer safety from substring
        # searches. Apostrophes and Unicode survive the exact JSON round trip.
        decode_literal = lambda text: json.loads(text[1:-1].replace("''", "'"))
        strict_venues = decode_literal(strict.split("\n")[0])
        discovery_venues = decode_literal(discovery)
        self.assertEqual(venues, strict_venues)
        self.assertEqual(67, len(discovery_venues))
        for row in discovery_venues:
            self.assertEqual(
                {"evidence_id", "venue_id", "venue_uuid", "artist_id", "artist_uuid"}, set(row)
            )

    def test_stamped_input_cannot_be_freshened_before_parsing(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "metadata.json"
            path.write_bytes(b"original")
            inputs = PREPARE.PreparationInputs()
            inputs.stamp(path)
            path.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "before parsing"):
                inputs.read(path)

    def test_read_bytes_and_late_source_mutation_keep_the_original_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "metadata.json"
            path.write_bytes(b"original")
            inputs = PREPARE.PreparationInputs()
            self.assertEqual(b"original", inputs.read(path))
            path.write_bytes(b"changed")
            self.assertEqual(b"original", inputs.read(path))
            with self.assertRaisesRegex(ValueError, "changed during preparation"):
                inputs.require_unchanged()

    def test_publication_failure_preserves_prefix_and_recovery_needs_new_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "metadata"
            source.write_bytes(b"original")
            inputs = PREPARE.PreparationInputs()
            inputs.read(source)
            source.write_bytes(b"changed")
            output = root / "failed"
            with self.assertRaisesRegex(ValueError, "changed during preparation"):
                PREPARE.publish_preparation(output, {"selection.json": b"[]\n"}, inputs,
                                            {"state": "completed"})
            self.assertEqual(b"[]\n", (output / "selection.json").read_bytes())
            self.assertFalse((output / "receipt.json").exists())
            self.assertEqual("failed", json.loads((output / "failure.json").read_text())["state"])
            with self.assertRaises(FileExistsError):
                PREPARE.publish_preparation(output, {}, inputs, {"state": "completed"})

            fresh = PREPARE.PreparationInputs()
            fresh.read(source)
            result = PREPARE.publish_preparation(root / "recovered", {"selection.json": b"[]\n"},
                                                 fresh, {"state": "completed"})
            self.assertEqual("completed", result["state"])

    def test_signal_after_atomic_publication_cannot_create_failure_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "new"
            link = PREPARE.os.link
            def interrupted_after_link(source, target):
                link(source, target)
                raise KeyboardInterrupt()
            with patch.object(PREPARE.os, "link", side_effect=interrupted_after_link):
                result = PREPARE.publish_preparation(output, {"selection.json": b"[]\n"},
                                                     PREPARE.PreparationInputs(), {"state": "completed"})
            self.assertEqual("completed", result["state"])
            self.assertTrue((output / "receipt.json").exists())
            self.assertFalse((output / "failure.json").exists())

    def test_signal_before_publication_keeps_only_failed_prefix(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "new"
            with patch.object(PREPARE.os, "link", side_effect=KeyboardInterrupt()):
                with self.assertRaises(KeyboardInterrupt):
                    PREPARE.publish_preparation(output, {"selection.json": b"[]\n"},
                                                PREPARE.PreparationInputs(), {"state": "completed"})
            self.assertFalse((output / "receipt.json").exists())
            self.assertTrue((output / "failure.json").exists())


if __name__ == "__main__":
    unittest.main()
