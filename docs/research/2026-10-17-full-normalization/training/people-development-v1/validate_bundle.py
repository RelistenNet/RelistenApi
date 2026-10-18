#!/usr/bin/env python3
"""Authenticate development people labels against frozen retained source evidence.

This checks provenance and annotation contracts, not the correctness of identity
judgments. Raw fields and reservation metadata remain outside Git. Validation
loads reservation eligibility before accessing selected source text.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import unicodedata


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
ANNOTATION = Path('local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(value):
    return hashlib.sha256(value).hexdigest()


def fingerprint(path):
    hasher = hashlib.sha256()
    size = 0
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            size += len(chunk)
            hasher.update(chunk)
    return {'bytes': size, 'sha256': hasher.hexdigest()}


def check_file(path, receipt):
    require(fingerprint(path) == {k: receipt[k] for k in ('bytes', 'sha256')},
            f'Changed file: {path.name}')


def read_jsonl(path):
    # Unicode line separators may occur inside JSON strings. Only physical LF
    # bytes delimit retained records; str.splitlines() would corrupt them.
    with path.open('rb') as stream:
        for number, line in enumerate(stream, 1):
            yield number, digest(line.rstrip(b'\r\n')), json.loads(line)


def read_selected_jsonl(path, line_numbers):
    """Select physical rows before decoding any reserved or held-out payload."""
    with path.open('rb') as stream:
        for number, line in enumerate(stream, 1):
            if number in line_numbers:
                yield number, digest(line.rstrip(b'\r\n')), json.loads(line)


def unique(rows, key):
    result = {}
    for row in rows:
        identifier = row[key]
        require(identifier not in result, f'Duplicate {key}: {identifier}')
        result[identifier] = row
    return result


def fold(name):
    return ' '.join(unicodedata.normalize('NFKC', name).casefold().split())


def expected_candidates(task, mentions):
    focus = mentions[task['focus_mention_id']]
    if task['task_kind'] == 'within_recording_coreference':
        return {key for key, item in mentions.items()
                if key != focus['mention_id'] and item['mention_kind'] == 'name'
                and item['evidence_id'] == focus['evidence_id']}
    name = fold(focus['name'])
    tokens = re.findall(r'\w+', name)
    final = tokens[-1] if tokens and len(tokens[-1]) >= 4 else None
    result = set()
    for key, item in mentions.items():
        if key == focus['mention_id']:
            continue
        other = fold(item['name'])
        other_tokens = re.findall(r'\w+', other)
        if name == other or (final and other_tokens and other_tokens[-1] == final):
            result.add(key)
    return result


def expected_groups(task, mentions, sources):
    groups = set()
    for key in [task['focus_mention_id'], *task.get('candidate_mention_ids', [])]:
        mention = mentions[key]
        evidence = mention['evidence_id']
        groups.update((f'recording:{evidence}', f'family:{sources[evidence]["family_group"]}',
                       'name_hypothesis:' + digest(fold(mention['name']).encode())))
    return groups


def build_split_components(labels):
    """Group all tasks, including unknowns, before any experiment splits them."""
    parents = {item['task_id']: item['task_id'] for item in labels}

    def find(key):
        while parents[key] != key:
            parents[key] = parents[parents[key]]
            key = parents[key]
        return key

    first = {}
    for item in labels:
        for group in item['split_group_ids']:
            key = item['task_id']
            if group in first:
                parents[find(key)] = find(first[group])
            else:
                first[group] = key
    components = {}
    for key in parents:
        components.setdefault(find(key), []).append(key)
    return sorted((sorted(items) for items in components.values()), key=lambda x: x[0])


def validate_citation(citation, originals):
    evidence = citation['evidence_id']
    require(evidence in originals, 'Citation references an unselected source')
    path = citation['field_path']
    require(path.startswith('/raw_record/row/') and path.count('/') == 3, 'Invalid field path')
    field = path.rsplit('/', 1)[-1]
    text = originals[evidence]['raw_record']['row'].get(field)
    require(isinstance(text, str), f'Citation field is not text: {field}')
    require(digest(text.encode()) == citation['field_sha256'], 'Changed citation field')
    span = citation['span']
    start, end = span['start'], span['end']
    require(type(start) is int and type(end) is int and 0 <= start < end <= len(text),
            'Invalid citation span bounds')
    require(span['unit'] == 'unicode_code_points' and text[start:end] == span['quote'],
            'Citation span does not match original text')
    expected = 'citation:' + digest(f'{evidence}|{field}|{start}|{span["quote"]}'.encode())
    require(citation['citation_id'] == expected, 'Invalid citation identifier')


def validate_family_groups(sources):
    # input_duplicate_family is the annotation builder's transitive union of
    # known recording families AND exact-input duplicates. Its legacy name
    # must not lead a consumer to split one recording family into several IDs.
    owners = {}
    for source in sources.values():
        family = source['known_recording_family']
        if family:
            group = source['family_group']
            require(owners.setdefault(family, group) == group,
                    'Known recording family spans multiple split groups')


def load_sources(bundle, root, manifest):
    index = json.loads((root / manifest['reservation_index']['path']).read_text())
    check_file(root / manifest['reservation_index']['path'], manifest['reservation_index'])
    eligible = unique(index['eligible_source_metadata'], 'evidence_id')
    excluded = {row['evidence_id'] for row in index['excluded_annotation_metadata']}
    reserved_groups = set(index['reserved_annotation_family_groups'])
    reserved_families = set(index['reserved_known_families'])
    for key, row in eligible.items():
        require(key not in excluded and row['family_group'] not in reserved_groups
                and row['family'] not in reserved_families, 'Reserved source is eligible')
        require(row['artist_partition'] == row['family_partition'] == 'development',
                'Eligible source is held out')
    sources = unique((row for _, _, row in read_jsonl(bundle / 'source-index.jsonl')), 'evidence_id')
    for key, row in sources.items():
        require(key in eligible, 'Selected source is not reservation-filtered development data')
        metadata = eligible[key]
        require(row['artist_partition'] == row['family_partition'] == 'development',
                'Selected source is held out')
        require(row['family_group'] == metadata['family_group']
                and row['known_recording_family'] == metadata['family']
                and row['artist']['uuid'] == metadata['artist_uuid']
                and row['annotation_line_number'] == metadata['source_line']
                and metadata['reference'] == f'source:{row["source_id"]}', 'Source metadata mismatch')
    validate_family_groups(sources)
    originals = {}
    for _, _, wrapper in read_jsonl(root / manifest['original_selected_evidence']['path']):
        # The frozen eligibility projection is checked before reading any text.
        record = wrapper['original_record']
        key = record['evidence_id']
        require(key in sources and key in eligible and key not in originals,
                'Unselected or duplicate original source')
        source = sources[key]
        require(wrapper['original_line_number'] == source['annotation_line_number']
                and wrapper['original_line_sha256'] == source['annotation_line_sha256'],
                'Original source receipt mismatch')
        require(record['artist_partition'] == record['family_partition'] == 'development'
                and record['input_duplicate_family'] == source['family_group']
                and record['known_recording_family'] == source['known_recording_family']
                and record['artist'] == source['artist'], 'Original source partition/metadata mismatch')
        require(record['raw_record']['kind'] == 'source_sample'
                and record['raw_record']['row']['id'] == source['source_id']
                and record['raw_record']['row']['uuid'] == source['source_uuid'], 'Original source identity mismatch')
        originals[key] = record
    require(originals.keys() == sources.keys(), 'Missing original sources')
    wanted = {row['annotation_line_number']: key for key, row in sources.items()}
    found = set()
    require(len(wanted) == len(sources), 'Duplicate original source line')
    for number, line_hash, record in read_selected_jsonl(root / ANNOTATION / 'evidence.jsonl', wanted):
        key = wanted[number]
        found.add(number)
        require(line_hash == sources[key]['annotation_line_sha256'] and record == originals[key],
                'Original annotation source changed')
    require(found == wanted.keys(), 'Missing original annotation lines')
    return sources, originals, index


def validate_tasks(labels, mentions, citations, sources, primary, collision_metadata):
    original_ids = set()
    for task in labels:
        key = task['task_id']
        kind = task['task_kind']
        focus = task['focus_mention_id']
        require(focus in mentions, 'Unknown focal mention')
        expected = 'people-development:' + digest(f'{kind}|{focus}|{task["selection_stratum"]}'.encode())
        require(key == expected, 'Invalid task identifier')
        require(task['status'] == 'draft_development' and task['evaluation_eligible'] is False
                and task['policy_activation_permitted'] is False
                and task['adjudicator'] == {'id': None, 'status': 'pending'}, 'Draft usage contract changed')
        require(bool(task['rationale']) and task['reviewer']['independent_human'] is False,
                'Missing draft provenance')
        require(set(task['supporting_citation_ids']) <= citations.keys(), 'Unknown supporting citation')
        require(len(task['split_group_ids']) == len(set(task['split_group_ids']))
                and set(task['split_group_ids']) == expected_groups(task, mentions, sources),
                'Incomplete split groups')
        involved = {mentions[m]['evidence_id'] for m in [focus, *task.get('candidate_mention_ids', [])]}
        require(all(citations[c]['evidence_id'] in involved for c in task['supporting_citation_ids']),
                'Supporting citation belongs to an unrelated source')
        answer = task['answer']
        require(set(answer.get('primary_source_ids', [])) <= primary.keys(), 'Unknown primary source')
        if kind == 'actor_kind':
            require(bool(task['supporting_citation_ids']), 'Actor kind needs source context')
            actor = answer['actor_kind']
            require(actor in ('person', 'group', 'unknown') and answer['resolved'] is (actor != 'unknown'),
                    'Unknown actor kind cannot become a resolved negative')
            continue
        require(kind in ('within_recording_coreference', 'cross_recording_identity'), 'Unknown task kind')
        candidates = task['candidate_mention_ids']
        require(len(candidates) == len(set(candidates))
                and set(candidates) == expected_candidates(task, mentions), 'Incomplete candidate pool')
        decisions = unique(answer['candidate_decisions'], 'candidate_mention_id')
        require(decisions.keys() == set(candidates) and answer['all_candidates_judged'] is True,
                'Missing or extra candidate judgment')
        for decision in decisions.values():
            require(decision['decision'] in ('same_entity', 'different_entities', 'insufficient_evidence')
                    and bool(decision['reason']), 'Invalid identity judgment')
            require(set(decision['supporting_citation_ids']) <= citations.keys(), 'Unknown decision citation')
            pair_sources = {mentions[focus]['evidence_id'],
                            mentions[decision['candidate_mention_id']]['evidence_id']}
            require(all(citations[c]['evidence_id'] in pair_sources
                        for c in decision['supporting_citation_ids']), 'Decision citation belongs to an unrelated pair')
            if decision['decision'] != 'insufficient_evidence':
                require(bool(decision['supporting_citation_ids']), 'Resolved identity needs source evidence')
        for original in task.get('original_tasks', []):
            identifier = original['original_task_id']
            require(identifier not in original_ids and collision_metadata.get(identifier) == original,
                    'Changed or duplicate original collision task')
            require(original['focus'] == focus and original['required_candidate'] in candidates,
                    'Original collision candidate missing')
            original_ids.add(identifier)
    require(original_ids == collision_metadata.keys(), 'Missing original collision tasks')


def validate_review(bundle, root, manifest, labels, mentions, citations, originals):
    """Preserve the first answer-blind review and independently compare its answers."""
    receipt = json.loads((bundle / 'review-receipt.json').read_text())
    for key in ('source_receipt', 'original_judgments', 'input'):
        check_file(root / receipt[key]['path'], receipt[key])
    for key, name in [('compact_judgments', 'independent-review.jsonl'),
                      ('comparison', 'review-comparison.jsonl')]:
        check_file(bundle / name, receipt[key])
    require(receipt['draft_labels_sha256'] == manifest['independent_review']['frozen_labels_sha256']
            and receipt['input']['sha256'] == manifest['independent_review']['input']['sha256']
            and receipt['draft_answers_seen_before_initial_freeze'] is False
            and receipt['independent_human_adjudication'] is False
            and receipt['policy_activation_permitted'] is False, 'Review provenance changed')
    initial = json.loads((root / receipt['source_receipt']['path']).read_text())
    require(initial['draft_answers_seen'] is False and initial['only_input_file_read'] is True
            and initial['held_out_tasks_read'] is False
            and initial['input_sha256'] == receipt['input']['sha256']
            and initial['output_sha256'] == receipt['original_judgments']['sha256']
            and initial['frozen_at'] == receipt['frozen_at'], 'Initial review receipt mismatch')
    packet = unique((row for _, _, row in read_jsonl(root / receipt['input']['path'])), 'task_id')
    original_review = unique((row for _, _, row in read_jsonl(root / receipt['original_judgments']['path'])),
                            'task_id')
    compact = unique((row for _, _, row in read_jsonl(bundle / 'independent-review.jsonl')), 'task_id')
    comparisons = unique((row for _, _, row in read_jsonl(bundle / 'review-comparison.jsonl')), 'task_id')
    tasks = unique(labels, 'task_id')
    require(packet.keys() == original_review.keys() == compact.keys() == comparisons.keys()
            and packet.keys() <= tasks.keys(), 'Review task coverage mismatch')
    actors, identities, agreements, candidate_count, actor_agreements = 0, 0, 0, 0, 0
    for key, question in packet.items():
        task, raw, saved = tasks[key], original_review[key], compact[key]
        require('answer' not in question and 'rationale' not in question
                and question['focus_mention_id'] == task['focus_mention_id']
                and raw['focus_mention_id'] == saved['focus_mention_id'] == task['focus_mention_id']
                and question['task_kind'] == raw['task_kind'] == saved['task_kind'] == task['task_kind'],
                'Review question identity changed')
        expected_mentions = {task['focus_mention_id'], *task.get('candidate_mention_ids', [])}
        require(set(question['candidate_mention_ids']) == set(task.get('candidate_mention_ids', []))
                and {m['mention_id'] for m in question['mentions']} == expected_mentions
                and all(m == mentions[m['mention_id']] for m in question['mentions']),
                'Review candidate context is incomplete')
        for field in question['original_fields']:
            evidence = field['evidence_id']
            require(evidence in originals, 'Review field references unselected evidence')
            require(field['field_text'] == originals[evidence]['raw_record']['row'][field['field_path'].rsplit('/', 1)[-1]],
                    'Review original field changed')
        for citation in question['citations']:
            require(citations.get(citation['citation_id']) == citation, 'Review citation changed')
        expected = {'task_id': key, 'task_kind': task['task_kind'], 'status': 'reviewed_not_adjudicated'}
        # The tracked copy omits repeated quotations; the untouched first answers
        # remain independently hash-pinned with every supporting citation intact.
        raw_answer = dict(raw['answer'])
        if task['task_kind'] == 'actor_kind':
            actors += 1
            raw_answer.pop('support', None)
            require(saved == {**raw, 'answer': raw_answer}, 'Compact actor review changed')
            draft, reviewed = task['answer']['actor_kind'], saved['answer']['actor_kind']
            expected.update(draft_actor_kind=draft, reviewed_actor_kind=reviewed, agreement=draft == reviewed)
            actor_agreements += expected['agreement']
            reviewed_items = [saved['answer']]
        else:
            identities += 1
            cleaned = [{k: v for k, v in item.items() if k != 'support'}
                       for item in raw['answer']['candidate_judgments']]
            require(saved == {**raw, 'answer': {**raw_answer, 'candidate_judgments': cleaned}},
                    'Compact identity review changed')
            reviewed_items = saved['answer']['candidate_judgments']
            judged = unique(reviewed_items, 'candidate_mention_id')
            require(judged.keys() == set(task['candidate_mention_ids']), 'Missing reviewed candidate')
            draft = {x['candidate_mention_id']: x['decision'] for x in task['answer']['candidate_decisions']}
            expected['candidate_comparisons'] = [
                {'candidate_mention_id': item['candidate_mention_id'],
                 'draft_decision': draft[item['candidate_mention_id']], 'reviewed_decision': item['decision'],
                 'agreement': draft[item['candidate_mention_id']] == item['decision']}
                for item in reviewed_items]
            expected['agreement'] = all(item['agreement'] for item in expected['candidate_comparisons'])
            candidate_count += len(reviewed_items)
            agreements += sum(item['agreement'] for item in expected['candidate_comparisons'])
        for item in reviewed_items:
            require(set(item['citation_ids']) <= citations.keys() and bool(item['rationale']),
                    'Review support or rationale missing')
        require(comparisons[key] == expected, 'Review comparison does not match frozen answers')
    require(receipt['selection'] == {'tasks': len(packet), 'actor_kind': actors,
                                    'identity_questions': identities, 'candidate_judgments': candidate_count}
            and receipt['agreement'] == {'actor_kind': actor_agreements, 'candidate_judgments': agreements,
                                         'disagreements': actors + candidate_count - actor_agreements - agreements},
            'Review counts changed')


def authenticate_files(bundle, root, manifest):
    for name, receipt in manifest['files'].items():
        check_file(bundle / name, receipt)
    for name in ('reservation_index', 'original_selected_evidence', 'original_collision_tasks'):
        check_file(root / manifest[name]['path'], manifest[name])
    for filename, key in [('evidence.jsonl', 'source_annotation_evidence_sha256'),
                          ('tasks.jsonl', 'source_annotation_tasks_sha256'),
                          ('manifest.json', 'source_annotation_manifest_sha256')]:
        require(fingerprint(root / ANNOTATION / filename)['sha256'] == manifest[key],
                f'Changed annotation {filename}')
    receipt = json.loads((bundle / 'reservation-receipt.json').read_text())
    require(receipt['index'] == manifest['reservation_index']
            and receipt['metadata_only_projection'] is True and receipt['seed_labels_read'] is False,
            'Reservation receipt mismatch')
    reservation_file = json.loads((root / manifest['reservation_index']['path']).read_text())
    source_receipts = [
        (Path('docs/research/2026-10-17-full-normalization/evaluation/cases.jsonl'),
         'seed_cases', 'source_cases_sha256'),
        (Path('local-dev/normalization-data/2026-10-17-cross-artist/snapshot.jsonl'),
         'snapshot', 'raw_snapshot_sha256'),
        (ANNOTATION / 'evidence.jsonl', 'annotation_evidence', 'annotation_evidence_sha256')]
    for path, receipt_key, index_key in source_receipts:
        require(fingerprint(root / path)['sha256'] == receipt['source_hashes'][receipt_key]
                == reservation_file[index_key], 'Changed reservation source')


def load_inventory(bundle, sources, originals):
    citations = unique((row for _, _, row in read_jsonl(bundle / 'citations.jsonl')), 'citation_id')
    for citation in citations.values():
        validate_citation(citation, originals)
    mentions = unique((row for _, _, row in read_jsonl(bundle / 'mentions.jsonl')), 'mention_id')
    for key, mention in mentions.items():
        citation = citations[mention['citation_id']]
        require(key == 'mention:' + digest(mention['citation_id'].encode()), 'Invalid mention identifier')
        require(mention['evidence_id'] == citation['evidence_id']
                and mention['name'] == citation['span']['quote']
                and mention['artist'] == sources[mention['evidence_id']]['artist'], 'Mention ownership mismatch')
        require(mention['mention_kind'] in ('name', 'credit_expression', 'unresolved_reference'),
                'Invalid mention kind')
        require(all(c in citations and citations[c]['evidence_id'] == mention['evidence_id']
                    for c in mention['context_citation_ids']), 'Mention context belongs to another source')
    pool = json.loads((bundle / 'candidate-pool.json').read_text())
    require(len(pool['mention_ids']) == len(mentions) and set(pool['mention_ids']) == mentions.keys()
            and pool['mentions_file_sha256'] == fingerprint(bundle / 'mentions.jsonl')['sha256'],
            'Changed mention inventory')
    require(pool['identity_assertion'] is False and pool['matcher_feature'] is False
            and pool['creation_authorized'] is False, 'Candidate inventory cannot assert identity')
    primary = unique((row for _, _, row in read_jsonl(bundle / 'primary-sources.jsonl')), 'source_id')
    for source in primary.values():
        require(sum(len(item['quote'].split()) for item in source['excerpts']) <= 25,
                'Primary source excerpt limit exceeded')
        for item in source['excerpts']:
            require(item['sha256'] == digest(item['quote'].encode()), 'Changed primary excerpt')
    return citations, mentions, primary


def load_collision_tasks(root, manifest, sources):
    collision = unique((row for _, _, row in read_jsonl(root / manifest['original_collision_tasks']['path'])),
                       'original_task_id')
    wanted = {row['original_line_number']: row for row in collision.values()}
    found = set()
    require(len(wanted) == len(collision), 'Duplicate original collision line')
    for number, line_hash, original in read_selected_jsonl(root / ANNOTATION / 'tasks.jsonl', wanted):
        metadata = wanted[number]
        found.add(number)
        require(line_hash == metadata['original_line_sha256']
                and original['task_id'] == metadata['original_task_id']
                and original['family_partition'] == 'development'
                and original['evidence_ids'] == metadata['original_evidence_ids']
                and original['candidate_reason'] == metadata['candidate_reason']
                and set(original['evidence_ids']) <= sources.keys(), 'Changed or held-out original collision task')
    require(found == wanted.keys(), 'Missing original collision lines')
    return collision


def validate_counts(manifest, labels, mentions, citations, sources, reservation, collision):
    selection = manifest['selection']
    require(len(labels) == selection['tasks'] and len(mentions) == selection['mentions']
            and len(citations) == selection['citations'] and len(sources) == selection['selected_source_records']
            and len({x['artist']['uuid'] for x in sources.values()}) == selection['selected_artists']
            and len(collision) == selection['original_collision_tasks']
            and Counter(x['task_kind'] for x in labels) == selection['task_kinds'], 'Selection counts changed')
    require(len(reservation['eligible_source_metadata']) == selection['eligible_development_sources'],
            'Eligible population count changed')
    actor = Counter(x['answer']['actor_kind'] for x in labels if x['task_kind'] == 'actor_kind')
    decisions = Counter(d['decision'] for x in labels for d in x['answer'].get('candidate_decisions', []))
    strength = Counter()
    for item in labels:
        if item['task_kind'] == 'actor_kind':
            strength[item['answer']['evidence_strength']] += 1
        for decision in item['answer'].get('candidate_decisions', []):
            strength[decision['evidence_strength']] += 1
    require(actor == manifest['answers']['actor_kind_tasks']
            and decisions == manifest['answers']['candidate_decisions_not_independent_tasks']
            and strength == manifest['answers']['evidence_strength_counts'], 'Answer counts changed')


def validate_pre_review_files(bundle, root, manifest):
    review = manifest['independent_review']
    check_file(root / review['input']['path'], review['input'])
    for key, filename in [('frozen_labels_sha256', 'labels.jsonl'),
                          ('frozen_mentions_sha256', 'mentions.jsonl'),
                          ('frozen_candidate_pool_sha256', 'candidate-pool.json')]:
        require(review[key] == fingerprint(bundle / filename)['sha256'], 'Pre-review frozen input changed')


def validate_bundle(bundle=HERE, root=ROOT):
    bundle, root = Path(bundle), Path(root)
    manifest = json.loads((bundle / 'manifest.json').read_text())
    require(manifest['usage'] == 'development_only' and manifest['policy_activation_permitted'] is False
            and manifest['identity_accuracy_established'] is False, 'Development usage contract changed')

    authenticate_files(bundle, root, manifest)
    sources, originals, reservation = load_sources(bundle, root, manifest)
    citations, mentions, primary = load_inventory(bundle, sources, originals)
    collision = load_collision_tasks(root, manifest, sources)
    labels = list(unique((row for _, _, row in read_jsonl(bundle / 'labels.jsonl')), 'task_id').values())

    validate_tasks(labels, mentions, citations, sources, primary, collision)
    validate_counts(manifest, labels, mentions, citations, sources, reservation, collision)
    validate_pre_review_files(bundle, root, manifest)
    validate_review(bundle, root, manifest, labels, mentions, citations, originals)
    return {'tasks': len(labels), 'mentions': len(mentions), 'sources': len(sources),
            'split_components': len(build_split_components(labels)), 'adjudication': 'pending'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, default=HERE)
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    try:
        print(json.dumps(validate_bundle(args.bundle, args.root), sort_keys=True))
    except (ValueError, KeyError, TypeError, OSError, json.JSONDecodeError) as error:
        parser.exit(1, f'Validation failed: {error}\n')
