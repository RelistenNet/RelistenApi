# Publish source actor aliases through the complete people lifecycle

A source can say that a named actor uses another name without claiming that the
actor taped, transferred or performed on this recording. Preserve that explicit
alias, resolve it to an existing identity through evaluated matching or trusted
review, and maintain only the source support that Relisten can still justify.
A conditional roster such as “some or all of the following” supports the quoted
alias relationship; it does not establish attendance.

This is required normalization work. Proposal extraction is one implemented
part. Source assignment, canonical alias support, review, candidate visibility,
withdrawal and evaluated publication must all be completed before these
assertions improve automatic matching. An offline export alone does not complete
the route.

Existing domain storage, retained processing, trusted review and API reads
already work locally. The main program now needs evaluated automatic matching
and backfill; this alias lifecycle supplies required evidence and support for
that step.

This plan orders the work for the implementing engineer and reviewer/operator.
The [binding contracts](source-actor-alias-contracts.md) specify identities,
authority, complete read sets and failure behavior. The
[main normalization plan](README.md) remains the program and rollout plan. Keep
Progress, Discoveries, Decisions and Outcomes below current as implementation
advances.

## Progress and current state

As of 2026-10-18:

- [x] Add the shared pure extractor, retained-input mapper and role-free assertion
  types. Preserve source-local names, relationships, headings and qualifiers.
- [x] Integrate optional source-actor scopes into recording/file/selected Local
  root extraction, explicit offline proposal extraction and the shared proposal
  projection. The integration build has zero errors and 25 existing warnings;
  16 new cases and 196 compatibility cases pass. Public before/after extraction
  preserves the legacy credit/title bytes and UUIDs.
- [x] Correct full-field roster parsing with the complete original field and 37
  new cases. All 259 extraction, compatibility and checkpoint checks pass. The
  repeated development run processes all 2,184 recordings from 1,765 artists with
  zero item errors and retains both known explicit roster aliases. Its default
  rows and manifest remain byte-exact against the historical tool; the public
  extraction fixture also preserves credit/title bytes and UUIDs. This verifies
  the correction and compatibility, not extraction accuracy across the catalog.
- [x] Keep music consumption independent of optional people output. All 126
  focused checks pass, including 5,001 files, 10,002 title correspondences,
  unchanged retry and historical correspondence review/replay. The full public
  producer retains its aliases; music readers size and load only their named
  projection under the unchanged 32 MiB bound. Empty field lists add no wrapper;
  explicitly empty fields retain incomplete evidence. The v2 recording envelope
  preserves credit/title proposal identities. Final binary compatibility again
  passes across all 2,184 sources and the public extraction fixture.
- [x] Authenticate alias output at the public recording checkpoint before any
  save. All 98 storage/publication and 88 extraction/recovery checks pass,
  including altered citations, scope membership, local references, coverage,
  selected Local roots and failure recovery. Alias-only replay shares the
  production builder; historical all-null absence supplies no clear. The three
  subsequent large-recording checks pass with all 5,001 files and 10,002
  mappings, unchanged retry and historical review/replay.
- [x] Verify phase 2 storage and read compatibility: migration 53, typed canonical
  alias outcomes and current readers are integrated. All 81 focused checks pass,
  including the nonempty schema-52-to-53 upgrade. Six warm synthetic SQL probes
  verify distinct-person retrieval and explicit semantic-name overflow. The
  broader run passes 313 checks with one historical-producer fixture failure;
  correcting that fixture to preserve absent alias data is followed by 27
  passing targeted checks. The broader and full suites have not been rerun
  after that correction.
- [x] Protect canonical alias writes across current person merges. All 16 alias
  publication checks and 101 metadata/candidate regressions pass, including
  rejection at either merge end, retired siblings, changed-source pending
  assertions and independent behavior after merge reversal.
- [x] Apply reviewed-name exclusions to aliases already accepted on other merged
  originals. [Migration 58](canonical-alias-review-visibility.md) preserves
  same-original explicit restoration and merge reversal without moving assertions,
  support or history. Focused migration, HTTP, managed-support and concurrency
  checks pass, along with affected broader regressions and synthetic query probes.
  The linked guide distinguishes the final index checks from the broader run.
- [x] Add original read proofs to the shared trusted-review transaction. All 85
  focused checks pass, including the nonempty 53-to-54 upgrade, stale reads,
  read-only target owners, rollback and exact replay. All 85 metadata regression
  checks also pass. Source-specific review is implemented below.
- [x] Require a separately evaluated source capability for role-free matching.
  All 106 focused policy checks and 160 people regression checks pass. The
  source branch reproduces complete comparisons and can select only existing
  identities; credit creation and scoring keep their existing contracts. The
  policy reader can share the source/candidate transaction without bypassing
  fixture restrictions. Real evaluation evidence and activation remain required.
- [x] Implement immutable source identity proposals and trusted source review.
  All 208 focused checks pass, followed by two positive-review input round-trip
  checks and a nonempty 55-to-56 upgrade. These services preserve exact original
  evidence and create no accepted canonical support or recording credits.
- [x] Authenticate configuration recovery against the actual contributed Work
  request. The unrelated-admission regression fails on the earlier reader and
  passes after correction, alongside 73 focused Work/recovery checks and 215
  source, policy and review checks. Genuine admission before checkpoint storage
  supports both exact resume and a new extraction. Review can advance editorial
  clocks without losing ownership of the complete contributor allocation.
  Original proof remains unchanged. The [verification
  receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-configuration-provenance-v1.json)
  retains the earlier failing behavior and corrected runs.
- [x] Implement local accepted support publication, complete source-scope
  replacement, withdrawal, replay, partial progress and stale recovery. The
  [publication guide](source-actor-publication.md) covers the service contracts.
  All 106 publisher, owner-read and nonempty 56-to-57 upgrade checks pass within
  the combined 198-case run. Six follow-up replay and smaller-scale cases also
  pass. The first 1,000-claim publication commits all 3,000 name-evidence
  supports. The [v2 checkpoint verification](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-checkpoint-v2.json)
  records a passing full repeated 1,000-claim application after repairing its
  checkpoint storage. The [current cohort](../../../research/2026-10-17-full-normalization/scale/evidence/source-person-cohort-v1.json)
  and [snapshot-reader](../../../research/2026-10-17-full-normalization/scale/evidence/people-facts-snapshot-reader-v1.json)
  changes have separate focused checks. Full-size distinct-person, repeated-claim
  and 1,001-source verification remain required on that final path; no source
  policy is active.
- [ ] Evaluate the complete source-actor route, verify its scale and checkpoint
  bounds, and enable it through the normal evaluated rollout process.

Phase 2 adds storage and read compatibility: migration 53, typed ordinary alias
outcomes, current visibility and `people-candidate-facts-v5`. The nonempty upgrade
rehearsal preserves existing IDs, revisions, review history and candidate clocks.
The source resolver and trusted review services are locally implemented through
migrations 55 and 56. Configuration recovery authenticates the original
contributed request and refuses ambiguous ownership. Complete accepted support
publication is locally implemented in migration 57. Larger capacity, database cost and
final regression verification remain required. No matching policy is activated.

### Verified storage and reader behavior

Migration 53 gives existing alias assertions independent baseline support and
adds source assignment, contributor support and trusted review history tables.
The nonempty upgrade preserves existing UUIDs, revisions, review history and
candidate clocks. `current_person_aliases` exposes accepted assertions with
reviewed, baseline or current managed authority; `resolved_person_aliases` uses
that same visibility rule. The typed ordinary alias writer respects reviewed
name claims even when a new assertion uses another UUID or citation. These are
storage and reader contracts, not a source-assignment publication service.

People snapshots now use `people-candidate-facts-v5`. Candidate retrieval applies
its limit after original assertions and redirect targets have been grouped into
distinct resolved people. It scans all threshold matches; bounding the returned
identities does not bound database rows visited. Matching names, bounded original
witnesses and all supporting owner observations remain separate. Repeated copies
of one name consume witness space without hiding a different person or making
semantic name membership incomplete.

The [verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-authority-v1.json)
retains failed and corrected runs. The broader regression run passes 313 of 314
checks. Its remaining historical-producer fixture is corrected to preserve the
optional alias account's original absence; all 27 targeted compatibility checks
then pass. Neither the broader suite nor the full suite has been rerun after
that correction.

Six local synthetic PostgreSQL plans execute in **60.266–68.630 ms**, using the
`person_candidate_alias` index. They visit 10,000–10,002 threshold-matching alias
rows, report no shared reads or temporary blocks in the warm plans, and use at
most 853 KiB for a sort. These measurements cover SQL execution only; full-service
latency and production cost remain unmeasured. A separate case with 10,000
different matching names correctly returns `Complete=false` at the 5,000-name
semantic bound while `NameCandidatesTruncated=false`. Its person list is
complete; its matching facts are not, so automatic acceptance remains blocked.
The probe cleanup completes without error.

Deploy migration 53 before the corresponding binary. Keep the current-alias
readers on every candidate and resolved-alias path during rollback. Retain the
authority and history tables; do not substitute raw alias-table reads or rewrite
old assertions. Source-actor acceptance remains inactive while the later phases
below are implemented and verified.

The corrected producer is `source-actor-aliases-v2/recording-actor-evidence-v1`.
An unfamiliar coordinated instrument row can retain roster context only under
the bounded continuation rule. It emits no alias from that row and makes its
field incomplete. Prose and unsupported section boundaries still end the roster.
The development run retains two assertions, one with a conditional qualifier,
and unresolved evidence on 37 sources. These are proposal counts, not people
counts. Future policy evaluation must cover the v2 attachment rule and incomplete
fields before these proposals can authorize accepted aliases.

The [verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-proposals-v2.json)
records the executed checks and compatibility evidence. The first parser run is
retained separately; its missed assertion is not counted as a successful result.

## Current code and terms

An **assertion** is an exact source statement about a local actor and explicit
alias. An **assignment** is a later accepted link from that assertion to an
existing canonical identity. **Support** links that assignment to a canonical
alias row. These are separate records with different authority and lifetimes.

The current code already supplies:

- [Assertion and coverage types](../../../../RelistenApi/Models/Normalization/SourceActorAssertions.cs),
  the [pure parser](../../../../RelistenApi/Services/Normalization/SourceActorAliasExtractor.cs)
  and [retained mapper](../../../../RelistenApi/Services/Normalization/RetainedSourceActorAliasExtractor.cs).
- [Recording proposal scopes](../../../../RelistenApi/Models/Normalization/NormalizationRecordingProcessing.cs)
  and [recording extraction](../../../../RelistenApi/Services/Normalization/NormalizationRecordingExtractor.cs),
  including [selected Local root input](../../../../RelistenApi/Services/Normalization/NormalizationRecordingExtractor.Local.cs).
- The explicit [offline extraction runner](../../../../tools/RelistenNormalization/NormalizationExtractionBatch.cs)
  and [role-free proposal projection](../../../../RelistenApi/Services/Normalization/PeopleActorProjection.cs).

`NormalizationRecordingSourceActorAliasScope` groups producer version, retained
input revision/resource, original recording origin, optional file origin and
`Extraction`. Its containing published extraction supplies publication identity.
Do not introduce a second proposal UUID. Local assertion/reference UUIDs must
always travel with their original resource, field, producer and input scope.

The [canonical people writer](../../../../RelistenApi/Services/Data/NormalizedPeopleService.cs),
[metadata review](../../../../RelistenApi/Services/Data/NormalizedPeopleService.Metadata.cs),
[people acceptance](people-acceptance.md),
[credit reconciliation](credit-reconciliation.md) and
[candidate freshness](candidate-universes.md) provide the existing ownership and
transaction conventions. Role-free assertions must not be forced through a
`CreditMention`, recording actor context or placeholder recording role.

## Ordered implementation phases

### 1. Finish and verify proposal extraction

Keep production and development on the same pure parser. The retained mapper
must validate exact original strings and Unicode spans, including HTML line
boundaries, repeated provider fields, unresolved files and selected Local root
coordinates. The explicit `extract-source-actors` mode retains proposals and
field coverage; existing `extract` and context/view contracts keep their old
semantics. The accepted source-actor context/view route is later work.

Fix roster continuity using complete original fields, not an isolated alias
line. A recognized instrument list provides parsing context; it creates no
participation or role. An unsupported boundary must leave a cited unresolved
scope rather than silently classify a later alias as absent. Keep song, venue,
date and unsupported ensemble uses out of the people assertions.

The phase is complete when source-backed full-field regressions, pure/retained
projection agreement and full-frame extraction receipts pass. Preserve all
inputs and old outputs. Report assertion yield, unresolved scope coverage and
item failures with their actual denominators. Yield is not identity accuracy.

### 2. Establish storage authority and compatible alias reads

Add the assignment, managed support, source review-state and unmanaged alias
support records in one additive migration. Backfill existing alias authority
before installing the current-alias projection so accepted aliases retain their
visibility. Keep raw assertion/history rows for metadata review and duplicate
detection.

Give the canonical alias writer a typed outcome that distinguishes accepted
rows, exact reusable duplicates, pending writes, protected natural claims and
conflicting UUIDs. Attach support only to authenticated accepted UUIDs. A null
`AutomaticMetadataState` result is not sufficient permission. Reviewed rejection
or retraction must veto the normalized-name claim across new UUIDs and sources.

Move every accepted alias lookup to `current_person_aliases`, including resolved
aliases, generic candidate retrieval, people facts and direct searches. Add the
next people facts contract: complete deterministic distinct alias names,
explicit semantic completeness and all original support owner observations,
with separately bounded evidence witnesses. Support-only retirement and
reactivation must advance both People and Compositions candidate universes.

Deploy this read compatibility with source-actor acceptance disabled. The phase
is complete when migration/backfill, every reader, natural-claim veto and
support-only visibility/invalidation tests pass. It does not yet authorize new
source assignments or publication.

### 3. Prepare source identity decisions and trusted review

A dedicated source-actor resolver authenticates the original published
extraction and exact scoped assertion, then retains the complete candidate pool,
actual comparisons and proposed action. Use the shared role-free projection.
Artist ownership is query context, not an accepted artist credit.

Automatic assignment requires independently evaluated policy applicability for
`source_actor_alias_assertion` and the exact producer/projection/reader versions.
Existing recording-credit receipts do not authorize this input. This form has
no identity creation, merge or kind capability. It may assign an existing
person/group; it does not infer person versus crew from a handle.

Source assignment review uses its own durable CAS revision as well as Work
editorial revision. Reject-before-assignment must be addressable. Exact review
carry-forward requires byte-identical original field content and a complete
coordinate/content mapping; equal names are insufficient. A changed reviewed
field holds new automatic assignments for review. Source review cannot override
canonical alias review.

The phase is complete when public services verify exact proposal authentication,
policy qualification/refusal, ambiguous and reviewed outcomes, redirect/reversal
and original input/owner freshness. Proposed decisions still write no accepted
alias support.

### 4. Publish and retire complete source support

Publish one original person owner per immutable stage. The typed writer loads
the saved stage and checks its exact extraction, decision authority, review
revision, complete membership and original observations. A caller supplies
attempt/stage IDs, never accepted assertion JSON or a chosen candidate.

Use one stable person-owned Work contribution:
`("relisten", "source_actor_aliases", "person_metadata")`. Source-specific support
remains separately keyed and evidenced. One contributor per recording would
exhaust Work membership limits; a single producer must support more than 1,000
source accounts without losing their ownership.

Its configured preparation retains the complete map of active source scopes and
zero-alias completion tombstones, including predecessor preparation/Work/header
identity. Each update changes only its prepared scopes. Load the complete map,
resources and original owner union before admission; never reconstruct the map
only from active support rows. Locate it by exact admitted headers, not union
generation, UUID ordering or creation time. Preserve other producers and their
original observations.

A complete successful scope may retire only its own superseded support.
Incomplete parsing, missing fields or pending/protected writes preserve prior
support. Last managed support disappearance hides an alias unless independent
review or unmanaged authority keeps it visible. Keep the last clear proof and
its bindings even after the source has no active alias.

Reassignment retires old support before exposing a new identity link. Redirects
retain the original owner instead of pretending the survivor is a new
assignment. A crash before commit changes nothing; a crash between person stages
reports the committed subset. Exact replay returns saved outcomes. Before each
new person stage, re-evaluate its machine claims against current pools under
the same still-active pinned policy. The original selected person only groups
likely related claims; it grants no authority. Keep complete original inputs
for deferred claims, and exhaust current evaluation before finalizing the
application. The [publication guide](source-actor-publication.md#prepare-one-persons-stage)
explains preparation order and useful partial progress after a later unrelated
failure. Never add fresh stamps to an admitted old decision.

The phase is complete when genuine service tests cover at least 1,001 distinct
source supports on one producer contribution, concurrent scope updates,
independent peer preservation, partial/complete replacement, last-support
withdrawal, reactivation, review veto, redirects and interrupted multi-person
reassignment. Complete membership and checkpoint overflow must fail explicitly;
raising caps or omitting known owners is not a substitute.

### 5. Evaluate and enable the complete route

Add an explicit source-actor-aware context/view contract with its own nullable
DTO field and producer-scoped references. Keep role-free facts out of
`CreditedActorFacts`. Production and offline candidate projections must consume
the same current alias visibility and complete matching aggregate.

Freeze fresh full-frame extraction, candidate routing and view results without
rewriting older targets or artifacts. Evaluate grammar coverage separately from
identity assignments. Use independent held-out identities/artists/formats/time,
local/rule/Luna comparisons and public C# score parity as required by the main
plan. Expanded source coverage does not inherit old model accuracy or policy
attestations.

Enable accepted publication only after phases 2–4 and the evaluated route are
complete. Start with bounded batches, inspect source support/visibility/review
outcomes, then expand through the current and future artist pipeline. This
program also has to satisfy complete membership and many-claim checkpoint
capacity. The [people checkpoint format](checkpoint-format.md) now shares repeated
original proof within each saved value under the unchanged 32 MiB limit. Source
assignment and support checkpoints still need their own complete lifecycle and
capacity verification; they do not inherit that result.

## Validation and retained evidence

From the repository root, build and run the relevant current extraction tests:

```sh
dotnet build RelistenApi.sln
./test-local.sh --no-build --filter 'FullyQualifiedName~SourceActor|FullyQualifiedName~RecordingExtraction|FullyQualifiedName~RecordingActorEvidence'
```

Use the explicit proposal-only batch mode on a retained snapshot, writing a new
output directory:

```sh
dotnet run --project tools/RelistenNormalization -- extract-source-actors \
  /path/to/retained-snapshot.jsonl /path/to/new-output-directory
```

Retain exact command/source/input/output hashes, producer versions, field-level
coverage, actual test totals and failures. Verify that tests were selected and
that every permitted source has an output or explicit item failure. Keep partial
batch outcomes explicit. These commands perform local verification; they do not
authorize a live provider call, accepted write or policy activation.

Each later phase needs public success, partial failure and recovery tests from
its actual entrypoints. Preserve legacy credit/title bytes and UUIDs, recording
credit counts, participation, public v3 response shapes and existing identity
UUIDs. Source assertions must never manufacture those facts. Run the repository
formatter check before each commit.

## Discoveries and decisions

- Full-field structure matters: a rejected earlier suffix can hide a later
  valid alias. A regression consisting only of the alias line cannot prove
  roster coverage.
- Complete matching names alone do not make candidate retrieval complete. The
  earlier raw-row lookahead reported truncation after reading repeated aliases
  for one person. People retrieval now groups original and resolved identities
  before its candidate limit. The warm synthetic plans above measure this scan;
  production cost remains unmeasured.
- Reuse canonical `person_aliases` and metadata review. A second global alias
  store would split authority and visibility.
- Keep one stable producer contribution per person. Per-source support and
  scope proofs remain distinct inside that complete ownership.
- A source-local explicit alias is useful without proving attendance, global
  identity or person/crew kind. This input needs no new creation authority.
- Proposal keys use the existing scope and containing extraction attempt.
  Adding another proposal UUID would duplicate an existing identity boundary.

## Outcomes and remaining work

The proposal path, full-field grammar correction, trusted source review and
accepted support publisher pass their focused local lifecycle checks. The
nonempty migration-57 rehearsal preserves existing canonical UUIDs, independent
native support and Work/review receipts, then publishes through the new service.
Scale verification must cover both many sources supporting one person and a
source naming many people. Migration 58 verifies component-wide visibility for
aliases accepted before a merge through public, migration and synthetic query
checks. Full-suite, catalog-scale cost, independent matching evaluation and
rollout checks remain required.

Rollback must stop new source-actor acceptance while retaining tables, original
proof and support history. Keep the current-alias predicate deployed or use a
rollback binary that understands managed support. Returning to a raw alias-table
reader would resurrect retired managed aliases.
