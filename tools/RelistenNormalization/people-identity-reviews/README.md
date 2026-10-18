# Validate first identity reviews and separate adjudication

`review_people_identity_packet.py` checks complete answer files against the
frozen, source-backed people packet. It validates coverage, exact citations and
links to the original first-answer bytes. It does not certify the identity
judgments, authenticate the reviewers or authorize accepted-data writes.

The packet already informed development. New independent reviews and separate
adjudication remain development evidence. Agreement does not turn this packet
into an untouched release evaluation set.

## Inputs and ownership

Pass an explicit packet directory, its externally pinned manifest hash, and the
path/hash of the packet tool source. The validator imports that pinned source
and reuses its file authentication and exact raw-field span helper. It reads only
the packet's `manifest.json`, `questions.jsonl`, `evidence.jsonl`, `REVIEW.md` and,
when present, the retained primary visible text and excerpt metadata described below.
It does not rebuild the packet, reopen the original source population, repeat
reservation selection, or read operator provenance, earlier labels or model
predictions. The packet's already-reviewed manifest is the source trust anchor.
The import compiles and executes the already-authenticated source bytes directly;
an existing Python bytecode cache cannot replace the recorded source.

Reader A and B each supply a separate frozen JSONL file and expected whole-file
SHA-256. Both must cover every packet question exactly once. Optional adjudication
requires its own file and SHA-256 together. First answers and adjudication remain
separate files outside the frozen packet; the command changes none of them.

The validator is in
`tools/RelistenNormalization/people-identity-reviews/`. The existing
`people-identity-packet/` tool and frozen packet remain unchanged. All review
files, copied raw contexts and validation output must stay in ignored private
storage.

## Supported packet formats

Validator version `people-identity-review-validation:3` uses the explicit adapter
`people-review-packet-formats:1` for two known formats:

- `people-identity-development-packet:1` requires state
  `frozen_unlabeled_development_packet` and reads counts from `pair_questions`
  and `recordings`. This is the existing 55-question packet.
- `people-identity-distinctness-controls:1` requires state
  `frozen_unlabeled_development_controls` and reads counts from `questions`
  and `complete_source_contexts`. This is the separate six-question packet.

Both formats require the same development-only exposure, authenticated source
artifacts, null packet answers/adjudication and complete unique review coverage.
Unknown schemas or unsupported states fail explicitly. The receipt records the
exact packet schema and adapter version. The original packet tool remains the
version-pinned source of file/span helpers; no second review validator or source
reservation validator is introduced.

Controls allow only `local_co_credit_distinctness`, `cross_source_linkage` and
`cross_source_contrast` questions. Cross-source questions retain the two-source
citation requirement. A local co-credit question instead has two distinct actor
anchors in one retained recording. Both anchors must verify against their exact
raw field. A resolved local answer must cite spans covering both anchors; one
complete co-credit clause can cover them both. Citation coverage does not decide
whether the actors are the same or different. All three identity decisions remain
available, and other question answers are not treated as premises.

The separate control packet's manifest SHA-256 is
`9fad481fb0b986dfeaeb7bff9f5b3b25b28e73becc345383d12492f313455a69`.
Use the same CLI with that packet path/hash and its separately frozen reader and
adjudication file paths/hashes. The existing 55-question files, row hashes and
validated receipts remain unchanged.

## First-answer contract

Every row has exactly these fields:

```text
question_id, decision, reason, evidence[], contradiction_basis,
reviewer { id, kind, answer_blind, prior_exposure[] }
```

`decision` is `same_entity`, `different_entities` or `insufficient_evidence`.
`reason` is nonempty. `reviewer.kind` is `human` or `agent`; its ID is nonempty,
`answer_blind` is true and `prior_exposure` is an explicit list of nonempty
disclosures. An empty list declares no prior exposure. One file identifies one
reviewer ID/kind; exposure may differ by question and is aggregated in the
receipt. A and B must identify different reviewers and different files. These
are declarations, not independently authenticated identities or independence.

Each citation contains `evidence_id`, `field_path`, `field_sha256` and
`span {start,end,quote,unit}`. A recording citation's exact raw field must belong
to a source in the frozen packet. Its field hash, Unicode code-point bounds and
original quote must match. The end offset is exclusive. A resolved answer requires citations from
both sources named in its question. Additional retained packet sources may
support the reasoning; they cannot replace either required source.

`different_entities` also requires a nonempty `contradiction_basis` explanation
with those cited sources. The validator checks that the explanation and exact
source support are present; it cannot judge whether they establish distinct
actors. Other decisions require `contradiction_basis: null`. An insufficient
answer may retain an empty evidence list. Missing evidence is never converted
into a negative label, and no class balance is prescribed.

## Versioned citation locators

The explicit citation adapter `people-review-citation-locators:1` accepts three
formats and records separate
counts for each in the receipt:

- `canonical_raw_field`: `/raw_record/row/<field>`.
- `wrapper_dot_raw_field`: `original_record.raw_record.row.<field>`, as used in
  the frozen first answers and adjudication. Only that exact prefix followed by
  one simple, known source field is permitted. The adapter creates a canonical
  pointer solely for the existing raw-field verification helper. It changes no
  original citation, answer file, decision or physical row hash.
- `primary_visible_text`: the exact `primary/sonicarchives-visible-text.txt`
  locator with evidence ID `primary:sonicarchives-self-introduction`. No other
  primary ID or arbitrary file locator is accepted.

Primary text and `primary/excerpts.json` must both be authenticated by the pinned
packet manifest. The excerpt metadata must name that known source; each retained
excerpt's hash and wording must match the full visible text. A primary citation's
field hash covers the complete retained UTF-8 file, **including its final LF**.
The actual file is 1,020 bytes with SHA-256
`cd1f52cd6b4d0380fbb6e5742b406d74843213d94c0a82de30e413e42043df5a`.
The validator decodes those exact bytes and checks the citation's Unicode
code-point span and quote without trimming or normalizing the text.

A raw recording ID cannot use the primary-file route to bypass its raw-field
check. Primary context also cannot replace the two recording-source citations
required for a resolved identity answer. These are locator compatibility rules;
they do not add semantic identity authority or change the frozen review data.

## Adjudication contract

Every adjudication row has exactly these fields:

```text
question_id, decision, reason, evidence[], contradiction_basis,
adjudicator { id, kind, prior_exposure[] },
first_answer_row_sha256 { reader_a, reader_b },
reviewer_disagreement
```

Adjudication covers every question, including first-review agreements. Its
adjudicator must have a different declared ID from both first reviewers. The
same reason, decision and citation requirements apply.

`first_answer_row_sha256` links the exact original row in each frozen file.
Hashes exclude one physical LF or CRLF terminator, but retain all other original
bytes, including spaces and property order. The validator does not parse and
reserialize a row before hashing it. U+2028 and U+2029 inside strings do not
separate JSONL records. A blank physical row or duplicate JSON object property
fails validation.

`reviewer_disagreement` must be a Boolean equal to the comparison of A's and B's
original **decisions**. Different rationales with the same decision remain a
decision agreement. The validator checks row-hash links and that Boolean for
every question; adjudication cannot silently relabel its first-answer inputs.

## Receipt and failure behavior

All files must validate before an output directory is created. Choose a new
directory outside the frozen packet with an existing parent. Existing output is
never overwritten. Source inputs, first answers, optional adjudication and the
executed validator/packet-tool source are copied exactly into SHA-named private
input directories. Copies use mode 0600; new directories use mode 0700.

The final `receipt.json` contains whole-file lengths/hashes, retained-copy paths,
per-review class counts, decision agreements/disagreements and optional
adjudication counts, including how many exact citations were checked.
It records the citation-locator adapter version, per-review format counts and
aggregate format counts. Authenticated primary text and excerpt bytes are retained
and rechecked with the other consumed inputs.
Adjudication reports changes on original agreements and
whether disputed decisions followed A, followed B or chose the third outcome.
It also counts changes from each reader on disputed questions. Counts retain
zero-valued classes.

The command rechecks originals and retained copies before publishing the final
receipt. A changed input or write failure leaves no completed receipt. Preserve
any partial output for diagnosis and retry in a new directory. A completed
receipt proves file/schema/citation integrity; it grants no matching-policy,
canonical identity, merge or recording-credit authority. Stdout contains the
aggregate summary and final receipt fingerprint, not review prose or source
quotes.

## Root runbook

The author supplies source and synthetic tests only. Root owns all test execution
and real-data validation. Python's standard library is sufficient; no .NET,
database, provider, model or network call is used.

From the repository root, run the tests after the source freeze:

```sh
python3 -B -m unittest discover \
  -s tools/RelistenNormalization/people-identity-reviews \
  -p 'test_review_people_identity_packet.py' -v
```

Tests locate the repository through `RelistenApi.sln` in the current directory
or an ancestor. For a scratch invocation from elsewhere, set
`RELISTEN_NORMALIZATION_ROOT` explicitly. Tests import the real packet helper
source but use invented source fields, questions, reviews and adjudication.

Validate the two frozen first-answer files before the adjudication is available:

```sh
python3 tools/RelistenNormalization/people-identity-reviews/review_people_identity_packet.py \
  --packet "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1" \
  --packet-manifest-sha256 1255af35db5b6d1a26710c784dc4ba7332b44509a7cd00666433eb2c53f573b9 \
  --packet-tool "$PWD/tools/RelistenNormalization/people-identity-packet/people_identity_packet.py" \
  --packet-tool-sha256 20ac70e6b999b03049141852127334a31426ebc048071d650663aaa7d0e3a5c2 \
  --reader-a "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1-reviews/reader-a/first-answers.jsonl" \
  --reader-a-sha256 7829c304a3852f8ad31eb08a6a516117231bffdcf1bba1714d941d20829512b2 \
  --reader-b "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1-reviews/reader-b/first-answers.jsonl" \
  --reader-b-sha256 454409fdfeb065c07275ca0080c548d9c157934f586d9af884fbf75d06a995ce \
  --output "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-first-review-validation-v1"
```

After the separate adjudicator freezes all answers, repeat with its exact
additional arguments:

```sh
  --adjudication "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1-reviews/adjudication-v1/adjudications.jsonl" \
  --adjudication-sha256 f626641e4be9f6a37e7a941076e407c4122c43d1c858ff9d607f3a8cf9d53882
```

Choose another new output directory.
Do not inspect or validate an adjudication file that is still being written.

Preserve the executed source hashes and
first-answer files. Keep labels from this packet distinct from the original
people-development bundle and from future untouched evaluation data.
