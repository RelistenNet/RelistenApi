"""Synthetic control contracts; root runs these from the repository root."""

import json
import hashlib
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import people_identity_controls as controls


class BaseHelpers(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Import only pinned source definitions; never call DevelopmentSources.load.
        cls.base = controls.load_base(Path.cwd())


class TestPinnedImport(unittest.TestCase):
    def test_import_executes_authenticated_source_without_loader_bytecode(self):
        source = b"MARKER = 'authenticated-source'\n"
        manifest = b'synthetic manifest\n'
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            directory = root / controls.BASE_TOOL
            directory.mkdir(parents=True)
            (directory / 'people_identity_packet.py').write_bytes(source)
            (directory / 'tool-manifest.json').write_bytes(manifest)
            with patch.object(controls, 'BASE_CODE_SHA256', hashlib.sha256(source).hexdigest()):
                with patch.object(controls, 'BASE_MANIFEST_SHA256', hashlib.sha256(manifest).hexdigest()):
                    with patch('importlib.machinery.SourceFileLoader.exec_module', side_effect=AssertionError('cached loader must not execute')):
                        module = controls.load_base(root)
            self.assertEqual(module.MARKER, 'authenticated-source')

    def test_changed_base_code_is_rejected_before_any_import(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            directory = root / controls.BASE_TOOL
            directory.mkdir(parents=True)
            (directory / 'people_identity_packet.py').write_bytes(b'raise RuntimeError("must not execute")\n')
            with patch.object(controls.importlib.util, 'spec_from_file_location') as import_source:
                with self.assertRaisesRegex(ValueError, 'Pinned base tool changed: people_identity_packet.py'):
                    controls.load_base(root)
                import_source.assert_not_called()

    def test_changed_base_manifest_is_rejected_before_import(self):
        source = Path.cwd() / controls.BASE_TOOL / 'people_identity_packet.py'
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            directory = root / controls.BASE_TOOL
            directory.mkdir(parents=True)
            (directory / 'people_identity_packet.py').write_bytes(source.read_bytes())
            (directory / 'tool-manifest.json').write_bytes(b'changed manifest\n')
            with patch.object(controls.importlib.util, 'spec_from_file_location') as import_source:
                with self.assertRaisesRegex(ValueError, 'Pinned base tool changed: tool-manifest.json'):
                    controls.load_base(root)
                import_source.assert_not_called()


def actors_fixture():
    def actor(name, eid, artist, group, start):
        return {
            'evidence_id': eid, 'reference': eid, 'artist_uuid': artist,
            'recording_duplicate_group': group, 'known_recording_family': group,
            'actor_kind': None, 'actor_identity': None,
            'reported_actor_anchor': {
                'evidence_id': eid, 'field_path': '/raw_record/row/taper',
                'field_sha256': 'synthetic-field-hash',
                'span': {'start': start, 'end': start + len(name), 'quote': name,
                         'unit': 'unicode_code_points'},
            },
        }
    return {
        'co_a': actor('Alex Example', 'source:1', 'artist-1', 'family-1', 0),
        'co_b': actor('Blair Example', 'source:1', 'artist-1', 'family-1', 15),
        'other_a': actor('Alex Example', 'source:2', 'artist-2', 'family-2', 0),
    }


class TestUnansweredQuestionScopes(BaseHelpers):
    def test_three_questions_compare_the_intended_direct_pairs(self):
        actors = actors_fixture()
        questions = controls.make_questions(self.base, actors, [], 'synthetic-control')
        self.assertEqual([q['question_kind'] for q in questions], [
            'local_co_credit_distinctness', 'cross_source_linkage', 'cross_source_contrast',
        ])
        expected = [('co_a', 'co_b'), ('co_a', 'other_a'), ('other_a', 'co_b')]
        for question, (left, right) in zip(questions, expected, strict=True):
            self.assertEqual(question['left'], actors[left])
            self.assertEqual(question['right'], actors[right])
            self.assertIsNone(question['answer'])
            self.assertIsNone(question['adjudication'])
            self.assertTrue(question['other_answers_are_not_premises'])
            self.assertFalse(question['exposure']['eligible_for_release_evaluation'])
            self.assertNotIn('expected_decision', question)
            self.assertNotIn('depends_on', question)
        self.assertEqual(len({q['question_id'] for q in questions}), 3)
        self.assertEqual(questions, controls.make_questions(self.base, actors, [], 'synthetic-control'))

    def test_local_question_keeps_one_source_and_does_not_claim_cross_source_scope(self):
        question = controls.make_questions(self.base, actors_fixture(), [], 'control')[0]
        self.assertEqual(question['left']['evidence_id'], question['right']['evidence_id'])
        self.assertEqual(question['left']['recording_duplicate_group'], question['right']['recording_duplicate_group'])
        self.assertEqual(set(question['allowed_decisions']), {
            'same_entity', 'different_entities', 'insufficient_evidence',
        })

    def test_duplicate_family_cannot_pose_as_cross_source_independence(self):
        actors = actors_fixture()
        actors['other_a']['recording_duplicate_group'] = 'family-1'
        with self.assertRaisesRegex(self.base.ContractError, 'repeated recording/duplicate group'):
            controls.make_questions(self.base, actors, [], 'control')

    def test_cross_source_question_requires_different_source_and_artist(self):
        for field, same in [('evidence_id', 'source:1'), ('artist_uuid', 'artist-1')]:
            with self.subTest(field=field):
                actors = actors_fixture()
                actors['other_a'][field] = same
                with self.assertRaisesRegex(self.base.ContractError, 'cross-source question scope'):
                    controls.make_questions(self.base, actors, [], 'control')

    def test_same_name_in_co_credit_does_not_prefill_a_label(self):
        actors = actors_fixture()
        span = actors['co_b']['reported_actor_anchor']['span']
        span.update(quote='Alex Example', end=span['start'] + len('Alex Example'))
        questions = controls.make_questions(self.base, actors, [], 'ambiguous-control')
        self.assertTrue(all(q['answer'] is None for q in questions))
        self.assertTrue(all(q['left']['actor_kind'] is None and q['right']['actor_kind'] is None for q in questions))


class TestExactControlCitations(BaseHelpers):
    def test_unicode_name_anchor_and_field_hash_are_checked_by_pinned_helper(self):
        text = 'A😀 B'
        eid = 'source:1'
        original = {'metadata': {'evidence_id': eid},
                    'original_record': {'raw_record': {'row': {'taper': text}}}}
        sources = SimpleNamespace(originals={eid: original})
        spec = {'synthetic': {'fields': {'taper': self.base.digest(text.encode())}}}
        with patch.dict(controls.SOURCE_SPECS, spec):
            citation = controls.source_citation(self.base, sources, {'synthetic': original},
                                                ('synthetic', 'taper', 1, 2, '😀'))
            self.assertEqual(citation['span']['quote'], '😀')
            with self.assertRaisesRegex(self.base.ContractError, 'exact reported name/alias anchor'):
                controls.source_citation(self.base, sources, {'synthetic': original},
                                         ('synthetic', 'taper', 1, 2, 'B'))
            with self.assertRaisesRegex(self.base.ContractError, 'Unicode bounds'):
                controls.source_citation(self.base, sources, {'synthetic': original},
                                         ('synthetic', 'taper', 1, 99, None))
            spec['synthetic']['fields']['taper'] = 'changed-hash'
            with self.assertRaisesRegex(self.base.ContractError, 'field hash'):
                controls.source_citation(self.base, sources, {'synthetic': original},
                                         ('synthetic', 'taper', 1, 2, None))


class TestFrozenControls(BaseHelpers):
    def test_answer_change_cannot_be_hidden_by_rehashing_own_manifest(self):
        files = {'questions.jsonl': b'{"answer":null}\n', 'evidence.jsonl': b'complete original\n',
                 'REVIEW.md': b'private reviewer instructions\n'}
        expected = {'scope': 'synthetic-controls'}
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'controls'
            self.base.publish_packet(output, files, dict(expected))
            with patch.object(controls, 'make_plan', return_value=(files, {'questions': 1})):
                with patch.object(controls, 'controls_manifest', return_value=expected):
                    controls.validate_controls(self.base, None, output)
                    (output / 'questions.jsonl').write_bytes(b'{"answer":"different_entities"}\n')
                    manifest = json.loads((output / 'manifest.json').read_bytes())
                    manifest['artifacts'] = [self.base.file_record(output / name, output) for name in files]
                    (output / 'manifest.json').write_bytes(self.base.encode_json(manifest))
                    with self.assertRaisesRegex(self.base.ContractError, 'immutable unanswered question changed'):
                        controls.validate_controls(self.base, None, output)

    def test_incomplete_source_context_cannot_replace_original(self):
        files = {'evidence.jsonl': b'full retained fields\n'}
        expected = {'scope': 'synthetic-controls'}
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'controls'
            self.base.publish_packet(output, files, dict(expected))
            (output / 'evidence.jsonl').write_bytes(b'cropped fields\n')
            manifest = json.loads((output / 'manifest.json').read_bytes())
            manifest['artifacts'] = [self.base.file_record(output / 'evidence.jsonl', output)]
            (output / 'manifest.json').write_bytes(self.base.encode_json(manifest))
            with patch.object(controls, 'make_plan', return_value=(files, {'questions': 1})):
                with patch.object(controls, 'controls_manifest', return_value=expected):
                    with self.assertRaisesRegex(self.base.ContractError, 'complete context'):
                        controls.validate_controls(self.base, None, output)

    def test_reviewer_guide_is_independent_and_preserves_unknown_answers(self):
        guide = controls.REVIEW_GUIDE
        for phrase in ['must not assume', 'insufficient_evidence', 'field_sha256',
                       'prior_exposure', 'separate', 'agent', 'outside this frozen packet']:
            self.assertIn(phrase, guide)
        self.assertNotIn('Refer to the operator', guide)
        self.assertIn('no required positive or negative balance', guide)


if __name__ == '__main__':
    unittest.main()
