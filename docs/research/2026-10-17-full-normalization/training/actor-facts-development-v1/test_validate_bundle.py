"""Behavioral regressions for original-field ownership and uncertainty boundaries."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('actor_bundle', HERE / 'validate_bundle.py')
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)


class SourceIsolationTests(unittest.TestCase):
    def test_physical_unicode_lines_and_unselected_invalid_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'rows.jsonl'
            payload = {'text': 'one\u2028two\u2029three\U0001f3b6'}
            path.write_bytes(b'\xffnot json\n' + json.dumps(payload, ensure_ascii=False).encode() + b'\n\xff\n')
            rows = list(v.read_jsonl(path, {2}))
            self.assertEqual([(n, row) for n, _, row in rows], [(2, payload)])

    def test_compact_notes_lists_do_not_publish_original_prose(self):
        original = {'notes': ['private-value', {'detail': 'source prose'}],
                    'actors': [{'wording': 'a source name', 'roles': ['Taper']}]}
        result = v.compact_review(original)
        self.assertEqual(result['notes_sha256'][0], v.digest(b'private-value'))
        self.assertEqual(result['notes_sha256'][1]['detail'], v.digest(b'source prose'))
        self.assertNotIn('source prose', json.dumps(result))
        self.assertEqual(result['actors'][0]['roles'], ['Taper'])

    def test_changed_file_is_rejected_even_in_optimized_python(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'source'
            path.write_bytes(b'original')
            receipt = v.fingerprint(path)
            path.write_bytes(b'modified')
            command = ('import importlib.util; from pathlib import Path; '
                       f's=importlib.util.spec_from_file_location("v",{str(HERE / "validate_bundle.py")!r}); '
                       'v=importlib.util.module_from_spec(s); s.loader.exec_module(v); '
                       f'v.check_file(Path({str(path)!r}),{receipt!r})')
            result = subprocess.run([sys.executable, '-O', '-c', command], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b'Changed file', result.stderr)


class RealBundleBoundaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = json.loads((HERE / 'manifest.json').read_text())
        cls.sources, cls.originals = v.load_sources(HERE, v.ROOT, cls.manifest)
        cls.fields = v.load_fields(HERE, v.ROOT, cls.manifest, cls.originals)
        cls.citations, cls.quotes = v.load_citations(HERE, cls.fields, cls.originals)
        cls.values = v.unique((r for _, _, r in v.read_jsonl(v.ROOT / cls.manifest['private_values']['path'])), 'value_ref')
        cls.labels = [r for _, _, r in v.read_jsonl(HERE / 'labels.jsonl')]

    def validate(self, labels):
        return v.validate_tasks(labels, self.fields, self.citations, self.quotes, self.values)

    def mutate_task(self, predicate, change):
        labels = copy.deepcopy(self.labels)
        task = next(t for t in labels if predicate(t))
        change(task)
        with self.assertRaises(ValueError):
            self.validate(labels)

    def test_complete_frozen_bundle(self):
        actual = v.validate_bundle()
        self.assertEqual(actual['tasks'], 543)

    def test_held_out_source_rejected_before_originals_decode(self):
        sources = copy.deepcopy(list(self.sources.values()))
        sources[0]['artist_partition'] = 'test'
        def metadata_only(path, *args):
            if Path(path).name == 'source-index.jsonl':
                return iter((i, '', row) for i, row in enumerate(sources, 1))
            self.fail('Original source decode attempted before rejecting held-out metadata')
        with patch.object(v, 'read_jsonl', side_effect=metadata_only):
            with self.assertRaisesRegex(ValueError, 'held out'):
                v.load_sources(HERE, v.ROOT, self.manifest)

    def test_reserved_source_rejected_before_originals_decode(self):
        sources = copy.deepcopy(list(self.sources.values()))
        reservation = json.loads((v.ROOT / self.manifest['reservation_index']['path']).read_text())
        sources[0]['evidence_id'] = reservation['excluded_annotation_metadata'][0]['evidence_id']
        def metadata_only(path, *args):
            if Path(path).name == 'source-index.jsonl':
                return iter((i, '', row) for i, row in enumerate(sources, 1))
            self.fail('Original source decode attempted before rejecting reservation')
        with patch.object(v, 'read_jsonl', side_effect=metadata_only):
            with self.assertRaisesRegex(ValueError, 'reserved or outside eligibility'):
                v.load_sources(HERE, v.ROOT, self.manifest)

    def test_contact_literal_needs_its_exact_original_evidence(self):
        def remove_literal(task):
            actor = next(a for a in task['actors'] if a['contacts'])
            actor['contacts'][0]['evidence'] = [actor['anchor']]
        self.mutate_task(lambda t: any(a['contacts'] for a in t['actors']), remove_literal)

    def test_changed_original_rejected_despite_refreshed_copy_receipt(self):
        manifest = copy.deepcopy(self.manifest)
        wrappers = [r for _, _, r in v.read_jsonl(v.ROOT / manifest['original_sources']['path'])]
        first = wrappers[0]['original_record']
        field = next(f['field'] for f in self.fields.values() if f['evidence_id'] == first['evidence_id'])
        first['raw_record']['row'][field] += ' changed'
        real_read = v.read_jsonl
        def changed_copy(path, *args):
            if Path(path) == v.ROOT / manifest['original_sources']['path']:
                return iter((i, '', row) for i, row in enumerate(wrappers, 1))
            return real_read(path, *args)
        with patch.object(v, 'read_jsonl', side_effect=changed_copy):
            with self.assertRaisesRegex(ValueError, 'Original annotation source changed'):
                v.load_sources(HERE, v.ROOT, manifest)

    def test_source_role_requires_original_role_evidence(self):
        self.mutate_task(lambda t: any(a['roles'] for a in t['actors']),
                         lambda t: next(a for a in t['actors'] if a['roles']).update(role_evidence=[]))

    def test_excluded_profile_cannot_use_an_absent_literal(self):
        def foreign_literal(task):
            excluded = task['excluded_references'][0]
            evidenced = {self.quotes[c] for c in excluded['evidence']}
            excluded['literal_ref'] = next(k for k, value in self.values.items() if value['text'] not in evidenced)
        self.mutate_task(lambda t: bool(t['excluded_references']), foreign_literal)

    def test_missing_whole_field_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'denominator'):
            self.validate(self.labels[:-1])

    def test_unknown_kind_cannot_be_complete_negative(self):
        self.mutate_task(lambda t: True,
                         lambda t: next(c for c in t['coverage'] if c['kind'] == 'Kind').update(complete=True))

    def test_unresolved_attachment_cannot_be_complete_negative(self):
        self.mutate_task(lambda t: any(u['kind'] == 'Contacts' for u in t['unresolved']),
                         lambda t: next(c for c in t['coverage'] if c['kind'] == 'Contacts').update(complete=True))

    def test_fact_cannot_use_another_fields_citation(self):
        foreign = self.labels[-1]['whole_field_citation']
        self.mutate_task(lambda t: t['actors'] and t['task_id'] != self.labels[-1]['task_id'],
                         lambda t: t['actors'][0]['context'][0].update(evidence=[foreign]))

    def test_alias_cannot_point_to_unknown_reference(self):
        self.mutate_task(lambda t: any(a['aliases'] for a in t['actors']),
                         lambda t: next(a for a in t['actors'] if a['aliases'])['aliases'][0].update(reference_uuid='foreign'))

    def test_same_actor_cannot_be_distinct_from_itself(self):
        def collapse(task):
            relation = next(r for r in task['relationships'] if r['relationship'] == 'DistinctActors')
            refs = {r['reference_uuid']: r for r in task['actor_references']}
            refs[relation['reference_uuids'][1]]['actor_uuid'] = refs[relation['reference_uuids'][0]]['actor_uuid']
        self.mutate_task(lambda t: any(r['relationship'] == 'DistinctActors' for r in t['relationships']), collapse)

    def test_malformed_span_rejected_in_optimized_python(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory)
            row = copy.deepcopy(next(iter(self.citations.values())))
            row['span']['end'] = True
            (bundle / 'citations.jsonl').write_text(json.dumps(row) + '\n')
            field = self.fields[row['task_id']]
            original = self.originals[field['evidence_id']]
            fixture = bundle / 'fixture.json'
            fixture.write_text(json.dumps({'fields': {row['task_id']: field}, 'originals': {field['evidence_id']: original}}))
            command = ('import importlib.util,json; from pathlib import Path; '
                       f's=importlib.util.spec_from_file_location("v",{str(HERE / "validate_bundle.py")!r}); '
                       'v=importlib.util.module_from_spec(s); s.loader.exec_module(v); '
                       f'd=json.loads(Path({str(fixture)!r}).read_text()); '
                       f'v.load_citations(Path({str(bundle)!r}),d["fields"],d["originals"])')
            result = subprocess.run([sys.executable, '-O', '-c', command], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b'Invalid citation span bounds', result.stderr)

    def test_grouping_includes_actorless_uncertain_fields(self):
        tasks = [
            {'task_id': 'a', 'actors': [1], 'split_group_ids': ['family:one']},
            {'task_id': 'uncertain', 'actors': [], 'split_group_ids': ['family:one', 'hypothesis:two']},
            {'task_id': 'b', 'actors': [1], 'split_group_ids': ['hypothesis:two']},
            {'task_id': 'c', 'actors': [], 'split_group_ids': ['family:three']},
        ]
        self.assertEqual(v.build_split_components(tasks), [['a', 'b', 'uncertain'], ['c']])
        components = v.build_split_components(self.labels)
        self.assertEqual(sum(map(len, components)), len(self.labels))


if __name__ == '__main__':
    unittest.main()
