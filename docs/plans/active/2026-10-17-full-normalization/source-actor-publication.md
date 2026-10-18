# Publish accepted source actor aliases

`SourceActorAliasApplicationService` applies a retained source identity proposal
to canonical person aliases. It preserves the source's exact wording and evidence,
then records which source supports each accepted alias. A source can identify an
alias without establishing a recording credit, performer attendance or person type.

This guide is for the engineer implementing the consumer and the operator
investigating a partial application. The [source lifecycle plan](source-actor-aliases.md)
tracks verification and release readiness. The [source contracts](source-actor-alias-contracts.md)
define extraction and identity authority. Migration 57 adds application storage;
independent matching evaluation and rollout remain required before enabling
automatic acceptance.

The [verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-publication-v1.json)
records the passing public lifecycle and upgrade checks, along with the capacity
work that still blocks release.

## Service calls and ownership

The application service accepts two UUIDs:

```csharp
Task<SourceActorAliasApplication> ApplyAsync(
    Guid applicationAttemptUuid,
    Guid resolutionAttemptUuid,
    CancellationToken ct = default);
```

The resolution must be a successfully published `SourceActorResolutionService`
attempt. Reuse the application UUID to resume its exact saved work. Reusing it
with another resolution conflicts. A terminal application returns its saved
result with `Replayed = true`. A new decision requires a new application; new
positive source evidence also requires a new extraction and resolution. A trusted
historical rejection can use its exact original resolution to withdraw the old
binding through a new application.

`GetApplicationAsync(applicationAttemptUuid, ct)` returns the plan, stage headers
and terminal result. Headers contain name outcomes and references to independently
stored proofs. Read one complete original proof with
`SourceActorAliasApplicationStore.GetStageAsync(stageUuid, ct)`. This avoids
loading every historical stage body just to resume an application with many people.

Planning reads each person's complete accepted map and contributor allocation
through `ReadConfiguredMapInSnapshotAsync`. Certified committed stages use a
projection from their immutable stored proof. Legacy, pending and stale recovery
retain the full reader. The planner caches at most one person's map within its
current database snapshot. It still compares every current assignment and support
with that complete map; empty clears and incomplete observations remain present.
The canonical publication callback and full audit reads retain their original
proof validation.

`NormalizedPeopleService.PublishSourceActorAliasStageAsync(applicationUuid,
stageUuid, ct)` publishes one saved stage. Callers supply IDs, never a winner,
candidate pool or accepted alias body. The application service normally manages
this call and the preceding admission.

Each stage owns `(people, person, originalPersonUuid)`. All accepted source aliases
for that original person share the stable input contributor
`("relisten", "source_actor_aliases", "person_metadata")`. The complete person
map retains every supported source scope, including empty clears. Recognized peer input
contributors retain their complete allocation. Each explicitly prepared peer uses
its current header revision as a precondition; admission advances that header
under the existing Work replacement contract. This records the new configuration
without rewriting accepted peer data or historical receipts. A merge adds a
resolved identity observation; it does not move original aliases or their support.

The store owns immutable plans, stage preparations, attached Work tickets and
receipts. The application service owns preparation and recovery. The canonical
writer owns aliases, source assignments and support transitions inside the Work
publication transaction. This consumer creates no person, merge, external ID,
recording credit or playback reference.

## Acceptance and current facts

A machine assignment requires a qualified, active source-actor policy. Before
accepting each remaining claim, read the current complete candidate pool and
repeat the comparison under the pinned policy. Credit-matching evaluation cannot
authorize this source-identity decision.

A trusted assignment instead authenticates the actual source-review operation,
revision, original selected person and retained preparation. It observes the
current identity component, even when that person was outside the machine pool.
An unchanged-field correspondence can carry the original review only when the
whole decoded field and its complete assertion/evidence mapping still match.
Changed wording remains subject to a fresh decision.

Preserve provider decode diagnostics, but distinguish failures that affect actor
evidence from unrelated show, venue and playback warnings. The shared
`RecordingActorDecodeIssues` policy keeps unknown issue kinds blocking. A missing
show context alone does not make a complete quoted alias field incomplete. Field
coverage and checked origins still determine which support can be replaced.

Both routes respect canonical alias review across the current identity component.
A source-reviewed identity does not turn its spelling into reviewed canonical
metadata. Canonical names can be accepted, exact duplicates, pending, protected
by review or conflicting. Only an accepted alias or exact duplicate receives
source support. Different source citations can retain distinct assertion UUIDs;
matching text alone does not combine their evidence.

Historical acceptance and current observations remain separate. A committed
claim keeps its original policy or human receipt. Later stages recheck its source,
review authority, target and actual name/support facts without renewing that old
machine decision. Policy deactivation stops uncommitted machine acceptance. It
can still permit finalization based entirely on names already committed under
their original receipts.

New committed stage headers also retain a certificate version and the
authenticated admission request hash. The writer records these only after the
original full validation, in the same transaction as the receipt and Work
publication. Historical readers check the actual published Work, person owner,
application version, request hash and complete retained ticket before using the
header. Current source, review, identity and support checks still apply. This
avoids decoding every old full stage to carry its already committed outcome.
If both certificate fields are absent, authenticate the full original proof.
A partial or contradictory certificate refuses recovery. Pending stages and
first publication always retain full validation; audit reads still return the
complete original stage.

## Replace and withdraw source support

For an ordinary complete field update, publish supported new names first. Retire
superseded support only after every required claim and name has an accepted or
exact-duplicate committed outcome and remains current. An ambiguous identity or
protected name keeps the earlier scope's support and produces an explicit
incomplete result. Successful independent fields can still finish.

A complete explicit zero-alias field can clear its earlier support. Missing
fields, incomplete extraction and historical payloads with no alias output cannot
clear it. Retain the original clear proof and configured bindings even after the
last alias disappears, so another source update cannot revive stale support.

A trusted rejection or retraction can withdraw its exact historical binding even
when the field has since disappeared. A trusted reassignment retires the old
binding before publishing the new selected original person. If the new name is
protected or processing stops, report the committed withdrawal and unfinished
replacement. Recovery never restores the disproved old assignment automatically.

Retiring one source does not retire another source's support. Independent native
or reviewed alias authority also remains. Raw assertions, original UUIDs and
source evidence are retained throughout.

## Failure, replay and configuration recovery

Save each immutable stage before contributed Work admission. Admission can change
configuration before the ticket attaches to the domain stage. Recovery recognizes
that exact original contributed request, complete ticket and contributor vector;
it does not select configuration by timestamp or input-union generation alone.
An unrelated ordinary Work request with identical observations cannot establish
ownership. Unsupported or ambiguous configuration refuses replacement.

A new stage reads the current accepted source/canonical projection and its input
heads in one snapshot. Recognized peer replacements carry those coherent current
observations and retain their exact owned keys. This uses the existing Work API;
it also advances their configuration headers. The stage keeps original accepted
proof separately and makes no new interpretation of peer output. Unknown
producers require their own typed proof and remain unsupported. A saved stage
never acquires newer observations on retry.

Work fences current inputs, catalog bindings, owner observations, candidate
revisions and policy facts before calling the canonical writer. Alias changes,
assignment/support transitions, the complete resulting map, the receipt and the
Work publication watermark commit together. Callback faults or cancellation roll
back that transaction and leave the saved stage retryable. A stale stage retains
its original proof and requires new preparation. A crash between stages leaves
all committed receipts available to the resumed application.

`Complete` means every required current field operation finished. `Partial`,
`Review`, `Unresolved` and `Stale` preserve the specific outcomes and committed
stage UUIDs. Inspect name dispositions and retirement counts before deciding
whether an operator action or a fresh preparation is needed. A completed Work
run alone does not prove the whole application completed.

## Bounds, migration and rollout

The source proposal supports up to 1,000 claims. Person membership uses the
explicit profile of 100 contributors and 100,000 stored bindings. Each encoded
plan, stage, receipt and application header is bounded at 32 MiB. The checkpoint
codec shares equal proof blocks without dropping evidence, correspondence maps
or observations. Overflow refuses rather than truncates; stage and conservative
outcome sizes are checked before admission, with final receipt checks inside the
publication transaction.

Local verification covers the public lifecycle and nonempty upgrade. The
[capacity evidence](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-publication-capacity-baseline-v1.json)
retains the separate large-input runs. The 1,000-claim first application commits
all 3,000 name-evidence supports, but its complete repeat is not yet verified.
The 1,000-person baseline also remains incomplete. Repeated candidate queries,
full stage storage and per-claim retained scope copies require further cost work;
the smaller passing cases do not establish catalog throughput.

Apply migrations 53–57 in order. Migration 57 adds two retained application/stage
tables and indexes; it does not reinterpret existing accepted authority. Deploy
compatible readers and resume code together. Registration starts no processing
or schedules. Automatic publication still requires evaluated policies, catalog
cost verification and an explicit orchestration/rollout decision. Rollback stops
new publication and retains accepted authority, current readers and all history;
the migration does not support deleting application history.
