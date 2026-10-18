"""Synthetic behavior checks, not an estimate of credit extraction accuracy."""

import json
from pathlib import Path
import subprocess
import sys
import unittest

import credit_tagger_experiment as tagger


def sample(text, names=(), *, field="description", key="synthetic"):
    assertions = []
    for name, role in names:
        start = text.index(name)
        assertions.append({"name": {"start": start, "end": start + len(name), "quote": name},
                           "role": role, "role_basis": {"kind": "prose", "secret": "basis-secret"}})
    label = {"label_id": key, "field_path": "/raw/" + field,
             "artist": {"uuid": "artist-secret"},
             "answer": {"exhaustive": True, "unresolved": False, "unknowns": [],
                        "assertions": assertions}}
    tokens = tagger.tokenize(text)
    return tagger.Example(label, text, tokens, tagger.token_features(tokens, field))


def training():
    return [sample("Recorded by Zoë Núñez", [("Zoë Núñez", "Taper"), ("Zoë Núñez", "Transferrer")]),
            sample("Recorded by Alice Jones", [("Alice Jones", "Taper"), ("Alice Jones", "Transferrer")]),
            sample("No recording credit here"), sample("Equipment and notes only")]


class LogisticCreditModelTests(unittest.TestCase):
    def test_fit_vocabulary_uses_only_training_raw_text_and_field(self):
        rows = training()
        model = tagger.LogisticBioTagger().fit(rows, "Taper")
        held_out = sample("Unseenquartz", key="held-out-secret")
        model.decode(held_out.features)
        vocabulary = set(model.to_json()["vocabulary"])
        self.assertIn("word=zoë", vocabulary)
        self.assertNotIn("word=unseenquartz", vocabulary)
        for forbidden in ("synthetic", "artist-secret", "basis-secret", "held-out-secret"):
            self.assertFalse(any(forbidden in feature for feature in vocabulary))

    def test_overlapping_role_heads_preserve_original_unicode_spans(self):
        models = tagger.fit_logistic_models(training(), ["Taper", "Transferrer"])
        original = sample("😀 Recorded by Zoë Núñez")
        start = original.text.index("Zoë")
        expected = [(start, len(original.text), "Taper"), (start, len(original.text), "Transferrer")]
        self.assertEqual(tagger.predict(models, original), expected)
        for first, last, _ in expected:
            self.assertEqual(original.text[first:last], "Zoë Núñez")

    def test_decoding_prevents_inside_tags_at_start_or_after_outside(self):
        model = tagger.LogisticBioTagger().fit(training(), "Taper")
        artifact = model.to_json()
        artifact["weights"] = {tag: {} for tag in artifact["classes"]}
        artifact["intercepts"] = {tag: 100.0 if tag == "I" else 0.0 for tag in artifact["classes"]}
        restored = tagger.LogisticBioTagger.from_json(artifact)
        tags = restored.decode([(), (), ()])
        self.assertEqual(tags, ["B", "I", "I"])
        artifact["weights"]["O"] = {"word=recorded": 1000.0}
        after_outside = tagger.LogisticBioTagger.from_json(artifact).decode(
            [(), ("word=recorded",), (), ()])
        self.assertEqual(after_outside, ["O", "O", "B", "I"])
        self.assertNotEqual(tags[0], "I")
        self.assertTrue(all(current != "I" or previous in ("B", "I")
                            for previous, current in zip(tags, tags[1:])))

    def test_no_positive_role_and_no_tokens_are_explicit_constant_outside(self):
        model = tagger.LogisticBioTagger().fit(training(), "Uploader")
        self.assertEqual(model.decode(sample("Someone New").features), ["O", "O"])
        self.assertEqual(model.to_json()["metadata"]["training_state"], "constant_class")
        self.assertEqual(model.to_json()["classes"], ["O"])
        empty = tagger.LogisticBioTagger().fit([sample("")], "Taper")
        self.assertEqual(empty.decode([]), [])
        self.assertEqual(empty.decode(sample("Unknown").features), ["O"])
        self.assertEqual(empty.to_json()["metadata"]["training_state"], "no_tokens")

    def test_single_positive_class_round_trips_and_remains_a_begin_tag(self):
        model = tagger.LogisticBioTagger().fit([sample("Zoë", [("Zoë", "Taper")])], "Taper")
        restored = tagger.LogisticBioTagger.from_json(json.loads(json.dumps(model.to_json())))
        self.assertEqual(restored.decode(sample("Alice Bob").features), ["B", "B"])

    def test_json_roundtrip_reproduces_model_and_predictions(self):
        model = tagger.LogisticBioTagger().fit(training(), "Taper", seed=173)
        artifact = json.loads(json.dumps(model.to_json()))
        restored = tagger.LogisticBioTagger.from_json(artifact)
        self.assertEqual(restored.to_json(), artifact)
        for original in training() + [sample("Unknown person and 😀 notes")]:
            self.assertEqual(restored.decode(original.features), model.decode(original.features))
        repeat = tagger.LogisticBioTagger().fit(training(), "Taper", seed=173)
        self.assertEqual(repeat.to_json(), artifact)

    def test_restored_inference_does_not_import_sklearn_or_numpy(self):
        original = training()[0]
        model = tagger.LogisticBioTagger().fit(training(), "Taper")
        script = """import json, sys
import credit_tagger_experiment as module
payload = json.load(sys.stdin)
model = module.LogisticBioTagger.from_json(payload["model"])
print(json.dumps(model.decode(payload["features"])))
if any(name.split(".")[0] in ("sklearn", "numpy") for name in sys.modules):
    raise RuntimeError("Restored inference imported a fitting dependency")
"""
        result = subprocess.run([sys.executable, "-S", "-c", script],
                                input=json.dumps({"model": model.to_json(), "features": original.features}),
                                text=True, capture_output=True, check=True,
                                cwd=Path(__file__).parent)
        self.assertEqual(json.loads(result.stdout), model.decode(original.features))

    def test_binary_emissions_roundtrip_and_predict_names(self):
        rows = [sample("Alice", [("Alice", "Taper")]), sample("notes")]
        model = tagger.LogisticBioTagger().fit(rows, "Taper")
        self.assertEqual(tagger.predict({"Taper": model}, rows[0]), [(0, 5, "Taper")])
        self.assertEqual(tagger.predict({"Taper": model}, rows[1]), [])
        restored = tagger.LogisticBioTagger.from_json(json.loads(json.dumps(model.to_json())))
        self.assertEqual(restored.decode(rows[0].features), model.decode(rows[0].features))

    def test_unknowns_and_injected_nonraw_features_cannot_be_training_negatives(self):
        unresolved = sample("Maybe someone")
        unresolved.label["answer"]["unresolved"] = True
        with self.assertRaisesRegex(ValueError, "complete resolved"):
            tagger.LogisticBioTagger().fit([unresolved], "Taper")
        injected = sample("Alice")
        injected.features[0] += ("label_id=secret",)
        with self.assertRaisesRegex(ValueError, "original text and field"):
            tagger.LogisticBioTagger().fit([injected], "Taper")

    def test_nonconverged_fit_fails_without_a_publishable_model(self):
        model = tagger.LogisticBioTagger()
        with self.assertRaisesRegex(RuntimeError, "did not converge"):
            model.fit(training(), "Taper", max_iterations=1)
        with self.assertRaisesRegex(ValueError, "not been fitted"):
            model.to_json()

    def test_json_rejects_unsupported_versions_and_nonfinite_weights(self):
        artifact = tagger.LogisticBioTagger().fit(training(), "Taper").to_json()
        artifact["version"] = "future-model"
        with self.assertRaisesRegex(ValueError, "Unsupported"):
            tagger.LogisticBioTagger.from_json(artifact)
        artifact["version"] = tagger.LogisticBioTagger.VERSION
        artifact["intercepts"]["O"] = float("nan")
        with self.assertRaisesRegex(ValueError, "emission weights"):
            tagger.LogisticBioTagger.from_json(artifact)


if __name__ == "__main__":
    unittest.main()
