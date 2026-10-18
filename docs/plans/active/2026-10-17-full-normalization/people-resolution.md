# Compare recording credits with global people

`NormalizationPeopleResolutionProcessor` connects a published recording extraction
to global people candidates. It retains each claim, checked file scope, candidate
snapshot and comparison in a durable proposal batch. This makes the same evidence
available to evaluation and the separate accepted-credit consumer.

The processor publishes proposals only; its decisions do not authorize accepted
credits. The separate [accepted-credit consumer](people-acceptance.md)
authenticates those proposals, applies evaluated policies and publishes through
the identity and recording-credit owners. Automatic matching remains inactive.
Independently evaluated policies and the remaining verification and rollout work
in the [main plan](README.md) are still required.

## Public service contract

`ResolveRecordingAsync(attemptUuid, extractionAttemptUuid, ct)` returns a
`NormalizationPeopleResolutionAttempt`. Its request pins the original extraction
attempt and processor version. Reusing the UUID resumes that request or returns
its terminal receipt. A different extraction requires a new resolution attempt.

`NormalizationPeopleResolutionStore.GetAsync(sourceUuid, ct)` returns the last
published proposal batch. Attempt and checkpoint readers retain historical work
for audit. Reading a batch does not establish that its dependencies remain current.

The proposal owner is `(people, recording_resolution, sourceUuid)`. Accepted
recording credits belong to `(people, recording, sourceUuid)`; person identities
belong to `(people, person, personUuid)`. Publishing proposals cannot acquire the
authority of either accepted-data owner or change the people candidate universe.

## Claims, candidates and decisions

Consume every input group in the published extraction, including unresolved prose
and decoder issues. Preserve each original credit proposal and its evidence. Use
the checked binding to resolve `FileOrigin` to a track UUID. An unbound file credit
remains explicitly unresolved; a null track UUID cannot widen it to recording scope.

Extracted names do not establish person versus group, a verified handle or a
provider-qualified person identifier. Keep those facts unknown. Artist context
can help compare names, but a person can work with several artists.

Read candidate facts through `FindPeopleFactsAsync`. Preserve every candidate and
comparison, the complete retrieval result, truncation and fact-limit issues,
identity redirects and original freshness stamps. A single promising pair does
not establish a unique match. A truncated search or an empty pool cannot establish
a new identity.

Read current recording credits and reviewed exclusions with their original owner
and people-universe observations. The global people epoch also fences an initially
absent accepted-credit owner. A later review must invalidate the older preparation.
Historical rejected claims remain distinguishable from current accepted claims.

Current baseline decisions require review or remain unresolved. Later evaluated
policies must distinguish a match to an existing person, identity creation,
ambiguity, incomplete retrieval and insufficient evidence. Keep the complete
candidate decision; a similarity score alone cannot authorize publication.

## Connect shadow results to person identities

Recording-level actor keys identify quoted assertions. They do not identify
distinct people. Keep witness rankings as diagnostics, and retain direct,
cited identity hypotheses separately. A hypothesis has an original source
anchor and extractor/method identity. Its handle grants no canonical UUID or
acceptance authority. Do not merge namesakes, infer identity through a chain of
pairwise links, or use repeated copies as independent corroboration.

Count distinct supporting recordings from actual observed membership, with
bounded examples for inspection. Repeated roles and local/model interpretations
from one recording count once. Separate matching-fact support from evidence
that two assertions describe the same person. These descriptive counts do not
change the frozen identity scorer or its five features.

When accepted people exist, export exact `FindPeopleFactsAsync` results through
`ShadowPeopleCandidateSnapshots`. The shadow adapter compares each resolved
`Hit.Uuid` once, using `PeopleActorProjection.Candidate` and its complete matching
aggregates. Preserve original query hashes, limits, issues and read sets in the
pinned artifact; each result references that snapshot without repeating its full
prose. Missing queries, complete empty pools and incomplete pools stay distinct.
The export can contain separate read-only transactions and asserts no global
snapshot or current freshness.

This export is optional for new people. The accepted-credit consumer already
supports stable, checkpointed identity creation under its separately qualified
creation policy. Shadow hypotheses supply evidence for evaluation; they do not
skip creation, review or publication contracts. A model ranking and a unique
source witness cannot substitute for a distinct accepted-person candidate pool.

## Original receipts and recovery

Derive the extraction observation from its saved publication ticket:

```csharp
new NormalizationSubjectObservation(
    batch.Ticket.SubjectUuid,
    batch.Ticket.DesiredGeneration,
    batch.Ticket.EditorialRevision,
    batch.AttemptUuid);
```

This pins the extraction that supplied the claims. Reading newer owner metadata
cannot make an older batch current. Combine this original observation with all
original provider revisions, catalog and mapping dependencies, candidate epochs
and reviewed-context observations. Conflicting generations require new preparation.

Each extraction contributor owns its complete resolution input set. Save the
complete membership receipt and preparation before contributed admission. Retain
the extraction owner as a causal dependency; candidate owners are observed facts.
Do not create dependency cycles by making every compared person a causal parent.

Configuration recovery must authenticate the original contributed Work request,
its complete ticket and the complete contributor allocation before replacing
inputs. Matching an attempt UUID, generation and contributor revisions alone is
insufficient: ordinary Work can carry the same observations without owning that
allocation. Refuse multiple authenticated matches rather than selecting the most
recent attempt. A genuine admission remains usable before proposal checkpoint
storage, so interruption at that point can recover.

Proposal configuration still requires the exact current proposal-owner generation.
Reviewing an accepted recording credit changes the separate accepted-credit owner;
it does not advance this proposal owner. The [accepted-credit recovery
contract](people-acceptance.md#recover-configured-inputs-after-credit-review)
explains why that consumer permits an earlier admitted owner generation while
requiring unchanged contributor allocation.

Save the immutable proposal checkpoint, then publish its pointer and terminal
receipt through the Work service transaction. Cancellation and unexpected failure
leave recoverable attempts. Stale or unsupported work preserves the prior proposal
batch and all accepted credits. Exact replay retains the original result.

## Verification and rollout

Migration 39 adds the people-resolution attempt and checkpoint store. Apply its
preceding contract migrations before using the processor. Registration starts no
imports, schedules or public normalized reads. The
[checkpoint format contract](checkpoint-format.md) requires pausing people writers,
deploying compatible binaries to every reader and resume path, then resuming
processing. Rollback also requires a compatible reader and retains all history.

The store allows 1,000 credit claims across the complete extraction and 32 MiB
per preparation or batch checkpoint. Private `people-checkpoint-v2` storage
shares exact repeated evidence and original observations within that value;
public readers still return every claim's complete expanded snapshot. Candidate
reads use their existing explicit bounds. An unsupported name query retains null candidate facts and its reason;
it is distinct from a successful empty search. Other claims can still be compared.
Oversized complete work fails without silently dropping claims.

Verify complete extraction consumption, cross-artist candidate context, namesakes,
new rivals, redirects, reviewed exclusions, changed inputs, unbound file scopes,
empty and truncated pools, interrupted preparation, rollback and exact replay.
Verify proposal publication leaves accepted credits, candidate generations and
playback unchanged. These tests establish processing behavior; held-out real-data
evaluation must establish matching accuracy before automatic acceptance.
