"""Synthetic contract tests; never read catalog data, labels or held-out answers."""

import copy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import people_identity_packet as packet


# Actual enriched contract retained by primary-site-http-receipt.json. The unit
# fixture never reads that source or a held-out payload.
VISIBLE_TEXT_RECEIPT = {
    'path': 'local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v2-discovery/primary/sonicarchives-visible-text.txt',
    'bytes': 1020,
    'sha256': 'cd1f52cd6b4d0380fbb6e5742b406d74843213d94c0a82de30e413e42043df5a',
    'recipe': 'Python HTMLParser convert_charrefs; exclude script/style data; join text nodes and collapse whitespace',
}


class TestFileAuthentication(unittest.TestCase):
    def test_retained_enriched_receipt_authenticates_required_file_fields(self):
        root = Path('/synthetic-repository')
        actual = {key: VISIBLE_TEXT_RECEIPT[key] for key in ('path', 'bytes', 'sha256')}
        with patch.object(packet, 'file_record', return_value=actual) as read_file:
            packet.authenticate(root, VISIBLE_TEXT_RECEIPT)
        read_file.assert_called_once_with(root / VISIBLE_TEXT_RECEIPT['path'], root)

    def test_enriched_receipt_rejects_changed_bytes_even_when_length_matches(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            path = root / 'visible-text.txt'
            path.write_bytes(b'original context')
            receipt = packet.file_record(path, root) | {'recipe': VISIBLE_TEXT_RECEIPT['recipe']}
            packet.authenticate(root, receipt)
            path.write_bytes(b'changed! context')
            self.assertEqual(path.stat().st_size, receipt['bytes'])
            with self.assertRaisesRegex(packet.ContractError, 'Input changed: visible-text.txt'):
                packet.authenticate(root, receipt)

    def test_descriptive_metadata_does_not_make_required_identity_fields_optional(self):
        for key in ('path', 'bytes', 'sha256'):
            with self.subTest(missing=key):
                receipt = {name: value for name, value in VISIBLE_TEXT_RECEIPT.items() if name != key}
                with patch.object(packet, 'file_record') as read_file:
                    with self.assertRaisesRegex(packet.ContractError, 'requires path, bytes and sha256'):
                        packet.authenticate(Path('/synthetic-repository'), receipt)
                    read_file.assert_not_called()


def metadata(eid='source:1', artist='artist-a', line=1, group='group-a'):
    return {
        'evidence_id': eid, 'reference': eid, 'source_line': line,
        'artist_uuid': artist, 'artist_partition': 'development',
        'family_partition': 'development', 'family_group': group,
        'family': 'known-' + group,
    }


def original(meta, text='A😀B\u2028complete context'):
    return {
        'evidence_id': meta['evidence_id'],
        'artist': {'uuid': meta['artist_uuid'], 'name': meta['artist_uuid']},
        'artist_partition': 'development', 'family_partition': 'development',
        'input_duplicate_family': meta['family_group'],
        'known_recording_family': meta['family'],
        'raw_record': {'row': {'id': int(meta['reference'].split(':')[1]),
                               'taper': text, 'untouched_context': 'retain this whole field'}},
    }


def index_for(meta):
    return {
        'eligible_source_metadata': [meta], 'excluded_annotation_metadata': [],
        'reserved_annotation_family_groups': [], 'reserved_known_families': [],
        'reserved_references': [], 'metadata_linked_parent_sources': [],
    }


def citation(eid, text, start=0, end=1):
    return {
        'evidence_id': eid, 'field_path': '/raw_record/row/taper',
        'field_sha256': packet.digest(text.encode()),
        'span': {'start': start, 'end': end, 'quote': text[start:end],
                 'unit': 'unicode_code_points'},
    }


def family_fixture():
    originals = {}
    occurrences = []
    for number, artist in enumerate(['artist-c', 'artist-a', 'artist-b'], 1):
        meta = metadata(f'source:{number}', artist, number, f'group-{number}')
        record = original(meta)
        originals[meta['evidence_id']] = {'metadata': meta, 'original_record': record}
        c = citation(meta['evidence_id'], record['raw_record']['row']['taper'])
        occurrences.append({
            'evidence_id': meta['evidence_id'], 'artist': record['artist'],
            'family_group': meta['family_group'], 'field': 'taper',
            'field_sha256': c['field_sha256'], 'kind': 'email',
            'match_span': c['span'], 'context_span': c['span'],
        })
    family = {
        'family_id': 'hypothesis-only', 'evidence_ids': list(reversed(originals)),
        'family_group_ids': [r['metadata']['family_group'] for r in originals.values()],
        'source_count': 3, 'artist_count': 3,
        'canonical_person_creation_authorized': False, 'policy_activation_permitted': False,
        'supporting_occurrences': occurrences, 'structured_credit_citations': [],
        'rationale': 'SECRET PREVIOUS ANSWER', 'linkage_status': 'same-or-different-draft',
        'person_kind': 'previous-kind-guess',
    }
    return originals, family


class TestReservationGate(unittest.TestCase):
    def test_gate_rejects_every_exclusion_kind_before_payload_read(self):
        meta = metadata()
        for field, value in [('artist_partition', 'test'), ('family_partition', 'validation')]:
            with self.subTest(field=field):
                changed = dict(meta, **{field: value})
                with self.assertRaises(packet.ContractError):
                    packet.development_metadata(index_for(changed))
        for field, value in [
            ('reserved_references', [meta['reference']]),
            ('reserved_known_families', [meta['family']]),
            ('reserved_annotation_family_groups', [meta['family_group']]),
            ('excluded_annotation_metadata', [{'evidence_id': meta['evidence_id']}]),
            ('metadata_linked_parent_sources', [{'reference': meta['reference']}]),
        ]:
            with self.subTest(field=field):
                idx = index_for(meta)
                idx[field] = value
                with self.assertRaises(packet.ContractError):
                    packet.development_metadata(idx)

    def test_unselected_payload_is_never_decoded_and_unicode_lines_survive(self):
        meta = metadata(line=2)
        payload = original(meta)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'raw.jsonl'
            # Forbidden raw text is intentionally not valid JSON. Decoding it
            # would fail this test before the safe physical line is reached.
            path.write_bytes(b'UNTOUCHED FORBIDDEN PAYLOAD\n' + packet.canonical(payload) + b'\n')
            with patch.object(packet.json, 'loads', wraps=json.loads) as decode:
                values = packet.selected_originals(path, packet.development_metadata(index_for(meta)))
            self.assertEqual(decode.call_count, 1)
            self.assertEqual(values[meta['evidence_id']]['original_record'], payload)
            self.assertIn('\u2028', payload['raw_record']['row']['taper'])
            self.assertEqual(values[meta['evidence_id']]['annotation_line'], 2)

    def test_load_authentication_failure_never_reaches_raw_decoder(self):
        with tempfile.TemporaryDirectory() as temp:
            with patch.object(packet, 'authenticate', side_effect=packet.ContractError('pin failed')):
                with patch.object(packet, 'selected_originals') as decode_raw:
                    with self.assertRaisesRegex(packet.ContractError, 'pin failed'):
                        packet.DevelopmentSources.load(Path(temp))
                    decode_raw.assert_not_called()

    def test_load_exclusion_failure_never_reaches_raw_decoder(self):
        bad = metadata()
        bad['artist_partition'] = 'test'
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            index_path = root / packet.RESERVATIONS
            index_path.parent.mkdir(parents=True)
            index_path.write_bytes(packet.encode_json(index_for(bad)))
            with patch.object(packet, 'authenticate'):
                with patch.object(packet, 'selected_originals') as decode_raw:
                    with self.assertRaisesRegex(packet.ContractError, 'non-development'):
                        packet.DevelopmentSources.load(root)
                    decode_raw.assert_not_called()

    def test_load_changed_shared_raw_hash_never_reaches_raw_decoder(self):
        meta = metadata()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            index_path = root / packet.RESERVATIONS
            index_path.parent.mkdir(parents=True)
            idx = index_for(meta)
            idx['annotation_evidence_sha256'] = 'expected-source-hash'
            index_path.write_bytes(packet.encode_json(idx))
            discovery = root / packet.DISCOVERY_PIN['path']
            discovery.parent.mkdir(parents=True)
            discovery.write_bytes(packet.encode_json({'artifacts': {}}))
            with patch.object(packet, 'authenticate'), patch.dict(packet.EXPECTED, {'eligible_originals_authenticated': 1}):
                with patch.object(packet, 'file_record', return_value={'path': str(packet.ANNOTATION), 'bytes': 1, 'sha256': 'changed-source-hash'}):
                    with patch.object(packet, 'selected_originals') as decode_raw:
                        with self.assertRaisesRegex(packet.ContractError, 'Shared annotation source bytes changed'):
                            packet.DevelopmentSources.load(root)
                        decode_raw.assert_not_called()

    def test_selected_payload_cannot_contradict_development_projection(self):
        meta = metadata()
        payload = original(meta)
        payload['family_partition'] = 'test'
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'raw.jsonl'
            path.write_bytes(packet.canonical(payload) + b'\n')
            with self.assertRaisesRegex(packet.ContractError, 'payload partition'):
                packet.selected_originals(path, {1: meta})


class TestSourceEvidence(unittest.TestCase):
    def test_codepoints_not_utf8_bytes_and_field_hash_are_required(self):
        meta = metadata()
        text = 'A😀B'
        originals = {meta['evidence_id']: {'original_record': original(meta, text)}}
        valid = citation(meta['evidence_id'], text, 1, 2)
        packet.verify_span(originals, valid, 'synthetic')
        wrong = copy.deepcopy(valid)
        wrong['span']['end'] = 5  # UTF-8 width is not a Unicode index.
        with self.assertRaisesRegex(packet.ContractError, 'Unicode bounds'):
            packet.verify_span(originals, wrong, 'synthetic')
        wrong = copy.deepcopy(valid)
        wrong['field_sha256'] = '0' * 64
        with self.assertRaisesRegex(packet.ContractError, 'field hash'):
            packet.verify_span(originals, wrong, 'synthetic')
        wrong = copy.deepcopy(valid)
        wrong['span']['quote'] = 'B'
        with self.assertRaisesRegex(packet.ContractError, 'exact source quote'):
            packet.verify_span(originals, wrong, 'synthetic')

    def test_inventory_checks_source_membership_not_just_matching_quotes(self):
        originals, family = family_fixture()
        self.assertEqual(packet.verify_inventory([family], originals), 6)
        family['evidence_ids'].remove('source:3')
        family['source_count'] = 2
        family['artist_count'] = 2
        family['family_group_ids'].remove('group-3')
        with self.assertRaisesRegex(packet.ContractError, 'occurrence source coverage'):
            packet.verify_inventory([family], originals)

    def test_star_questions_are_deterministic_unlabeled_and_answer_blind(self):
        originals, family = family_fixture()
        questions = packet.make_questions([(34, family)], originals)
        reordered = copy.deepcopy(family)
        reordered['evidence_ids'].reverse()
        self.assertEqual(questions, packet.make_questions([(34, reordered)], originals))
        self.assertEqual(len(questions), 2)
        for q in questions:
            self.assertIsNone(q['answer'])
            self.assertIsNone(q['adjudication'])
            self.assertIsNone(q['left']['actor_kind'])
            self.assertFalse(q['exposure']['eligible_for_release_evaluation'])
            self.assertNotEqual(q['left']['artist_uuid'], q['right']['artist_uuid'])
            self.assertNotIn('inventory_line', q)
        encoded = packet.canonical(questions).decode()
        for secret in ['SECRET PREVIOUS ANSWER', 'same-or-different-draft', 'previous-kind-guess']:
            self.assertNotIn(secret, encoded)

    def test_first_reviewer_guide_is_self_contained(self):
        guide = packet.REVIEW_GUIDE
        for field in ['question_id', 'field_sha256', 'unicode_code_points', 'contradiction_basis', 'prior_exposure']:
            self.assertIn(field, guide)
        self.assertNotIn('Refer to the tool README', guide)
        self.assertNotIn('SECRET PREVIOUS ANSWER', guide)

    def test_cross_artist_duplicate_recording_pair_is_rejected(self):
        originals, family = family_fixture()
        for item in originals.values():
            item['metadata']['family_group'] = 'same-upload-family'
        with self.assertRaisesRegex(packet.ContractError, 'repeats a recording'):
            packet.make_questions([(34, family)], originals)


class TestFrozenPublication(unittest.TestCase):
    def source_stub(self, files):
        return SimpleNamespace(
            planned_files=lambda: (files, {'test': 1}), inputs=[],
            tool_manifest_sha256='synthetic-tool', tool_manifest={'artifacts': []},
        )

    def test_no_overwrite_and_no_manifest_after_partial_failure(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'packet'
            with patch.object(packet, 'file_record', side_effect=OSError('simulated disk failure')):
                with self.assertRaises(OSError):
                    packet.publish_packet(output, {'evidence.jsonl': b'context\n'}, {})
            self.assertFalse((output / 'manifest.json').exists())
            with self.assertRaises(FileExistsError):
                packet.publish_packet(output, {'evidence.jsonl': b'replacement\n'}, {})
            self.assertEqual((output / 'evidence.jsonl').read_bytes(), b'context\n')

    def test_rehashing_tampered_context_does_not_replace_authenticated_sources(self):
        files = {'evidence.jsonl': b'complete original context\n'}
        sources = self.source_stub(files)
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'packet'
            packet.publish_packet(output, files, packet.packet_manifest(sources, {'test': 1}))
            packet.verify_packet(output, sources)
            (output / 'evidence.jsonl').write_bytes(b'missing context\n')
            manifest = json.loads((output / 'manifest.json').read_bytes())
            manifest['artifacts'] = [packet.file_record(output / 'evidence.jsonl', output)]
            (output / 'manifest.json').write_bytes(packet.encode_json(manifest))
            with self.assertRaisesRegex(packet.ContractError, 'complete source context'):
                packet.verify_packet(output, sources)

    def test_reviews_cannot_be_written_into_the_frozen_packet(self):
        files = {'questions.jsonl': b'null answers\n'}
        sources = self.source_stub(files)
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'packet'
            packet.publish_packet(output, files, packet.packet_manifest(sources, {'test': 1}))
            (output / 'my-answers.jsonl').write_bytes(b'answers\n')
            with self.assertRaisesRegex(packet.ContractError, 'Unexpected or missing packet files'):
                packet.verify_packet(output, sources)


if __name__ == '__main__':
    unittest.main()
