#!/usr/bin/env python3
"""Authenticate field-scoped actor-fact drafts and their declared coverage.

This validates retained source ownership, exact raw Unicode coordinates, local
reference relationships and uncertainty. It does not adjudicate source meaning
or certify person identity. No runtime extractor or model outputs are loaded.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
ANNOTATION = Path('local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/evidence.jsonl')
KINDS = {'Aliases', 'Profiles', 'Contacts', 'Context', 'Kind', 'Relationships'}
ROLES = {'Taper', 'Transferrer', 'MatrixBuilder', 'MasteringEngineer', 'RecordingEngineer', 'Uploader', 'Collector', 'Musician', 'Other'}
CONTEXT = {'IndividualSpeakerStatement', 'ExplicitPersonDescription', 'ExplicitGroupDescription', 'GroupMemberStatement', 'NamedRoleContext'}
PROFILES = {'AttributedProfile', 'CreditedProjectProfile', 'ExplicitOwnerStatement'}
RELATIONSHIPS = {'ExplicitAlias', 'JointRoles', 'DistinctActors'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(value):
    return hashlib.sha256(value).hexdigest()


def fingerprint(path):
    hasher, size = hashlib.sha256(), 0
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            hasher.update(chunk)
            size += len(chunk)
    return {'bytes': size, 'sha256': hasher.hexdigest()}


def check_file(path, receipt):
    require(fingerprint(path) == {key: receipt[key] for key in ('bytes', 'sha256')}, f'Changed file: {path.name}')


def read_jsonl(path, line_numbers=None):
    # Filter physical rows before payload decoding. Byte-only hashing above
    # neither decodes nor selects reserved or held-out source text.
    with path.open('rb') as stream:
        for number, line in enumerate(stream, 1):
            if line_numbers is None or number in line_numbers:
                yield number, digest(line.rstrip(b'\r\n')), json.loads(line)


def unique(rows, key):
    result = {}
    for row in rows:
        identifier = row[key]
        require(identifier not in result, f'Duplicate {key}')
        result[identifier] = row
    return result


def visible_wording(raw):
    return ' '.join(re.sub(r'<[^>]*>', ' ', raw).split())


def authenticate_files(bundle, root, manifest):
    require(manifest['usage'] == 'development_only' and manifest['evaluation_eligible'] is False
            and manifest['policy_activation_permitted'] is False, 'Development usage contract changed')
    for name, receipt in manifest['files'].items():
        check_file(bundle / name, receipt)
    for key in ('original_sources', 'private_values', 'annotation_aid', 'frozen_discovery_inventory',
                'frozen_discovery_receipt', 'reservation_index'):
        check_file(root / manifest[key]['path'], manifest[key])
    for receipt in manifest['contract_snapshots']:
        check_file(root / receipt['path'], receipt)
    require(fingerprint(root / ANNOTATION)['sha256'] == manifest['source_annotation_evidence_sha256'],
            'Changed original annotation evidence')


def load_sources(bundle, root, manifest):
    reservation = json.loads((root / manifest['reservation_index']['path']).read_text())
    eligible = unique(reservation['eligible_source_metadata'], 'evidence_id')
    excluded = {row['evidence_id'] for row in reservation['excluded_annotation_metadata']}
    reserved_groups = set(reservation['reserved_annotation_family_groups'])
    reserved_families = set(reservation['reserved_known_families'])
    sources = unique((row for _, _, row in read_jsonl(bundle / 'source-index.jsonl')), 'evidence_id')
    for key, source in sources.items():
        require(key in eligible and key not in excluded, 'Selected source is reserved or outside eligibility')
        meta = eligible[key]
        require(meta['artist_partition'] == meta['family_partition'] == 'development'
                and source['artist_partition'] == source['family_partition'] == 'development',
                'Selected source is held out')
        require(source['family_group'] == meta['family_group'] and source['family_group'] not in reserved_groups
                and source['known_recording_family'] == meta['family'] and meta['family'] not in reserved_families
                and source['original_line_number'] == meta['source_line']
                and source['artist']['uuid'] == meta['artist_uuid'], 'Source eligibility metadata mismatch')
    originals = {}
    for _, _, wrapper in read_jsonl(root / manifest['original_sources']['path']):
        record = wrapper['original_record']
        key = record['evidence_id']
        require(key in sources and key not in originals, 'Unselected or duplicate original source')
        source = sources[key]
        require(wrapper['original_line_number'] == source['original_line_number']
                and wrapper['original_line_sha256'] == source['original_line_sha256'], 'Original source receipt mismatch')
        require(record['artist_partition'] == record['family_partition'] == 'development'
                and record['artist'] == source['artist'] and record['input_duplicate_family'] == source['family_group']
                and record['known_recording_family'] == source['known_recording_family'], 'Original source metadata mismatch')
        originals[key] = record
    require(originals.keys() == sources.keys(), 'Missing original source')
    wanted = {source['original_line_number']: key for key, source in sources.items()}
    require(len(wanted) == len(sources), 'Duplicate original source line')
    found = set()
    for number, line_hash, original in read_jsonl(root / ANNOTATION, wanted):
        key = wanted[number]
        require(line_hash == sources[key]['original_line_sha256'] and original == originals[key],
                'Original annotation source changed')
        found.add(number)
    require(found == wanted.keys(), 'Missing original annotation line')
    families = {}
    for source in sources.values():
        known = source['known_recording_family']
        if known:
            require(families.setdefault(known, source['family_group']) == source['family_group'],
                    'Known recording family crosses split groups')
    return sources, originals


def load_fields(bundle, root, manifest, originals):
    discovery = list(row for _, _, row in read_jsonl(root / manifest['frozen_discovery_inventory']['path']))
    expected = defaultdict(set)
    for family in discovery:
        for occurrence in family['supporting_occurrences']:
            expected[occurrence['evidence_id'], occurrence['field']].add(family['family_id'])
        for citation in family['structured_credit_citations'] + family.get('person_context_citations', []):
            expected[citation['evidence_id'], citation['field_path'].rsplit('/', 1)[-1]].add(family['family_id'])
    fields = unique((row for _, _, row in read_jsonl(bundle / 'field-index.jsonl')), 'task_id')
    require({(row['evidence_id'], row['field']) for row in fields.values()} == expected.keys(),
            'Incomplete original-field selection')
    require(len(fields) == len(expected), 'Duplicate original-field selection')
    for key, field in fields.items():
        require(field['evidence_id'] in originals, 'Field references unselected source')
        original = originals[field['evidence_id']]
        text = original['raw_record']['row'][field['field']]
        require(isinstance(text, str) and len(text) > 0, 'Selected field is not text')
        require(key == 'actor-field:' + digest(f'{field["evidence_id"]}|{field["field"]}'.encode())
                and field['field_path'] == '/raw_record/row/' + field['field']
                and field['field_sha256'] == digest(text.encode()) and field['code_points'] == len(text),
                'Original field identity/hash mismatch')
        require(field['artist_partition'] == field['family_partition'] == 'development'
                and field['artist'] == original['artist'] and field['family_group'] == original['input_duplicate_family']
                and set(field['discovery_family_ids']) == expected[field['evidence_id'], field['field']],
                'Field partition or discovery ownership mismatch')
    return fields


def load_citations(bundle, fields, originals):
    citations = unique((row for _, _, row in read_jsonl(bundle / 'citations.jsonl')), 'citation_id')
    quotes = {}
    for key, citation in citations.items():
        require(citation['task_id'] in fields, 'Citation references unselected field')
        field = fields[citation['task_id']]
        text = originals[field['evidence_id']]['raw_record']['row'][field['field']]
        span = citation['span']
        start, end = span['start'], span['end']
        require(type(start) is int and type(end) is int and 0 <= start < end <= len(text), 'Invalid citation span bounds')
        quote = text[start:end]
        require(citation['evidence_id'] == field['evidence_id'] and citation['field_path'] == field['field_path']
                and citation['field_sha256'] == field['field_sha256']
                and span['quote_sha256'] == digest(quote.encode()) and span['unit'] == 'unicode_code_points',
                'Citation original field/span mismatch')
        require(key == 'citation:' + digest(f'{field["task_id"]}|{start}|{end}'.encode()), 'Invalid citation identifier')
        require('quote' not in span, 'Raw source quotations belong in ignored originals')
        quotes[key] = quote
    return citations, quotes


def check_evidence(ids, task_id, citations):
    require(bool(ids) and len(ids) == len(set(ids)), 'Missing or duplicate evidence')
    require(all(key in citations and citations[key]['task_id'] == task_id for key in ids),
            'Evidence belongs to another original field')


def validate_actors(task, citations, quotes, values):
    key = task['task_id']
    refs = unique(task['actor_references'], 'reference_uuid')
    actors = unique(task['actors'], 'actor_uuid')
    for ref in refs.values():
        uuid.UUID(ref['reference_uuid'])
        require(ref['actor_uuid'] in actors, 'Reference points to unknown actor')
        check_evidence([ref['anchor']], key, citations)
        require(ref['wording_ref'] in values and values[ref['wording_ref']]['text'] == visible_wording(quotes[ref['anchor']]),
                'Reference wording does not match original anchor')
    for actor in actors.values():
        uuid.UUID(actor['actor_uuid'])
        reference = refs.get(actor['reference_uuid'])
        require(reference is not None and reference['actor_uuid'] == actor['actor_uuid']
                and actor['anchor'] == reference['anchor'] and actor['wording_ref'] == reference['wording_ref'],
                'Primary actor/reference binding mismatch')
        require(len(actor['roles']) == len(set(actor['roles'])) and set(actor['roles']) <= ROLES, 'Invalid source role')
        require(not actor['roles'] or actor['role_evidence'], 'Source roles lack original lexical evidence')
        if actor['role_evidence']:
            check_evidence(actor['role_evidence'], key, citations)
        owned_anchors = {ref['anchor'] for ref in refs.values() if ref['actor_uuid'] == actor['actor_uuid']}
        for kind in ('aliases', 'profiles', 'contacts', 'context'):
            unique(actor[kind], 'uuid')
            for fact in actor[kind]:
                check_evidence(fact['evidence'], key, citations)
                require(bool(owned_anchors & set(fact['evidence'])), 'Fact lacks its credited actor anchor')
                if kind == 'aliases':
                    alias = refs.get(fact['reference_uuid'])
                    require(alias is not None and alias['actor_uuid'] == actor['actor_uuid']
                            and fact['wording_ref'] == alias['wording_ref'], 'Alias points to another actor')
                elif kind in ('contacts', 'profiles'):
                    require(fact['literal_ref'] in values and fact['lookup_key_ref'] in values, 'Missing private reference value')
                    literal = values[fact['literal_ref']]['text']
                    require(any(quotes[c] == literal for c in fact['evidence']), 'Reference literal lacks exact source evidence')
                    if kind == 'profiles':
                        require(fact['relationship'] in PROFILES and bool(fact['provider']) and bool(fact['namespace']),
                                'Invalid profile relationship')
                    else:
                        require(fact['contact_type'] == 'email', 'Unexpected contact type in this frozen corpus')
                elif kind == 'context':
                    require(fact['context_kind'] in CONTEXT, 'Invalid actor context kind')
        if actor['kind'] is not None:
            kind = actor['kind']
            require(kind['kind'] in ('person', 'group'), 'Invalid expressed actor kind')
            check_evidence(kind['evidence'], key, citations)
            required = ({'ExplicitPersonDescription', 'IndividualSpeakerStatement'}
                        if kind['kind'] == 'person' else {'ExplicitGroupDescription'})
            require(any(fact['context_kind'] in required for fact in actor['context']),
                    'Actor kind lacks explicit expressed-kind context')
    return refs, actors


def validate_relationships(task, refs, actors, citations, quotes):
    unique(task['relationships'], 'uuid')
    for relation in task['relationships']:
        kind, endpoints = relation['relationship'], relation['reference_uuids']
        require(kind in RELATIONSHIPS and len(endpoints) == len(set(endpoints)) and bool(endpoints)
                and set(endpoints) <= refs.keys(), 'Invalid field-local relationship endpoints')
        check_evidence(relation['evidence'], task['task_id'], citations)
        owners = {refs[ref]['actor_uuid'] for ref in endpoints}
        require(set(relation['roles']) <= ROLES and len(relation['roles']) == len(set(relation['roles'])),
                'Invalid relationship roles')
        if kind == 'ExplicitAlias':
            require(len(endpoints) >= 2 and len(owners) == 1, 'Alias endpoints are not one source actor')
            require(any(re.search(r'\b(?:aka|a\.k\.a\.?)\b', quotes[c], re.I) for c in relation['evidence']),
                    'Explicit alias lacks an original alias statement')
        elif kind == 'DistinctActors':
            require(len(endpoints) >= 2 and len(owners) == len(endpoints), 'Distinct actors collapse an alias or reference')
        else:
            require(len(owners) == 1 and len(relation['roles']) >= 2
                    and set(relation['roles']) <= set(actors[next(iter(owners))]['roles']),
                    'Joint roles need one actor with the cited source roles')


def validate_tasks(labels, fields, citations, quotes, values):
    tasks = unique(labels, 'task_id')
    require(tasks.keys() == fields.keys(), 'Incomplete whole-field task denominator')
    for key, task in tasks.items():
        field = fields[key]
        require(task['evidence_id'] == field['evidence_id'] and task['field_path'] == field['field_path']
                and task['field_sha256'] == field['field_sha256'], 'Task field binding changed')
        check_evidence([task['whole_field_citation']], key, citations)
        whole = citations[task['whole_field_citation']]['span']
        require(whole['start'] == 0 and whole['end'] == field['code_points'], 'Task is not the whole original field')
        require(task['status'] == 'draft_development' and task['evaluation_eligible'] is False
                and task['policy_activation_permitted'] is False
                and task['adjudicator'] == {'id': None, 'status': 'pending'}, 'Draft/adjudication contract changed')
        groups = {'recording:' + field['evidence_id'], 'retained_family:' + field['family_group'],
                  *['discovery_hypothesis:' + value for value in field['discovery_family_ids']]}
        require(len(task['split_group_ids']) == len(groups) and set(task['split_group_ids']) == groups,
                'Incomplete source/family/hypothesis split groups')
        refs, actors = validate_actors(task, citations, quotes, values)
        validate_relationships(task, refs, actors, citations, quotes)
        for item in task['unresolved']:
            require(item['kind'] in KINDS and bool(item['reason'])
                    and set(item['candidate_reference_uuids']) <= refs.keys(), 'Invalid unresolved actor evidence')
            check_evidence(item['evidence'], key, citations)
            require(set(item['value_refs']) <= values.keys(), 'Unresolved reference value missing')
        for item in task['excluded_references']:
            require(item['kind'] == 'Profiles' and item['literal_ref'] in values and bool(item['reason']),
                    'Invalid excluded reference')
            check_evidence(item['evidence'], key, citations)
            require(any(quotes[c] == values[item['literal_ref']]['text'] for c in item['evidence']),
                    'Excluded reference literal lacks exact source evidence')
        coverage = unique(task['coverage'], 'kind')
        require(coverage.keys() == KINDS, 'Missing per-kind whole-field coverage')
        for kind, item in coverage.items():
            require(type(item['complete']) is bool and bool(item['reason'])
                    and item['fields'] == [task['whole_field_citation']], 'Coverage lost whole-field ownership')
            if kind == 'Kind' and all(actor['kind'] is None for actor in actors.values()):
                require(item['complete'] is False, 'Unknown actor kind cannot become negative evidence')
            if any(unknown['kind'] == kind for unknown in task['unresolved']):
                require(item['complete'] is False, 'Unresolved facts cannot become complete negatives')
    return tasks


def build_split_components(labels):
    """Group the full field population before selecting scored facts or folds."""
    tasks = unique(labels, 'task_id')
    parents = {key: key for key in tasks}

    def find(key):
        while parents[key] != key:
            parents[key] = parents[parents[key]]
            key = parents[key]
        return key

    owners = {}
    for key, task in tasks.items():
        for group in task['split_group_ids']:
            previous = owners.setdefault(group, key)
            left, right = find(key), find(previous)
            parents[max(left, right)] = min(left, right)
    components = defaultdict(list)
    for key in tasks:
        components[find(key)].append(key)
    return sorted(sorted(members) for members in components.values())


def validate_blind_packet(root, manifest, fields, originals):
    receipt_meta = manifest['independent_review']['selection_receipt']
    check_file(root / receipt_meta['path'], receipt_meta)
    receipt = json.loads((root / receipt_meta['path']).read_text())
    check_file(root / receipt['input']['path'], receipt['input'])
    for name, key in [('labels.jsonl', 'frozen_labels_sha256'),
                      ('citations.jsonl', 'frozen_citations_sha256'),
                      ('field-index.jsonl', 'frozen_fields_sha256')]:
        require(receipt[key] == manifest['files'][name]['sha256'], 'Blind review draft freeze changed')
    packet = unique((row for _, _, row in read_jsonl(root / receipt['input']['path'])), 'task_id')
    require(set(packet) == {row['task_id'] for row in receipt['selection']}
            and len(packet) == receipt['tasks'], 'Blind packet selection changed')
    hypotheses = set()
    for key, row in packet.items():
        require(set(row) == {'task_id', 'evidence_id', 'artist', 'field', 'field_path', 'field_sha256', 'field_text'},
                'Blind packet contains unexpected answer or metadata fields')
        require(key in fields, 'Blind review field outside selected population')
        field = fields[key]
        require(all(row[name] == field[name] for name in ('evidence_id', 'artist', 'field', 'field_path', 'field_sha256'))
                and row['field_text'] == originals[field['evidence_id']]['raw_record']['row'][field['field']],
                'Blind packet original field changed')
        hypotheses.update(field['discovery_family_ids'])
    require(len(hypotheses) == receipt['represented_discovery_hypotheses']
            and sum(len(row['field_text']) for row in packet.values()) == receipt['total_field_code_points']
            and max(len(row['field_text']) for row in packet.values()) == receipt['maximum_field_code_points'],
            'Blind packet field coverage changed')


def hash_review_text(value):
    if isinstance(value, str):
        return digest(value.encode())
    if isinstance(value, list):
        return [hash_review_text(item) for item in value]
    if isinstance(value, dict):
        return {key: hash_review_text(item) for key, item in value.items()}
    return value


def compact_review(value):
    """Preserve first answers without publishing contact values or long prose."""
    if isinstance(value, list):
        return [compact_review(item) for item in value]
    if not isinstance(value, dict):
        return value
    result = {}
    for key, item in value.items():
        if key in ('quote', 'wording', 'notes', 'reason'):
            result[key + '_sha256'] = hash_review_text(item)
        else:
            result[key] = compact_review(item)
    return result


def validate_independent_review(bundle, root, manifest, fields, originals):
    review = manifest['independent_review']
    for key in ('first_answers', 'first_answer_receipt', 'comparison_script'):
        check_file(root / review[key]['path'], review[key])
    answers = list(row for _, _, row in read_jsonl(root / review['first_answers']['path']))
    compact = list(row for _, _, row in read_jsonl(bundle / 'independent-review.jsonl'))
    require(compact == compact_review(answers), 'Compact independent review changed first answers')
    receipt = json.loads((root / review['first_answer_receipt']['path']).read_text())
    require(receipt['blind_boundary']['frozen_before_comparison'] is True
            and receipt['output']['sha256'] == review['first_answers']['sha256'], 'First-answer freeze not preserved')
    packet_receipt = json.loads((root / review['selection_receipt']['path']).read_text())
    require({row['task_id'] for row in answers} == {row['task_id'] for row in packet_receipt['selection']}
            and len(answers) == packet_receipt['tasks'], 'Independent review field denominator changed')

    def authenticate_spans(value, text):
        if isinstance(value, list):
            for item in value:
                authenticate_spans(item, text)
        elif isinstance(value, dict):
            if 'start' in value or 'end' in value:
                start, end = value['start'], value['end']
                require(type(start) is int and type(end) is int and 0 <= start < end <= len(text)
                        and value['quote'] == text[start:end], 'Independent review span changed original source')
            for item in value.values():
                authenticate_spans(item, text)

    for answer in answers:
        field = fields[answer['task_id']]
        require(answer['field_sha256'] == field['field_sha256'], 'Independent review field hash changed')
        authenticate_spans(answer, originals[field['evidence_id']]['raw_record']['row'][field['field']])


def counts(labels, sources, citations, values):
    return {'tasks': len(labels), 'source_records': len(sources), 'actors': sum(len(t['actors']) for t in labels),
            'actor_references': sum(len(t['actor_references']) for t in labels), 'citations': len(citations), 'values': len(values),
            'aliases': sum(len(a['aliases']) for t in labels for a in t['actors']),
            'contacts': sum(len(a['contacts']) for t in labels for a in t['actors']),
            'profiles': sum(len(a['profiles']) for t in labels for a in t['actors']),
            'context': sum(len(a['context']) for t in labels for a in t['actors']),
            'kind_positive': sum(a['kind'] is not None for t in labels for a in t['actors']),
            'relationships': dict(Counter(r['relationship'] for t in labels for r in t['relationships'])),
            'unresolved_by_kind': dict(Counter(r['kind'] for t in labels for r in t['unresolved'])),
            'complete_fields_by_kind': dict(Counter(c['kind'] for t in labels for c in t['coverage'] if c['complete']))}


def validate_bundle(bundle=HERE, root=ROOT):
    bundle, root = Path(bundle), Path(root)
    manifest = json.loads((bundle / 'manifest.json').read_text())
    authenticate_files(bundle, root, manifest)
    sources, originals = load_sources(bundle, root, manifest)
    fields = load_fields(bundle, root, manifest, originals)
    validate_blind_packet(root, manifest, fields, originals)
    citations, quotes = load_citations(bundle, fields, originals)
    values = unique((row for _, _, row in read_jsonl(root / manifest['private_values']['path'])), 'value_ref')
    for key, value in values.items():
        require(key == 'value:' + digest(value['text'].encode()) and value['sha256'] == digest(value['text'].encode()),
                'Private reference value/hash changed')
    labels = list(row for _, _, row in read_jsonl(bundle / 'labels.jsonl'))
    validate_tasks(labels, fields, citations, quotes, values)
    validate_independent_review(bundle, root, manifest, fields, originals)
    components = build_split_components(labels)
    require(manifest['internal_grouping'] == {'input_tasks': len(labels), 'components': len(components),
            'maximum_component_fields': max(map(len, components)), 'before_scored_fact_filtering': True},
            'Full-population split grouping changed')
    actual = counts(labels, sources, citations, values)
    require(actual == manifest['counts'], 'Annotation counts changed')
    require(len(fields) == manifest['source_scope']['whole_fields']
            and len(sources) == manifest['source_scope']['source_records']
            and len({s['artist']['uuid'] for s in sources.values()}) == manifest['source_scope']['artists']
            and Counter(f['field'] for f in fields.values()) == manifest['source_scope']['field_kinds'],
            'Selected source/field population changed')
    return actual


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, default=HERE)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    try:
        print(json.dumps(validate_bundle(args.bundle, args.root), sort_keys=True))
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(1, f'Validation failed: {error}\n')
