# Two separate credit-actor control contexts

This offline tool freezes **four complete development source records and six
unanswered questions**, separate from the existing 55-question packet. Each of
two controls has three questions: local co-credit distinctness, cross-source
linkage, and cross-source contrast. Thus **two questions are local and four are
cross-source**. Six questions are not six known negatives or six independent
identities. No label balance is prescribed.

The controls concern David Keller/Grateful Dave versus Steve LaPlaca, and Timothy
Brown versus Roger Venezia. Their exact source contexts support asking the
questions; they do not supply adjudicated answers. In particular, a contrast
answer must not assume that the local question is different or the linkage
question is same. Repeated contacts are evidence to assess, not authenticated
ownership. Contact following coordinated names remains unassigned until judged.

## Run the tool

Run from the repository root with the existing integrated packet tool unchanged.
Tests import the pinned helper's definitions and use synthetic contexts; they
never call `DevelopmentSources.load()` or read real/held-out source payloads.

```sh
python3 -m unittest discover \
  -s tools/RelistenNormalization/people-identity-controls \
  -p 'test_people_identity_controls.py' -v

python3 tools/RelistenNormalization/people-identity-controls/people_identity_controls.py \
  --root "$PWD" build \
  --output "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-distinctness-controls-v1"

python3 tools/RelistenNormalization/people-identity-controls/people_identity_controls.py \
  --root "$PWD" validate \
  --packet "$PWD/local-dev/normalization-data/2026-10-17-cross-artist/people-identity-distinctness-controls-v1"
```

Use a **new direct child** of the ignored cross-artist data directory. No existing
packet, label, tool or manifest is rewritten. A failed output remains preserved;
select a new output name after diagnosis. The shared publication helper creates
private directories/files (0700/0600) and publishes the manifest last. No .NET,
network, provider, model, database, subprocess or accepted-data command is used.
No captured HTML is executed or rendered.

## Reused trust boundary

Before importing any code, `load_base()` authenticates the explicit sibling paths:

- `tools/RelistenNormalization/people-identity-packet/people_identity_packet.py`:
  SHA-256 `20ac70e6b999b03049141852127334a31426ebc048071d650663aaa7d0e3a5c2`.
- Its `tool-manifest.json`:
  SHA-256 `850cbf62de2b9602b74f5c44f35978e4c93c82928ffecc384a9291b8822fac67`.

The module executes the already authenticated source bytes from that exact path,
without accepting cached bytecode or rereading the source after hashing. It is
not selected by an ambient package name. Its `DevelopmentSources.load()`
authenticates fixed reservation/source and
old-label sentinel pins, validates all eligibility metadata before raw decoding,
selects the 2,184 approved physical raw lines, and checks all **979 discovery span
references**. This tool reuses that implementation instead of copying the
reservation/source validator. It also reuses its span, hash, encoding and
manifest-last publication helpers. Original answers remain byte-only sentinels.

A changed sibling code/manifest fails before import. Do not repair a failed run
by silently relaxing these pins. A legitimate next helper version requires a
new reviewed controls contract; the existing 55-question packet remains bound
to its original version. This tool never calls the old packet's build/main or
modifies its sources, manifest, labels or questions.

The new `tool-manifest.json` pins this control script, tests and runbook. Root
should compare its hash to the reviewed handoff. The emitted packet manifest
pins both helper files, authenticated source inputs, this bundle and all output
bytes. `validate` regenerates expected contexts/questions from authenticated
sources and compares complete bytes; rewriting an answer/context and merely
rehashing the local manifest cannot pass.

## Exact retained contexts and questions

All source references below are eligible artist-development/family-development
records in the existing discovery. Paths are relative to:

`local-dev/normalization-data/2026-10-17-cross-artist/training-development-people-v2-discovery/`

**David/Steve control:**

- `source:3690066`, Some Ska Band,
  `source_sample:55614a97-d705-9b45-4a14-6d8f6a0ecde7`.
  `eligible-original-sources.jsonl:811`, original annotation line 4427.
  Taper has David `[0:12]`, explicit alias Grateful Dave `[17:30]`, a contact
  candidate `[32:59]`, and Steve `[65:78]`, within coordinated clause `[0:78]`.
  Its transferrer field credits David's expression alone; preserve that local
  difference rather than spreading recording/transfer roles across actors.
- `source:3692346`, The Toasters,
  `source_sample:b61f5925-7f47-5cda-38a9-396fc72a5377`.
  Eligible-original line 2100, annotation line 7931. Taper/transferrer `[0:60]`
  retain David's name, explicit alias and directly adjacent recurring token.
- Questions compare David@3690066 with Steve@3690066; David@3690066 with
  David@3692346; and David@3692346 with Steve@3690066. All start unanswered.

**Timothy/Roger control:**

- `source:3648757`, Rebirth Brass Band,
  `source_sample:1e2fbd90-bb2c-12cc-e152-0e7d4fdda3dc`.
  Eligible-original line 616, annotation line 3881. Taper/transferrer `[0:29]`
  coordinate Roger `[0:13]` and Timothy `[16:29]`. Description `[119:192]`
  preserves the patcher clause and following contact candidate `[164:191]`.
  The tool does **not** assign that candidate to either actor from list order.
- `source:3715315`, Justin Trawick,
  `source_sample:cbf355ae-9984-c4ef-0bbf-9b40bf0b2167`.
  Eligible-original line 832, annotation line 4480. Taper/transferrer `[0:43]`
  retain Timothy `[0:13]` and directly adjacent contact candidate `[15:42]`.
- Questions compare Timothy@3648757 with Roger@3648757; Timothy@3648757 with
  Timothy@3715315; and Timothy@3715315 with Roger@3648757. No earlier answer
  is an input or premise for the contrast answer.

Endpoints are exclusive Unicode code-point indices. `SOURCE_SPECS` pins exact
evidence IDs, original annotation lines and all cited raw-field hashes;
`CONTROL_SPECS` pins the actor and context spans. The public name/alias spans
must match literal names. Contact excerpts are derived from authenticated raw
fields at execution, without copying their values into tracked source or this
runbook. Every new control citation goes through the pinned span verifier.

The four records have four different artists and recording/duplicate groups.
Their complete original metadata, known-family groups, duplicate groups, source
fields and raw physical-line hashes are retained in `evidence.jsonl`. Nothing is
reduced to a cropped contact/name string. Question sides retain unknown/null
actor kinds and identities; the original local roles remain available in full.
Keep all three questions in one control together in any development analysis;
question-level sampling would overcount the same supporting evidence.

## Packet files and independent review

The packet contains `evidence.jsonl` (four full contexts), `questions.jsonl`
(six immutable null-answer questions), self-contained `REVIEW.md`, and
`manifest.json`. It has no earlier discovery rationale, model prediction or
preassigned identity label. Raw contact values stay in ignored evidence only.
The manifest contains pins/counts; it does not certify semantic label truth.

For first review provide only questions, complete contexts and `REVIEW.md`.
Withhold this operator runbook, earlier discovery rationale/labels and other
reviewers' answers. Record prior development exposure. Reviewers may be Codex
agents or humans; independent source-grounded development judgment is allowed.
Each writes a new answer JSONL outside the frozen packet with question ID,
three-way decision, source-grounded reason, exact citations, explicit
contradiction basis when declaring different actors, and reviewer/exposure
metadata. The reviewer guide defines the fields without referring back here.

Freeze and hash both first-answer files before comparison. A separate
adjudicator then reviews their agreements and disagreements against original
contexts, preserves explicit insufficient outcomes and records adjudicator
identity/kind plus both first-answer hashes. Never overwrite old labels or
first answers, infer an actor's global identity from these coordinates, or
force a negative because these contexts were selected as control candidates.
The validator authenticates evidence/questions, not future answers or reviewer
independence. This small tool intentionally has no new labeling framework.

All contexts have prior development exposure. New review/adjudication remains
development material, with no identity-accuracy claim, canonical-person write
or policy authority. Future release evaluation still requires independently
checked untouched identities, artists, later recordings and unfamiliar formats,
with time-cutoff knowledge and family/alias leakage controlled separately.
