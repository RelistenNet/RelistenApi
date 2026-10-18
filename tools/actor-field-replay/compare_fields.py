#!/usr/bin/env python3
"""Compare retained actor-fact drafts with actual C# whole-field outputs.

Exact raw anchor spans establish alignment. Unaligned anchors remain diagnostics;
partial coverage, reviewer disagreements and unknown kind never supply negatives.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
from types import ModuleType

FACETS = {'roles': 'Roles', 'aliases': 'Aliases', 'contacts': 'Contacts', 'profiles': 'Profiles', 'context': 'Context'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fingerprint(path):
    raw = path.read_bytes()
    return {'bytes': len(raw), 'sha256': hashlib.sha256(raw).hexdigest()}


def read_jsonl(path):
    with path.open('rb') as stream:
        for line in stream:
            yield json.loads(line)


def unique(rows, key):
    result = {}
    for row in rows:
        require(row[key] not in result, 'Duplicate ' + key)
        result[row[key]] = row
    return result


def check_file(path, receipt):
    require(fingerprint(path) == {key: receipt[key] for key in ('bytes', 'sha256')}, 'Changed replay artifact: ' + path.name)


def load_frozen(bundle, root):
    helper_path = Path(__file__).with_name('export_fields.py')
    helper_bytes = helper_path.read_bytes()
    helper = ModuleType('actor_replay_export_helper')
    helper.__file__ = str(helper_path)
    exec(compile(helper_bytes, str(helper_path), 'exec'), helper.__dict__)
    frozen = helper.FrozenActorFields.validate(bundle, root)
    frozen.read_set[helper_path] = {'bytes': len(helper_bytes), 'sha256': hashlib.sha256(helper_bytes).hexdigest()}
    frozen.verify_unchanged()
    manifest = frozen.manifest
    fields = unique(frozen.read_rows(bundle / 'field-index.jsonl'), 'task_id')
    labels = unique(frozen.read_rows(bundle / 'labels.jsonl'), 'task_id')
    citations = unique(frozen.read_rows(bundle / 'citations.jsonl'), 'citation_id')
    originals = {row['original_record']['evidence_id']: row['original_record']
                 for row in frozen.read_rows(root / manifest['original_sources']['path'])}
    review = unique(frozen.read_rows(bundle / 'independent-review-comparison.jsonl'), 'task_id')
    first_answers = unique(frozen.read_rows(bundle / 'independent-review.jsonl'), 'task_id')
    for key, row in review.items():
        row['first_answer'] = first_answers[key]
    return frozen, fields, labels, citations, originals, review


def validate_output_citations(value, field, text):
    if isinstance(value, list):
        for item in value:
            validate_output_citations(item, field, text)
    elif isinstance(value, dict):
        if 'startCodePoint' in value or 'endCodePoint' in value:
            start, end = value['startCodePoint'], value['endCodePoint']
            require(type(start) is int and type(end) is int and 0 <= start < end <= len(text)
                    and value['field'] == field['field']
                    and value['jsonPointer'] == '/' + field['field'].replace('~', '~0').replace('/', '~1')
                    and value['quote'] == text[start:end], 'Output citation escaped its original Unicode field')
        for item in value.values():
            validate_output_citations(item, field, text)


def load_replay(replay, bundle, fields, originals):
    status_path = replay / 'status.json'
    status = json.loads(status_path.read_text())
    require(status['version'] == 'actor-field-replay-v1' and status['expectedFields'] == len(fields),
            'Unexpected replay contract/population')
    check_file(bundle / 'manifest.json', status['bundleManifest'])
    check_file(bundle / 'field-index.jsonl', status['fieldIndex'])
    check_file(Path(status['input']['path']), status['input'])
    check_file(Path(status['exportReceipt']['path']), status['exportReceipt'])
    require(status['results'] is not None, 'Replay produced no retained result file')
    check_file(replay / 'results.jsonl', status['results'])
    exported = unique(read_jsonl(Path(status['input']['path'])), 'task_id')
    require(exported.keys() == fields.keys(), 'Replay input lost whole-field tasks')
    for key, row in exported.items():
        field = fields[key]
        text = originals[field['evidence_id']]['raw_record']['row'][field['field']]
        require(all(row[name] == field[name] for name in ('evidence_id', 'field', 'field_path', 'field_sha256'))
                and row['field_text'] == text, 'Replay input differs from the frozen original field')
    results = unique(read_jsonl(replay / 'results.jsonl'), 'taskId')
    require(results.keys() <= fields.keys(), 'Output contains tasks outside the frozen population')
    for key, row in results.items():
        field = fields[key]
        require(row['field'] == field['field'] and row['fieldSha256'] == field['field_sha256']
                and row['evidenceId'] == field['evidence_id'] and row['state'] in ('extracted', 'error'),
                'Output task binding/state changed')
        if row['state'] == 'extracted':
            require(row['extraction'] is not None and row['error'] is None, 'Extracted outcome lost its result')
            text = originals[field['evidence_id']]['raw_record']['row'][field['field']]
            validate_output_citations(row['extraction'], field, text)
        else:
            require(row['error'] is not None, 'Failed outcome lost its error')
    require(status['processedFields'] == len(results)
            and status['errorFields'] == sum(row['state'] == 'error' for row in results.values()),
            'Replay result counts differ from status')
    completed = status['state'] == 'completed'
    if completed:
        require(results.keys() == fields.keys() and status['errorFields'] == 0, 'Completed replay is partial')
        require(json.loads((replay / 'manifest.json').read_text()) == status, 'Completion manifest differs from status')
    else:
        require(not (replay / 'manifest.json').exists(), 'Partial replay has a completion manifest')
    return status, results, completed


def span(evidence):
    return evidence['startCodePoint'], evidence['endCodePoint']


def label_span(citation, citations):
    raw = citations[citation]['span']
    return raw['start'], raw['end']


def hash_text(value):
    return hashlib.sha256(value.encode()).hexdigest()


def disputes(review, label=None, citations=None):
    if review is None:
        return {}
    result = defaultdict(set)
    for difference in review['coverage_differences']:
        result[difference['kind']].add('independent_coverage_disagreement')
        if difference['kind'] == 'Context':
            result['Roles'].add('independent_context_coverage_disagreement')
    for actor in review['aligned_actors']:
        for key in actor['differences']:
            result[FACETS.get(key, key)].add('independent_fact_disagreement')
    if review['draft_only_anchor_spans'] or review['review_only_anchor_spans']:
        for kind in ['Actors', *FACETS.values(), 'Relationships']:
            result[kind].add('independent_anchor_disagreement')
    if review['draft_relationship_counts'] != review['review_relationship_counts']:
        result['Relationships'].add('independent_relationship_disagreement')
    for kind in review.get('draft_unresolved_by_kind', {}).keys() | review.get('review_unresolved_by_kind', {}).keys():
        if review.get('draft_unresolved_by_kind', {}).get(kind, 0) != review.get('review_unresolved_by_kind', {}).get(kind, 0):
            result[kind].add('independent_unresolved_evidence_disagreement')
            if kind == 'Context':
                result['Roles'].add('independent_unresolved_role_context_disagreement')
    if label is not None and 'first_answer' in review:
        expected = {label_span(actor['anchor'], citations): actor for actor in label['actors']}
        for actor in review['first_answer']['actors']:
            anchor = actor['anchor_span']['start'], actor['anchor_span']['end']
            if anchor in expected:
                draft_profiles = {(item['literal_ref'][6:], item['relationship']) for item in expected[anchor]['profiles']}
                first_profiles = {(item['literal_span']['quote_sha256'], item['relationship']) for item in actor['profiles']}
                if draft_profiles != first_profiles:
                    result['Profiles'].add('independent_profile_value_or_relationship_disagreement')
    return {key: sorted(reasons) for key, reasons in result.items()}


def actual_references(extraction):
    references = {}
    unanchored = []
    for credit in extraction['credits']:
        facts = credit.get('actorFacts')
        if facts is None:
            unanchored.append({'name_sha256': hash_text(credit['name']), 'role': credit['role']})
            continue
        key = facts['actorUuid'], facts['referenceUuid'], span(facts['anchor'])
        if key not in references:
            references[key] = {'span': span(facts['anchor']), 'actor_uuid': facts['actorUuid'],
                'reference_uuid': facts['referenceUuid'], 'roles': set(), 'aliases': set(), 'contacts': set(),
                'profiles': set(), 'context': set(), 'kind': None}
        actor = references[key]
        actor['roles'].add(credit['role'])
        actor['aliases'].update(hash_text(item['wording']) for item in facts['aliases'])
        actor['contacts'].update(hash_text(item['literal']) for item in facts['contacts'])
        actor['profiles'].update((hash_text(item['literal']), item['relationship']) for item in facts['profiles'])
        actor['context'].update(item['contextKind'] for item in facts['context'])
        actor['kind'] = facts['kind']['kind'] if facts['kind'] else None
    by_span = defaultdict(list)
    for actor in references.values():
        by_span[actor['span']].append(actor)
    return by_span, unanchored


def expected_facets(actor):
    return {'roles': set(actor['roles']), 'aliases': {item['wording_ref'][6:] for item in actor['aliases']},
        'contacts': {item['literal_ref'][6:] for item in actor['contacts']},
        'profiles': {(item['literal_ref'][6:], item['relationship']) for item in actor['profiles']},
        'context': {item['context_kind'] for item in actor['context']}}


def relationship_diagnostics(label, extraction, citations, coverage, masks):
    references = {ref['reference_uuid']: label_span(ref['anchor'], citations) for ref in label['actor_references']}
    expected = [{'kind': item['relationship'], 'roles': sorted(item['roles']),
                 'endpoint_spans': sorted(references[key] for key in item['reference_uuids'])}
                for item in label['relationships']]
    actual_anchors = defaultdict(set)
    actual_claims = {}
    for credit in extraction['credits']:
        facts = credit.get('actorFacts')
        if facts is not None:
            actual_anchors[facts['referenceUuid']].add(span(facts['anchor']))
            for item in facts.get('relationships', []):
                # Deduplicate repeated role mentions without discarding conflicting UUID payloads.
                actual_claims[json.dumps(item, sort_keys=True)] = item
    actual = []
    for item in actual_claims.values():
        fully_anchored = all(len(actual_anchors[key]) == 1 for key in item['referenceUuids'])
        actual.append({'kind': item['relationship'], 'roles': sorted(item['roles']),
            'reference_uuids': item['referenceUuids'],
            'endpoint_spans': sorted(next(iter(actual_anchors[key])) for key in item['referenceUuids']) if fully_anchored else None,
            'alignment': 'unique_local_reference_anchors' if fully_anchored else 'unscored_endpoint_anchor_unavailable_or_ambiguous'})
    matches = sum(any(item['kind'] == gold['kind'] and item['roles'] == gold['roles']
                      and item['endpoint_spans'] == gold['endpoint_spans'] for item in actual) for gold in expected)
    return {'expected': expected, 'actual': actual, 'exact_fully_anchored_correspondences': matches,
        'coverage_complete': coverage['Relationships'], 'disputed': bool(masks.get('Relationships')),
        'scored': False, 'reason': 'Relationship diagnostics only; unavailable alias endpoints are not inferred from wording or evidence containers.'}


def compare_field(label, field, citations, result, review):
    masks = disputes(review, label, citations)
    coverage = {row['kind']: row['complete'] for row in label['coverage']}
    extraction = result['extraction'] if result is not None and result['state'] == 'extracted' else {'credits': [], 'unresolved': []}
    actual, unanchored = actual_references(extraction)
    expected_names = Counter(label_span(ref['anchor'], citations) for ref in label['actor_references'])
    expected_actors = defaultdict(list)
    for actor in label['actors']:
        expected_actors[label_span(actor['anchor'], citations)].append(actor)
    named_matches = {anchor for anchor, count in expected_names.items() if count == 1 and len(actual.get(anchor, [])) == 1}
    ambiguous = {anchor for anchor in expected_names.keys() | actual.keys()
                 if expected_names.get(anchor, 0) > 1 or len(actual.get(anchor, [])) > 1}
    facets = []
    for anchor, actors in expected_actors.items():
        aligned = len(actors) == 1 and len(actual.get(anchor, [])) == 1
        for actor in actors:
            for kind, expected in expected_facets(actor).items():
                domain = FACETS[kind]
                complete = coverage.get(domain, coverage['Context'] if domain == 'Roles' else False)
                mask = masks.get(domain, [])
                observed = actual[anchor][0][kind] if aligned else set()
                facets.append({'anchor_span': list(anchor), 'kind': domain,
                    'alignment': 'exact_unique_raw_span' if aligned else 'unaligned_or_ambiguous',
                    'coverage_complete': complete, 'disputed': bool(mask), 'dispute_reasons': mask,
                    'expected': sorted(expected), 'observed': sorted(observed),
                    'matched_labeled_positive': len(expected & observed) if aligned else 0,
                    'missing_labeled_positive': len(expected - observed) if aligned else 0,
                    'unaligned_labeled_positive': len(expected) if not aligned else 0,
                    'extra_in_complete_undisputed_draft_field': len(observed - expected) if aligned and complete and not mask else 0,
                    'unscored_extra_partial_or_disputed': len(observed - expected) if aligned and (not complete or mask) else 0})
    return {'task_id': label['task_id'], 'independent_reviewed': review is not None, 'field': field['field'], 'field_sha256': field['field_sha256'],
        'outcome': result['state'] if result else 'not_processed', 'error': result.get('error') if result else None,
        'disputes': masks, 'coverage': coverage,
        'names': {'expected_reference_count': sum(expected_names.values()), 'actual_anchored_reference_count': sum(map(len, actual.values())),
            'exact_unique_raw_span_matches': len(named_matches), 'expected_unaligned_spans': sorted(expected_names.keys() - named_matches - ambiguous),
            'actual_unaligned_spans': sorted(actual.keys() - named_matches - ambiguous), 'ambiguous_spans': sorted(ambiguous),
            'unanchored_credits': unanchored},
        'facets': facets, 'relationships': relationship_diagnostics(label, extraction, citations, coverage, masks),
        'unknown_kind_is_not_negative': True,
        'actual_kind_counts': dict(Counter(actor['kind'] or 'unknown' for group in actual.values() for actor in group)),
        'expected_unresolved_by_kind': dict(Counter(item['kind'] for item in label['unresolved'])),
        'actual_extraction_unresolved': len(extraction['unresolved'])}


def summarize(diagnostics):
    buckets = defaultdict(Counter)
    for field in diagnostics:
        for facet in field['facets']:
            coverage = 'disputed' if facet['disputed'] else ('complete_undisputed_draft' if facet['coverage_complete'] else 'partial_undisputed_draft')
            target = buckets[facet['kind'] + '/' + coverage]
            target['actor_facet_comparisons'] += 1
            target['expected_labeled_positive'] += len(facet['expected'])
            for key in ('matched_labeled_positive', 'missing_labeled_positive', 'unaligned_labeled_positive',
                        'extra_in_complete_undisputed_draft_field', 'unscored_extra_partial_or_disputed'):
                target[key] += facet[key]
    return {'whole_fields': len(diagnostics), 'outcomes': dict(Counter(row['outcome'] for row in diagnostics)),
        'independently_reviewed_fields': sum(row['independent_reviewed'] for row in diagnostics),
        'not_independently_reviewed_fields': sum(not row['independent_reviewed'] for row in diagnostics),
        'fields_with_independent_disputes': sum(bool(row['disputes']) for row in diagnostics),
        'relationships': {'expected': sum(len(row['relationships']['expected']) for row in diagnostics),
            'actual': sum(len(row['relationships']['actual']) for row in diagnostics),
            'exact_fully_anchored_correspondences': sum(row['relationships']['exact_fully_anchored_correspondences'] for row in diagnostics),
            'actual_unscored_endpoints': sum(item['endpoint_spans'] is None for row in diagnostics for item in row['relationships']['actual']),
            'scored': False},
        'names': {'expected_references': sum(row['names']['expected_reference_count'] for row in diagnostics),
            'actual_anchored_references': sum(row['names']['actual_anchored_reference_count'] for row in diagnostics),
            'exact_unique_raw_span_matches': sum(row['names']['exact_unique_raw_span_matches'] for row in diagnostics),
            'unaligned_actual_spans': sum(len(row['names']['actual_unaligned_spans']) for row in diagnostics),
            'ambiguous_spans': sum(len(row['names']['ambiguous_spans']) for row in diagnostics)},
        'facets': {key: dict(value) for key, value in sorted(buckets.items())}, 'kind_accuracy_denominator': 0}


def run(bundle, root, replay, output):
    require(not output.exists(), 'Choose a new comparison output directory')
    replay_snapshot = fingerprint(replay / 'status.json')
    frozen, fields, labels, citations, originals, review = load_frozen(bundle, root)
    manifest = frozen.manifest
    status, results, completed = load_replay(replay, bundle, fields, originals)
    diagnostics = [compare_field(label, fields[key], citations, results.get(key), review.get(key)) for key, label in labels.items()]
    report = {'schema_version': 'actor-field-replay-comparison-v1',
        'state': 'completed' if completed else 'diagnostic_partial_replay', 'usage': 'development_only',
        'evaluation_eligible': False, 'policy_activation_permitted': False,
        'counts': summarize(diagnostics), 'source_scope': manifest['source_scope'],
        'provenance': {'bundle_manifest': frozen.read_set[bundle / 'manifest.json'], 'replay_status': fingerprint(replay / 'status.json'),
            'replay_results': fingerprint(replay / 'results.jsonl'), 'comparison_script': fingerprint(Path(__file__)),
            'frozen_validator': frozen.read_set[bundle / 'validate_bundle.py'],
            'source_authentication_helper': frozen.read_set[Path(__file__).with_name('export_fields.py')], 'api_assembly': status['apiAssembly'],
            'tool_assembly': status['toolAssembly'], 'extractor_version': status['extractorVersion'],
            'frozen_label_sha256': manifest['files']['labels.jsonl']['sha256'],
            'frozen_review_sha256': manifest['files']['independent-review.jsonl']['sha256']},
        'interpretation': ['Exact unique raw spans establish alignment; equal strings never repair unmatched or ambiguous anchors.',
            'Positive misses and extra facts are provisional draft comparisons, not adjudicated errors or launch accuracy.',
            'Partial and disputed domains are separate buckets; extra facts there are unscored. Unknown kind never supplies a negative.',
            'Unaligned actual references remain diagnostics, including in complete fields. No global identity is inferred.',
            'Assembly hashes identify the binaries recorded by the C# process; this comparison does not rerun or authenticate historical loaded code.']}
    def recheck_retained_snapshots():
        # Recheck after analysis and again after writing diagnostics, just before publication.
        frozen.verify_unchanged()
        check_file(replay / 'status.json', replay_snapshot)
        for name in ('input', 'exportReceipt'):
            check_file(Path(status[name]['path']), status[name])
        check_file(replay / 'results.jsonl', status['results'])

    recheck_retained_snapshots()
    output.mkdir(parents=True, exist_ok=False)
    diagnostic_path = output / 'fields.jsonl'
    write_diagnostics(diagnostic_path, diagnostics)
    report['diagnostics'] = fingerprint(diagnostic_path)
    pending = output / 'report.pending.json'
    with pending.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write('\n')
    recheck_retained_snapshots()
    # Atomic create-new publication retains the pending checkpoint if interrupted afterward.
    (output / 'report.json').hardlink_to(pending)
    return report


def write_diagnostics(path, diagnostics):
    with path.open('x', encoding='utf-8') as stream:
        for row in diagnostics:
            stream.write(json.dumps(row, sort_keys=True) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('bundle', 'repo-root', 'replay', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run(args.bundle, args.repo_root, args.replay, args.output)
        print(json.dumps({'state': report['state'], 'counts': report['counts']}, sort_keys=True))
        return 0 if report['state'] == 'completed' else 1
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(1, 'Comparison failed: ' + str(error) + '\n')


if __name__ == '__main__':
    raise SystemExit(main())
