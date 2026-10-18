"""Synthetic contracts only; the operator runs these, never a provider or DB.

The actual pinned packet helper owns authentication, physical-line gates and
publication. Fixtures exercise that helper's real enriched receipt contract.
"""

import copy
from contextlib import chdir
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid

import select_people_evidence as selection


def decoded_rows(data):
    return [json.loads(line) for line in data.split(b'\n') if line]


class TestPeopleEvidenceSelection(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        repo = Path(os.environ['RELISTEN_REPO_ROOT']).resolve()
        pins = json.loads(Path(__file__).with_name('base-inputs.json').read_bytes())['artifacts']
        pin, = [row for row in pins if row['path'] == selection.HELPER]
        path = repo / selection.HELPER
        if selection.stamp(path, selection.HELPER) != pin:
            raise AssertionError('Synthetic tests require the reviewed packet helper')
        spec = importlib.util.spec_from_file_location('selection_test_packet_helper', path)
        cls.helpers = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.helpers
        spec.loader.exec_module(cls.helpers)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name).resolve()
        self.bundle = self.repo / 'bundle'
        self.bundle.mkdir()
        for name in ('select_people_evidence.py', 'selection-contract.json', 'base-inputs.json'):
            (self.bundle / name).write_bytes(b'{"synthetic":true}\n')
        self.config = {'coverage_per_artist': 0, 'probability_clusters': 0, 'seed': ''}
        self.amendment = copy.deepcopy(selection.EMPTY_AMENDMENT)
        # 1--2 share a recording, 2--3 a duplicate group. Closure must include 2
        # even when it is never directly selected or mentioned by an amendment.
        self.rows = [self.meta(1, 'a', 'fa', 'g1'), self.meta(2, 'a', 'fa', 'g2'),
                     self.meta(3, 'b', 'fb', 'g2'), self.meta(4, 'c', 'fc', 'g4'),
                     self.meta(5, 'd', 'fd', 'g5'), self.meta(6, 'd', 'fe', 'g6')]
        excluded = self.meta(7, 'x', 'fx', 'gx')
        excluded['family_partition'] = 'test'
        self.index = {
            'eligible_source_metadata': self.rows, 'excluded_annotation_metadata': [excluded],
            'reserved_references': ['source:7', 'local:source:1', 'local:track:1', 'venue:1'],
            'reserved_annotation_family_groups': ['gx'], 'reserved_known_families': ['fx'],
            'metadata_linked_parent_sources': [], 'metadata_matches': [],
            'known_exposed_track_references': ['track:1'],
            'exposure_note': 'Synthetic prior access; do not treat this as untouched.',
            'annotation_evidence_sha256': 'replaced_when_capture_fixture_is_written',
        }
        self.coverage = {
            'totals': {'artists': 10}, 'coverage': {'source': {
                'eligible_artists': 8, 'population_rows': 1000,
                'sampled_rows': 60, 'artists_without_eligible_rows': 2,
            }},
        }
        self.prior = {'counts': {'eligible_originals_authenticated': 6}, 'exposure': {
            'prior_discovery_and_extraction_exposure': True, 'eligible_for_release_evaluation': False,
        }}
        self.sources = selection.execution_sources(self.bundle)
        self.loader_patch = patch.object(selection, 'load_helpers', return_value=(self.helpers, [], self.sources))
        self.frame_patch = patch.object(selection, 'load_frame', side_effect=lambda *_: (self.index, self.coverage, self.prior))
        self.loader_patch.start()
        self.frame_patch.start()
        self.addCleanup(self.loader_patch.stop)
        self.addCleanup(self.frame_patch.stop)

    def meta(self, number, artist, family, group):
        return {
            'artist_partition': 'development', 'family_partition': 'development',
            'artist_uuid': str(uuid.uuid5(uuid.NAMESPACE_URL, artist)),
            'evidence_id': 'source_sample:' + str(uuid.uuid5(uuid.NAMESPACE_URL, str(number))),
            'family': family, 'family_group': group,
            'reference': 'source:' + str(number), 'source_line': number,
        }

    def plan(self):
        return selection.plan_selection(self.index, self.coverage, self.prior,
                                        self.amendment, self.config, self.helpers)

    def native_match(self, number, kind, family, source_id=None, local=False):
        reference = ('local:' if local else '') + kind + ':' + str(number)
        return {
            'family': family, 'id': number, 'kind': kind, 'reference': reference,
            'snapshot_line': 100 + number, 'source_id': source_id,
            'uuid': str(uuid.uuid5(uuid.NAMESPACE_URL, reference)),
        }

    def add_mixed_exclusions(self):
        excluded = self.index['excluded_annotation_metadata']
        excluded.extend([self.meta(9, 'x', 'fy', 'gy'), self.meta(10, 'x', 'fz', 'gz')])
        for line, number, group in ((8, 1, 'g5'), (11, 2, 'gt2'), (12, 3, 'gt3')):
            track = self.meta(line, 'd', 'ft' + str(number), group)
            track['reference'] = 'track:' + str(number)
            track['evidence_id'] = track['evidence_id'].replace('source_sample:', 'track_sample:')
            track['family_partition'] = 'test'
            excluded.append(track)

    def select(self, clusters=1, coverage=0):
        self.config.update(probability_clusters=clusters, coverage_per_artist=coverage, seed='42' * 32)

    def add_bridge(self, left, right):
        self.amendment['bridges'].append({
            'left_source_key': selection.resource_key(left), 'right_source_key': selection.resource_key(right),
            'basis': 'known_identity_family', 'provenance': 'synthetic-metadata:bridge-1',
        })

    def build(self, name='selection'):
        packet = self.repo / name
        selection.build(self.repo, packet, self.config, self.amendment, self.bundle)
        return packet

    def raw_fixture(self):
        physical = []
        for row in self.rows:
            record = {
                'evidence_id': row['evidence_id'], 'artist_partition': 'development',
                'family_partition': 'development', 'artist': {'uuid': row['artist_uuid']},
                'input_duplicate_family': row['family_group'], 'known_recording_family': row['family'],
                'raw_record': {'row': {'id': int(row['reference'].split(':')[1]),
                                       'taper': 'A “complete” field\u2028with Unicode',
                                       'description': 'Full contact/role context is retained.\nSecond line.'}},
            }
            physical.append(self.helpers.canonical(record) + b'\n')
        physical.append(b'not-json: held-out payload must never be decoded\n')
        raw = self.repo / selection.ANNOTATION
        raw.parent.mkdir(parents=True)
        raw.write_bytes(b''.join(physical))
        record = self.helpers.file_record(raw, self.repo)
        self.index['annotation_evidence_sha256'] = record['sha256']
        # Real retained receipts may carry descriptive metadata beyond identity.
        (self.repo / selection.ANNOTATION_MANIFEST).write_bytes(selection.json_bytes({
            'files': {'evidence.jsonl': {'bytes': record['bytes'], 'sha256': record['sha256'],
                                        'recipe': 'physical JSONL, source text preserves Unicode'}},
        }))
        return raw

    def test_default_inventories_every_permitted_row_and_missingness_without_raw_access(self):
        with patch.object(self.helpers, 'selected_originals', side_effect=AssertionError('raw decode')):
            packet = self.build()
            _, _, files, counts = selection.verify(self.repo, packet, self.bundle)
        self.assertEqual((6, 4, 4), (counts['permitted_metadata_sources'], counts['permitted_metadata_artists'],
                                    counts['selection_eligible_components']))
        self.assertEqual([], decoded_rows(files['selection.jsonl']))
        self.assertEqual(6, len(decoded_rows(files['inventory.jsonl'])))
        self.assertTrue(all(row['selected_union_sources'] == 0 for row in decoded_rows(files['artist-counts.jsonl'])))
        denominators = json.loads(files['denominators.json'])
        self.assertEqual((10, 8, 2), (denominators['historical_catalog_registered_artists'],
                                    denominators['historical_catalog_source_eligible_artists'],
                                    denominators['historical_catalog_artists_without_source_rows']))
        self.assertFalse(denominators['full_per_artist_catalog_roster_available'])
        self.assertFalse(denominators['global_catalog_inclusion_probabilities_known'])
        for row in decoded_rows(files['inventory.jsonl']):
            self.assertEqual('unqualified', row['identity_family_closure'])
            self.assertFalse(row['eligible_for_release_evaluation'])
            self.assertIsNone(row['provider'])
            self.assertIsNone(row['source_knowledge_cutoff'])
            self.assertIsNone(row['format'])
            self.assertIn('prior_development_packet_payload_decode', row['exposure'])

    def test_reservation_bridge_through_unselected_records_closes_before_sampling(self):
        self.add_bridge('source:3', 'source:7')
        self.amendment['exposures'].append({
            'source_key': selection.resource_key('source:7'), 'classification': 'reserved_access',
            'provenance': 'synthetic-metadata:exposure-7',
        })
        self.select(clusters=3, coverage=1)
        files, counts = self.plan()
        self.assertEqual(3, counts['excluded_by_closure_sources'])
        self.assertEqual({'source:4', 'source:5', 'source:6'}, {
            row['original_metadata']['reference'] for row in decoded_rows(files['selection.jsonl'])})
        for row in decoded_rows(files['inventory.jsonl'])[:3]:
            self.assertFalse(row['selection_eligible'])
            self.assertIn('additional_reserved_exposure', row['exclusion_reasons'])
            self.assertIn('additional_reserved_access', row['exposure'])
        artist = next(row for row in decoded_rows(files['artist-counts.jsonl'])
                      if row['artist_uuid'] == self.rows[0]['artist_uuid'])
        self.assertEqual(0, artist['selected_union_sources'])

    def test_source_track_and_local_namespaces_never_alias_without_explicit_bridge(self):
        files, _ = self.plan()
        first = decoded_rows(files['inventory.jsonl'])[0]
        self.assertTrue(first['selection_eligible'])
        self.assertNotIn('original_exposed_track', first['exposure'])
        self.assertEqual(5, len({selection.resource_key(reference) for reference in
                                ['source:1', 'track:1', 'venue:1', 'local:source:1', 'local:track:1']}))
        self.add_bridge('source:1', 'local:source:1')
        _, counts = self.plan()
        self.assertEqual(3, counts['excluded_by_closure_sources'])

    def test_probability_sample_selects_whole_components_with_conditional_exact_probability(self):
        self.select(clusters=2)
        files, counts = self.plan()
        chosen = decoded_rows(files['selection.jsonl'])
        full = decoded_rows(files['inventory.jsonl'])
        selected_groups = {row['component_key'] for row in chosen}
        self.assertEqual(2, len(selected_groups))
        self.assertEqual({row['source_key'] for row in full if row['component_key'] in selected_groups},
                         {row['source_key'] for row in chosen})
        for row in chosen:
            self.assertEqual({'numerator': 2, 'denominator': 4},
                             row['probability_sample']['source_inclusion_probability'])
            self.assertEqual({'numerator': 1, 'denominator': 1},
                             row['probability_sample']['within_component_inclusion_probability'])
            self.assertIsNone(row['union_inclusion_probability'])
        self.assertEqual(len(chosen), counts['probability_sources'])

    def test_coverage_challenge_and_probability_panels_reconcile_without_union_calibration(self):
        self.select(clusters=2, coverage=1)
        files, counts = self.plan()
        self.assertEqual(counts['selected_union_sources'], counts['probability_sources']
                         + counts['coverage_sources'] - counts['panel_overlap_sources'])
        for row in decoded_rows(files['selection.jsonl']):
            self.assertIsNone(row['union_inclusion_probability'])
        artists = decoded_rows(files['artist-counts.jsonl'])
        self.assertTrue(all(row['coverage_challenge_sources'] >= 1 for row in artists))
        self.assertEqual(6, sum(row['permitted_metadata_sources'] for row in artists))

    def test_reordered_safe_metadata_and_bridge_order_preserve_selection_bytes(self):
        self.select(clusters=2, coverage=1)
        self.add_bridge('source:4', 'source:5')
        self.add_bridge('source:5', 'source:6')
        before, counts = self.plan()
        self.index['eligible_source_metadata'].reverse()
        self.amendment['bridges'].reverse()
        after, after_counts = self.plan()
        # Input order is preserved in operator provenance; actual selection and
        # inventory must remain stable even when safe metadata enumeration moves.
        for name in ('inventory.jsonl', 'selection.jsonl', 'capture-requests.jsonl', 'artist-counts.jsonl', 'denominators.json'):
            self.assertEqual(before[name], after[name], name)
        self.assertEqual(counts, after_counts)

    def test_non_development_and_duplicate_metadata_fail_before_decode(self):
        self.index['eligible_source_metadata'][0]['family_partition'] = 'test'
        with self.assertRaisesRegex(ValueError, 'non-development'):
            self.plan()
        self.index['eligible_source_metadata'][0]['family_partition'] = 'development'
        self.index['eligible_source_metadata'][1]['reference'] = 'source:1'
        with self.assertRaisesRegex(ValueError, 'Duplicate metadata'):
            self.plan()

    def test_full_flow_retains_excluded_tracks_and_linked_parents_before_selected_decode(self):
        # Match the real safe-index resource mix: three excluded sources and
        # three excluded tracks. Track numeric IDs overlap eligible source IDs.
        self.add_mixed_exclusions()
        # The original gate already rejects an eligible row named directly as a
        # reserved parent. Use an unselected parent with the same family so this
        # test reaches the selector's transitive closure instead of failing there.
        self.index['metadata_linked_parent_sources'] = [self.native_match(8, 'source', 'fc')]
        permitted = self.helpers.development_metadata(self.index)
        self.assertEqual(6, len(permitted))
        self.assertIn('source:4', {row['reference'] for row in permitted.values()})
        self.select(clusters=2)
        raw = self.raw_fixture()
        raw.write_bytes(raw.read_bytes() + b'malformed excluded resource payload\n' * 5)
        pin = self.helpers.file_record(raw, self.repo)
        self.index['annotation_evidence_sha256'] = pin['sha256']
        (self.repo / selection.ANNOTATION_MANIFEST).write_bytes(selection.json_bytes({
            'files': {'evidence.jsonl': pin},
        }))
        packet = self.build()
        _, _, files, counts = selection.verify(self.repo, packet, self.bundle)
        self.assertEqual(6, counts['permitted_metadata_sources'])
        self.assertEqual(6, counts['original_excluded_metadata_rows'])
        self.assertEqual(2, counts['excluded_by_closure_sources'])
        self.assertEqual({'source:1', 'source:2', 'source:3', 'source:6'}, {
            row['original_metadata']['reference'] for row in decoded_rows(files['selection.jsonl'])})
        inventory = {row['original_metadata']['reference']: row
                     for row in decoded_rows(files['inventory.jsonl'])}
        self.assertIn('original_reserved_parent_source', inventory['source:4']['exclusion_reasons'])
        self.assertIn('original_excluded_annotation', inventory['source:5']['exclusion_reasons'])
        ledger = json.loads(files['reservation-exposure-ledger.json'])
        self.assertEqual(3, sum(row['reference'].startswith('track:')
                                for row in ledger['original_metadata']['excluded_annotation_metadata']))
        output = self.repo / 'local-dev/normalization-data/track-safe-originals'
        self.assertEqual({'complete_originals': 4, 'raw_payloads_decoded': 4},
                         selection.capture(self.repo, packet, output, self.bundle))

    def test_matched_tracks_close_known_native_parents_and_exposure_before_full_flow_capture(self):
        self.add_mixed_exclusions()
        # None of these five matched tracks is one of the excluded track rows.
        # Track 41 has no family itself: only its authenticated parent link can
        # propagate its exposure to source 4's recording family.
        parents = [self.native_match(number, 'source', family) for number, family in
                   ((21, 'fc'), (22, 'fp2'), (23, 'fp3'), (24, 'fp4'))]
        tracks = [self.native_match(number, 'track', family, parent) for number, parent, family in
                  ((41, 21, None), (42, 22, 'fp2'), (43, 23, 'fp3'),
                   (44, 23, 'fp3'), (45, 24, 'fp4'))]
        local_parent = self.native_match(21, 'source', 'fc', local=True)
        local_track = self.native_match(41, 'track', 'fc', 21, local=True)
        absent = self.native_match(46, 'track', None, 999)
        wrong_namespace = self.native_match(47, 'track', None, 22, local=True)
        missing_id = self.native_match(48, 'track', None)
        self.index['metadata_matches'] = parents + tracks + [
            local_parent, local_track, absent, wrong_namespace, missing_id,
        ]
        self.index['metadata_linked_parent_sources'] = copy.deepcopy(parents)
        self.index['known_exposed_track_references'].extend(['track:41', 'local:track:41'])
        self.index['reserved_references'].extend(['track:41', 'local:track:41'])
        self.assertEqual(6, len(self.helpers.development_metadata(self.index)))
        self.select(clusters=2)
        raw = self.raw_fixture()
        raw.write_bytes(raw.read_bytes() + b'malformed excluded resource payload\n' * 5)
        pin = self.helpers.file_record(raw, self.repo)
        self.index['annotation_evidence_sha256'] = pin['sha256']
        (self.repo / selection.ANNOTATION_MANIFEST).write_bytes(selection.json_bytes({'files': {'evidence.jsonl': pin}}))
        packet = self.build()
        _, _, files, counts = selection.verify(self.repo, packet, self.bundle)
        self.assertEqual(6, counts['authenticated_metadata_parent_links'])
        self.assertEqual(4, counts['unresolved_metadata_parent_links'])
        ledger = json.loads(files['reservation-exposure-ledger.json'])
        components = {member: row for row in ledger['components'] for member in row['members']}
        for track in tracks + [local_track]:
            key = selection.resource_key(track['reference'])
            prefix = 'local:' if track['reference'].startswith('local:') else ''
            parent_key = selection.resource_key(prefix + 'source:' + str(track['source_id']))
            self.assertEqual(components[key]['component_key'], components[parent_key]['component_key'])
        self.assertIn('original_exposed_track', components[selection.resource_key('source:21')]['exposure'])
        self.assertNotEqual(components[selection.resource_key('source:21')]['component_key'],
                            components[selection.resource_key('local:source:21')]['component_key'])
        self.assertNotIn(selection.resource_key('source:999'), components)
        self.assertNotIn(selection.resource_key('local:source:22'), components)
        statuses = {row['track_reference']: row for row in ledger['known_parent_links']}
        self.assertEqual('parent_not_retained_in_safe_metadata', statuses['track:46']['status'])
        self.assertEqual('parent_not_retained_in_safe_metadata', statuses['local:track:47']['status'])
        self.assertEqual('parent_id_missing', statuses['track:48']['status'])
        self.assertTrue(statuses['track:41']['metadata_match_pointer'].startswith('/metadata_matches/'))
        self.assertTrue(statuses['track:41']['parent_pointer'].startswith('/metadata_matches/'))
        inventory = {row['original_metadata']['reference']: row for row in decoded_rows(files['inventory.jsonl'])}
        self.assertIn('original_exposed_track', inventory['source:4']['exposure'])
        self.assertFalse(inventory['source:4']['selection_eligible'])
        self.assertEqual({'source:1', 'source:2', 'source:3', 'source:6'}, {
            row['original_metadata']['reference'] for row in decoded_rows(files['selection.jsonl'])})
        output = self.repo / 'local-dev/normalization-data/matched-parent-originals'
        self.assertEqual(4, selection.capture(self.repo, packet, output, self.bundle)['raw_payloads_decoded'])

    def test_conflicting_known_parent_coordinates_fail_before_any_original_decode(self):
        parent = self.native_match(21, 'source', 'fc')
        track = self.native_match(41, 'track', 'different-family', 21)
        self.index['metadata_matches'] = [parent, track]
        with patch.object(self.helpers, 'selected_originals', side_effect=AssertionError('raw decode')):
            with self.assertRaisesRegex(ValueError, 'track/parent families differ'):
                self.plan()

    def test_capture_rejects_outside_root_before_any_original_decode(self):
        self.select(clusters=1)
        packet = self.build()
        with patch.object(self.helpers, 'selected_originals', side_effect=AssertionError('outside path decoded')):
            with self.assertRaisesRegex(ValueError, 'must remain under ignored'):
                selection.capture(self.repo, packet, self.repo / 'outside-originals', self.bundle)

    def test_runtime_source_identity_excludes_guide_tests_and_private_review_history(self):
        before = selection.execution_sources(self.bundle)
        (self.bundle / 'README.md').write_text('An ordinary maintained guide edit.\n')
        (self.bundle / 'test_select_people_evidence.py').write_text('# Updated synthetic tests.\n')
        private = self.bundle / 'private-verification'
        private.mkdir()
        (private / 'source-review.md').write_text('Additional review history.\n')
        self.assertEqual(before, selection.execution_sources(self.bundle))
        (self.bundle / 'select_people_evidence.py').write_text('# Executed code changed.\n')
        self.assertNotEqual(before, selection.execution_sources(self.bundle))

    def test_unknown_bridge_and_excess_sample_or_missing_seed_fail_explicitly(self):
        self.add_bridge('source:1', 'source:999')
        with self.assertRaisesRegex(ValueError, 'outside authenticated metadata'):
            self.plan()
        self.amendment['bridges'].clear()
        self.select(clusters=5)
        with self.assertRaisesRegex(ValueError, 'exceed'):
            self.plan()
        self.config['seed'] = ''
        with self.assertRaisesRegex(ValueError, 'preregistered'):
            self.plan()

    def test_partial_publication_has_no_complete_manifest_and_recovers_only_in_new_directory(self):
        original_open = Path.open

        def fail_selection(path, mode='r', *args, **kwargs):
            if path.name == 'selection.jsonl' and mode == 'xb':
                raise OSError('synthetic interrupted write')
            return original_open(path, mode, *args, **kwargs)

        with patch.object(Path, 'open', fail_selection):
            with self.assertRaisesRegex(OSError, 'interrupted'):
                self.build('partial')
        partial = self.repo / 'partial'
        self.assertTrue((partial / 'inventory.jsonl').exists())
        self.assertFalse((partial / 'manifest.json').exists())
        with self.assertRaises(FileNotFoundError):
            selection.verify(self.repo, partial, self.bundle)
        with self.assertRaises(FileExistsError):
            self.build('partial')
        packet = self.build('recovered')
        self.assertEqual(6, selection.verify(self.repo, packet, self.bundle)[3]['permitted_metadata_sources'])

    def test_validator_recomputes_selection_even_when_mutated_artifact_hash_is_updated(self):
        self.select(clusters=1)
        packet = self.build()
        path = packet / 'selection.jsonl'
        rows = decoded_rows(path.read_bytes())
        rows[0]['identity_label'] = 'invented_label'
        path.write_bytes(selection.rows_bytes(rows))
        manifest = json.loads((packet / 'manifest.json').read_bytes())
        manifest['artifacts'] = [selection.stamp(path, path.name) if row['path'] == path.name else row
                                 for row in manifest['artifacts']]
        (packet / 'manifest.json').write_bytes(selection.json_bytes(manifest))
        with self.assertRaisesRegex(ValueError, 'Selection differs'):
            selection.verify(self.repo, packet, self.bundle)

    def test_capture_uses_real_gate_preserves_complete_unicode_and_ignores_malformed_heldout(self):
        self.select(clusters=4)
        self.raw_fixture()
        packet = self.build()
        output = self.repo / 'local-dev/normalization-data/selected-originals'
        output.parent.mkdir(parents=True, exist_ok=True)
        counts = selection.capture(self.repo, packet, output, self.bundle)
        self.assertEqual({'complete_originals': 6, 'raw_payloads_decoded': 6}, counts)
        rows = decoded_rows((output / 'originals.jsonl').read_bytes())
        self.assertEqual(6, len(rows))
        self.assertTrue(all('Second line.' in row['original_record']['raw_record']['row']['description'] for row in rows))
        self.assertIn('\u2028', rows[0]['original_record']['raw_record']['row']['taper'])
        self.assertEqual(counts, selection.verify_capture(self.repo, packet, output, self.bundle))
        with patch.object(self.helpers, 'selected_originals', side_effect=AssertionError('existing output read raw')):
            with self.assertRaisesRegex(ValueError, 'already exists'):
                selection.capture(self.repo, packet, output, self.bundle)

    def test_capture_provenance_accepts_path_aliases_but_rejects_a_different_packet_directory(self):
        self.select(clusters=4)
        self.raw_fixture()
        packet = self.build()
        alias = self.repo / 'selection-alias'
        alias.symlink_to(packet, target_is_directory=True)
        relative_packet = Path('selection')
        temporary_alias = Path(self.temporary.name) / 'selection'
        cases = ((packet, relative_packet), (relative_packet, packet),
                 (alias, relative_packet), (temporary_alias, packet))
        outputs = []
        with chdir(self.repo):
            for number, (capture_packet, verify_packet) in enumerate(cases):
                with self.subTest(capture=str(capture_packet), verify=str(verify_packet)):
                    output = Path('local-dev/normalization-data/path-copy-' + str(number))
                    expected = selection.capture(self.repo, capture_packet, output, self.bundle)
                    self.assertEqual(expected, selection.verify_capture(self.repo, verify_packet, output, self.bundle))
                    manifest = json.loads((output / 'manifest.json').read_bytes())
                    self.assertEqual(str((packet / 'manifest.json').resolve()), manifest['selection_manifest']['path'])
                    outputs.append(output.resolve())
        # An explicitly named artifact remains relative. Only the actual selected
        # manifest provenance is canonicalized; arbitrary saved paths are not.
        self.assertEqual('manifest.json', selection.stamp(packet / 'manifest.json', 'manifest.json')['path'])
        foreign_packet = self.build('different-packet')
        self.assertEqual((packet / 'manifest.json').read_bytes(), (foreign_packet / 'manifest.json').read_bytes())
        with self.assertRaisesRegex(ValueError, 'Packet manifest contract differs'):
            selection.verify_capture(self.repo, foreign_packet, outputs[0], self.bundle)

    def test_capture_bridge_exclusion_precedes_malformed_blocked_development_decode(self):
        self.add_bridge('source:3', 'source:7')
        self.select(clusters=3)
        raw = self.raw_fixture()
        lines = raw.read_bytes().split(b'\n')
        lines[0:3] = [b'blocked-malformed-development-json'] * 3
        raw.write_bytes(b'\n'.join(lines))
        pin = self.helpers.file_record(raw, self.repo)
        self.index['annotation_evidence_sha256'] = pin['sha256']
        (self.repo / selection.ANNOTATION_MANIFEST).write_bytes(selection.json_bytes({
            'files': {'evidence.jsonl': pin},
        }))
        packet = self.build()
        output = self.repo / 'local-dev/normalization-data/selected-originals'
        self.assertEqual(3, selection.capture(self.repo, packet, output, self.bundle)['raw_payloads_decoded'])

    def test_changed_raw_hash_and_selected_source_identity_are_rejected_without_output(self):
        self.select(clusters=4)
        raw = self.raw_fixture()
        packet = self.build()
        original = raw.read_bytes()
        raw.write_bytes(original + b'changed bytes\n')
        output = self.repo / 'local-dev/normalization-data/selected-originals'
        with self.assertRaisesRegex(ValueError, 'Input changed'):
            selection.capture(self.repo, packet, output, self.bundle)
        self.assertFalse(output.exists())
        raw.write_bytes(original)
        # A frozen annotation with a wrong selected source tuple cannot bypass
        # the real selected_originals contract merely by matching its file hash.
        lines = original.split(b'\n')
        row = json.loads(lines[0])
        row['raw_record']['row']['id'] = 999
        lines[0] = self.helpers.canonical(row)
        raw.write_bytes(b'\n'.join(lines))
        pin = self.helpers.file_record(raw, self.repo)
        self.index['annotation_evidence_sha256'] = pin['sha256']
        (self.repo / selection.ANNOTATION_MANIFEST).write_bytes(selection.json_bytes({'files': {'evidence.jsonl': pin}}))
        changed_packet = self.build('changed-source')
        with self.assertRaisesRegex(ValueError, 'source ID'):
            selection.capture(self.repo, changed_packet, output, self.bundle)
        self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
