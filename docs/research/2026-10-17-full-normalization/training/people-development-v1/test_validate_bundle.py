"""Behavioral regressions for development label provenance and candidate completeness."""
import copy
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('people_bundle', HERE / 'validate_bundle.py')
validator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator)


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def write_jsonl(path, rows):
    path.write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in rows))


class TestDevelopmentPeopleBundle(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bundle = self.root / 'bundle'
        shutil.copytree(HERE, self.bundle, ignore=shutil.ignore_patterns('__pycache__'))
        self.manifest = json.loads((self.bundle / 'manifest.json').read_text())
        annotation = self.root / validator.ANNOTATION
        annotation.parent.mkdir(parents=True)
        annotation.symlink_to(validator.ROOT / validator.ANNOTATION, target_is_directory=True)
        for relative in [Path('docs/research/2026-10-17-full-normalization/evaluation/cases.jsonl'),
                         Path('local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl')]:
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.symlink_to(validator.ROOT / relative)
        for key in ('reservation_index', 'original_selected_evidence', 'original_collision_tasks'):
            relative = self.manifest[key]['path']
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(validator.ROOT / relative, target)
        review_receipt = json.loads((self.bundle / 'review-receipt.json').read_text())
        for key in ('source_receipt', 'original_judgments', 'input'):
            review = review_receipt[key]['path']
            shutil.copyfile(validator.ROOT / review, self.root / review)

    def refresh(self, name):
        self.manifest['files'][name] = validator.fingerprint(self.bundle / name)
        frozen = {'labels.jsonl': 'frozen_labels_sha256', 'mentions.jsonl': 'frozen_mentions_sha256',
                  'candidate-pool.json': 'frozen_candidate_pool_sha256'}
        if name in frozen:
            self.manifest['independent_review'][frozen[name]] = self.manifest['files'][name]['sha256']
        write_json(self.bundle / 'manifest.json', self.manifest)

    def run_cli(self, expected_error=None):
        result = subprocess.run([sys.executable, '-O', str(HERE / 'validate_bundle.py'),
                                 '--bundle', str(self.bundle), '--root', str(self.root)],
                                capture_output=True, text=True)
        if expected_error:
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn(expected_error, result.stderr)
        else:
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout)['tasks'], 168)
        return result

    def rows(self, name):
        return [row for _, _, row in validator.read_jsonl(self.bundle / name)]

    def test_frozen_bundle_validates_with_optimized_python(self):
        self.run_cli()

    def test_corrupt_span_rejected_even_after_outer_receipt_refresh(self):
        rows = self.rows('citations.jsonl')
        rows[0]['span']['end'] += 1
        write_jsonl(self.bundle / 'citations.jsonl', rows)
        self.refresh('citations.jsonl')
        self.run_cli('Citation span does not match original text')

    def test_unbounded_span_does_not_silently_slice_to_matching_suffix(self):
        originals = {'e': {'raw_record': {'row': {'taper': '🎵 A\u2028B'}}}}
        citation = {'evidence_id': 'e', 'field_path': '/raw_record/row/taper',
                    'field_sha256': validator.digest('🎵 A\u2028B'.encode()),
                    'span': {'start': 2, 'end': 999, 'quote': 'A\u2028B', 'unit': 'unicode_code_points'}}
        with self.assertRaisesRegex(ValueError, 'bounds'):
            validator.validate_citation(citation, originals)

    def test_held_out_partition_rejected_after_receipt_refresh(self):
        rows = self.rows('source-index.jsonl')
        rows[0]['artist_partition'] = 'test'
        write_jsonl(self.bundle / 'source-index.jsonl', rows)
        self.refresh('source-index.jsonl')
        self.run_cli('Selected source is held out')

    def test_reservation_rejected_before_selected_text_is_used(self):
        path = self.root / self.manifest['reservation_index']['path']
        index = json.loads(path.read_text())
        selected = self.rows('source-index.jsonl')[0]['evidence_id']
        index['excluded_annotation_metadata'].append({'evidence_id': selected})
        write_json(path, index)
        self.manifest['reservation_index'].update(validator.fingerprint(path))
        receipt_path = self.bundle / 'reservation-receipt.json'
        receipt = json.loads(receipt_path.read_text())
        receipt['index'] = self.manifest['reservation_index']
        write_json(receipt_path, receipt)
        self.refresh('reservation-receipt.json')
        write_json(self.bundle / 'manifest.json', self.manifest)
        self.run_cli('Reserved source is eligible')

    def test_changed_original_source_fails_even_after_outer_receipt_refresh(self):
        path = self.root / self.manifest['original_selected_evidence']['path']
        rows = [row for _, _, row in validator.read_jsonl(path)]
        rows[0]['original_record']['raw_record']['row']['taper'] = 'Changed actor'
        write_jsonl(path, rows)
        self.manifest['original_selected_evidence'].update(validator.fingerprint(path))
        write_json(self.bundle / 'manifest.json', self.manifest)
        self.run_cli('Original annotation source changed')

    def test_missing_candidate_and_answer_rejected_after_outer_receipt_refresh(self):
        rows = self.rows('labels.jsonl')
        task = next(x for x in rows if x['task_kind'] == 'cross_recording_identity')
        removed = task['candidate_mention_ids'].pop()
        task['answer']['candidate_decisions'] = [x for x in task['answer']['candidate_decisions']
                                                if x['candidate_mention_id'] != removed]
        mentions = validator.unique(self.rows('mentions.jsonl'), 'mention_id')
        sources = validator.unique(self.rows('source-index.jsonl'), 'evidence_id')
        task['split_group_ids'] = sorted(validator.expected_groups(task, mentions, sources))
        citations = validator.unique(self.rows('citations.jsonl'), 'citation_id')
        involved = {mentions[m]['evidence_id'] for m in [task['focus_mention_id'], *task['candidate_mention_ids']]}
        task['supporting_citation_ids'] = [c for c in task['supporting_citation_ids']
                                           if citations[c]['evidence_id'] in involved]
        write_jsonl(self.bundle / 'labels.jsonl', rows)
        self.refresh('labels.jsonl')
        self.run_cli('Incomplete candidate pool')

    def test_duplicate_mention_is_rejected_even_after_outer_receipt_refresh(self):
        rows = self.rows('mentions.jsonl')
        rows.append(copy.deepcopy(rows[0]))
        write_jsonl(self.bundle / 'mentions.jsonl', rows)
        self.refresh('mentions.jsonl')
        self.run_cli('Duplicate mention_id')

    def test_unknown_actor_cannot_become_a_resolved_negative(self):
        rows = self.rows('labels.jsonl')
        task = next(x for x in rows if x['task_kind'] == 'actor_kind'
                    and x['answer']['actor_kind'] == 'unknown')
        task['answer']['resolved'] = True
        write_jsonl(self.bundle / 'labels.jsonl', rows)
        self.refresh('labels.jsonl')
        self.run_cli('Unknown actor kind cannot become a resolved negative')

    def test_comparison_is_recomputed_from_first_answers(self):
        rows = self.rows('review-comparison.jsonl')
        actor = next(x for x in rows if x['task_kind'] == 'actor_kind')
        actor['reviewed_actor_kind'] = 'unknown' if actor['reviewed_actor_kind'] != 'unknown' else 'person'
        write_jsonl(self.bundle / 'review-comparison.jsonl', rows)
        self.refresh('review-comparison.jsonl')
        receipt_path = self.bundle / 'review-receipt.json'
        receipt = json.loads(receipt_path.read_text())
        receipt['comparison'].update(validator.fingerprint(self.bundle / 'review-comparison.jsonl'))
        write_json(receipt_path, receipt)
        self.refresh('review-receipt.json')
        self.run_cli('Review comparison does not match frozen answers')

    def test_unselected_annotation_rows_are_never_decoded(self):
        path = self.root / 'mixed-annotation.jsonl'
        selected = b'{"text":"selected"}'
        path.write_bytes(b'\xff not UTF-8 or JSON\n' + selected + b'\nnot JSON\n')
        rows = list(validator.read_selected_jsonl(path, {2}))
        self.assertEqual(rows, [(2, validator.digest(selected), {'text': 'selected'})])
        # Selecting a malformed row must still fail; only unselected payloads
        # are opaque. Hashing the whole file is independent of JSON decoding.
        with self.assertRaises(UnicodeDecodeError):
            list(validator.read_selected_jsonl(path, {1}))

    def test_physical_jsonl_preserves_unicode_line_separator(self):
        path = self.root / 'unicode.jsonl'
        write_jsonl(path, [{'text': '🎵 A\u2028B\u2029C'}, {'text': 'next'}])
        rows = list(validator.read_jsonl(path))
        self.assertEqual([x[0] for x in rows], [1, 2])
        self.assertEqual(rows[0][2]['text'], '🎵 A\u2028B\u2029C')
        text = rows[0][2]['text']
        citation = {'citation_id': 'citation:' + validator.digest('e|taper|2|A\u2028B'.encode()),
                    'evidence_id': 'e', 'field_path': '/raw_record/row/taper',
                    'field_sha256': validator.digest(text.encode()),
                    'span': {'start': 2, 'end': 5, 'quote': 'A\u2028B', 'unit': 'unicode_code_points'}}
        validator.validate_citation(citation, {'e': {'raw_record': {'row': {'taper': text}}}})

    def test_known_recording_family_cannot_cross_metadata_split_groups(self):
        sources = {'first': {'known_recording_family': 'show:one', 'family_group': 'union:a'},
                   'second': {'known_recording_family': 'show:one', 'family_group': 'union:a'},
                   'third': {'known_recording_family': 'show:two', 'family_group': 'union:a'}}
        validator.validate_family_groups(sources)
        sources['second']['family_group'] = 'union:b'
        with self.assertRaisesRegex(ValueError, 'spans multiple split groups'):
            validator.validate_family_groups(sources)

    def test_unknown_task_connects_alias_and_cross_recording_components(self):
        # An unresolved annotation still shares source/name context. Dropping it
        # before grouping would let related examples enter separate splits.
        labels = [{'task_id': 'alias', 'split_group_ids': ['recording:one']},
                  {'task_id': 'unknown', 'split_group_ids': ['recording:one', 'name_hypothesis:x']},
                  {'task_id': 'other_artist', 'split_group_ids': ['name_hypothesis:x']}]
        self.assertEqual(validator.build_split_components(labels), [['alias', 'other_artist', 'unknown']])
        self.assertEqual(len(validator.build_split_components([labels[0], labels[2]])), 2)


if __name__ == '__main__':
    unittest.main()
