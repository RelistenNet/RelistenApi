#!/usr/bin/env python3
"""Build blind, reproducible annotation tasks from a completed catalog snapshot.

Selection features locate review material. They are never labels or assertions of
identity. The output contains exact captured records and empty answer fields.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import unicodedata
from uuid import UUID

VERSION = 'blind-annotation-v1'
DOMAINS = {'source_sample': 'people', 'venue_sample': 'venues', 'song_sample': 'repertoire',
           'track_sample': 'file_contents', 'setlist_sample': 'setlists'}
TARGETS = {'source_sample': 6000, 'venue_sample': 6000, 'song_sample': None,
           'track_sample': 4500, 'setlist_sample': None}
PROFILES = {'source_sample': ('source_profile', 'rows', 'sources', 8),
            'venue_sample': ('venue_profile', 'rows', 'venues', 4),
            'song_sample': ('song_profile', 'rows', 'songs', 32),
            'track_sample': ('track_profile', 'active', 'tracks', 8),
            'setlist_sample': ('setlist_profile', 'shows', 'setlist_shows', 8)}


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'))


def digest(value):
    return hashlib.sha256(value.encode('utf-8')).hexdigest()


def rank(value):
    return digest(VERSION + ':' + value)


def text_key(value):
    return ' '.join(unicodedata.normalize('NFKC', value or '').casefold().split())


def load_snapshot(path):
    """Require full relation coverage, unique resources and a final completion marker."""
    artists, profiles, records = {}, defaultdict(dict), defaultdict(list)
    seen_ids, seen_uuids = defaultdict(set), defaultdict(set)
    header = totals = completion = None
    hasher = hashlib.sha256()
    with path.open('rb') as stream:
        for number, line in enumerate(stream, 1):
            hasher.update(line)
            try:
                record = json.loads(line)
                kind = record['kind']
            except (ValueError, KeyError, TypeError) as error:
                raise ValueError(f'Invalid snapshot record at line {number}.') from error
            if completion is not None:
                raise ValueError('Records follow the completion marker.')
            if number == 1 and kind != 'snapshot':
                raise ValueError('The first record must identify the snapshot.')
            if kind == 'snapshot':
                if header is not None:
                    raise ValueError('Duplicate snapshot header.')
                header = record
                if record.get('read_only') != 'on' or record.get('in_recovery') is not True:
                    raise ValueError('A read-only replica capture is required.')
            elif kind == 'snapshot_complete':
                completion = record
            elif kind == 'totals':
                if totals is not None:
                    raise ValueError('Duplicate population totals.')
                totals = record['rows']
            elif kind == 'artist':
                row = record['row']
                if row['id'] in artists:
                    raise ValueError('Duplicate artist.')
                UUID(row['uuid'])
                artists[row['id']] = record
            elif kind in {profile[0] for profile in PROFILES.values()}:
                row = record['row']
                if row['artist_id'] in profiles[kind]:
                    raise ValueError('Duplicate artist profile.')
                profiles[kind][row['artist_id']] = row
            elif kind in DOMAINS:
                row = record['row']
                UUID(row['uuid'])
                if row['id'] in seen_ids[kind] or row['uuid'] in seen_uuids[kind]:
                    raise ValueError('Duplicate sampled resource.')
                seen_ids[kind].add(row['id'])
                seen_uuids[kind].add(row['uuid'])
                records[kind].append(record)
            elif kind not in ('schema', 'indexes'):
                raise ValueError(f'Unsupported snapshot record kind: {kind}.')
    if header is None or completion is None or totals is None:
        raise ValueError('The snapshot is incomplete.')
    if len(artists) != totals['artists']:
        raise ValueError('Artist totals do not reconcile.')
    for kind, (profile, field, relation, cap) in PROFILES.items():
        rows = profiles[profile]
        if set(rows) - set(artists):
            raise ValueError('A population profile has no artist context.')
        selected = Counter(r['row']['artist_id'] for r in records[kind])
        expected = {a: min(p[field], cap) for a, p in rows.items() if p[field]}
        if dict(selected) != expected:
            raise ValueError(f'Incomplete or unattributed {kind} coverage.')
        total_field = 'rows' if kind == 'track_sample' else field
        if sum(p[total_field] for p in rows.values()) != totals[relation]:
            raise ValueError(f'{kind} population does not reconcile.')
    if sum(p['memberships'] for p in profiles['setlist_profile'].values()) != totals['setlist_memberships']:
        raise ValueError('Setlist membership totals do not reconcile.')
    return header, completion, artists, records, hasher.hexdigest()


def features(record):
    """Coarse syntax strata intentionally avoid person/song extraction."""
    row, kind = record['row'], record['kind']
    if kind == 'source_sample':
        credits = ' '.join(str(row.get(f) or '') for f in ('taper', 'transferrer'))
        return ('empty_credit_fields' if not credits.strip() else
                'long_credit_fields' if len(credits) > 120 else
                'compound_credit_syntax' if re.search(r'[;,&/]', credits) else
                'description_html' if '<' in (row.get('description') or '') else 'plain_credit_fields')
    if kind == 'venue_sample':
        return ('missing_location' if not (row.get('location') or '').strip() else
                'past_names' if row.get('past_names') else
                'coordinates' if row.get('latitude') is not None else 'name_location')
    if kind == 'track_sample':
        title = row.get('title') or ''
        return ('missing_context' if record.get('source_context') is None else
                'empty_title' if not title.strip() else
                'separator_syntax' if re.search(r'[>→»/]', title) else
                'annotation_syntax' if re.search(r'[\[(*]', title) else 'plain_title')
    return 'stored_membership' if kind == 'setlist_sample' else 'stored_repertoire'


def select(records, target):
    """Cover every artist first; fill evenly across available syntax strata."""
    ordered = sorted(records, key=lambda r: rank(r['kind'] + ':' + r['row']['uuid']))
    if target is None or len(ordered) <= target:
        return ordered
    first_by_artist = {}
    for record in ordered:
        first_by_artist.setdefault(record['row']['artist_id'], record)
    chosen = {r['row']['uuid']: r for r in first_by_artist.values()}
    buckets = defaultdict(list)
    for record in ordered:
        if record['row']['uuid'] not in chosen:
            buckets[features(record)].append(record)
    # Artist coverage can exceed a requested cap; it is never silently dropped.
    positions = Counter()
    while len(chosen) < max(target, len(first_by_artist)):
        progressed = False
        for name in sorted(buckets):
            if positions[name] < len(buckets[name]) and len(chosen) < target:
                record = buckets[name][positions[name]]
                positions[name] += 1
                chosen[record['row']['uuid']] = record
                progressed = True
        if not progressed:
            break
    return sorted(chosen.values(), key=lambda r: rank(r['kind'] + ':' + r['row']['uuid']))


class Families:
    """Join known show families and byte-equivalent annotation inputs before splitting."""
    def __init__(self):
        self.parents = {}

    def root(self, key):
        self.parents.setdefault(key, key)
        if self.parents[key] != key:
            self.parents[key] = self.root(self.parents[key])
        return self.parents[key]

    def join(self, left, right):
        a, b = self.root(left), self.root(right)
        if a != b:
            self.parents[max(a, b)] = min(a, b)


def evidence_key(record):
    return record['kind'] + ':' + record['row']['uuid']


def duplicate_key(record):
    row, kind = record['row'], record['kind']
    if kind == 'source_sample':
        value = {f: row.get(f) for f in ('taper', 'transferrer', 'description', 'taper_notes', 'source', 'lineage')}
        if not any(v for v in value.values()):
            return None
    elif kind == 'venue_sample':
        value = {f: row.get(f) for f in ('name', 'location', 'past_names', 'latitude', 'longitude')}
    elif kind == 'track_sample':
        # Short equal titles across unrelated shows do not duplicate the whole input.
        value = {'artist_id': row['artist_id'], 'title': row.get('title'),
                 'source_id': row.get('source_id'), 'source_set_id': row.get('source_set_id')}
    elif kind == 'song_sample':
        value = {'artist_id': row['artist_id'], 'name': row.get('name'), 'upstream_identifier': row.get('upstream_identifier')}
    else:
        return None
    return kind + ':' + digest(canonical(value))


def partition(key):
    bucket = int(rank('partition:' + key)[:8], 16) % 100
    return 'development' if bucket < 60 else 'validation' if bucket < 80 else 'test'


def family_assignments(records):
    groups, family_owner, duplicate_owner = Families(), {}, {}
    for record in records:
        key = evidence_key(record)
        groups.root(key)
        family = record.get('family')
        if family:
            groups.join(key, family_owner.setdefault(family, key))
        duplicate = duplicate_key(record)
        if duplicate:
            groups.join(key, duplicate_owner.setdefault(duplicate, key))
    return {evidence_key(r): digest(groups.root(evidence_key(r))) for r in records}


def pair_candidates(selected, group_ids):
    """Unlabeled candidate pairs include collisions; equal strings never become truth."""
    pairs = {}
    configurations = [('people', 'source_sample', ('taper', 'transferrer')),
                      ('venues', 'venue_sample', ('name', 'location')),
                      ('compositions', 'song_sample', ('name',)),
                      ('repertoire', 'song_sample', ('name',))]
    for domain, kind, fields in configurations:
        buckets = defaultdict(list)
        for record in selected[kind]:
            row = record['row']
            for field in fields:
                key = text_key(row.get(field))
                if key and len(key) <= 160:
                    scope = str(row['artist_id']) if domain == 'repertoire' else 'global'
                    buckets[(scope, field, key)].append(record)
        candidates = []
        for (_, field, _), members in sorted(buckets.items()):
            members = sorted(members, key=lambda r: rank(evidence_key(r)))
            # Bounded adjacent pairs cover each collision block without quadratic growth.
            for left, right in zip(members, members[1:]):
                if domain == 'compositions' and left['row']['artist_id'] == right['row']['artist_id']:
                    continue
                ids = sorted((evidence_key(left), evidence_key(right)))
                pair_id = domain + ':' + digest('|'.join(ids))
                candidates.append((pair_id, ids, field))
        for pair_id, ids, field in sorted(candidates, key=lambda p: rank(p[0]))[:500]:
            groups = sorted({group_ids[k] for k in ids})
            # A pair spanning existing partitions is reserved for development. It must
            # not pull a validation/test item into training; consumers enforce this flag.
            partitions = {partition(g) for g in groups}
            pair_partition = next(iter(partitions)) if len(partitions) == 1 else 'cross_partition_review_only'
            pairs[pair_id] = {'task_id': pair_id, 'task_kind': 'entity_link', 'domain': domain,
                             'evidence_ids': ids, 'candidate_reason': 'equal_folded_' + field,
                             'family_partition': pair_partition, 'answer': None,
                             'allowed_decisions': ['same_entity', 'different_entities', 'insufficient_evidence'],
                             'required_label_fields': ['decision', 'evidence', 'reason', 'reviewer', 'adjudicator']}
    return [pairs[k] for k in sorted(pairs)]


def write_jsonl(path, rows):
    hasher, count = hashlib.sha256(), 0
    with path.open('xb') as stream:
        for row in rows:
            line = (canonical(row) + '\n').encode('utf-8')
            stream.write(line)
            hasher.update(line)
            count += 1
    return {'rows': count, 'sha256': hasher.hexdigest(), 'bytes': path.stat().st_size}


def build(snapshot, output, targets=None):
    header, completion, artists, records, input_hash = load_snapshot(snapshot)
    if output.exists():
        raise FileExistsError(f'Output already exists: {output}')
    output.mkdir(parents=True)
    # No complete receipt exists if selection or any subsequent write fails.
    targets = TARGETS if targets is None else targets
    selected = {kind: select(rows, targets.get(kind)) for kind, rows in records.items()}
    all_records = [r for rows in records.values() for r in rows]
    group_ids = family_assignments(all_records)
    evidence, tasks, coverage = [], [], {}
    for kind in sorted(DOMAINS):
        rows = selected.get(kind, [])
        for record in rows:
            row, key = record['row'], evidence_key(record)
            artist = artists[row['artist_id']]['row']
            group = group_ids[key]
            evidence.append({'evidence_id': key, 'resource': {'provider': 'relisten_catalog_snapshot',
                              'resource_type': kind.removesuffix('_sample'), 'resource_uuid': row['uuid']},
                             'artist': {'uuid': artist['uuid'], 'name': artist['name']},
                             'known_recording_family': record.get('family'), 'input_duplicate_family': group,
                             'family_partition': partition(group), 'artist_partition': partition('artist:' + artist['uuid']),
                             'raw_record': record})
            tasks.append({'task_id': 'extract:' + key, 'task_kind': 'evidence_extraction', 'domain': DOMAINS[kind],
                          'evidence_ids': [key], 'selection_stratum': features(record),
                          'family_partition': partition(group), 'artist_partition': partition('artist:' + artist['uuid']),
                          'answer': None, 'required_label_fields': ['assertions', 'unknowns', 'reviewer', 'adjudicator']})
        eligible = {r['row']['artist_id'] for r in records[kind]}
        included = {r['row']['artist_id'] for r in rows}
        coverage[DOMAINS[kind]] = {'available_rows': len(records[kind]), 'selected_rows': len(rows),
            'eligible_artists': len(eligible), 'selected_artists': len(included),
            'missing_eligible_artists': len(eligible - included), 'requested_rows': targets.get(kind),
            'strata': dict(Counter(features(r) for r in rows)),
            'family_partitions': dict(Counter(partition(group_ids[evidence_key(r)]) for r in rows)),
            'artist_partitions': dict(Counter(partition('artist:' + artists[r['row']['artist_id']]['row']['uuid']) for r in rows))}
    pairs = pair_candidates(selected, group_ids)
    tasks.extend(pairs)
    files = {'evidence.jsonl': write_jsonl(output / 'evidence.jsonl', evidence),
             'tasks.jsonl': write_jsonl(output / 'tasks.jsonl', tasks)}
    receipt = {'state': 'complete_unlabeled', 'builder_version': VERSION,
               'builder_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
               'input_sha256': input_hash, 'input_bytes': snapshot.stat().st_size,
               'snapshot': header, 'snapshot_complete': completion,
               'selection': 'hash rank; one row per artist, then round-robin syntax strata',
               'coverage': coverage, 'files': files,
               'entity_link_tasks': dict(Counter(t['domain'] for t in pairs)),
               'entity_link_partitions': dict(Counter(t['family_partition'] for t in pairs)),
               'labels_completed': 0, 'accuracy_estimated': False,
               'limits': ['Candidate pair reasons are selection features, never identity labels.',
                 'No proposed extractor or model output is included.',
                 'Family partitions group known show families and exact annotation inputs in the entire snapshot.',
                 'Artist partitions are a separate suite: shared global identities and exact inputs may cross artists.',
                 'Cross-partition pair tasks are for adjudication only and cannot enter training or scored holdouts.',
                 'Equal per-artist selection is not a uniform catalog probability sample.',
                 'Hard negatives and unknowns must be adjudicated; candidate counts are not class counts.',
                 'Composition identity generally needs independent writer/work evidence absent from these rows.',
                 'Stored setlist membership does not provide order, repeats, timing or segues.']}
    temporary = output / 'manifest.writing'
    with temporary.open('x', encoding='utf-8') as stream:
        stream.write(json.dumps(receipt, indent=2, sort_keys=True) + '\n')
    temporary.rename(output / 'manifest.json')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('snapshot', type=Path)
    parser.add_argument('new_output_directory', type=Path)
    args = parser.parse_args()
    result = build(args.snapshot, args.new_output_directory)
    print(json.dumps({'state': result['state'], 'coverage': result['coverage'],
                      'entity_link_tasks': result['entity_link_tasks'], 'labels_completed': 0}, indent=2))
