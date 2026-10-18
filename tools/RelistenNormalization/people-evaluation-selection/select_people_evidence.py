#!/usr/bin/env python3
"""Inventory retained development metadata, select sources, and optionally copy originals.

Selection never opens annotation payloads. The separate capture command first
revalidates the frozen selection, then uses the pinned development-only line gate.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import importlib.util
import json
from pathlib import Path
import random
import re
import sys


VERSION = 'people-evaluation-selection:3'
HELPER = 'tools/RelistenNormalization/people-identity-packet/people_identity_packet.py'
RESERVATIONS = 'local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v1/reserved-reference-index.json'
RESERVATION_RECEIPT = 'docs/research/2026-10-17-full-normalization/training/people-development-v1/reservation-receipt.json'
COVERAGE = 'docs/research/2026-10-17-full-normalization/scale/evidence/coverage.json'
PRIOR_PACKET = 'local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-verification-v1/discovery/packet-manifest.json'
ANNOTATION_MANIFEST = 'local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/manifest.json'
ANNOTATION = 'local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/evidence.jsonl'
METADATA_FIELDS = {
    'artist_partition', 'artist_uuid', 'evidence_id', 'family', 'family_group',
    'family_partition', 'reference', 'source_line',
}
SCOPE = {
    'partition': 'development', 'identity_family_closure': 'unqualified',
    'eligible_for_release_evaluation': False, 'policy_activation_permitted': False,
    'canonical_person_creation_authorized': False,
}
EMPTY_AMENDMENT = {
    'schema_version': 'people-selection-reservations:1',
    'bridges': [], 'excluded_source_keys': [], 'exposures': [],
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode()


def rows_bytes(rows):
    return b''.join((json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(',', ':')) + '\n').encode() for row in rows)


def stamp(path, name=None):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return {'path': name or str(path), 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()}


def resource_key(reference):
    """Retained source, track, venue and local IDs never alias by numeric suffix."""
    require(isinstance(reference, str), 'Reference must be a namespaced string')
    if re.fullmatch(r'(source|track|venue):[1-9][0-9]*', reference):
        return 'catalog-2026-10-17/' + reference
    require(re.fullmatch(r'local:(source|track):[1-9][0-9]*', reference) is not None,
            'Unsupported reference namespace: ' + reference)
    return 'local-catalog/' + reference.removeprefix('local:')


def recording_family_node(key, family):
    return 'recording/' + key.split('/', 1)[0] + '/' + family


def load_helpers(repo, bundle):
    """Authenticate executable code before import; imports perform no data reads."""
    lock = json.loads((bundle / 'base-inputs.json').read_bytes())
    require(lock['schema_version'] == 'people-evaluation-selection-inputs:1', 'Wrong dependency lock')
    inputs = lock['artifacts']
    paths = [record['path'] for record in inputs]
    require(len(paths) == len(set(paths)) and set(paths) == {
        HELPER, RESERVATIONS, RESERVATION_RECEIPT, COVERAGE, PRIOR_PACKET, ANNOTATION_MANIFEST,
    }, 'Dependency lock must contain only the complete executed-helper/data authority')
    helper_pin, = [record for record in inputs if record['path'] == HELPER]
    require(stamp(repo / HELPER, HELPER) == helper_pin, 'Pinned packet helper changed')
    spec = importlib.util.spec_from_file_location('people_selection_pinned_packet', repo / HELPER)
    helpers = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = helpers
    spec.loader.exec_module(helpers)
    for record in inputs:
        helpers.authenticate(repo, record)
    require(helpers.RESERVATION_PIN == next(record for record in inputs if record['path'] == RESERVATIONS),
            'Packet helper reservation authority differs')
    return helpers, inputs, execution_sources(bundle)


def execution_sources(bundle):
    # A retained run records the code/contract/lock it used. Ordinary README, test
    # and private review edits do not alter runtime authority or output identity.
    return [stamp(bundle / name, name) for name in (
        'select_people_evidence.py', 'selection-contract.json', 'base-inputs.json',
    )]


class ReservationClosure:
    """Conservatively join safe recording groups and explicit metadata bridges.

    No string/contact similarity creates an identity link. Additional bridges only
    constrain selection and propagate exposure; they are never identity labels.
    """

    def __init__(self, index, amendment):
        self.parent = {}
        self.reasons = defaultdict(set)
        self.exposures = defaultdict(set)
        self.parent_links = []
        seen_keys = set()
        for permitted, rows in ((True, index['eligible_source_metadata']),
                                (False, index['excluded_annotation_metadata'])):
            for row in rows:
                require(set(row) == METADATA_FIELDS, 'Unexpected safe metadata fields')
                key = resource_key(row['reference'])
                require(key not in seen_keys, 'Duplicate metadata reference')
                require(not permitted or key.startswith('catalog-2026-10-17/source:'),
                        'Permitted inventory requires source metadata')
                seen_keys.add(key)
                self.find(key)
                self.join(key, 'duplicate/' + key.split('/', 1)[0] + '/' + row['family_group'])
                if row['family'] is not None:
                    self.join(key, recording_family_node(key, row['family']))
        for reference in index['reserved_references']:
            self.block(resource_key(reference), 'original_reserved_reference')
        for row in index['metadata_linked_parent_sources']:
            key = resource_key(row['reference'])
            self.block(key, 'original_reserved_parent_source')
            if row['family'] is not None:
                self.join(key, recording_family_node(key, row['family']))
        matched_tracks = self.join_metadata_matches(index)
        for group in index['reserved_annotation_family_groups']:
            self.block('duplicate/catalog-2026-10-17/' + group, 'original_reserved_duplicate_group')
        for family in index['reserved_known_families']:
            self.block('recording/catalog-2026-10-17/' + family, 'original_reserved_recording_family')
        for row in index['excluded_annotation_metadata']:
            self.block(resource_key(row['reference']), 'original_excluded_annotation')
        for reference in index['known_exposed_track_references']:
            key = resource_key(reference)
            self.exposures[key].add('original_exposed_track')
            self.find(key)
            if key not in matched_tracks:
                self.parent_links.append({
                    'track_reference': reference, 'track_key': key,
                    'metadata_match_pointer': None, 'source_id': None,
                    'expected_parent_key': None, 'parent_pointer': None,
                    'status': 'track_not_in_metadata_matches',
                })
        # The retained packet receipt and its pinned loader establish that all
        # permitted originals were decoded. They do not enumerate per-row labels.
        for row in index['eligible_source_metadata']:
            self.exposures[resource_key(row['reference'])].add('prior_development_packet_payload_decode')
        self.apply_amendment(amendment)
        self.components = defaultdict(list)
        for node in sorted(self.parent):
            self.components[self.find(node)].append(node)
        self.component_keys = {
            root: 'component:' + hashlib.sha256(rows_bytes(members)).hexdigest()
            for root, members in self.components.items()
        }
        self.component_reasons = defaultdict(set)
        self.component_exposures = defaultdict(set)
        for node, reasons in self.reasons.items():
            self.component_reasons[self.find(node)].update(reasons)
        for node, exposures in self.exposures.items():
            self.component_exposures[self.find(node)].update(exposures)

    def join_metadata_matches(self, index):
        """Use retained native coordinates, never a numeric-ID namespace guess.

        A track's source_id is connected only when a matching source row exists
        in the authenticated safe projection. Explicit recording-family metadata
        still contributes a family edge; absent parent rows remain unresolved.
        """
        fields = {'family', 'id', 'kind', 'reference', 'snapshot_line', 'source_id', 'uuid'}
        rows = []
        parents = {}
        for collection in ('metadata_matches', 'metadata_linked_parent_sources'):
            for number, row in enumerate(index[collection]):
                pointer = f'/{collection}/{number}'
                require(set(row) == fields, pointer + ': safe match fields differ')
                require(row['kind'] in {'source', 'track'} and type(row['id']) is int
                        and row['id'] > 0, pointer + ': invalid native resource identity')
                require(collection != 'metadata_linked_parent_sources' or row['kind'] == 'source',
                        pointer + ': linked parent must be a source')
                local = row['reference'].startswith('local:')
                expected = ('local:' if local else '') + row['kind'] + ':' + str(row['id'])
                require(row['reference'] == expected, pointer + ': reference and native ID differ')
                require(type(row['snapshot_line']) is int and row['snapshot_line'] > 0,
                        pointer + ': invalid metadata locator')
                require(row['family'] is None or isinstance(row['family'], str), pointer + ': invalid recording family')
                key = resource_key(row['reference'])
                self.find(key)
                if row['family'] is not None:
                    self.join(key, recording_family_node(key, row['family']))
                if row['kind'] == 'source':
                    require(row['source_id'] is None, pointer + ': source row has a parent source ID')
                    require(key not in parents or parents[key][0] == row,
                            pointer + ': conflicting retained parent metadata')
                    parents.setdefault(key, (row, pointer))
                elif collection == 'metadata_matches':
                    rows.append((row, pointer, key))
        matched = set()
        for row, pointer, key in rows:
            require(key not in matched, pointer + ': duplicate matched track')
            matched.add(key)
            source_id = row['source_id']
            require(source_id is None or (type(source_id) is int and source_id > 0),
                    pointer + ': invalid parent source ID')
            parent_key = (key.split('/', 1)[0] + '/source:' + str(source_id)
                          if source_id is not None else None)
            parent = parents.get(parent_key)
            if parent is not None:
                parent_row, parent_pointer = parent
                require(row['family'] is None or parent_row['family'] is None
                        or row['family'] == parent_row['family'], pointer + ': track/parent families differ')
                self.join(key, parent_key)
                status = 'authenticated_parent'
            else:
                parent_pointer = None
                status = 'parent_id_missing' if source_id is None else 'parent_not_retained_in_safe_metadata'
            self.parent_links.append({
                'track_reference': row['reference'], 'track_key': key,
                'metadata_match_pointer': pointer, 'source_id': source_id,
                'expected_parent_key': parent_key, 'parent_pointer': parent_pointer,
                'status': status,
            })
        return matched

    def find(self, node):
        self.parent.setdefault(node, node)
        root = node
        while self.parent[root] != root:
            root = self.parent[root]
        while self.parent[node] != node:
            previous = self.parent[node]
            self.parent[node] = root
            node = previous
        return root

    def join(self, left, right):
        left, right = sorted((self.find(left), self.find(right)))
        self.parent[right] = left

    def block(self, node, reason):
        self.find(node)
        self.reasons[node].add(reason)

    def apply_amendment(self, amendment):
        require(set(amendment) == set(EMPTY_AMENDMENT)
                and amendment['schema_version'] == EMPTY_AMENDMENT['schema_version'], 'Unsupported reservation amendment')
        known_keys = {node for node in self.parent if node.startswith(('catalog-', 'local-catalog/'))}
        for item in amendment['bridges']:
            require(set(item) == {'left_source_key', 'right_source_key', 'basis', 'provenance'}, 'Bridge fields differ')
            require(item['basis'] in {'recording_duplicate', 'known_identity_family'}
                    and isinstance(item['provenance'], str) and item['provenance'].strip(), 'Bridge needs metadata provenance')
            left, right = item['left_source_key'], item['right_source_key']
            require(left in known_keys and right in known_keys and left != right, 'Bridge endpoint outside authenticated metadata')
            self.join(left, right)
        for key in amendment['excluded_source_keys']:
            require(key in known_keys, 'Additional exclusion outside authenticated metadata')
            self.block(key, 'additional_metadata_exclusion')
        for item in amendment['exposures']:
            require(set(item) == {'source_key', 'classification', 'provenance'}, 'Exposure fields differ')
            key = item['source_key']
            require(key in known_keys and item['classification'] in {'development_access', 'reserved_access'}
                    and isinstance(item['provenance'], str) and item['provenance'].strip(), 'Invalid exposure metadata')
            self.exposures[key].add('additional_' + item['classification'])
            if item['classification'] == 'reserved_access':
                self.block(key, 'additional_reserved_exposure')

    def describe(self, key):
        root = self.find(key)
        return {
            'component_key': self.component_keys[root],
            'exclusion_reasons': sorted(self.component_reasons[root]),
            'exposure': sorted(self.component_exposures[root]),
        }

    def ledger(self):
        return [{
            'component_key': self.component_keys[root], 'members': members,
            'exclusion_reasons': sorted(self.component_reasons[root]),
            'exposure': sorted(self.component_exposures[root]),
        } for root, members in sorted(self.components.items(), key=lambda item: self.component_keys[item[0]])]


def validate_config(config):
    require(set(config) == {'coverage_per_artist', 'probability_clusters', 'seed'}, 'Selection configuration fields differ')
    for field in ('coverage_per_artist', 'probability_clusters'):
        require(type(config[field]) is int and config[field] >= 0, field + ' must be a nonnegative integer')
    require(isinstance(config['seed'], str), 'Seed must be a string')
    require(not (config['coverage_per_artist'] or config['probability_clusters'])
            or re.fullmatch(r'[0-9a-f]{64}', config['seed']) is not None,
            'Selection requires a preregistered 32-byte hex seed')


def plan_selection(index, coverage, prior_packet, amendment, config, helpers):
    """Validate the whole frame and its closure before choosing either panel."""
    validate_config(config)
    permitted = helpers.development_metadata(index)
    require(prior_packet['counts']['eligible_originals_authenticated'] == len(permitted), 'Prior exposure population differs')
    require(prior_packet['exposure']['prior_discovery_and_extraction_exposure'] is True
            and prior_packet['exposure']['eligible_for_release_evaluation'] is False, 'Prior packet exposure contract differs')
    closure = ReservationClosure(index, amendment)
    inventory = []
    for row in sorted(permitted.values(), key=lambda item: resource_key(item['reference'])):
        key = resource_key(row['reference'])
        detail = closure.describe(key)
        inventory.append({
            'source_key': key, 'original_metadata': row, **detail,
            'selection_eligible': not detail['exclusion_reasons'], **SCOPE,
            'provider': None, 'source_knowledge_cutoff': None, 'recording_date': None,
            'format': None, 'actor_kind': None, 'identity_label': None,
        })
    groups = defaultdict(list)
    for row in inventory:
        if row['selection_eligible']:
            groups[row['component_key']].append(row)
    n, population = config['probability_clusters'], len(groups)
    require(n <= population, 'Requested probability clusters exceed the eligible retained frame')
    probability_groups = set(random.Random(config['seed']).sample(sorted(groups), n))
    per_artist = defaultdict(set)
    for group, rows in groups.items():
        for row in rows:
            per_artist[row['original_metadata']['artist_uuid']].add(group)
    coverage_groups = set()
    for artist, candidates in sorted(per_artist.items()):
        ranked = sorted(candidates, key=lambda group: hashlib.sha256(
            (config['seed'] + '/coverage/' + artist + '/' + group).encode()).hexdigest())
        coverage_groups.update(ranked[:config['coverage_per_artist']])
    selected = []
    for row in inventory:
        group = row['component_key']
        if group not in probability_groups | coverage_groups:
            continue
        selected.append({
            **row,
            'artist_coverage_challenge': group in coverage_groups,
            'probability_sample': ({
                'unit': 'complete_retained_metadata_component',
                'source_inclusion_probability': {'numerator': n, 'denominator': population},
                'within_component_inclusion_probability': {'numerator': 1, 'denominator': 1},
            } if group in probability_groups else None),
            'union_inclusion_probability': None,
        })
    requests = [{
        'source_key': row['source_key'], 'evidence_id': row['original_metadata']['evidence_id'],
        'original_metadata': row['original_metadata'],
        'annotation_path': ANNOTATION, 'annotation_sha256': index['annotation_evidence_sha256'],
        'mode': 'retained_allowlisted_original_only', 'external_acquisition_authorized': False,
        'complete_original_context_required': True,
    } for row in selected]
    artist_counts = []
    for artist in sorted({row['original_metadata']['artist_uuid'] for row in inventory}):
        rows = [row for row in inventory if row['original_metadata']['artist_uuid'] == artist]
        picks = [row for row in selected if row['original_metadata']['artist_uuid'] == artist]
        artist_counts.append({
            'artist_uuid': artist, 'permitted_metadata_sources': len(rows),
            'selection_eligible_sources': sum(row['selection_eligible'] for row in rows),
            'excluded_by_closure_sources': sum(not row['selection_eligible'] for row in rows),
            'selected_union_sources': len(picks),
            'coverage_challenge_sources': sum(row['artist_coverage_challenge'] for row in picks),
            'probability_sources': sum(row['probability_sample'] is not None for row in picks),
        })
    counts = {
        'permitted_metadata_sources': len(inventory), 'permitted_metadata_artists': len(artist_counts),
        'original_duplicate_groups': len({row['family_group'] for row in permitted.values()}),
        'selection_eligible_sources': sum(len(rows) for rows in groups.values()),
        'selection_eligible_components': population,
        'excluded_by_closure_sources': sum(not row['selection_eligible'] for row in inventory),
        'original_excluded_metadata_rows': len(index['excluded_annotation_metadata']),
        'authenticated_metadata_parent_links': sum(row['status'] == 'authenticated_parent' for row in closure.parent_links),
        'unresolved_metadata_parent_links': sum(row['status'] != 'authenticated_parent' for row in closure.parent_links),
        'selected_union_sources': len(selected), 'selected_union_components': len(probability_groups | coverage_groups),
        'probability_components': n, 'probability_sources': sum(len(groups[key]) for key in probability_groups),
        'coverage_components': len(coverage_groups), 'coverage_sources': sum(len(groups[key]) for key in coverage_groups),
        'panel_overlap_sources': sum(len(groups[key]) for key in probability_groups & coverage_groups),
    }
    source_coverage = coverage['coverage']['source']
    denominators = {
        'historical_catalog_registered_artists': coverage['totals']['artists'],
        'historical_catalog_source_eligible_artists': source_coverage['eligible_artists'],
        'historical_catalog_sources': source_coverage['population_rows'],
        'historical_retained_sample_sources': source_coverage['sampled_rows'],
        'historical_catalog_artists_without_source_rows': source_coverage['artists_without_eligible_rows'],
        'safe_permitted_metadata_artist_roster': len(artist_counts),
        'registered_artists_outside_safe_permitted_roster': coverage['totals']['artists'] - len(artist_counts),
        'full_per_artist_catalog_roster_available': False,
        'probability_population': 'selection_eligible_components_in_permitted_retained_development_frame',
        'global_catalog_inclusion_probabilities_known': False,
        'unknown_provider_clock_format_sources': len(inventory),
    }
    require(denominators['historical_catalog_registered_artists']
            == denominators['historical_catalog_source_eligible_artists']
            + denominators['historical_catalog_artists_without_source_rows'], 'Historical artist denominators do not reconcile')
    files = {
        'inventory.jsonl': rows_bytes(inventory), 'selection.jsonl': rows_bytes(selected),
        'capture-requests.jsonl': rows_bytes(requests), 'artist-counts.jsonl': rows_bytes(artist_counts),
        'denominators.json': json_bytes(denominators), 'selection-spec.json': json_bytes(config),
        'additional-reservations.json': json_bytes(amendment),
        'reservation-exposure-ledger.json': json_bytes({
            'original_metadata': {key: value for key, value in index.items() if key != 'eligible_source_metadata'},
            'additional_metadata': amendment, 'components': closure.ledger(),
            'known_parent_links': closure.parent_links,
            'prior_packet_exposure': prior_packet['exposure'],
            'per_source_prior_labels_or_model_requests': 'not_enumerated', **SCOPE,
        }),
    }
    return files, counts


def load_frame(repo, helpers):
    index = json.loads((repo / RESERVATIONS).read_bytes())
    receipt = json.loads((repo / RESERVATION_RECEIPT).read_bytes())
    require(receipt['index'] == helpers.RESERVATION_PIN, 'Reservation receipt authority differs')
    require(receipt['metadata_only_projection'] is True, 'Reservation metadata projection is not qualified')
    metadata = helpers.development_metadata(index)
    require(len(metadata) == receipt['counts']['eligible_development_sources'] == 2184, 'Permitted source denominator changed')
    require(len({row['artist_uuid'] for row in metadata.values()}) == 1765, 'Permitted artist denominator changed')
    require(len({row['family_group'] for row in metadata.values()}) == 2172, 'Duplicate-group denominator changed')
    return index, json.loads((repo / COVERAGE).read_bytes()), json.loads((repo / PRIOR_PACKET).read_bytes())


def selection_manifest(inputs, sources, counts, config):
    return {
        'schema_version': VERSION, 'state': 'complete_development_metadata_selection', **SCOPE,
        'inputs': inputs, 'execution_sources': sources, 'counts': counts,
        'selection_spec': config, 'raw_payloads_decoded': 0,
        'sampling_algorithm': 'python.Random(seed).sample(sorted_components,n)',
        'python_sampling_version': list(sys.version_info[:2]),
        'probability_scope': 'eligible_retained_development_components_only',
        'panel_union_is_calibration_sample': False,
        'hash_note': 'Manifest excludes itself. Labels and reviews belong outside this packet.',
    }


def build(repo, output, config, amendment, bundle):
    helpers, inputs, sources = load_helpers(repo, bundle)
    frame = load_frame(repo, helpers)
    files, counts = plan_selection(*frame, amendment, config, helpers)
    helpers.publish_packet(output, files, selection_manifest(inputs, sources, counts, config))
    return counts


def verify(repo, packet, bundle):
    helpers, inputs, sources = load_helpers(repo, bundle)
    frame = load_frame(repo, helpers)
    manifest = json.loads((packet / 'manifest.json').read_bytes())
    require(manifest['schema_version'] == VERSION and manifest['state'] == 'complete_development_metadata_selection',
            'Missing/incomplete selection manifest')
    config = json.loads((packet / 'selection-spec.json').read_bytes())
    amendment = json.loads((packet / 'additional-reservations.json').read_bytes())
    files, counts = plan_selection(*frame, amendment, config, helpers)
    wanted = selection_manifest(inputs, sources, counts, config)
    verify_files(packet, files, wanted, helpers)
    return helpers, frame[0], files, counts


def verify_files(packet, files, wanted, helpers):
    manifest = json.loads((packet / 'manifest.json').read_bytes())
    require({key: value for key, value in manifest.items() if key != 'artifacts'} == wanted, 'Packet manifest contract differs')
    require(len(manifest['artifacts']) == len(files)
            and {record['path'] for record in manifest['artifacts']} == set(files), 'Selection artifact coverage differs')
    require({str(path.relative_to(packet)) for path in packet.rglob('*') if path.is_file()}
            == set(files) | {'manifest.json'}, 'Unexpected/missing selection files')
    for name, data in files.items():
        helpers.authenticate(packet, next(record for record in manifest['artifacts'] if record['path'] == name))
        require((packet / name).read_bytes() == data, 'Selection differs: ' + name)


def plan_originals(repo, packet, bundle):
    """Decode only the verified selected physical lines; never query a provider."""
    helpers, index, files, counts = verify(repo, packet, bundle)
    rows = [json.loads(line) for line in files['selection.jsonl'].split(b'\n') if line]
    require(bool(rows), 'No selected sources; a metadata inventory needs no raw copy')
    metadata = {row['original_metadata']['source_line']: row['original_metadata'] for row in rows}
    annotation_manifest = json.loads((repo / ANNOTATION_MANIFEST).read_bytes())
    expected = annotation_manifest['files']['evidence.jsonl']
    require(expected['sha256'] == index['annotation_evidence_sha256'], 'Annotation authority differs')
    raw_pin = {'path': ANNOTATION, 'bytes': expected['bytes'], 'sha256': expected['sha256']}
    helpers.authenticate(repo, raw_pin)  # Whole-file byte hash; no JSON decoding.
    originals = helpers.selected_originals(repo / ANNOTATION, metadata)
    helpers.authenticate(repo, raw_pin)  # Detect input mutation during line reads.
    require(len(originals) == counts['selected_union_sources'], 'Selected original denominator differs')
    originals_bytes = helpers.encode_rows([originals[row['original_metadata']['evidence_id']] for row in rows])
    manifest = {
        'schema_version': 'people-evaluation-selected-originals:3',
        'state': 'complete_development_originals', **SCOPE,
        'selection_manifest': stamp((packet / 'manifest.json').resolve()), 'annotation': raw_pin,
        'execution_sources': json.loads((packet / 'manifest.json').read_bytes())['execution_sources'],
        'counts': {'complete_originals': len(originals), 'raw_payloads_decoded': len(originals)},
        'new_labels': 0, 'external_acquisition_authorized': False,
    }
    return helpers, {'originals.jsonl': originals_bytes}, manifest


def capture(repo, packet, output, bundle):
    # Reject an existing output before hashing or decoding any raw input.
    require(not output.exists(), 'Capture output already exists; use a new directory')
    require(output.resolve().is_relative_to((repo / 'local-dev/normalization-data').resolve()),
            'Complete raw originals must remain under ignored local-dev/normalization-data')
    helpers, files, manifest = plan_originals(repo, packet, bundle)
    helpers.publish_packet(output, files, manifest)
    return manifest['counts']


def verify_capture(repo, packet, originals, bundle):
    helpers, files, manifest = plan_originals(repo, packet, bundle)
    verify_files(originals, files, manifest, helpers)
    return manifest['counts']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo-root', required=True, type=Path)
    commands = parser.add_subparsers(dest='command', required=True)
    inventory = commands.add_parser('inventory')
    inventory.add_argument('--output', required=True, type=Path)
    inventory.add_argument('--coverage-per-artist', type=int, default=0)
    inventory.add_argument('--probability-clusters', type=int, default=0)
    inventory.add_argument('--seed', default='')
    inventory.add_argument('--additional-reservations', type=Path)
    check = commands.add_parser('verify')
    check.add_argument('--packet', required=True, type=Path)
    copy = commands.add_parser('capture')
    copy.add_argument('--packet', required=True, type=Path)
    copy.add_argument('--output', required=True, type=Path)
    originals_check = commands.add_parser('verify-capture')
    originals_check.add_argument('--packet', required=True, type=Path)
    originals_check.add_argument('--originals', required=True, type=Path)
    args = parser.parse_args()
    repo, bundle = args.repo_root.resolve(), Path(__file__).resolve().parent
    if args.command == 'inventory':
        config = {key: getattr(args, key) for key in ('coverage_per_artist', 'probability_clusters', 'seed')}
        amendment = (json.loads(args.additional_reservations.read_bytes())
                     if args.additional_reservations else EMPTY_AMENDMENT)
        counts = build(repo, args.output, config, amendment, bundle)
    elif args.command == 'verify':
        counts = verify(repo, args.packet, bundle)[3]
    elif args.command == 'capture':
        counts = capture(repo, args.packet, args.output, bundle)
    else:
        counts = verify_capture(repo, args.packet, args.originals, bundle)
    print(json.dumps({'state': 'complete', 'counts': counts}, sort_keys=True))


if __name__ == '__main__':
    main()
