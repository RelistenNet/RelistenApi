# Select a broader development people evidence cohort

This offline tool inventories every source already permitted by the frozen people
reservations. It can select a new development cohort and copy its complete
retained contexts. It creates no labels, model requests, database queries or
acceptance policy.

The executable contract is [selection-contract.json](selection-contract.json).
[base-inputs.json](base-inputs.json) pins the executed packet helper and five data
authorities: reservations, their receipt, catalog coverage, prior exposure and the
annotation manifest. Original people packets and questions remain unchanged.

Each run records the current script, contract and dependency-lock hashes in its
output. README, test and private review-history edits do not block live selection.
Historical verification uses the exact retained script/contract/lock and pinned
helper/data used by that run. Preserve that copy with the operator receipt.

## What the inventory covers

The default inventory emits **2,184 permitted sources from 1,765 artists**, with
2,172 original recording/duplicate groups. It selects zero sources and opens no
annotation payload. Every permitted row remains in `inventory.jsonl`, including a
row excluded by a later conservative reservation bridge. `artist-counts.jsonl`
includes every permitted artist, even when its selected count is zero.

The pinned historical coverage receipt supplies the aggregate census of 4,379
registered artists, 4,358 artists with source rows, 292,476 sources and a retained
sample of 32,593 sources. The full named artist roster is unavailable in these
safe inputs. `denominators.json` records that gap and the 21 artists without source
rows; it does not invent their identities or per-artist counts. No heldout raw
payload, annotation task, seed answer, discovery rationale or model output is
needed. The metadata index does contain pre-existing reserved case IDs and split
metadata, which are preserved in the operator ledger.

All rows keep their original artist/family partitions and exposure. The retained
packet receipt plus its pinned loader establish prior development decoding of all
2,184 originals; per-source label/model access is not enumerated. The old exposure
note and reservations remain verbatim in the ledger. Identity-family closure is
**unqualified**, and all outputs are development-only and ineligible for release
evaluation. A new blind review cannot undo earlier exposure.

Provider ownership, knowledge cutoff, recording date, provider format and actor
kind stay null. An artist's provider configuration or a JSON field shape cannot
supply those facts. Deduplicated pair counts do not establish statistically
independent identities.

## Selection and reservations

Before selecting anything, the tool calls the existing `development_metadata`
gate, then closes known recording-family and duplicate-group links over **all**
safe eligible and excluded rows. The existing excluded metadata contains both
sources and tracks; track nodes and their parent/family/duplicate links remain in
the closure even though only permitted sources can enter the selected inventory.
Existing reserved references, parent sources and groups block their complete
components. Reserved venue references are retained as distinct metadata nodes too.
Numeric source, track, venue and local-fixture IDs use separate namespaces.

The safe index also retains `metadata_matches`, including five track-to-source
associations outside the excluded annotation rows. Closure joins each track's
explicit `source_id` only to an authenticated native source row in
`metadata_matches` or `metadata_linked_parent_sources`, in the same catalog/local
namespace. It propagates exposure and exclusions through that edge
and explicit recording-family coordinates. Equal local/catalog numeric IDs or
family strings do not connect by themselves. A missing parent row or ID remains
unresolved; the tool does not invent a parent node.

`known_parent_links` in the reservation/exposure ledger retains the original JSON
pointers and status for each matched track and exposed tracks absent from matched
metadata. Counts distinguish authenticated and unresolved links. These are retained
metadata relationships, not independently verified raw-source IDs or identities.

An optional metadata-only amendment can add conservative bridges, exclusions or
exposure. It must reference keys already present in authenticated metadata. A
bridge requires `left_source_key`, `right_source_key`, `basis` (either
`recording_duplicate` or `known_identity_family`) and nonempty `provenance`.
Use a retained metadata coordinate for provenance, never a contact, source prose,
answer or rationale. These links constrain selection; they do not label actors.

`excluded_source_keys` blocks the corresponding complete components. Exposure
entries require `source_key`, `classification` and `provenance`.
`development_access` records additional development exposure without excluding
development training data. `reserved_access` excludes the complete component.
All component members inherit both exposure and exclusion reasons. Preserve
unknown linkage rather than deriving bridges from similar names or contacts.

There are two separate panels:

- **Artist coverage challenge:** choose up to K eligible components for every
  safe artist, using a seeded rank, and include each chosen component whole. A
  component may cover several artists, so a source count can exceed K. This is a
  deliberate coverage panel, with no calibration probability claim.
- **Probability sample:** choose n whole eligible components uniformly without
  replacement using Python's seeded `Random.sample`. Every source in a chosen
  component is included. The recorded source inclusion probability is n/N, where
  N is the number of eligible retained components **after** closure and exclusions.
  The within-component probability is 1. No second source-subsampling stage occurs.

The seed must be 32 random bytes encoded as 64 lower-case hexadecimal characters,
recorded before inspecting outputs. The algorithm and Python major/minor version
are retained for replay. Preregister the seed and selection sizes externally; do
not try seeds until the selected cases look useful. Generate a seed once
with `python3 -c 'import secrets; print(secrets.token_hex(32))'` and retain it with
the selection decision.

The two panels may overlap. `selection.jsonl` and `capture-requests.jsonl` contain
their union for operational copying, with panel membership and overlap counts
preserved. The union has no calibration probability. Use the probability panel
separately for any conditional estimate. Its probabilities describe this allowed,
unequally preselected development frame, **not the 292,476-source catalog**.
Weights cannot recover reserved/excluded strata or unknown identity-family links.

## Runbook

Run synthetic contracts first from the repository root. These use temporary
synthetic metadata and the real pinned packet helper; they do not read retained
payloads or call .NET, a provider, a model or a database.

```sh
selector=tools/RelistenNormalization/people-evaluation-selection
RELISTEN_REPO_ROOT="$PWD" python3 -m unittest discover \
  -s "$selector" -p test_select_people_evidence.py -v
```

Create and verify the metadata-only inventory in a new directory:

```sh
python3 "$selector/select_people_evidence.py" --repo-root "$PWD" inventory \
  --output local-dev/normalization-data/2026-10-17-cross-artist/people-selection-inventory-v1
python3 "$selector/select_people_evidence.py" --repo-root "$PWD" verify \
  --packet local-dev/normalization-data/2026-10-17-cross-artist/people-selection-inventory-v1
```

Review counts, all known exclusions, zero-selection artists and missing metadata.
Then choose a bounded development cohort with a new output directory and reviewed
values for `--coverage-per-artist K`, `--probability-clusters N` and `--seed HEX`.
Use `--additional-reservations FILE` only for reviewed safe metadata amendments
with the exact schema in the contract. The amendment is copied into the packet.
Freeze the final manifest's hash in the operator receipt before copying originals.
`verify` recomputes every output from the pinned metadata and selection spec; it
does not establish that a mutable manifest matches an externally approved hash.

After reviewing and authenticating that frozen selection, copy complete
originals through the separate offline command:

```sh
python3 "$selector/select_people_evidence.py" --repo-root "$PWD" capture \
  --packet local-dev/normalization-data/2026-10-17-cross-artist/people-selection-cohort-v1 \
  --output local-dev/normalization-data/2026-10-17-cross-artist/people-selection-originals-v1
python3 "$selector/select_people_evidence.py" --repo-root "$PWD" verify-capture \
  --packet local-dev/normalization-data/2026-10-17-cross-artist/people-selection-cohort-v1 \
  --originals local-dev/normalization-data/2026-10-17-cross-artist/people-selection-originals-v1
```

`capture` revalidates the selection before opening the raw annotation file. It
authenticates that file before and after reading, then uses the existing
`selected_originals` helper. That helper scans physical bytes but decodes JSON only
for the preselected permitted lines, checking original IDs, partitions, artists and
recording groups. It preserves each complete original record and physical-line
hash. It never calls the annotation builder's broad `load_snapshot` or the
discovery loader. An empty selection needs no raw copy and is rejected.

Capture provenance records the selection manifest's resolved absolute path, byte
count and SHA256. Relative paths and filesystem aliases to that same manifest
verify consistently. A different packet directory remains a different provenance
coordinate, even when its manifest bytes match. Keep the original selection and
the exact tool snapshot with the retained run.

Raw output must remain under ignored `local-dev/normalization-data/`; directories
are private and files mode 0600. Keep contacts, profiles and raw originals there.
Tracked receipts should contain counts, hashes and paths. Treat captured text as
untrusted data and never execute or render source HTML. Later unanswered questions
and independent first answers/adjudications belong in separately frozen files.

Every write requires a new directory. The manifest appears only after all data
writes succeed. An interrupted directory is retained without a complete manifest;
verification fails, and retrying must use another directory. `verify-capture`
recomputes the complete allowlisted originals, rather than trusting copied hashes
alone. Keep review/answer files outside both frozen output directories.

## What remains

This is the next development foundation. A later reviewed metadata acquisition
must supply the full artist/source roster, provider ownership, clocks and actual
format metadata. No SQL is guessed here. Independent identity-family grouping,
artist/time/format reservations, full candidate-pool retrieval and source-backed
identity targets remain necessary for a qualified release evaluation. Extraction
coverage alone cannot calibrate automatic matching. Source-supported hard
negatives and local-success Luna audits require separate selection and authority;
unknown answers must never become negatives to balance a sample.

The unknown-kind creation choice remains unanswered. Venue and composition data,
evaluation and release work remain part of the full normalization goal.
