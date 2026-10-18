"""Freeze two separate development distinctness/linkage control contexts, unanswered."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys


BASE_TOOL = Path('tools/RelistenNormalization/people-identity-packet')
BASE_CODE_SHA256 = '20ac70e6b999b03049141852127334a31426ebc048071d650663aaa7d0e3a5c2'
BASE_MANIFEST_SHA256 = '850cbf62de2b9602b74f5c44f35978e4c93c82928ffecc384a9291b8822fac67'
VERSION = 'people-identity-distinctness-controls:1'
DECISIONS = ['different_entities', 'insufficient_evidence', 'same_entity']
EXPOSURE = {
    'partition': 'development', 'prior_discovery_exposure': True,
    'eligible_for_release_evaluation': False,
    'canonical_person_creation_authorized': False, 'policy_activation_permitted': False,
}
SCOPE = (
    'Two candidate controls, six unanswered development questions. Local distinctness, '
    'cross-source linkage and cross-source contrast require separate source judgments. '
    'A contrast answer must not assume either preceding answer or a negative class.'
)

# These are source coordinates and field hashes, not accepted actor identities.
# Literal contacts are derived only from authenticated raw fields at build time.
SOURCE_SPECS = {
    'david_co': {
        'reference': 'source:3690066',
        'evidence_id': 'source_sample:55614a97-d705-9b45-4a14-6d8f6a0ecde7',
        'annotation_line': 4427,
        'fields': {
            'taper': 'ae48c565b240ba84b68bd4d7b5459d679f76752647385f7d780099f16ee3559f',
            'transferrer': 'ecfa1f812971e2637d610387d6a54f8370b6b97cbc6b2364244205bed34fce9e',
        },
    },
    'david_other': {
        'reference': 'source:3692346',
        'evidence_id': 'source_sample:b61f5925-7f47-5cda-38a9-396fc72a5377',
        'annotation_line': 7931,
        'fields': {
            'taper': 'ecfa1f812971e2637d610387d6a54f8370b6b97cbc6b2364244205bed34fce9e',
            'transferrer': 'ecfa1f812971e2637d610387d6a54f8370b6b97cbc6b2364244205bed34fce9e',
        },
    },
    'timothy_co': {
        'reference': 'source:3648757',
        'evidence_id': 'source_sample:1e2fbd90-bb2c-12cc-e152-0e7d4fdda3dc',
        'annotation_line': 3881,
        'fields': {
            'taper': 'b2ef21c8d574c392ee9fa6761cbb1a033173c62b1b82f149d13eb69c4f2b2bbb',
            'transferrer': 'b2ef21c8d574c392ee9fa6761cbb1a033173c62b1b82f149d13eb69c4f2b2bbb',
            'description': '7174c51ab081b63c1bb5e0e2b6719442c57df4816d2860c08111a64a4a2cbcfd',
        },
    },
    'timothy_other': {
        'reference': 'source:3715315',
        'evidence_id': 'source_sample:cbf355ae-9984-c4ef-0bbf-9b40bf0b2167',
        'annotation_line': 4480,
        'fields': {
            'taper': '40574a1d6be1e4ef3df9c3ad22623626bb40eda4fc0d446432d21e3a5f04da62',
            'transferrer': '40574a1d6be1e4ef3df9c3ad22623626bb40eda4fc0d446432d21e3a5f04da62',
        },
    },
}

# Tuples are (source key, field, start, end, exact public name when applicable).
# Context and contact excerpts are obtained from their fixed field/hash/span;
# their unredacted text belongs only in the new ignored evidence packet.
CONTROL_SPECS = [
    {
        'control_id': 'david-keller-steve-laplaca-v1',
        'actors': {
            'co_a': ('david_co', 'taper', 0, 12, 'David Keller'),
            'co_b': ('david_co', 'taper', 65, 78, 'Steve LaPlaca'),
            'other_a': ('david_other', 'taper', 0, 12, 'David Keller'),
        },
        'context': [
            ('co_credit_clause', ('david_co', 'taper', 0, 78, None)),
            ('explicit_alias_wording', ('david_co', 'taper', 17, 30, 'Grateful Dave')),
            ('contact_candidate_no_owner_asserted', ('david_co', 'taper', 32, 59, None)),
            ('structured_transferrer_field', ('david_co', 'transferrer', 0, 60, None)),
            ('other_recording_credit', ('david_other', 'taper', 0, 60, None)),
            ('other_explicit_alias_wording', ('david_other', 'taper', 17, 30, 'Grateful Dave')),
            ('other_contact_candidate_no_owner_asserted', ('david_other', 'taper', 32, 59, None)),
            ('other_structured_transferrer_field', ('david_other', 'transferrer', 0, 60, None)),
        ],
    },
    {
        'control_id': 'timothy-brown-roger-venezia-v1',
        'actors': {
            'co_a': ('timothy_co', 'taper', 16, 29, 'Timothy Brown'),
            'co_b': ('timothy_co', 'taper', 0, 13, 'Roger Venezia'),
            'other_a': ('timothy_other', 'taper', 0, 13, 'Timothy Brown'),
        },
        'context': [
            ('co_credit_clause', ('timothy_co', 'taper', 0, 29, None)),
            ('structured_transferrer_field', ('timothy_co', 'transferrer', 0, 29, None)),
            ('description_clause_and_following_contact', ('timothy_co', 'description', 119, 192, None)),
            ('following_contact_candidate_attachment_unadjudicated', ('timothy_co', 'description', 164, 191, None)),
            ('other_recording_credit', ('timothy_other', 'taper', 0, 43, None)),
            ('other_contact_candidate_no_owner_asserted', ('timothy_other', 'taper', 15, 42, None)),
            ('other_structured_transferrer_field', ('timothy_other', 'transferrer', 0, 43, None)),
        ],
    },
]


def load_base(root: Path):
    """Authenticate the explicit sibling code and manifest before importing it."""
    directory = root / BASE_TOOL
    authenticated = {}
    for name, expected in [('people_identity_packet.py', BASE_CODE_SHA256),
                           ('tool-manifest.json', BASE_MANIFEST_SHA256)]:
        data = (directory / name).read_bytes()
        actual = hashlib.sha256(data).hexdigest()
        if actual != expected:
            raise ValueError(f'Pinned base tool changed: {name}')
        authenticated[name] = data
    name = '_relisten_people_packet_' + BASE_CODE_SHA256[:12]
    spec = importlib.util.spec_from_file_location(name, directory / 'people_identity_packet.py')
    if spec is None or spec.loader is None:
        raise ValueError('Cannot load the explicit pinned packet module')
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module  # dataclass resolves annotations through this entry.
    # Execute exactly the bytes just authenticated, without accepting a cached
    # .pyc or rereading a source file that could change after its hash check.
    code = compile(authenticated['people_identity_packet.py'], str(directory / 'people_identity_packet.py'), 'exec')
    exec(code, module.__dict__)
    return module


def selected_contexts(base, sources) -> dict[str, dict]:
    selected = {}
    for key, spec in SOURCE_SPECS.items():
        original = sources.originals[spec['evidence_id']]
        label = spec['reference']
        base.require(original['metadata']['reference'] == label, f'{label}: source reference')
        base.require(original['annotation_line'] == spec['annotation_line'], f'{label}: physical source line')
        for field, wanted in spec['fields'].items():
            text = original['original_record']['raw_record']['row'][field]
            base.require(base.digest(text.encode('utf-8')) == wanted, f'{label}: {field} source hash')
        selected[key] = original
    base.require(len({x['metadata']['evidence_id'] for x in selected.values()}) == 4, 'Four distinct complete source records required')
    base.require(len({x['metadata']['artist_uuid'] for x in selected.values()}) == 4, 'Four development artists required')
    base.require(len({x['metadata']['family_group'] for x in selected.values()}) == 4, 'Four distinct retained recording/duplicate groups required')
    return selected


def source_citation(base, sources, selected: dict, span_spec: tuple) -> dict:
    key, field, start, end, expected_name = span_spec
    original = selected[key]
    text = original['original_record']['raw_record']['row'][field]
    quote = text[start:end]
    if expected_name is not None:
        base.require(quote == expected_name, f'{key}: exact reported name/alias anchor')
    citation = {
        'evidence_id': original['metadata']['evidence_id'],
        'field_path': '/raw_record/row/' + field,
        'field_sha256': SOURCE_SPECS[key]['fields'][field],
        'span': {'start': start, 'end': end, 'quote': quote, 'unit': 'unicode_code_points'},
    }
    base.verify_span(sources.originals, citation, f'control {key}/{field}[{start}:{end}]')
    return citation


def actor_context(original: dict, citation: dict) -> dict:
    meta = original['metadata']
    return {
        'evidence_id': meta['evidence_id'], 'reference': meta['reference'],
        'artist_uuid': meta['artist_uuid'], 'recording_duplicate_group': meta['family_group'],
        'known_recording_family': meta['family'], 'reported_actor_anchor': citation,
        'actor_kind': None, 'actor_identity': None,
    }


QUESTION_SPECS = [
    ('local_co_credit_distinctness', 'co_a', 'co_b',
     'Does this source-local coordinated credit support distinct actors, one actor/alias, or insufficient evidence? Preserve local role and contact attachment uncertainty.'),
    ('cross_source_linkage', 'co_a', 'other_a',
     'Do these credited mentions across recordings support the same actor, different actors, or insufficient evidence? A repeated name/contact is corroboration to assess, not an answer.'),
    ('cross_source_contrast', 'other_a', 'co_b',
     'Judge these two credited mentions directly from their full source contexts: same actor, different actors, or insufficient evidence. Do not assume the outcome of either other question.'),
]


def make_questions(base, actors: dict, support: list, control_id: str) -> list[dict]:
    questions = []
    for kind, left_key, right_key, instruction in QUESTION_SPECS:
        left, right = actors[left_key], actors[right_key]
        same_source = left['evidence_id'] == right['evidence_id']
        if kind == 'local_co_credit_distinctness':
            base.require(same_source, f'{control_id}: local question must retain one source')
        else:
            base.require(not same_source and left['artist_uuid'] != right['artist_uuid'], f'{control_id}: cross-source question scope')
            base.require(left['recording_duplicate_group'] != right['recording_duplicate_group'], f'{control_id}: repeated recording/duplicate group')
        key = {'version': VERSION, 'control': control_id, 'kind': kind,
               'left': left['reported_actor_anchor'], 'right': right['reported_actor_anchor']}
        questions.append({
            'question_id': 'people-identity-control:' + base.digest(base.canonical(key)),
            'control_id': control_id, 'question_kind': kind, 'instruction': instruction,
            'left': left, 'right': right, 'supporting_context': support,
            'allowed_decisions': DECISIONS, 'exposure': EXPOSURE,
            'other_answers_are_not_premises': True, 'answer': None, 'adjudication': None,
        })
    return questions


def make_plan(base, sources) -> tuple[dict[str, bytes], dict]:
    selected = selected_contexts(base, sources)
    questions = []
    span_count = 0
    for control in CONTROL_SPECS:
        actors = {}
        for key, span_spec in control['actors'].items():
            citation = source_citation(base, sources, selected, span_spec)
            actors[key] = actor_context(selected[span_spec[0]], citation)
            span_count += 1
        support = []
        for purpose, span_spec in control['context']:
            support.append({'purpose': purpose, 'citation': source_citation(base, sources, selected, span_spec)})
            span_count += 1
        questions.extend(make_questions(base, actors, support, control['control_id']))
    base.require(len(questions) == len({q['question_id'] for q in questions}) == 6, 'Exactly six distinct unanswered control questions required')
    counts = {
        'controls': 2, 'complete_source_contexts': 4, 'artists': 4,
        'recording_duplicate_groups': 4, 'questions': 6,
        'questions_by_kind': {kind: 2 for kind, _, _, _ in QUESTION_SPECS},
        'control_source_spans_checked': span_count,
        'base_discovery_spans_checked': sources.span_count,
        'completed_labels': 0, 'negative_labels_assumed': 0,
    }
    files = {
        'evidence.jsonl': base.encode_rows([selected[k] for k in sorted(selected)]),
        'questions.jsonl': base.encode_rows(questions), 'REVIEW.md': REVIEW_GUIDE.encode('utf-8'),
    }
    return files, counts


def controls_manifest(base, sources, counts: dict) -> dict:
    tools = Path(__file__).resolve().parent
    tool_bytes = (tools / 'tool-manifest.json').read_bytes()
    tool_manifest = json.loads(tool_bytes)
    for record in tool_manifest['artifacts']:
        base.authenticate(tools, record)
    return {
        'schema_version': VERSION, 'state': 'frozen_unlabeled_development_controls',
        'scope': SCOPE, 'exposure': EXPOSURE, 'counts': counts,
        'base_tool': base.file_record(sources.root / BASE_TOOL / 'people_identity_packet.py', sources.root),
        'base_manifest': base.file_record(sources.root / BASE_TOOL / 'tool-manifest.json', sources.root),
        'source_inputs': sources.inputs,
        'tool_manifest_sha256': base.digest(tool_bytes), 'tool_artifacts': tool_manifest['artifacts'],
        'separation': 'A new packet: existing 55 questions and their tool/manifest are not modified or invalidated.',
        'hash_note': 'Manifest excludes itself. Review answers belong outside the frozen packet.',
    }


def validate_controls(base, sources, packet: Path) -> dict:
    files, counts = make_plan(base, sources)
    manifest = json.loads((packet / 'manifest.json').read_bytes())
    expected = controls_manifest(base, sources, counts)
    base.require({k: v for k, v in manifest.items() if k != 'artifacts'} == expected, 'Control scope, source pins, exposure or counts changed')
    records = manifest['artifacts']
    base.require(len(records) == len(files) and {r['path'] for r in records} == set(files), 'Control artifact coverage')
    actual = {str(p.relative_to(packet)) for p in packet.rglob('*') if p.is_file()}
    base.require(actual == set(files) | {'manifest.json'}, 'Unexpected/missing control files; keep reviews outside packet')
    for record in records:
        base.authenticate(packet, record)
    for name, data in files.items():
        base.require((packet / name).read_bytes() == data, f'{name}: complete context or immutable unanswered question changed')
    return counts


REVIEW_GUIDE = """# Two development credit-actor controls

This packet has four complete retained source records and six unanswered
questions. Read questions.jsonl and full evidence.jsonl contexts. Each control
has a local co-credit question, cross-recording linkage question and a separate
cross-recording contrast question. These are correlated questions, not six
independent identities or six known negative labels.

Judge every question directly from its own source context and exact reported
name anchors. The contrast answer must not assume that either other answer is
same_entity or different_entities. Coordinated names require local attachment
and actor/alias assessment; contact following two names must not be assigned by
list order. Preserve structured taper/transferrer credits as local source facts.
Do not transfer roles to another source or conflate a person, account or project.

Use same_entity, different_entities or insufficient_evidence for each question.
A matching contact is not authenticated ownership; unequal names or contacts do
not prove different people. Unknown actor kind and attachment must stay explicit.
There is no required positive or negative balance. No network lookup is authorized.

Give a source-grounded reason and exact citations. A citation needs evidence_id,
field_path, field_sha256 and span (start/end/quote/unit=unicode_code_points).
End offsets are exclusive; retain original raw wording and field hashes.
Resolved cross-source answers need support from both recordings. Local answers
need the coordinated clause and both actor anchors in that source. A different
answer must explain evidenced distinctness/contradiction, not merely unequal
strings or the presumed answer to another question.

Write a new answer JSONL file outside this frozen packet, one row per question:
question_id, decision, reason, evidence (citation list), contradiction_basis
(null unless different_entities), and reviewer (id, kind=human|agent,
answer_blind=true, prior_exposure=list). Do not edit questions, source records,
old labels or another reviewer's first answers. Disclose prior development
exposure. Do not read the operator runbook, discovery rationales, old answers or
another review before freezing and hashing your first answers.

Independent answer-blind Codex development judgment is permitted. A separate
adjudicator later compares preserved first answers against these original
contexts, including agreements and unknowns, and records unresolved disagreements.
No supplied validator certifies semantic label truth or reviewer independence.
The contexts were already exposed during development: neither fresh review nor
adjudication makes them an untouched release test or authorizes identity writes.
"""


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    commands = parser.add_subparsers(dest='command', required=True)
    build = commands.add_parser('build')
    build.add_argument('--output', required=True, type=Path)
    validate = commands.add_parser('validate')
    validate.add_argument('--packet', required=True, type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    base = load_base(root)
    sources = base.DevelopmentSources.load(root)
    if args.command == 'build':
        output = args.output.resolve()
        base.require(output.parent == (root / base.DATA).resolve(), 'Use a new direct child of the ignored cross-artist directory')
        files, counts = make_plan(base, sources)
        base.publish_packet(output, files, controls_manifest(base, sources, counts))
        result = validate_controls(base, sources, output)
        print(json.dumps({'state': 'built_unanswered_development_controls', 'counts': result,
                          'manifest': base.file_record(output / 'manifest.json')}))
    else:
        result = validate_controls(base, sources, args.packet.resolve())
        print(json.dumps({'state': 'authenticated_unanswered_development_controls', 'counts': result,
                          'identity_accuracy_established': False}))


if __name__ == '__main__':
    main()
