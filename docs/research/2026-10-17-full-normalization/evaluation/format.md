# Normalization evaluation corpus format

The corpus records imported evidence and expected decisions separately. Run
`validate.py` before changing labels or using them in an evaluation:

```sh
python3 docs/research/2026-10-17-full-normalization/evaluation/validate.py \
  --evidence docs/research/2026-10-17-full-normalization/evaluation/evidence.jsonl \
  --cases docs/research/2026-10-17-full-normalization/evaluation/cases.jsonl \
  --manifest docs/research/2026-10-17-full-normalization/evaluation/manifest.json
```

The command checks references, exact citations, split separation and file hashes.
Its JSON output describes coverage. It does not measure extraction or matching
accuracy, certify a label, or establish an identity from equal text.

## Evidence

Each line of `evidence.jsonl` is one JSON object with a unique string `id`, a
`kind`, artist identity (`artist_id`, `artist_name`), `provider`, `entity_id` and
`fields`. `fields` preserves persisted values, including nulls and structured
values. Capture receipts explain selection, provider attribution and database
snapshot. Provider associations alone do not prove ownership of a recording.

## Cases

Each line of `cases.jsonl` contains:

- `id`: unique case identifier.
- `domain`: `venues`, `songs`, `compositions`, `people` or `lineage`.
- `task`: `extract`, `match` or `relationship`.
- `split`: `development` or `evaluation`.
- `group`: a curator-assigned related family. Keep related examples together,
  including aliases and recordings that could leak the expected decision.
- `inputs`: nonempty array of `{evidence_id, field}` references.
- `expected`: object with `status` and domain-specific assertions. Status is
  `supported`, `different` or `unresolved`. A supported expectation requires a
  nonempty `assertions` object or array. The validator does not interpret those
  assertions; [assertions.md](assertions.md) defines comparison semantics for domain evaluators.
- `basis`: nonempty array of citations to fields listed in `inputs`.
- `rationale`: explanation of the expected decision and its evidence limits.
- `annotation_status`: `development_labeled` or `independently_reviewed`.

Use `supported` when the evidence supports the specified extraction or
relationship. Use `different` for an evidenced distinction, rather than a lack
of proof of equality. Use `unresolved` when the inputs cannot establish the
requested decision. In particular, matching song titles or person names can
support candidate retrieval while leaving identity unresolved.

A basis item has exactly one citation form:

```json
{"evidence_id":"source:123","field":"transferrer","quote":"Transferred by Jane"}
```

`quote` must be a nonempty, exact substring of a string field. For absent or
structured data, cite the complete persisted value:

```json
{"evidence_id":"setlist:456","field":"order_available","value":false}
```

`value` must match the field exactly, including JSON types. Null, an empty
string, false and zero are distinct. A citation to null can support the
observation that a captured field is missing; it cannot prove the upstream
provider never supplied that information.

The validator rejects reuse of an evidence ID or a group across splits. Curators
must also identify relationships across different evidence IDs: the validator
cannot discover that two rows describe the same recording family. Development
labels in the evaluation split remain provisional until independently reviewed.

## Integrity receipt

`manifest.json` lists `files: [{path, sha256}]`, with paths relative to the
manifest. The validator verifies every listed file and requires the evidence
file to be listed. Include `cases.jsonl` to detect a truncated or changed label
file even when all five domains remain represented. If present,
`record_counts.evidence` and `record_counts.cases` must equal the row counts.

A deliberate corpus revision requires a new receipt after the revised labels
have been reviewed. Do not regenerate hashes merely to suppress an unexpected
mismatch. Keep original capture receipts and SQL beside the combined corpus.

Run the validator's behavioral tests with:

```sh
python3 -B -m unittest discover \
  -s docs/research/2026-10-17-full-normalization/evaluation \
  -p 'test_*.py'
```
