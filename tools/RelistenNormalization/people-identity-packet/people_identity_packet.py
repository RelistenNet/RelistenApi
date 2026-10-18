"""Build and authenticate one development-only people identity review packet.

No provider, model, database, subprocess or runtime-extractor dependencies. Shared
annotation payloads are decoded only after a pinned metadata reservation gate.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
from typing import Any


DATA = Path('local-dev/normalization-data/2026-10-17-cross-artist')
DISCOVERY = DATA / 'training-development-people-v2-discovery'
RESERVATIONS = DATA / 'training-development-people-v1/reserved-reference-index.json'
ANNOTATION = DATA / 'annotation-v1/evidence.jsonl'
RESEARCH = Path('docs/research/2026-10-17-full-normalization')
VERSION = 'people-identity-development-packet:1'
DISCOVERY_PIN = {
    'path': str(RESEARCH / 'scale/evidence/people-identity-discovery-v2.json'),
    'bytes': 10449,
    'sha256': 'a10c1c45662ac4cf8531ad3ee6923a150c0bef40d91d9a90e1f6379037f7b30e',
}
RESERVATION_PIN = {
    'path': str(RESERVATIONS), 'bytes': 955704,
    'sha256': 'f4f1d8c43d886800d4d0e8ef2d14c438a8f1ab92904ed0be53ef6436ebf32331',
}
# Byte-only sentinels: original development answers are never parsed by this tool.
ORIGINAL_PINS = [
    ('people-development-v1/labels.jsonl', 344888, 'e813c310b673bf66ee7dbeec5c87b9453d39bea6432f941587e79e6a9efaf4f2'),
    ('people-development-v1/mentions.jsonl', 117015, '6690ddb2e90bb3332e31e1e512800209b5dce88dc7e1edd20ac4b4d57f5c0a52'),
    ('people-development-v1/candidate-pool.json', 20599, '3176d472711662503b57087f1ad6d6f6f9c6c77b53d8738b926764559de08219'),
    ('actor-facts-development-v1/labels.jsonl', 2206691, '4fd5fe8eb60435d522163a60adeadd2542bf2f7047538d338b5f7fdcf9fcd428'),
    ('credits-development-v1/labels.jsonl', 158865, '5f2eb545f8d5d66b4750d74e883cc179e673e4775f5df9207fac027069599c12'),
]
SELECTED_LINES = (1, 4, 5, 7, 10, 23, 26, 31, 34, 35, 36)
EXPECTED = {
    'eligible_originals_authenticated': 2184, 'discovery_hypotheses_checked': 38,
    'discovery_spans_checked': 979, 'hypotheses': 11, 'recordings': 82,
    'artists': 66, 'recording_duplicate_groups': 80, 'pair_questions': 55,
    'completed_identity_labels': 0,
}
DECISIONS = {'same_entity', 'different_entities', 'insufficient_evidence'}
EXPOSURE = {
    'partition': 'development', 'eligible_for_release_evaluation': False,
    'prior_discovery_and_extraction_exposure': True,
    'human_identity_adjudication_completed': False,
    'canonical_person_creation_authorized': False, 'policy_activation_permitted': False,
}
SCOPE = (
    'Development source-context adjudication only. Hypothesis IDs and repeated '
    'contacts/profiles are selection coordinates, not accepted identities. '
    'The 55 questions supply no predetermined positive or negative labels.'
)


class ContractError(ValueError):
    """An authenticated input, evidence locator or review contract changed."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ContractError(message)


def canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode('utf-8')


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_record(path: Path, relative_to: Path | None = None) -> dict:
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return {
        'path': str(path.relative_to(relative_to) if relative_to else path),
        'bytes': path.stat().st_size, 'sha256': h.hexdigest(),
    }


def authenticate(root: Path, record: dict) -> None:
    required = ('path', 'bytes', 'sha256')
    require(all(key in record for key in required), 'File receipt requires path, bytes and sha256')
    expected = {key: record[key] for key in required}
    actual = file_record(root / record['path'], root)
    # Parent receipts authenticate descriptive metadata, such as a text recipe.
    # File identity depends only on the required path, byte count and content hash.
    require(actual == expected, f"Input changed: {record['path']}")


def json_rows(path: Path):
    # A source string can contain U+2028: str.splitlines() corrupts JSONL boundaries.
    with path.open(encoding='utf-8') as stream:
        for number, line in enumerate(stream, 1):
            require(bool(line.strip()), f'{path.name}:{number}: blank physical JSONL record')
            yield number, json.loads(line)


def development_metadata(index: dict) -> dict[int, dict]:
    """Validate the entire safe metadata projection before opening raw payloads."""
    excluded = {row['evidence_id'] for row in index['excluded_annotation_metadata']}
    reserved_groups = set(index['reserved_annotation_family_groups'])
    reserved_families = set(index['reserved_known_families'])
    reserved_refs = set(index['reserved_references']) | {
        row['reference'] for row in index['metadata_linked_parent_sources']
    }
    selected = {}
    evidence_ids = set()
    for meta in index['eligible_source_metadata']:
        label = f"metadata {meta['reference']}"
        require(meta['artist_partition'] == meta['family_partition'] == 'development', f'{label}: non-development partition')
        require(meta['evidence_id'] not in excluded, f'{label}: excluded evidence')
        require(meta['family_group'] not in reserved_groups and meta['family'] not in reserved_families, f'{label}: reserved family')
        require(meta['reference'] not in reserved_refs, f'{label}: reserved or exposed reference')
        line = meta['source_line']
        require(type(line) is int and line > 0 and line not in selected, f'{label}: duplicate/invalid physical line')
        require(meta['evidence_id'] not in evidence_ids, f'{label}: duplicate evidence ID')
        selected[line] = meta
        evidence_ids.add(meta['evidence_id'])
    return selected


def selected_originals(path: Path, metadata: dict[int, dict]) -> dict[str, dict]:
    """Read bytes for every line, but decode JSON only for preselected safe lines."""
    originals = {}
    with path.open('rb') as stream:
        for number, physical_line in enumerate(stream, 1):
            if number not in metadata:
                continue
            meta = metadata[number]  # Gate selection occurred before json.loads.
            record = json.loads(physical_line)
            label = f"source {meta['reference']} at annotation line {number}"
            require(record['evidence_id'] == meta['evidence_id'], f'{label}: evidence ID')
            require(record['artist_partition'] == record['family_partition'] == 'development', f'{label}: payload partition')
            require(record['artist']['uuid'] == meta['artist_uuid'], f'{label}: artist')
            require(record['input_duplicate_family'] == meta['family_group'], f'{label}: duplicate group')
            require(record['known_recording_family'] == meta['family'], f'{label}: known recording family')
            require(meta['reference'] == 'source:' + str(record['raw_record']['row']['id']), f'{label}: source ID')
            originals[meta['evidence_id']] = {
                'annotation_line': number,
                'annotation_line_sha256': digest(physical_line.rstrip(b'\r\n')),
                'metadata': meta, 'original_record': record,
            }
    require(len(originals) == len(metadata), 'Selected physical source lines missing')
    return originals


def verify_span(originals: dict[str, dict], citation: dict, label: str) -> None:
    eid = citation['evidence_id']
    require(eid in originals, f'{label}: source outside authenticated development population')
    field_path = citation['field_path']
    require(field_path.startswith('/raw_record/row/') and field_path.count('/') == 3, f'{label}: raw field locator')
    field = field_path.rsplit('/', 1)[1]
    text = originals[eid]['original_record']['raw_record']['row'][field]
    require(isinstance(text, str), f'{label}: non-text source field')
    span = citation['span']
    start, end = span['start'], span['end']
    require(type(start) is int and type(end) is int and 0 <= start < end <= len(text), f'{label}: Unicode bounds')
    require(span['unit'] == 'unicode_code_points', f'{label}: wrong span units')
    require(digest(text.encode('utf-8')) == citation['field_sha256'], f'{label}: field hash')
    require(text[start:end] == span['quote'], f'{label}: exact source quote')


def occurrence_citations(occurrence: dict) -> list[dict]:
    return [{
        'evidence_id': occurrence['evidence_id'],
        'field_path': '/raw_record/row/' + occurrence['field'],
        'field_sha256': occurrence['field_sha256'], 'span': occurrence[kind],
    } for kind in ('match_span', 'context_span')]


def verify_inventory(inventory: list[dict], originals: dict[str, dict]) -> int:
    """Check all 38 discovery hypotheses and all 979 spans, not just the selection."""
    seen = set()
    spans = 0
    for number, family in enumerate(inventory, 1):
        label = f'inventory line {number}'
        require(family['family_id'] not in seen, f'{label}: duplicate hypothesis ID')
        seen.add(family['family_id'])
        require(family['canonical_person_creation_authorized'] is False and family['policy_activation_permitted'] is False, f'{label}: publication authority')
        eids = set(family['evidence_ids'])
        require(eids <= originals.keys(), f'{label}: excluded/non-development input')
        require(len(eids) == len(family['evidence_ids']) == family['source_count'], f'{label}: source count')
        require(len({originals[eid]['metadata']['artist_uuid'] for eid in eids}) == family['artist_count'] >= 2, f'{label}: cross-artist coverage')
        require(set(family['family_group_ids']) == {originals[eid]['metadata']['family_group'] for eid in eids}, f'{label}: recording groups')
        occurrences = family['supporting_occurrences']
        require({item['evidence_id'] for item in occurrences} == eids, f'{label}: occurrence source coverage')
        citations = list(family['structured_credit_citations']) + family.get('person_context_citations', [])
        for occurrence in occurrences:
            original = originals[occurrence['evidence_id']]
            require(occurrence['artist'] == original['original_record']['artist'], f'{label}: occurrence artist')
            require(occurrence['family_group'] == original['metadata']['family_group'], f'{label}: occurrence recording group')
            citations.extend(occurrence_citations(occurrence))
        for citation in citations:
            require(citation['evidence_id'] in eids, f'{label}: citation outside hypothesis')
            verify_span(originals, citation, label)
            spans += 1
    return spans


def question_context(family: dict, eid: str, originals: dict[str, dict]) -> dict:
    """Show local reference attachments without copying discovery judgments/rationale."""
    original = originals[eid]
    occurrences = []
    for item in family['supporting_occurrences']:
        if item['evidence_id'] == eid:
            occurrences.append({key: item[key] for key in (
                'field', 'field_sha256', 'kind', 'match_span', 'context_span',
            )})
    return {
        'evidence_id': eid, 'reference': original['metadata']['reference'],
        'artist_uuid': original['metadata']['artist_uuid'],
        'recording_duplicate_group': original['metadata']['family_group'],
        'focal_reference_occurrences': occurrences,
        'actor_kind': None, 'actor_identity': None,
        'instruction': 'Identify the actor, if supported, attached to this reference in its full local context; do not infer person kind or ownership.',
    }


def make_questions(families: list[tuple[int, dict]], originals: dict[str, dict]) -> list[dict]:
    questions = []
    for line, family in families:
        per_artist = {}
        for eid in sorted(family['evidence_ids']):
            per_artist.setdefault(originals[eid]['metadata']['artist_uuid'], eid)
        artists = sorted(per_artist)
        left = per_artist[artists[0]]
        for artist in artists[1:]:
            right = per_artist[artist]
            require(originals[left]['metadata']['family_group'] != originals[right]['metadata']['family_group'], f'inventory {line}: pair repeats a recording/duplicate group')
            identity = {'version': VERSION, 'hypothesis_id': family['family_id'], 'left': left, 'right': right}
            questions.append({
                'question_id': 'people-identity-development:' + digest(canonical(identity)),
                'hypothesis_id': family['family_id'],
                'left': question_context(family, left, originals),
                'right': question_context(family, right, originals),
                'task': 'Do these source-local credit references support the same credited actor, different actors, or insufficient evidence? Judge local attachment and actor/project distinction first.',
                'allowed_decisions': sorted(DECISIONS),
                'exposure': EXPOSURE, 'answer': None, 'adjudication': None,
            })
    require(len({row['question_id'] for row in questions}) == len(questions), 'Duplicate pair question IDs')
    return questions


@dataclass
class DevelopmentSources:
    root: Path
    originals: dict[str, dict]
    inventory: list[dict]
    inputs: list[dict]
    primary_files: dict[str, bytes]
    primary_source: dict
    primary_http: dict
    span_count: int
    tool_manifest: dict
    tool_manifest_sha256: str

    @classmethod
    def load(cls, root: Path) -> DevelopmentSources:
        # Authenticate and validate reservation metadata before opening raw records.
        authenticate(root, DISCOVERY_PIN)
        authenticate(root, RESERVATION_PIN)
        index = json.loads((root / RESERVATIONS).read_bytes())
        metadata = development_metadata(index)
        require(len(metadata) == EXPECTED['eligible_originals_authenticated'], 'Eligible metadata count changed')
        discovery_receipt = json.loads((root / DISCOVERY_PIN['path']).read_bytes())
        inputs = [DISCOVERY_PIN, RESERVATION_PIN]
        for record in discovery_receipt['artifacts'].values():
            authenticate(root, record)
            inputs.append(record)
        for path, size, sha256 in ORIGINAL_PINS:
            record = {'path': str(RESEARCH / 'training' / path), 'bytes': size, 'sha256': sha256}
            authenticate(root, record)
            inputs.append(record)
        raw_record = file_record(root / ANNOTATION, root)
        require(raw_record['sha256'] == index['annotation_evidence_sha256'], 'Shared annotation source bytes changed')
        inputs.append(raw_record)  # Whole-file byte hashing never parses held-out text.
        originals = selected_originals(root / ANNOTATION, metadata)
        inventory = [row for _, row in json_rows(root / DISCOVERY / 'family-inventory.jsonl')]
        require(len(inventory) == EXPECTED['discovery_hypotheses_checked'], 'Discovery hypothesis count changed')
        spans = verify_inventory(inventory, originals)
        require(spans == EXPECTED['discovery_spans_checked'], '979 discovery-span denominator changed')
        primary_source, = [row for _, row in json_rows(root / DISCOVERY / 'primary-sources.jsonl')]
        primary_http = json.loads((root / DISCOVERY / 'primary-site-http-receipt.json').read_bytes())
        require(primary_http['source_id'] == primary_source['source_id'] and primary_http['requested_url'] == primary_source['url'], 'Primary page identity changed')
        primary_files = {}
        for key, output in [('body', 'primary/sonicarchives-homepage.html'), ('visible_text', 'primary/sonicarchives-visible-text.txt')]:
            record = primary_http[key]
            authenticate(root, record)
            primary_files[output] = (root / record['path']).read_bytes()
        visible = primary_files['primary/sonicarchives-visible-text.txt'].decode('utf-8').rstrip('\n')
        require({check['quote_sha256'] for check in primary_http['exact_excerpt_checks']} == {q['sha256'] for q in primary_source['excerpts']}, 'Primary excerpt coverage')
        for check in primary_http['exact_excerpt_checks']:
            span = check['visible_text_span']
            require(0 <= span['start'] < span['end'] <= len(visible), 'Primary visible-text bounds')
            require(visible[span['start']:span['end']] == span['quote'] and digest(span['quote'].encode()) == check['quote_sha256'], 'Primary exact excerpt')
        tools = Path(__file__).resolve().parent
        manifest_bytes = (tools / 'tool-manifest.json').read_bytes()
        tool_manifest = json.loads(manifest_bytes)
        for record in tool_manifest['artifacts']:
            authenticate(tools, record)
        return cls(root, originals, inventory, inputs, primary_files, primary_source, primary_http, spans, tool_manifest, digest(manifest_bytes))

    def planned_files(self) -> tuple[dict[str, bytes], dict]:
        families = [(line, self.inventory[line - 1]) for line in SELECTED_LINES]
        # Line pins are intentional: support categories stay operator provenance,
        # rather than being silently used as pair answers or shown to reviewers.
        require(sum(f['support_category'] == 'person_context' for _, f in families) == 7, 'Seven reviewed hypothesis selections changed')
        require(sum(f['linkage_status'] == 'insufficient_evidence' for _, f in families) == 4, 'Four ambiguous hypothesis selections changed')
        eids = sorted({eid for _, family in families for eid in family['evidence_ids']})
        selected = [self.originals[eid] for eid in eids]
        questions = make_questions(families, self.originals)
        counts = {
            'eligible_originals_authenticated': len(self.originals),
            'discovery_hypotheses_checked': len(self.inventory), 'discovery_spans_checked': self.span_count,
            'hypotheses': len(families), 'recordings': len(selected),
            'artists': len({row['metadata']['artist_uuid'] for row in selected}),
            'recording_duplicate_groups': len({row['metadata']['family_group'] for row in selected}),
            'pair_questions': len(questions), 'completed_identity_labels': 0,
        }
        require(counts == EXPECTED, f'Packet denominators changed: {counts}')
        operator = {
            'scope': SCOPE, 'exposure': EXPOSURE,
            'selection': [{'inventory_line': line, 'hypothesis_id': f['family_id'],
                           'display_name': f['display_name'], 'source_evidence_ids': f['evidence_ids'],
                           'prior_status': f['judgment_status'], 'prior_linkage_status': f['linkage_status']}
                          for line, f in families],
            'review_warning': 'Operator file exposes prior selection judgments. Do not provide it, old labels or discovery rationales to an answer-blind reviewer. Existing exposure still makes this development data.',
            'negative_class_gap': 'Same-hypothesis sampling may yield zero different_entities labels; do not silently add questions or convert unknowns to negatives.',
        }
        files = {
            'evidence.jsonl': encode_rows(selected), 'questions.jsonl': encode_rows(questions),
            'operator-provenance.json': encode_json(operator),
            'primary/excerpts.json': encode_json(self.primary_source),
            'primary/http-receipt.json': encode_json(self.primary_http),
            'REVIEW.md': REVIEW_GUIDE.encode('utf-8'), **self.primary_files,
        }
        return files, counts


def encode_json(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode('utf-8')


def encode_rows(rows: list[dict]) -> bytes:
    return b''.join(canonical(row) + b'\n' for row in rows)


REVIEW_GUIDE = """# Development identity review

Read questions.jsonl and complete evidence.jsonl contexts, then retained primary
context where relevant. Do not execute or render the captured HTML. Each question
contains candidate reference spans, not an asserted actor or an answer.

Do not read operator-provenance.json, the operator/tool README, discovery
rationales, earlier labels or another reviewer's answers before freezing your
first answers. This packet was already selected using development work: new blind
answers do not make it an untouched test or release gold. Disclose your prior
context exposure. Independent agent or human development review is permitted.

First determine local reference attachment and actor/project/account distinctions.
A shared contact alone is not proof of identity; unequal names/contacts are not
proof of different people. Keep recording and transfer roles local to each source.
Unknown actor kind and ambiguous attachment must remain explicit. The permissible
identity answers are same_entity, different_entities and insufficient_evidence.
Do not infer different_entities to balance a class or fill missing evidence.

Write one row per question into a separate new JSONL file, outside this frozen
packet. Never edit questions/evidence or an original development label. Each row
must retain the question_id and include these fields:

- decision: one of the three identity answers above.
- reason: the source-grounded reasoning, including any unresolved attachment,
  actor-kind or person/project distinction.
- reviewer: id, kind (human or agent), answer_blind (true), prior_exposure (list).
- evidence: exact raw-field citations, each with evidence_id, field_path,
  field_sha256 and span (start, end, quote, unit=unicode_code_points).
- contradiction_basis: an evidenced distinctness/contradiction reason for a
  different_entities answer; null otherwise. Unequal strings are not that reason.

A resolved identity answer must cite support from both recordings. Use the exact
original raw field, preserving its hash, wording and Unicode code-point offsets;
end is exclusive. Complete further source contexts in evidence.jsonl may supply
corroboration. The primary page identifies a claimed speaker, not every recording
credit or an authenticated owner. No new network lookup is authorized.

Freeze and hash your first-answer file before seeing another reviewer's output.
A separate adjudicator later compares both preserved answers with these original
contexts, retains explicit insufficient outcomes and records any disagreement.
Do not claim that agreement alone establishes label correctness or release gold.

All complete local contexts are retained. Additional discovery hypotheses are
outside this packet. Do not add questions or change the 55-question denominator.
"""


def publish_packet(output: Path, files: dict[str, bytes], manifest: dict) -> None:
    """Exclusive new directory; manifest appears only after every data write succeeds."""
    output.mkdir(mode=0o700, exist_ok=False)
    artifacts = []
    for name, data in files.items():
        path = output / name
        path.parent.mkdir(mode=0o700, exist_ok=True)
        with path.open('xb') as stream:
            stream.write(data)
        path.chmod(0o600)
        artifacts.append(file_record(path, output))
    manifest['artifacts'] = artifacts
    temporary = output / 'manifest.partial.json'
    with temporary.open('xb') as stream:
        stream.write(encode_json(manifest))
    temporary.chmod(0o600)
    os.replace(temporary, output / 'manifest.json')


def packet_manifest(sources: DevelopmentSources, counts: dict) -> dict:
    return {
        'schema_version': VERSION, 'state': 'frozen_unlabeled_development_packet',
        'scope': SCOPE, 'exposure': EXPOSURE, 'counts': counts,
        'selected_inventory_lines': list(SELECTED_LINES), 'inputs': sources.inputs,
        'tool_manifest_sha256': sources.tool_manifest_sha256,
        'tool_artifacts': sources.tool_manifest['artifacts'],
        'hash_note': 'Manifest excludes its own hash. First answers and adjudications belong in separate files.',
    }


def verify_packet(packet: Path, sources: DevelopmentSources) -> dict:
    manifest = json.loads((packet / 'manifest.json').read_bytes())
    files, counts = sources.planned_files()
    wanted = packet_manifest(sources, counts)
    require({key: value for key, value in manifest.items() if key != 'artifacts'} == wanted, 'Packet scope, source pins, counts or exposure changed')
    require({record['path'] for record in manifest['artifacts']} == set(files), 'Packet artifact coverage changed')
    require(len(manifest['artifacts']) == len(files), 'Duplicate manifest artifact entries')
    actual_files = {str(path.relative_to(packet)) for path in packet.rglob('*') if path.is_file()}
    require(actual_files == set(files) | {'manifest.json'}, 'Unexpected or missing packet files; keep reviews outside the frozen packet')
    for record in manifest['artifacts']:
        authenticate(packet, record)
    for name, data in files.items():
        require((packet / name).read_bytes() == data, f'{name}: complete source context or immutable question changed')
    return counts


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path, help='Repository containing the pinned retained inputs')
    commands = parser.add_subparsers(dest='command', required=True)
    build = commands.add_parser('build', help='Create a new ignored private packet; no overwrite')
    build.add_argument('--output', required=True, type=Path)
    validate = commands.add_parser('validate', help='Reauthenticate sources and immutable packet; optional separate reviews')
    validate.add_argument('--packet', required=True, type=Path)
    args = parser.parse_args()
    sources = DevelopmentSources.load(args.root.resolve())
    if args.command == 'build':
        output = args.output.resolve()
        require(output.parent == (sources.root / DATA).resolve(), 'Output must be a new direct child of the ignored cross-artist data directory')
        files, counts = sources.planned_files()
        # Check original answers again before publishing any new packet files.
        for path, size, sha256 in ORIGINAL_PINS:
            authenticate(sources.root, {'path': str(RESEARCH / 'training' / path), 'bytes': size, 'sha256': sha256})
        publish_packet(output, files, packet_manifest(sources, counts))
        print(json.dumps({'state': 'built_unlabeled_development_packet', 'counts': verify_packet(output, sources), 'manifest': file_record(output / 'manifest.json')}))
    else:
        counts = verify_packet(args.packet.resolve(), sources)
        report = {'state': 'authenticated_development_packet', 'counts': counts, 'identity_accuracy_established': False}
        print(json.dumps(report, sort_keys=True))


if __name__ == '__main__':
    main()
