# People identity development packet v1

This tool prepares **55 unanswered cross-artist credit-actor questions** from
**11 existing development hypotheses, 82 recordings, 66 artists and 80 known
recording/duplicate groups**. It preserves full source records, reference spans
and unknown actor kinds. It creates no person, identity label or policy.

The sampling hypotheses have already informed development. Answer-blind new
reviews and later adjudication remain development evidence; they cannot become
an untouched release test. Equal contacts do not prove identity, unequal
contacts/names do not prove different people, and an actor's role on one
recording does not establish that role on another.

## Run the tool

Python's standard library is sufficient. No .NET, database, provider, model,
network, subprocess, or accepted-data writer is used by the tool or tests.

From the repository root:

```sh
python3 -m unittest discover \
  -s tools/RelistenNormalization/people-identity-packet \
  -p 'test_people_identity_packet.py' -v

python3 tools/RelistenNormalization/people-identity-packet/people_identity_packet.py \
  --root "$PWD" build \
  --output "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1"

python3 tools/RelistenNormalization/people-identity-packet/people_identity_packet.py \
  --root "$PWD" validate \
  --packet "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-adjudication-v1"
```

The output must be a **new direct child** of the existing ignored cross-artist
data directory. Existing files/directories are never overwritten. If a write
fails, preserve the partial directory and diagnose it; choose a new output name
for a subsequent build. A manifest is published only after all data writes.
Data files use owner-only permissions (0600), and new directories use 0700.
Keep the entire packet and subsequent review data ignored/private. A tracked
summary may contain counts and hashes, without raw contacts or complete fields.

The successful build prints its manifest hash and the exact expected counts.
Save that output separately and pin the manifest before giving evidence to a
reviewer. `tool-manifest.json` authenticates this source, tests and runbook; the
operator should compare its hash to the reviewed handoff. Neither source input
pins nor the output contract can be overridden by a CLI flag.

## Authentication and exclusion order

`DevelopmentSources.load()` implements the sequence explicitly:

1. Authenticate the committed discovery receipt and reservation-index bytes.
2. Parse only the safe reservation/eligibility metadata projection. Reject any
   eligible item outside artist-development **or** family-development, or inside
   reserved references, excluded evidence IDs, reserved recording/duplicate
   groups, reserved known families or metadata-linked exposed parent sources.
   Finish this check for the whole metadata projection before opening raw data.
3. Authenticate all 11 artifacts pinned by the discovery receipt, plus the
   original label/mention/candidate sentinels. These old answers are byte-hashed,
   **never parsed** by this tool.
4. Hash the entire shared `annotation-v1/evidence.jsonl` as bytes. Iterate its
   physical byte lines, selecting only the 2,184 preapproved line numbers before
   `json.loads`. Unselected raw payloads—including held-out and reserved
   payloads—are never decoded. Check the decoded source's identity, partitions,
   artist and recording/duplicate group against its approved metadata.
5. Validate all 38 discovery hypotheses and **all 979 exact source span
   references**, including field hashes, Unicode code-point bounds, source
   membership and cross-artist counts. This is source integrity, not validation
   of a hypothesis as true. The dedicated eligible-original wrapper file is
   authenticated as an existing artifact but does not need to be decoded.
6. Authenticate the retained complete primary HTML and derived visible text;
   verify both retained primary excerpts against their recorded text spans.
   The HTML is evidence bytes: never execute or render it.
7. Select the fixed 11 inventory entries, build the unanswered questions and
   preserve the 82 complete source contexts. Authenticate the tool bundle and
   recheck old-label sentinels before writing the new packet.

JSONL is streamed by physical newline records. Do not use `str.splitlines()`:
retained source strings include Unicode line separators. Every citation uses
Unicode code points, not UTF-8 byte offsets; the raw field hash binds that
coordinate system to its original text.

The fixed trust anchors are in the source and tool manifest:

- Discovery receipt:
  `docs/research/2026-10-17-full-normalization/scale/evidence/people-identity-discovery-v2.json`,
  10,449 bytes, SHA-256
  `a10c1c45662ac4cf8531ad3ee6923a150c0bef40d91d9a90e1f6379037f7b30e`.
- Reservation index:
  `local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v1/reserved-reference-index.json`,
  955,704 bytes, SHA-256
  `f4f1d8c43d886800d4d0e8ef2d14c438a8f1ab92904ed0be53ef6436ebf32331`.
- Shared annotation evidence: SHA-256
  `968505f1b24b67e710aeb3b0aefcbbdaca9c59b2eb9106567f0c04c9717ec5c0`,
  obtained from that pinned reservation index before any raw decoding.
- The five original sentinels cover people labels, mentions and candidate pool,
  actor-fact labels, and credit-extraction labels. Exact paths, sizes and hashes
  are in `ORIGINAL_PINS`. No 168-task or 543-field validator is duplicated.

Any changed pinned source fails closed. A legitimate new capture or reservation
version needs a newly reviewed tool/version; do not update hashes merely to make
a failed run pass.

## Selection, questions and preserved contexts

Inventory lines are fixed at
`1,4,5,7,10,23,26,31,34,35,36` under the pinned discovery directory. Seven have
additional prior contextual review, and four deliberately preserve attachment or
identity ambiguity. Those selection judgments are **not answers**.

For each hypothesis, choose the first sorted evidence ID for each artist, sort
artists by UUID and pair the first artist's source with each remaining artist's
source. Counts by inventory line are:

```text
1:17  4:9  5:8  7:4  10:3  23:1  26:1  31:1  34:6  35:1  36:4
```

The resulting 55 questions are deterministic. Each pair has different artists
and different retained recording/duplicate groups. This produces 40 questions
from additionally reviewed hypotheses and 15 from ambiguous hypotheses; it does
not prescribe positive/negative class counts. Repeated questions within one
hypothesis remain correlated, so 55 questions are not 55 independent identities.
All original known-family, duplicate-group and artist coordinates are retained.
No new train/test split is asserted.

Each side includes the original reference match and local context spans, field
hashes, source ID and group/artist coordinates. `actor_kind` and `actor_identity`
remain null. An email/profile reference is a focal candidate attachment, not an
asserted human owner. Complete context can distinguish a person, project,
account, co-credit or unresolved attachment before a pair decision is made.

The frozen packet contains:

- `evidence.jsonl`: complete authenticated original records for all 82 sources,
  plus safe metadata and exact original physical-line hashes.
- `questions.jsonl`: 55 immutable questions with `answer: null` and
  `adjudication: null`. No earlier rationale, judgment, actor-kind guess, model
  prediction or accepted identity is copied into questions.
- `primary/`: complete retained primary HTML, visible text, primary source
  metadata and its existing HTTP/excerpt receipt. Use only when relevant.
- `REVIEW.md`: self-contained first-answer instructions/schema. It does not refer annotators to this operator runbook or prior discovery judgments.
- `operator-provenance.json`: selection IDs, prior statuses and exposure. This
  file reveals earlier discovery work and must not be given to a first reviewer.
- `manifest.json`: input pins, tool hashes, output hashes, counts and development
  restrictions. It excludes its own hash to avoid recursive hashing.

`validate` reauthenticates the original inputs and tool bundle, verifies every
output hash and compares the complete output records with the authenticated
source plan. Replacing context and recomputing only an output manifest cannot
pass. The validator rejects extra files in the frozen packet; answers belong in
a separate review directory. It checks provenance and unchanged questions,
**not the correctness of a future identity answer**.

## Independent labeling and adjudication route

Give each first-pass reviewer only `questions.jsonl`, `evidence.jsonl`, relevant
primary evidence and `REVIEW.md`. Do not give discovery rationales, old answers,
operator provenance or another reviewer's output. Ask reviewers to disclose
prior exposure: some source contexts have already informed existing development
work. Do not claim a review is blind to that earlier exposure.

Each reviewer writes a new file outside the frozen packet with one row per
question. Preserve their first output unchanged and hash it before comparison.
Suggested review row contract:

```json
{
  "question_id": "people-identity-development:<copied question ID>",
  "decision": "insufficient_evidence",
  "reason": "The exact source evidence does not resolve the candidate attachment.",
  "reviewer": {
    "id": "reviewer identifier",
    "kind": "human or agent",
    "answer_blind": true,
    "prior_exposure": []
  },
  "evidence": [
    {
      "evidence_id": "copied source evidence ID",
      "field_path": "/raw_record/row/taper",
      "field_sha256": "copied exact raw-field hash",
      "span": {"start": 0, "end": 1, "quote": "exact text", "unit": "unicode_code_points"}
    }
  ],
  "contradiction_basis": null
}
```

`decision` must be `same_entity`, `different_entities` or
`insufficient_evidence`. Resolved decisions need exact cited support from both
recordings, explicit local attachment and actor/project distinctions. A
`different_entities` decision must state an evidenced distinctness or
contradiction basis; unequal names, roles, hashes or contacts are insufficient.
Unknown actor kind or uncertain attachment remains explicit in the rationale.
External assertions would require a separate approved capture; this packet does
not authorize new acquisition.

A separate adjudicator then reviews both frozen first-answer files, all
agreements and disagreements, plus the original contexts. Write a new
adjudication file with question ID, decision, citations, reason, adjudicator
ID/kind, both first-answer row hashes and any unresolved disagreement. Keep an
explicit insufficient outcome when evidence cannot settle it. Do not overwrite
original development labels or the new first answers. Record complete per-class
counts only afterward. No supplied tool function certifies reviewer identity,
semantic label truth or human adjudication.

The next implementation can validate those review-file schemas/spans against
this frozen packet; that is separate from building the evidence packet. Current
unit tests and CLI intentionally stay focused on authentication, exclusion,
complete contexts, null-answer questions and immutable publication.

## Negative-class gap and separate later increment

These 55 same-hypothesis questions may yield **zero cross-recording negatives**.
The existing identity development bundle has zero actual cross-recording
negative labels. Do not broaden 55 or label unknowns as different to fill a
class. The following eligible discovery contexts support a **separate later
review increment**, with no new labels asserted here:

1. **David Keller/Grateful Dave versus Steve LaPlaca.** Inventory line 9.
   `eligible-original-sources.jsonl:811`, original annotation line 4427, source
   `3690066` (Some Ska Band), taper `[0:78]` explicitly co-credits David's alias
   and attached contact with Steve. David's name is `[0:12]`, alias `[17:30]`,
   Steve `[65:78]`; field hash
   `ae48c565b240ba84b68bd4d7b5459d679f76752647385f7d780099f16ee3559f`.
   Source `3692346` (The Toasters), eligible-original line 2100 / annotation
   7931, repeats David's alias and directly attached identical token in taper/
   transferrer `[0:60]`, field hash
   `ecfa1f812971e2637d610387d6a54f8370b6b97cbc6b2364244205bed34fce9e`.
   Candidate: David@3692346 versus Steve@3690066, after separately adjudicating
   David's cross-source linkage and source-local distinctness. A bare Steve
   occurrence in source3333910 does not establish another identity link.
2. **Timothy Brown versus Roger Venezia.** Inventory line 8. Source `3648757`
   (Rebirth Brass Band), eligible-original line 616 / annotation 3881, taper/
   transferrer `[0:29]` explicitly co-credits them: Roger `[0:13]`, Timothy
   `[16:29]`, field hash
   `b2ef21c8d574c392ee9fa6761cbb1a033173c62b1b82f149d13eb69c4f2b2bbb`.
   Description `[119:162]` gives the coordinated patcher credit and a following
   contact `[164:191]`; retain that attachment ambiguity. Source `3715315`
   (Justin Trawick), eligible-original line 832 / annotation 4480, taper
   `[0:43]` directly attaches the same token to Timothy; field hash
   `40574a1d6be1e4ef3df9c3ad22623626bb40eda4fc0d446432d21e3a5f04da62`.
   Candidate: Timothy@3715315 versus Roger@3648757, after explicitly judging
   linkage and attachment. Shared contact must not make Roger an alias.

All quoted span endpoints above are exclusive Unicode indices. These sources
are in the existing eligible development inventory, but are **not additional
questions or complete contexts in this packet**. They need their own separately
frozen evidence packet/review. Scott/Chad name repetitions and Charlie/Arielle
ordered contact lists remain insufficient cross-recording linkage evidence.

## Leakage, cutoff and release evaluation

Preserve the reservation index's complete reference/family exclusions,
including exposed `source:3717752` and the metadata-linked parents of track IDs
33689437 and 7018353. Only the safe metadata index and selected development
payloads are decoded. The broad unlabeled pack's validation/test payloads and
any untouched answers are not inputs to this tool.

Before any future scored dataset, group complete tasks—including actorless and
unresolved fields—by recording/duplicate families and independently supported
identity/alias connections. A discovery hypothesis is a conservative development
grouping coordinate, not proof of identity. An artist holdout alone cannot
exclude people appearing under other artists.

The plan's `evaluation-strategy.md:23–25` requires separate suites for unseen
identities, held-out artists, later recordings and unfamiliar formats. This
packet satisfies none of those untouched-release requirements. For temporal
suites, freeze candidates, aliases and supporting contact/profile knowledge as
of the cutoff. A recording date is not the date a profile owner became known;
the retained Sonic Archives primary observation is 2026-10-18 UTC. Test families
that inform a parser/model/prompt change must move to development and be
replaced. Keep absent evidence separate from contradictory evidence.
