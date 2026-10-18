"""Public export contracts, using synthetic originals and a local validator stub.

The frozen validator has its own provenance tests. These cases protect export
ownership, exact Unicode fields, mutation detection and incomplete-run behavior.
"""

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("export_actor_fields", HERE / "export_fields.py")
exporter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(exporter)


def write_jsonl(path, rows):
    with path.open("wb") as stream:
        for row in rows:
            stream.write((json.dumps(row, ensure_ascii=False) + "\n").encode("utf-8"))


class ExportFieldsTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.root = self.directory / "primary"
        self.bundle = self.root / "bundle"
        self.bundle.mkdir(parents=True)
        self.output = self.directory / "output"
        self.originals_path = self.root / "originals.jsonl"
        self.fields_path = self.bundle / "field-index.jsonl"
        self.originals = [
            {"original_record": {"evidence_id": f"source:{i}", "raw_record": {"row": {}}}}
            for i in range(207)
        ]
        self.fields = []
        for i in range(543):
            source = self.originals[i % 207]["original_record"]
            field = ("taper", "transferrer", "description")[i // 207]
            text = f"Original {i}: <b>wording</b>\u2028next\u2029line 🎶\nraw newline"
            source["raw_record"]["row"][field] = text
            self.fields.append({
                "task_id": f"actor-field:{i}",
                "evidence_id": source["evidence_id"],
                "field": field,
                "field_path": f"/raw_record/row/{field}",
                "field_sha256": hashlib.sha256(text.encode()).hexdigest(),
                "code_points": len(text),
            })
        self.manifest = {"files": {}, "independent_review": {}, "contract_snapshots": []}
        # The real validator is loaded only by the explicit real export command.
        # Its stub can fail without ever decoding synthetic source payloads.
        (self.bundle / "validate_bundle.py").write_text("# synthetic validator\n")
        for name in ("labels.jsonl", "citations.jsonl"):
            (self.bundle / name).write_text("[]\n")
            self.manifest["files"][name] = exporter.fingerprint(self.bundle / name)
        for key in (
            "private_values", "annotation_aid", "frozen_discovery_inventory",
            "frozen_discovery_receipt", "reservation_index",
        ):
            self.manifest[key] = self.support_file(key)
        for key in ("selection_receipt", "first_answers", "first_answer_receipt", "comparison_script"):
            self.manifest["independent_review"][key] = self.support_file(key)
        self.packet_path = self.root / "blind-packet.jsonl"
        self.packet_path.write_bytes(b"frozen blind packet\n")
        selection = self.root / "selection_receipt"
        selection.write_text(json.dumps({
            "input": {"path": self.packet_path.name, **exporter.fingerprint(self.packet_path)}
        }))
        self.manifest["independent_review"]["selection_receipt"] = {
            "path": selection.name, **exporter.fingerprint(selection)
        }
        annotation = self.root / "annotation.jsonl"
        annotation.write_bytes(b"\xffunselected raw text is never decoded by the exporter\n")
        self.manifest["source_annotation_evidence_sha256"] = exporter.fingerprint(annotation)["sha256"]
        self.refresh_sources()
        self.validator = SimpleNamespace(
            ANNOTATION=Path("annotation.jsonl"),
            validate_bundle=Mock(return_value={"tasks": 543, "source_records": 207}),
        )
        loader = patch.object(exporter, "load_validator", return_value=self.validator)
        loader.start()
        self.addCleanup(loader.stop)

    def support_file(self, name):
        path = self.root / name
        path.write_bytes(b"frozen support receipt\n")
        return {"path": name, **exporter.fingerprint(path)}

    def refresh_sources(self):
        write_jsonl(self.originals_path, self.originals)
        write_jsonl(self.fields_path, self.fields)
        self.manifest["original_sources"] = {
            "path": self.originals_path.name, **exporter.fingerprint(self.originals_path)
        }
        self.manifest["files"]["field-index.jsonl"] = exporter.fingerprint(self.fields_path)
        (self.bundle / "manifest.json").write_text(json.dumps(self.manifest))

    def run_export(self, output=None):
        return exporter.export_fields(self.bundle, self.root, output or self.output)

    def read_output(self):
        with (self.output / "fields.jsonl").open("rb") as stream:
            return [json.loads(line) for line in stream]

    def assert_failed_output(self, error_type):
        self.assertFalse((self.output / "receipt.json").exists())
        failure = json.loads((self.output / "failure.json").read_bytes())
        self.assertEqual(failure["state"], "failed")
        self.assertEqual(failure["error_type"], error_type)

    def test_export_preserves_whole_raw_unicode_fields_without_answers(self):
        result = self.run_export()
        rows = self.read_output()
        self.assertEqual(len(rows), 543)
        self.assertEqual((self.output / "fields.jsonl").read_bytes().count(b"\n"), 543)
        for expected, actual in zip(self.fields, rows):
            self.assertEqual(set(actual), {
                "task_id", "evidence_id", "field", "field_path", "field_sha256", "field_text"
            })
            self.assertEqual(actual["task_id"], expected["task_id"])
            source = self.originals[int(expected["evidence_id"].split(":")[1])]["original_record"]
            self.assertEqual(actual["field_text"], source["raw_record"]["row"][expected["field"]])
            self.assertEqual(hashlib.sha256(actual["field_text"].encode()).hexdigest(), actual["field_sha256"])
        self.assertEqual(result, json.loads((self.output / "receipt.json").read_bytes()))
        self.assertEqual(result["fields_file"], {
            "name": "fields.jsonl", **exporter.fingerprint(self.output / "fields.jsonl")
        })
        self.assertEqual(result["frozen_bundle"]["manifest"], exporter.fingerprint(self.bundle / "manifest.json"))
        self.validator.validate_bundle.assert_called_once_with(self.bundle, self.root)

    def test_failed_validation_never_loads_copied_originals_or_creates_output(self):
        self.validator.validate_bundle.side_effect = ValueError("Ineligible development selection")
        with patch.object(exporter.FrozenActorFields, "read_rows", side_effect=AssertionError("raw decode")):
            with self.assertRaisesRegex(ValueError, "Ineligible"):
                self.run_export()
        self.assertFalse(self.output.exists())

    def test_existing_output_is_preserved_before_validation(self):
        self.run_export()
        before = {p.name: p.read_bytes() for p in self.output.iterdir()}
        self.validator.validate_bundle.reset_mock()
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.run_export()
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.output.iterdir()})
        self.validator.validate_bundle.assert_not_called()

    def test_tampered_copied_input_fails_before_decoding(self):
        with self.originals_path.open("ab") as stream:
            stream.write(b"tampered\n")
        with patch.object(exporter.FrozenActorFields, "read_rows", side_effect=AssertionError("raw decode")):
            with self.assertRaisesRegex(ValueError, "Frozen input changed"):
                self.run_export()
        self.assertFalse(self.output.exists())

    def test_input_changed_during_validation_cannot_be_exported(self):
        def change_source(*args):
            with self.originals_path.open("ab") as stream:
                stream.write(b"changed after validation\n")
            return {"tasks": 543, "source_records": 207}
        self.validator.validate_bundle.side_effect = change_source
        with self.assertRaisesRegex(ValueError, "Frozen input changed"):
            self.run_export()
        self.assertFalse(self.output.exists())

    def test_missing_or_duplicate_field_is_not_a_truncated_success(self):
        for mode in ("missing", "duplicate"):
            with self.subTest(mode=mode):
                original = copy.deepcopy(self.fields)
                if mode == "missing":
                    self.fields.pop()
                else:
                    self.fields[-1] = copy.deepcopy(self.fields[0])
                self.refresh_sources()
                output = self.directory / mode
                with self.assertRaisesRegex(ValueError, "denominator|Duplicate"):
                    self.run_export(output)
                self.assertFalse((output / "receipt.json").exists())
                self.assertEqual(json.loads((output / "failure.json").read_bytes())["state"], "failed")
                self.fields = original

    def test_changed_field_hash_is_rejected_even_with_updated_copy_fingerprint(self):
        source = self.originals[0]["original_record"]["raw_record"]["row"]
        source["taper"] = "different original value"
        self.refresh_sources()
        with self.assertRaisesRegex(ValueError, "field hash"):
            self.run_export()
        self.assert_failed_output("ValueError")

    def test_cancellation_retains_prefix_without_completed_receipt(self):
        original = exporter.FrozenActorFields.field_rows
        def cancelled(frozen):
            yield next(original(frozen))
            raise KeyboardInterrupt()
        with patch.object(exporter.FrozenActorFields, "field_rows", cancelled):
            with self.assertRaises(KeyboardInterrupt):
                self.run_export()
        self.assertEqual(len(self.read_output()), 1)
        self.assert_failed_output("KeyboardInterrupt")
        self.run_export(self.directory / "recovery")

    def test_late_source_mutation_retains_fields_but_cannot_publish_completion(self):
        original_bytes = self.originals_path.read_bytes()
        original = exporter.FrozenActorFields.write_fields
        def changed(frozen, path):
            kinds = original(frozen, path)
            self.originals_path.write_bytes(original_bytes + b" ")
            return kinds
        with patch.object(exporter.FrozenActorFields, "write_fields", changed):
            with self.assertRaisesRegex(ValueError, "Frozen input changed"):
                self.run_export()
        self.assertEqual(len(self.read_output()), 543)
        self.assert_failed_output("ValueError")
        retained = (self.output / "fields.jsonl").read_bytes()
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.run_export()
        self.assertEqual(retained, (self.output / "fields.jsonl").read_bytes())
        self.originals_path.write_bytes(original_bytes)
        self.run_export(self.directory / "recovery")

    def test_failed_completion_publication_has_no_success_receipt(self):
        with patch.object(exporter.os, "link", side_effect=OSError("disk failure")):
            with self.assertRaisesRegex(OSError, "disk failure"):
                self.run_export()
        self.assertEqual(len(self.read_output()), 543)
        self.assert_failed_output("OSError")
        self.assertTrue((self.output / "receipt.pending.json").exists())

    def test_mutation_of_validator_blind_packet_blocks_completion(self):
        original = exporter.FrozenActorFields.write_fields
        def change_packet(frozen, path):
            kinds = original(frozen, path)
            self.packet_path.write_bytes(b"changed blind packet\n")
            return kinds
        with patch.object(exporter.FrozenActorFields, "write_fields", change_packet):
            with self.assertRaisesRegex(ValueError, "Frozen input changed: blind-packet"):
                self.run_export()
        self.assertEqual(len(self.read_output()), 543)
        self.assert_failed_output("ValueError")

    def test_interruption_after_atomic_publication_has_one_completed_outcome(self):
        original = exporter.publish_receipt
        def publish_then_interrupt(output, receipt):
            original(output, receipt)
            raise KeyboardInterrupt()
        with patch.object(exporter, "publish_receipt", publish_then_interrupt):
            result = self.run_export()
        self.assertEqual(result["state"], "completed")
        self.assertEqual(result, json.loads((self.output / "receipt.json").read_bytes()))
        self.assertFalse((self.output / "failure.json").exists())


if __name__ == "__main__":
    unittest.main()
