"""Behavioral checks for source alignment and uncertainty-aware replay diagnostics."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('compare_fields', HERE / 'compare_fields.py')
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)


def evidence(start=2, end=5, quote='Ada'):
    return {'field': 'taper', 'jsonPointer': '/taper', 'quote': quote, 'startCodePoint': start, 'endCodePoint': end}


def fixture():
    citation = {'anchor': {'span': {'start': 2, 'end': 5}}}
    actor = {'actor_uuid': 'expected', 'anchor': 'anchor', 'roles': ['Taper'], 'aliases': [], 'contacts': [],
             'profiles': [], 'context': [], 'kind': None}
    label = {'task_id': 'one', 'actors': [actor], 'actor_references': [{'anchor': 'anchor', 'reference_uuid': 'label-ref'}], 'relationships': [], 'unresolved': [],
             'coverage': [{'kind': kind, 'complete': kind != 'Kind'} for kind in ['Aliases', 'Profiles', 'Contacts', 'Context', 'Kind', 'Relationships']]}
    facts = {'actorUuid': 'actual', 'referenceUuid': 'reference', 'anchor': evidence(), 'aliases': [], 'contacts': [],
             'profiles': [], 'context': [], 'kind': None}
    result = {'state': 'extracted', 'error': None, 'extraction': {'credits': [{'name': 'Ada', 'role': 'Taper', 'actorFacts': facts}], 'unresolved': []}}
    field = {'field': 'taper', 'field_sha256': c.hash_text('🌿 Ada')}
    return label, field, citation, result


class RetainedReplayFixture:
    def __init__(self, root):
        self.root = root
        self.bundle = root / 'bundle'
        self.bundle.mkdir()
        (self.bundle / 'manifest.json').write_text('{}')
        (self.bundle / 'field-index.jsonl').write_text('{}\n')
        self.fields, self.originals, self.rows, self.inputs = {}, {}, [], []
        for number in range(2):
            key, source = str(number), 'source:' + str(number)
            field = {'evidence_id': source, 'field': 'taper', 'field_path': '/raw_record/row/taper',
                     'field_sha256': c.hash_text('🌿 Ada')}
            self.fields[key] = field
            self.originals[source] = {'raw_record': {'row': {'taper': '🌿 Ada'}}}
            self.inputs.append({'task_id': key, **field, 'field_text': '🌿 Ada'})
            result = fixture()[3]
            self.rows.append({'taskId': key, 'evidenceId': source, 'field': 'taper',
                              'fieldSha256': field['field_sha256'], **result})
        self.state = 'completed'

    def write(self):
        input_path, receipt_path = self.root / 'input.jsonl', self.root / 'receipt.json'
        input_path.write_text(''.join(json.dumps(row) + '\n' for row in self.inputs))
        receipt_path.write_text('{}')
        result_path = self.root / 'results.jsonl'
        result_path.write_text(''.join(json.dumps(row) + '\n' for row in self.rows))
        def fp(path):
            return {'path': str(path), **c.fingerprint(path)}
        status = {'state': self.state, 'version': 'actor-field-replay-v1', 'expectedFields': 2,
                  'extractorVersion': 'synthetic', 'apiAssembly': {}, 'toolAssembly': {},
                  'processedFields': len(self.rows), 'errorFields': sum(row['state'] == 'error' for row in self.rows),
                  'bundleManifest': fp(self.bundle / 'manifest.json'), 'fieldIndex': fp(self.bundle / 'field-index.jsonl'),
                  'input': fp(input_path), 'exportReceipt': fp(receipt_path), 'results': fp(result_path)}
        (self.root / 'status.json').write_text(json.dumps(status))
        if self.state == 'completed':
            (self.root / 'manifest.json').write_text(json.dumps(status))

    def load(self):
        return c.load_replay(self.root, self.bundle, self.fields, self.originals)


class ComparisonTests(unittest.TestCase):
    def compare(self, label, field, citations, result, review=None):
        return c.compare_field(label, field, citations, result, review)

    def test_exact_unique_span_matches_role_and_preserves_unknown_kind(self):
        row = self.compare(*fixture())
        self.assertEqual(row['names']['exact_unique_raw_span_matches'], 1)
        role = next(item for item in row['facets'] if item['kind'] == 'Roles')
        self.assertEqual(role['matched_labeled_positive'], 1)
        self.assertEqual(c.summarize([row])['kind_accuracy_denominator'], 0)

    def test_equal_name_at_different_anchor_stays_unaligned(self):
        label, field, citations, result = fixture()
        result['extraction']['credits'][0]['actorFacts']['anchor'] = evidence(7, 10)
        row = self.compare(label, field, citations, result)
        self.assertEqual(row['names']['exact_unique_raw_span_matches'], 0)
        self.assertEqual(row['names']['actual_unaligned_spans'], [(7, 10)])
        role = next(item for item in row['facets'] if item['kind'] == 'Roles')
        self.assertEqual(role['unaligned_labeled_positive'], 1)
        self.assertEqual(role['missing_labeled_positive'], 0)

    def test_two_actor_ids_at_same_span_are_ambiguous_not_forced_matches(self):
        label, field, citations, result = fixture()
        duplicate = copy.deepcopy(result['extraction']['credits'][0])
        duplicate['actorFacts']['actorUuid'] = 'another'
        result['extraction']['credits'].append(duplicate)
        row = self.compare(label, field, citations, result)
        self.assertEqual(row['names']['exact_unique_raw_span_matches'], 0)
        self.assertEqual(row['names']['ambiguous_spans'], [(2, 5)])

    def test_same_reference_repeated_for_roles_is_not_an_extra_actor(self):
        label, field, citations, result = fixture()
        duplicate = copy.deepcopy(result['extraction']['credits'][0])
        duplicate['role'] = 'Transferrer'
        result['extraction']['credits'].append(duplicate)
        row = self.compare(label, field, citations, result)
        self.assertEqual(row['names']['actual_anchored_reference_count'], 1)
        role = next(item for item in row['facets'] if item['kind'] == 'Roles')
        self.assertEqual(role['extra_in_complete_undisputed_draft_field'], 1)

    def test_contact_attachment_does_not_move_between_nearby_actors(self):
        label, field, citations, result = fixture()
        label['actors'][0]['contacts'] = [{'literal_ref': 'value:' + c.hash_text('contact-value')}]
        other = copy.deepcopy(result['extraction']['credits'][0])
        other['actorFacts']['actorUuid'] = 'other'
        other['actorFacts']['anchor'] = evidence(7, 10)
        other['actorFacts']['contacts'] = [{'literal': 'contact-value'}]
        result['extraction']['credits'].append(other)
        row = self.compare(label, field, citations, result)
        contact = next(item for item in row['facets'] if item['kind'] == 'Contacts')
        self.assertEqual(contact['matched_labeled_positive'], 0)
        self.assertEqual(contact['missing_labeled_positive'], 1)

    def test_extra_contact_is_unscored_when_coverage_is_partial(self):
        label, field, citations, result = fixture()
        next(item for item in label['coverage'] if item['kind'] == 'Contacts')['complete'] = False
        result['extraction']['credits'][0]['actorFacts']['contacts'] = [{'literal': 'contact-value'}]
        row = self.compare(label, field, citations, result)
        contact = next(item for item in row['facets'] if item['kind'] == 'Contacts')
        self.assertEqual(contact['unscored_extra_partial_or_disputed'], 1)
        self.assertEqual(contact['extra_in_complete_undisputed_draft_field'], 0)

    def test_disputed_complete_contact_is_separate_from_undisputed_draft(self):
        label, field, citations, result = fixture()
        result['extraction']['credits'][0]['actorFacts']['contacts'] = [{'literal': 'contact-value'}]
        review = {'coverage_differences': [{'kind': 'Contacts'}], 'aligned_actors': [],
                  'draft_only_anchor_spans': [], 'review_only_anchor_spans': [],
                  'draft_relationship_counts': {}, 'review_relationship_counts': {}}
        row = self.compare(label, field, citations, result, review)
        summary = c.summarize([row])
        self.assertEqual(summary['facets']['Contacts/disputed']['unscored_extra_partial_or_disputed'], 1)
        self.assertNotIn('Contacts/complete_undisputed_draft', summary['facets'])

    def test_absent_and_failed_outcomes_keep_whole_field_denominator(self):
        label, field, citations, _ = fixture()
        missing = self.compare(label, field, citations, None)
        failed = self.compare(label, field, citations, {'state': 'error', 'error': {'type': 'failure'}, 'extraction': None})
        summary = c.summarize([missing, failed])
        self.assertEqual(summary['whole_fields'], 2)
        self.assertEqual(summary['outcomes'], {'not_processed': 1, 'error': 1})
        self.assertEqual(missing['names']['actual_anchored_reference_count'], 0)

    def test_unicode_raw_html_evidence_requires_exact_original_codepoints(self):
        text = '🌿 <b>Ada</b>\u2028tail'
        field = {'field': 'taper'}
        value = {'nested': [evidence(2, 12, '<b>Ada</b>')]}
        c.validate_output_citations(value, field, text)
        value['nested'][0]['startCodePoint'] = 3
        with self.assertRaisesRegex(ValueError, 'Unicode field'):
            c.validate_output_citations(value, field, text)

    def test_unanchored_credit_remains_a_diagnostic(self):
        label, field, citations, result = fixture()
        result['extraction']['credits'][0]['actorFacts'] = None
        row = self.compare(label, field, citations, result)
        self.assertEqual(len(row['names']['unanchored_credits']), 1)
        self.assertEqual(row['names']['actual_unaligned_spans'], [])

    def test_profile_relationship_difference_is_preserved(self):
        label, field, citations, result = fixture()
        label['actors'][0]['profiles'] = [{'literal_ref': 'value:' + c.hash_text('profile-value'), 'relationship': 'CreditedProjectProfile'}]
        result['extraction']['credits'][0]['actorFacts']['profiles'] = [{'literal': 'profile-value', 'relationship': 'AttributedProfile'}]
        row = self.compare(label, field, citations, result)
        profile = next(item for item in row['facets'] if item['kind'] == 'Profiles')
        self.assertEqual(profile['matched_labeled_positive'], 0)
        self.assertEqual(profile['missing_labeled_positive'], 1)
        self.assertEqual(profile['extra_in_complete_undisputed_draft_field'], 1)

    def test_context_coverage_dispute_also_masks_role_negatives(self):
        label, field, citations, result = fixture()
        result['extraction']['credits'][0]['role'] = 'Other'
        review = {'coverage_differences': [{'kind': 'Context'}], 'aligned_actors': [],
                  'draft_only_anchor_spans': [], 'review_only_anchor_spans': [],
                  'draft_relationship_counts': {}, 'review_relationship_counts': {}}
        row = self.compare(label, field, citations, result, review)
        role = next(item for item in row['facets'] if item['kind'] == 'Roles')
        self.assertTrue(role['disputed'])
        self.assertEqual(role['extra_in_complete_undisputed_draft_field'], 0)

    def test_first_review_profile_relationship_difference_is_disputed(self):
        label, field, citations, result = fixture()
        label['actors'][0]['profiles'] = [{'literal_ref': 'value:' + c.hash_text('profile'), 'relationship': 'CreditedProjectProfile'}]
        review = {'coverage_differences': [], 'aligned_actors': [],
                  'draft_only_anchor_spans': [], 'review_only_anchor_spans': [],
                  'draft_relationship_counts': {}, 'review_relationship_counts': {},
                  'first_answer': {'actors': [{'anchor_span': {'start': 2, 'end': 5}, 'profiles': [
                      {'literal_span': {'quote_sha256': c.hash_text('profile')}, 'relationship': 'AttributedProfile'}]}]}}
        row = self.compare(label, field, citations, result, review)
        profile = next(item for item in row['facets'] if item['kind'] == 'Profiles')
        self.assertTrue(profile['disputed'])

    def test_independent_review_presence_is_visible_separately_from_disputes(self):
        label, field, citations, result = fixture()
        review = {'coverage_differences': [], 'aligned_actors': [], 'draft_only_anchor_spans': [],
                  'review_only_anchor_spans': [], 'draft_relationship_counts': {}, 'review_relationship_counts': {}}
        rows = [self.compare(label, field, citations, result), self.compare(label, field, citations, result, review)]
        summary = c.summarize(rows)
        self.assertEqual(summary['independently_reviewed_fields'], 1)
        self.assertEqual(summary['not_independently_reviewed_fields'], 1)
        self.assertEqual(summary['fields_with_independent_disputes'], 0)

    def test_alias_endpoint_without_raw_anchor_is_retained_unscored(self):
        label, field, citations, result = fixture()
        label['relationships'] = [{'relationship': 'ExplicitAlias', 'roles': [], 'reference_uuids': ['label-ref', 'label-alias']}]
        label['actor_references'].append({'reference_uuid': 'label-alias', 'anchor': 'alias'})
        citations['alias'] = {'span': {'start': 12, 'end': 18}}
        facts = result['extraction']['credits'][0]['actorFacts']
        facts['aliases'] = [{'referenceUuid': 'alias-ref', 'wording': 'Alias'}]
        facts['relationships'] = [{'relationship': 'ExplicitAlias', 'roles': [], 'referenceUuids': ['reference', 'alias-ref']}]
        row = self.compare(label, field, citations, result)
        relationships = row['relationships']
        self.assertEqual(len(relationships['expected']), 1)
        self.assertEqual(len(relationships['actual']), 1)
        self.assertIsNone(relationships['actual'][0]['endpoint_spans'])
        self.assertFalse(relationships['scored'])
        self.assertEqual(relationships['exact_fully_anchored_correspondences'], 0)

    def test_relationship_with_two_unique_local_reference_anchors_is_correspondence(self):
        label, field, citations, result = fixture()
        label['relationships'] = [{'relationship': 'DistinctActors', 'roles': [], 'reference_uuids': ['label-ref', 'second-ref']}]
        label['actor_references'].append({'reference_uuid': 'second-ref', 'anchor': 'second'})
        citations['second'] = {'span': {'start': 7, 'end': 10}}
        second = copy.deepcopy(result['extraction']['credits'][0])
        second['actorFacts'].update(actorUuid='second', referenceUuid='second-ref', anchor=evidence(7, 10))
        result['extraction']['credits'].append(second)
        result['extraction']['credits'][0]['actorFacts']['relationships'] = [
            {'relationship': 'DistinctActors', 'roles': [], 'referenceUuids': ['reference', 'second-ref']}]
        row = self.compare(label, field, citations, result)
        self.assertEqual(row['relationships']['exact_fully_anchored_correspondences'], 1)
        self.assertFalse(row['relationships']['scored'])

    def test_validator_mutation_during_diagnostics_prevents_final_report_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            data = RetainedReplayFixture(Path(directory))
            data.write()
            validator = data.bundle / 'validate_bundle.py'
            validator.write_text('original validator')
            for name in ('labels.jsonl', 'independent-review.jsonl'):
                (data.bundle / name).write_text('fixture')
            manifest = {'files': {name: c.fingerprint(data.bundle / name) for name in ('labels.jsonl', 'independent-review.jsonl')},
                        'source_scope': {}}
            class Frozen:
                read_set = {data.bundle / 'manifest.json': c.fingerprint(data.bundle / 'manifest.json'),
                            validator: c.fingerprint(validator),
                            HERE / 'export_fields.py': c.fingerprint(HERE / 'export_fields.py')}
                def verify_unchanged(self):
                    for path, expected in self.read_set.items():
                        c.check_file(path, expected)
            frozen = Frozen()
            frozen.manifest = manifest
            labels = {}
            citations = fixture()[2]
            for key in data.fields:
                label = fixture()[0]
                label['task_id'] = key
                labels[key] = label
            real_write = c.write_diagnostics
            def mutate_after_diagnostics(path, rows):
                real_write(path, rows)
                validator.write_text('changed validator')
            output = data.root / 'comparison'
            with patch.object(c, 'load_frozen', return_value=(frozen, data.fields, labels, citations, data.originals, {})), \
                 patch.object(c, 'write_diagnostics', side_effect=mutate_after_diagnostics):
                with self.assertRaisesRegex(ValueError, 'Changed replay artifact: validate_bundle.py'):
                    c.run(data.bundle, data.root, data.root, output)
            self.assertTrue((output / 'fields.jsonl').exists())
            self.assertFalse((output / 'report.json').exists())

    def test_completed_replay_cannot_drop_a_task_even_with_updated_result_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            data = RetainedReplayFixture(Path(directory))
            data.rows.pop()
            data.write()
            with self.assertRaisesRegex(ValueError, 'Completed replay is partial'):
                data.load()

    def test_changed_output_quote_cannot_be_justified_by_refreshing_result_receipts(self):
        with tempfile.TemporaryDirectory() as directory:
            data = RetainedReplayFixture(Path(directory))
            data.rows[0]['extraction']['credits'][0]['actorFacts']['anchor']['quote'] = 'someone else'
            data.write()
            with self.assertRaisesRegex(ValueError, 'Unicode field'):
                data.load()

    def test_changed_input_cannot_be_justified_by_refreshing_outer_input_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            data = RetainedReplayFixture(Path(directory))
            data.inputs[0]['field_text'] = 'different original'
            data.write()
            with self.assertRaisesRegex(ValueError, 'frozen original field'):
                data.load()

    def test_partial_replay_retains_unprocessed_tasks_without_claiming_completion(self):
        with tempfile.TemporaryDirectory() as directory:
            data = RetainedReplayFixture(Path(directory))
            data.state = 'cancelled'
            data.rows.pop()
            data.write()
            _, rows, completed = data.load()
            self.assertFalse(completed)
            self.assertEqual(set(rows), {'0'})
            self.assertEqual(len(data.fields), 2)

    def test_physical_jsonl_keeps_unicode_paragraph_separators_inside_field(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'rows.jsonl'
            expected = {'text': 'a\u2028b\u2029c'}
            path.write_text(json.dumps(expected, ensure_ascii=False) + '\n', encoding='utf-8')
            self.assertEqual(list(c.read_jsonl(path)), [expected])


if __name__ == '__main__':
    unittest.main()
