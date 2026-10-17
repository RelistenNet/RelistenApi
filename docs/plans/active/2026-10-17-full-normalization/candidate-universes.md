# Fence candidate decisions before publication

A decision can become stale even when its chosen identity has not changed.
Another person can acquire the same alias, another artist can record a
composition, or a recording credit can add artist context to an existing taper.
Checking only the chosen identity's owner cannot detect these changes.

Migration 30 adds candidate-universe generations: revisions for the pools from
which a matcher selects identities. Work admission and publication check these
generations alongside input, catalog, accepted-owner and editorial revisions.
The current increment implements those checks and writer invalidation. The
transactional candidate-facts reader and automatic resolver remain required.

## Read the facts and their generation together

`NormalizationCandidateUniverseKey` identifies one of four pool types:

| Domain | Scope UUID | Facts it protects |
| --- | --- | --- |
| `people` | Empty UUID | Global people, names, identifiers, redirects and recording-credit context |
| `venues` | Empty UUID | Physical venues, dated names and locations, identifiers, redirects and assignments |
| `compositions` | Empty UUID | Global works, names, identifiers, author context and repertoire relationships |
| `repertoire` | Artist UUID | That artist's repertoire, identifiers and accepted song/work relationships |

`NormalizationCandidateUniverseDependency` pairs a key with the generation read
with its facts. Empty results still need this dependency. Global clocks always
exist; an absent repertoire clock reads as zero. Migration seeds clocks for
existing positive dictionaries, and later clocks survive deletion or withdrawal.
Generations are opaque revisions, not identity counts.

The forthcoming facts reader must select candidates, load accepted assertions,
resolve redirects and capture complete owner and universe revisions in one
primary read-only repeatable-read transaction. `NormalizationCandidateService.FindAsync`
still supplies retrieval only. Reading a fresh generation after an earlier
`FindAsync` result cannot establish that the earlier result is current.

A stable generation does not make a truncated pool exhaustive. Preserve
truncation and unknown facts, including unsupported handle classification,
geographic locality keys, identifier authority and complete composition
authorship. Return all dated venue assertions rather than selecting one to fit
the current pair matcher. People context uses accepted credits and current
source ownership, including recordings without valid playback joins.

Repertoire facts that include linked works consume both the artist repertoire
and global compositions generations. The facts reader must use immutable
`music_revisions.evidence` as its authoritative music citation list. Appending
revision history or its normalized reference index does not change an accepted
fact until a current row points to that revision. Reviewed exclusions remain
subject to the resolver's complete owner/editorial read set; this initial
candidate-facts projection does not include their ledger. Do not add exclusion,
setlist-use or file-use features without their corresponding invalidation.

## Admit the original observations

Pass the original dependencies as the optional `candidateUniverses` argument to
`BeginWorkAsync` or `BeginContributedWorkAsync`. Admission initializes absent
repertoire clocks at zero and then compares the caller's observed generation.
A mismatch raises `NormalizationConflictException` before configuration or
contributor changes commit. Reload the entire factual snapshot; fresh clocks
cannot justify older candidates or assertions.

The returned ticket's `CandidateUniverses` contains the complete owner's pool
read set. A contribution owns keys, while supplied generations remain factual
preconditions. Replacing one contribution cannot remove another contribution's
keys. Baselines, tombstones, configuration compare-and-swap, exact replay and
pending queries follow the existing [contributed-input contract](catalog-binding.md).
An omitted or empty candidate set retains existing serialized ticket and request
hash shapes. The current single-recording proposal processor rejects additional
candidate dependencies because it cannot consume those facts.

## Declare writes before taking owner locks

Typed domain services pass complete keys as `candidateWriteScopes` to
`TryPublishAsync` or `ApplyReviewAsync`. These are coordination keys; writers do
not supply replacement generations. Music services derive them from the
immutable work-owner tuple, then validate payload ownership inside the existing
callback transaction. Repertoire writers reserve their artist scope and global
compositions. Composition writers reserve compositions. Setlist/file-only
writers reserve no candidate scope.

Person identity writers reserve people and compositions because redirects can
change resolved work authors. Their publication entrypoint reserves both pools
conservatively; automatic credit-only changes advance only people. Reviewed
credit commands also update the linked person's identity revision, so they
reserve both pools. Venue writers reserve venues.

The lock order is catalog artist revisions, source identifiers, imported venues,
mapping registry, candidate scopes, retained resources, work owners, then domain
rows. Candidate keys sort by ordinal domain and UUID. Consumed scopes take shared
locks; declared writes take exclusive locks immediately, including scopes that
are both read and written. Do not take a shared lock and upgrade it in a callback.

Accepted-row triggers advance generations in the same transaction. Identity
creation, alias/ID changes, retractions, redirects, work credits and repertoire
links all invalidate their affected pools. Operational timestamp-only updates
do not. Catalog source insertion, deletion, UUID replacement or artist
reassignment, and artist presence/UUID changes, invalidate people context.
This deliberately starts with coarse invalidation. A precheck for existing
credits could race a newly committed credit; measure contention before refining
the scheme. Artist deletion prelocks the mapping registry and people scope
before foreign-key cascades.

Direct SQL maintenance must prelock all affected catalog and candidate scopes
in the same order, or stop normalization workers. Database triggers provide
transactional invalidation, not a direct-SQL admission protocol. Cross-owner
catalog bulk writes retain the requirement to prelock or retry the whole
transaction after a deadlock. `TRUNCATE` is offline maintenance and advances
every retained candidate scope.

## Publish, retry and roll back

Publication compares the ticket's original generations under the acquired
locks. A stale result never invokes the domain callback. Failure or cancellation
rolls back accepted facts, generation advances and publication watermarks
together. Exact terminal replay returns its original result without mutation.

A writer can change a pool it consumed. On success, its published watermark
acknowledges the post-write generation while the immutable audit ticket retains
the original observed generation. This keeps its own accepted change from
immediately making it pending. Other consumers still become pending.

Fresh attempts with unchanged matching facts must also settle. The
[unchanged-publication contract](unchanged-publication.md) preserves current
claim revisions and deduplicates exact citations. New support still invalidates
matching facts. This prevents repeated equal writes from keeping candidate
consumers pending after the catalog stops changing.

Apply migration 30 before code that consumes these dependencies. Before enabling
automatic matching, drain older work that lacks the required fences. For binary
rollback, stop normalization coordinators and publication; older binaries do not
check candidate generations. Retain clocks, evidence and contributor ownership.
The down migration refuses destructive rollback. No matching policy or public
read flag is enabled by this migration.

Verification covers public identity/credit/music writers, empty-result arrivals,
new aliases, source ownership changes, contributor unions, self-publication,
exact replay, rollback, cancellation and catalog cascade lock order. It verifies
freshness and recovery; matching accuracy still requires the independent
[evaluation program](evaluation-strategy.md).
