"""Contract tests for the development experiment; these do not estimate extraction accuracy."""

import json
import hashlib
from types import SimpleNamespace
from unittest.mock import patch
from pathlib import Path
import tempfile
import unittest

import credit_tagger_experiment as tagger


def example(text, mentions=(), *, key="one", artist="artist-one", **answer):
    label = {
        "label_id": key,
        "artist": {"uuid": artist},
        "field_path": "/raw_record/row/taper",
        "input_duplicate_family": "duplicate-" + key,
        "known_recording_family": "family-" + key,
        "answer": {"exhaustive": True, "unresolved": False, "unknowns": [],
                   "assertions": [{"name": {"start": start, "end": end, "quote": text[start:end]}, "role": role}
                                  for start, end, role in mentions], **answer},
    }
    tokens = tagger.tokenize(text)
    return tagger.Example(label, text, tokens, tagger.token_features(tokens, "taper"))


class CreditTaggerContracts(unittest.TestCase):
    def test_jsonl_preserves_unicode_line_separators_and_original_codepoints(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "input.jsonl"
            original = "prefix\u2028name\u0085tail 😀"
            path.write_text(json.dumps({"text": original}, ensure_ascii=False) + "\n" +
                            json.dumps({"text": "next"}) + "\n", encoding="utf-8")
            rows = list(tagger.read_jsonl(path))
            self.assertEqual([row["text"] for row in rows], [original, "next"])
            start = original.index("name")
            tagger.validate_span(original, {"start": start, "end": start + 4, "quote": "name"})

    def test_predictions_cite_original_unicode_including_astral_prefix(self):
        sample = example("😀 Zoë Núñez", [(2, 11, "Taper")])
        model = tagger.BioTagger({"O": {}, "B": {"word=zoë": 5}, "I": {"word=núñez": 5}})
        predicted = tagger.predict({"Taper": model}, sample)
        self.assertEqual(predicted, [(2, 11, "Taper")])
        self.assertEqual(sample.text[predicted[0][0]:predicted[0][1]], "Zoë Núñez")

    def test_constrained_decoder_does_not_start_with_inside_label(self):
        model = tagger.BioTagger({"O": {}, "B": {}, "I": {"bias": 100}})
        tags = model.decode([("bias",), ("bias",), ("bias",)])
        self.assertNotEqual(tags[0], "I")
        self.assertTrue(all(current != "I" or previous in ("B", "I")
                            for previous, current in zip(tags, tags[1:])))

    def test_overlapping_roles_are_independent_without_changing_name_boundaries(self):
        sample = example("Alice Jones", [(0, 11, "Taper"), (0, 11, "Transferrer")])
        self.assertEqual(tagger.gold_tags(sample, "Taper"), ["B", "I"])
        self.assertEqual(tagger.gold_tags(sample, "Transferrer"), ["B", "I"])
        fixed = {"O": {}, "B": {"word=alice": 5}, "I": {"word=jones": 5}}
        self.assertEqual(tagger.predict({role: tagger.BioTagger(fixed) for role in ("Taper", "Transferrer")}, sample),
                         [(0, 11, "Taper"), (0, 11, "Transferrer")])

    def test_unknown_and_nonexhaustive_fields_are_not_negative_training_sequences(self):
        complete = example("No credit")
        unresolved = example("Maybe Alice", unresolved=True, unknowns=[{"span": {"start": 0, "end": 11}}])
        partial = example("Alice and someone", [(0, 5, "Taper")], exhaustive=False)
        self.assertEqual([item.key for item in (complete, unresolved, partial) if item.complete], ["one"])
        self.assertEqual(partial.gold, [(0, 5, "Taper")])

    def test_explicit_receipt_exclusion_masks_an_otherwise_complete_positive_field(self):
        sample = example("Alice", [(0, 5, "Taper")])
        field_hash = hashlib.sha256(sample.text.encode()).hexdigest()
        sample.label.update({"field_sha256": field_hash, "field_codepoints": 5,
                             "evidence_id": "source:one", "family_partition": "development",
                             "artist_partition": "development"})
        sample.label["answer"]["assertions"][0]["role_basis"] = {"kind": "structured_field", "field": "taper"}
        raw = {"label_id": sample.key, "field_text": sample.text, "field_sha256": field_hash,
               "field_path": sample.label["field_path"], "evidence_id": "source:one"}
        with tempfile.TemporaryDirectory() as directory:
            labels, fields = Path(directory) / "labels.jsonl", Path(directory) / "fields.jsonl"
            tagger.write_jsonl(labels, [sample.label])
            tagger.write_jsonl(fields, [raw])
            loaded = tagger.load_examples(labels, fields, [sample.key])[0]
            self.assertFalse(loaded.complete)
            self.assertEqual(loaded.gold, sample.gold)
            with self.assertRaises(ValueError):
                tagger.load_examples(labels, fields, ["not-a-labeled-field"])

    def test_failed_fitting_does_not_publish_a_completed_report(self):
        samples = [example("Alice", [(0, 5, "Taper")], key="a", artist="a"),
                   example("equipment", key="b", artist="b")]
        args = SimpleNamespace(folds=2, epochs=1, seed=173)
        with tempfile.TemporaryDirectory() as directory:
            args.output = Path(directory) / "failed-attempt"
            with patch.object(tagger, "validate_receipts", return_value=({}, {}, {})), \
                 patch.object(tagger, "load_examples", return_value=samples), \
                 patch.object(tagger, "baseline_predictions", return_value={"a": [], "b": []}), \
                 patch.object(tagger, "fit_models", side_effect=RuntimeError("interrupted")):
                args.labels = args.fields = args.baseline = None
                with self.assertRaises(RuntimeError):
                    tagger.run(args)
            self.assertTrue((args.output / "splits.json").exists())
            self.assertFalse((args.output / "report.json").exists())
            self.assertFalse((args.output / "artifacts.json").exists())

    def test_artist_family_duplicate_and_lexical_names_cannot_cross_folds(self):
        samples = [example("Alice", [(0, 5, "Taper")], key="a", artist="a"),
                   example("ALICE", [(0, 5, "Taper")], key="b", artist="b"),
                   example("one", key="c", artist="c"),
                   example("two", key="d", artist="c"),
                   example("three", key="e", artist="e"),
                   example("four", key="f", artist="f"),
                   example("five", key="g", artist="g")]
        samples[4].label["known_recording_family"] = samples[3].label["known_recording_family"]
        samples[5].label["input_duplicate_family"] = samples[4].label["input_duplicate_family"]
        folds, _ = tagger.grouped_folds(samples, 2)
        self.assertEqual(folds["a"], folds["b"])
        self.assertEqual(folds["c"], folds["d"])
        self.assertEqual(folds["d"], folds["e"])
        self.assertEqual(folds["e"], folds["f"])
        self.assertEqual(len(set(folds.values())), 2)

    def test_trained_weights_roundtrip_keeps_exact_span_predictions(self):
        positive = example("Alice Jones", [(0, 11, "Taper")])
        negative = example("Equipment only", key="negative")
        model = tagger.BioTagger().fit([positive, negative], "Taper", 12, 173)
        original = tagger.predict({"Taper": model}, positive)
        self.assertEqual(original, positive.gold)
        restored = tagger.BioTagger(json.loads(json.dumps(model.weights)))
        self.assertEqual(tagger.predict({"Taper": restored}, positive), original)
        self.assertEqual(tagger.predict({"Taper": restored}, negative), [])

    def test_parser_phrase_offsets_are_not_used_as_name_offsets(self):
        sample = example("Taped by Alice Jones", [(9, 20, "Taper")])
        sample.label["resource"] = {"resource_uuid": "source"}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "baseline.jsonl"
            tagger.write_jsonl(path, [{"kind": "source_sample", "resourceUuid": "source",
                "inputs": {"taper": sample.text}, "creditExtraction": {"credits": [
                    {"name": "Alice Jones", "role": "taper", "evidence": {"field": "taper",
                        "startCodePoint": 0, "endCodePoint": 20, "quote": sample.text}}]}}])
            proposal = tagger.baseline_predictions(path, [sample])[sample.key][0]
            self.assertEqual(proposal["name_span"], [9, 20])
            self.assertEqual(proposal["evidence"]["startCodePoint"], 0)

    def test_baseline_ambiguous_literal_name_remains_unanchored(self):
        sample = example("Alice / Alice")
        sample.label["resource"] = {"resource_uuid": "source"}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "baseline.jsonl"
            tagger.write_jsonl(path, [{"kind": "source_sample", "resourceUuid": "source",
                "inputs": {"taper": sample.text}, "creditExtraction": {"credits": [
                    {"name": "Alice", "role": "taper", "evidence": {"field": "taper",
                        "startCodePoint": 0, "endCodePoint": 13, "quote": sample.text}}]}}])
            self.assertIsNone(tagger.baseline_predictions(path, [sample])[sample.key][0]["name_span"])


if __name__ == "__main__":
    unittest.main()
