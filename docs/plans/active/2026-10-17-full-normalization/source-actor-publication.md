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

Current target proofs use the [bounded batch reader](source-target-batches.md).
Preparation shares component acquisition within its live snapshot while retaining
each original target. Publication independently rereads every target under Work's
owner protections. Historical proofs never substitute for those current checks.

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

## Prepare one person's stage

Authenticate the complete original source and current trusted reviews once in a
read-only repeatable-read transaction. Process historical withdrawals and trusted
assignments before machine additions.

For machine additions, find the first current winner in source order. Use its
original proposed person only to group likely related claims, then refresh those
claims against current pools. Add only claims whose current winner is the selected
person. A missing original hint stays a singleton. Retain all queried pools and
owners, including negative results and other winners examined in that group.

Keep every original input, field and assertion in the saved stage. Deferred
claims have no current pool or selected person; they are not final unsupported
results. Only a pass that exhausts all remaining eligible machine claims may
compute readiness, process every previous person's map and classify completion.
A later stage starts a new snapshot. A saved stage always resumes its original
proof before any fresh preparation.

Historical withdrawal discovery can be deferred only when a source-wide query
finds no review rows and an actual source-owner observation fences that absence.
Do not filter the query to current fields or negative reviews. Any review,
including accepted reassignment on removed text, keeps the full historical scan;
a missing owner does too. A concurrent first review invalidates the prepared
stage through its existing owner fence.

An independent valid person may now commit before a later unrelated candidate,
target or old-map failure. This preserves useful partial progress. Complete
source/review authentication remains upfront, and a terminal Complete result
still requires the exhausted pass and all previous maps. The
[verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-person-cohort-v1.json)
records the public tests and remaining large-input measurements.

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

Reading a saved stage reuses the complete typed serialization already produced
for its byte-limit check. The reader compares that serialization's value against
an independently reconstructed stage, preserving hidden and unused proof
rejection. It does not compare raw transport JSON: supported optional nulls and
JSONB whitespace can differ without changing the typed evidence. The local JSON
document is disposed before return. Nothing is cached between reads.

The [stage-read verification](../../../research/2026-10-17-full-normalization/scale/evidence/source-stage-typed-json-v1.json)
records 110 distinct passing application and small-lifecycle cases. Three new
compatibility cases also pass against the preceding codec. Complete decoded and
re-encoded outputs match on three retained synthetic stages in both run orders.
Managed allocations per read decrease by approximately 7–14 MB; elapsed timings
are mixed. These measurements establish neither peak memory nor whole-application
throughput. Full-size lifecycles and final-code broad regression remain required.

Local verification covers the public lifecycle and nonempty upgrade. The
multi-source test must also prove that an incomplete recording keeps its own
bindings, last complete observation and original accepted evidence/support rows.
Alias visibility through another recording cannot establish this preservation.
Compare all assignment owner UUIDs against independently read source-resolution
and extraction owners. The [three-source regression](../../../research/2026-10-17-full-normalization/scale/evidence/source-incomplete-preservation-v1.json)
passes; run the same lifecycle with 1,001 sources for full-size verification,
which remains required.

The [current 1,000-claim repeat](../../../research/2026-10-17-full-normalization/scale/evidence/source-current-thousand-repeat-v1.json)
passes both applications, retains all aliases and reads every saved stage back
without losing proof. It took about 16 minutes on the shared host; this does not
establish isolated throughput. The [capacity baseline](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-publication-capacity-baseline-v1.json)
and [earlier checkpoint verification](../../../research/2026-10-17-full-normalization/scale/evidence/source-actor-checkpoint-v2.json)
retain the original runs and the earlier snapshot of 3,000 name-evidence supports.
The preceding 1,000-person baseline failed at its one-hour cancellation limit.
The [text-reader follow-up](recording-text-scan.md) records the partial progress,
retained proofs and a verified coordinate-counting correction. Full-size
distinct-person and multi-source tests, final broad regression and representative
catalog cost checks remain required. The smaller passing cases and pure
extraction timings do not establish catalog throughput.

Apply migrations 53–58 in order. Migration 57 adds two retained application/stage
tables and indexes; it does not reinterpret existing accepted authority. Migration
58 adds component-wide canonical alias review visibility and its history indexes
while preserving original assertions, support and review history. Deploy
compatible readers and resume code together. Registration starts no processing
or schedules. Automatic publication still requires evaluated policies, catalog
cost verification and an explicit orchestration/rollout decision. Rollback stops
new publication and retains accepted authority, current readers and all history;
the migration does not support deleting application history.
