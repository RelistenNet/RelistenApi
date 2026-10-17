"""Exercise completed captures, leakage groups and blind output contracts."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import build_tasks as builder


def uid(number):
    return f'00000000-0000-0000-0000-{number:012d}'


def snapshot_records():
    records = [{'kind': 'snapshot', 'read_only': 'on', 'in_recovery': True, 'snapshot': 'test'},
               {'kind': 'totals', 'rows': {'artists': 3, 'sources': 6, 'tracks': 3,
                'venues': 3, 'songs': 3, 'setlist_shows': 3, 'setlist_memberships': 3}}]
    for artist in range(1, 4):
        records.append({'kind': 'artist', 'row': {'id': artist, 'uuid': uid(artist), 'name': f'Artist {artist}'}})
        for kind, (profile, field, _, _) in builder.PROFILES.items():
            count = 2 if kind == 'source_sample' else 1
            row = {'artist_id': artist, field: count}
            if kind == 'track_sample':
                row['rows'] = count
            if kind == 'setlist_sample':
                row['memberships'] = 1
            records.append({'kind': profile, 'row': row})
            for index in range(count):
                number = artist * 100 + list(builder.PROFILES).index(kind) * 10 + index
                row = {'id': number, 'uuid': uid(number), 'artist_id': artist,
                       'taper': 'Pat Jones', 'transferrer': 'Pat Jones', 'description': f'Raw account {artist}/{index}',
                       'name': 'Common venue' if kind == 'venue_sample' else 'Common title',
                       'location': 'City', 'title': 'Song > Song', 'source_id': artist * 100,
                       'source_set_id': artist * 1000}
                record = {'kind': kind, 'row': row}
                if kind in ('source_sample', 'track_sample'):
                    record['family'] = f'artist:{artist}:show:100'
                if kind == 'setlist_sample':
                    record['unordered_memberships'] = [{'song_id': artist}]
                records.append(record)
    records.append({'kind': 'snapshot_complete', 'completed_at': 'test'})
    return records


class BuildTasksTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.snapshot = self.root / 'snapshot.jsonl'
        self.write(snapshot_records())

    def tearDown(self):
        self.temp.cleanup()

    def write(self, rows):
        self.snapshot.write_text(''.join(json.dumps(r) + '\n' for r in rows))

    def build(self, name='out', targets=None):
        return builder.build(self.snapshot, self.root / name, targets)

    def read(self, name):
        return [json.loads(line) for line in (self.root / 'out' / name).read_text().splitlines()]

    def test_blind_pack_retains_exact_evidence_and_covers_every_artist(self):
        result = self.build(targets={kind: 1 for kind in builder.DOMAINS})
        self.assertEqual('complete_unlabeled', result['state'])
        self.assertEqual(0, result['labels_completed'])
        for coverage in result['coverage'].values():
            self.assertEqual(3, coverage['selected_artists'])
            self.assertEqual(0, coverage['missing_eligible_artists'])
        original = {builder.evidence_key(r): r for r in snapshot_records() if r['kind'] in builder.DOMAINS}
        evidence = self.read('evidence.jsonl')
        for row in evidence:
            self.assertEqual(original[row['evidence_id']], row['raw_record'])
        for task in self.read('tasks.jsonl'):
            self.assertIsNone(task['answer'])
            self.assertNotIn('prediction', task)
            self.assertTrue(set(task['evidence_ids']) <= {r['evidence_id'] for r in evidence})

    def test_known_show_families_and_duplicate_venues_share_partition(self):
        self.build()
        evidence = self.read('evidence.jsonl')
        by_family = {}
        for row in evidence:
            family = row['known_recording_family']
            if family:
                by_family.setdefault(family, set()).add((row['input_duplicate_family'], row['family_partition']))
        self.assertTrue(all(len(partitions) == 1 for partitions in by_family.values()))
        venues = [r for r in evidence if r['raw_record']['kind'] == 'venue_sample']
        self.assertEqual(1, len({v['input_duplicate_family'] for v in venues}))
        self.assertEqual(1, len({v['family_partition'] for v in venues}))

    def test_family_bridge_in_unselected_evidence_still_groups_selected_rows(self):
        records = snapshot_records()
        first_artist = sorted((r for r in records if r['kind'] == 'source_sample' and r['row']['artist_id'] == 1),
                              key=lambda r: builder.rank(builder.evidence_key(r)))
        second_artist = sorted((r for r in records if r['kind'] == 'source_sample' and r['row']['artist_id'] == 2),
                               key=lambda r: builder.rank(builder.evidence_key(r)))
        selected, bridge, other = first_artist[0], first_artist[1], second_artist[0]
        bridge['row']['description'] = other['row']['description']
        self.write(records)
        self.build(targets={'source_sample': 3})
        evidence = {r['evidence_id']: r for r in self.read('evidence.jsonl')}
        self.assertNotIn(builder.evidence_key(bridge), evidence)
        self.assertEqual(evidence[builder.evidence_key(selected)]['input_duplicate_family'],
                         evidence[builder.evidence_key(other)]['input_duplicate_family'])

    def test_deterministic_outputs_do_not_depend_on_sample_row_order(self):
        first = self.build('first')
        rows = snapshot_records()
        self.write([rows[0], *reversed(rows[1:-1]), rows[-1]])
        second = self.build('second')
        for filename in ('evidence.jsonl', 'tasks.jsonl'):
            self.assertEqual((self.root / 'first' / filename).read_bytes(), (self.root / 'second' / filename).read_bytes())
        self.assertEqual(first['files'], second['files'])
        self.assertNotEqual(first['input_sha256'], second['input_sha256'])

    def test_pairs_are_unlabeled_and_point_to_available_evidence(self):
        result = self.build()
        pairs = [r for r in self.read('tasks.jsonl') if r['task_kind'] == 'entity_link']
        self.assertGreater(len(pairs), 0)
        self.assertGreater(result['entity_link_tasks']['compositions'], 0)
        for pair in pairs:
            self.assertIsNone(pair['answer'])
            self.assertIn('insufficient_evidence', pair['allowed_decisions'])
            self.assertIn('different_entities', pair['allowed_decisions'])
        self.assertFalse(result['accuracy_estimated'])

    def test_artist_holdout_assignments_are_consistent_across_domains(self):
        self.build()
        by_artist = {}
        for row in self.read('evidence.jsonl'):
            by_artist.setdefault(row['artist']['uuid'], set()).add(row['artist_partition'])
        self.assertTrue(all(len(partitions) == 1 for partitions in by_artist.values()))

    def test_truncation_trailing_records_and_duplicate_resources_are_rejected(self):
        original = snapshot_records()
        duplicate = next(r for r in original if r['kind'] == 'source_sample')
        cases = [original[:-1], original + [original[1]], original[:-1] + [duplicate, original[-1]]]
        for index, rows in enumerate(cases):
            with self.subTest(index=index):
                self.write(rows)
                with self.assertRaises(ValueError):
                    self.build(str(index))
                self.assertFalse((self.root / str(index) / 'manifest.json').exists())

    def test_missing_artist_sample_or_wrong_population_is_rejected(self):
        for remove_sample in (True, False):
            rows = snapshot_records()
            if remove_sample:
                rows.remove(next(r for r in rows if r['kind'] == 'venue_sample'))
            else:
                rows[1]['rows']['venues'] += 1
            self.write(rows)
            with self.assertRaises(ValueError):
                self.build(str(remove_sample))

    def test_writable_capture_and_invalid_json_are_rejected(self):
        rows = snapshot_records()
        rows[0]['read_only'] = 'off'
        self.write(rows)
        with self.assertRaises(ValueError):
            self.build()
        self.snapshot.write_text('{truncated')
        with self.assertRaises(ValueError):
            self.build()

    def test_unknown_evidence_kind_is_not_silently_ignored(self):
        rows = snapshot_records()
        rows.insert(-1, {'kind': 'new_domain_sample', 'row': {}})
        self.write(rows)
        with self.assertRaisesRegex(ValueError, 'Unsupported snapshot record kind'):
            self.build()

    def test_existing_output_is_preserved(self):
        self.build()
        manifest = (self.root / 'out' / 'manifest.json').read_bytes()
        with self.assertRaises(FileExistsError):
            self.build()
        self.assertEqual(manifest, (self.root / 'out' / 'manifest.json').read_bytes())

    def test_failed_write_leaves_no_complete_manifest(self):
        with patch.object(builder, 'write_jsonl', side_effect=OSError('disk full')):
            with self.assertRaises(OSError):
                self.build()
        self.assertTrue((self.root / 'out').exists())
        self.assertFalse((self.root / 'out' / 'manifest.json').exists())


if __name__ == '__main__':
    unittest.main()
