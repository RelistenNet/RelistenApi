# Keep reviewed alias exclusions effective after a merge

A rejected alias must not become usable again because another original identity
already held the same alias before a merge. Migration 58 applies that rule to
current alias reads while retaining every original assertion and its support.
Focused migration, HTTP, managed-support and concurrency checks pass, as do the
affected broader regressions and synthetic query probes. The
[verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/canonical-alias-review-visibility-v1.json)
separates the final index checks from the broader run. Full-suite and full-size
publication checks remain required.

This guide is for the engineer changing alias readers and the operator applying
the migration. [Source alias contracts](source-actor-alias-contracts.md) define
support ownership; [candidate facts](candidate-facts.md) define matching snapshots.
This change adds no HTTP fields or matching authority.

## Original claims and resolved identity

Each stored person UUID is an original identity for its assertions. A merge
resolves several originals to one person without moving aliases, source
assignments or review history. Each record keeps its own owner. The current
component is the set connected by current `person_redirects` edges in either
direction. Reversing an edge restores the resulting separate components.

For each original and exact normalized `name_key`, both conditions define an
exclusion:

- A current reviewed assertion is rejected or superseded, or a retained alias
  history row records that original wording.
- That same original has no current reviewed acceptance of the same name.

A current reviewed acceptance restores only its own original's claim. Acceptance
on another original cannot override an unresolved exclusion. If any original in
the component still excludes the name, none of that component's aliases with
that name is current. Display names remain separate metadata.

For example, two merged originals both hold the alias “River.” Rejecting it on
the first original hides both alias assertions. Accepting it on the second does
not undo the first review. Explicit acceptance on the first can restore its
claim within the merged person. If the merge is reversed while the first remains
rejected, the second can use its independently accepted alias.

## Storage and read flow

Migration 58 adds `person_alias_review_exclusions`, indexes retained history by
both `(person_uuid, name_key)` and `(name_key, person_uuid)`, and replaces
`current_person_aliases`. Review writes start at an original owner; read plans
filter the exclusion union by name before joining the component. The two indexes
serve those different access paths. The migration does not
rewrite raw assertions, support, review history, original owners or UUIDs.

An alias must still be accepted and have reviewed, unmanaged or current managed
support before the exclusion rule applies. Losing the last managed support
removes visibility under the existing authority contract. Restoring support
does not override canonical review.

All behavioral readers use `current_person_aliases` directly or through
`resolved_person_aliases`: candidate retrieval, complete matching names, bounded
witnesses, support counts and public person search/fetch. Metadata review and
duplicate detection retain raw reads so hidden claims remain reviewable.

Review and merge changes commit through their existing canonical services.
The view derives visibility from those rows; it stores no second review state.
An existing read-only repeatable-read snapshot keeps its original component,
visibility and owner observations. A new snapshot sees the correction. The
[snapshot reader](candidate-facts.md#reuse-people-facts-within-one-snapshot)
cannot carry facts between those snapshots.

## Migration, freshness and rollback

Apply 58 after migrations 53–57. It locks the global Compositions then People
candidate rows before history-index or view DDL, matching canonical writers'
lock order. Taking a relation lock first could deadlock with a writer that
already holds a candidate row and needs to append history.

The changed view bypasses entity triggers, so the migration advances People
and Compositions candidate generations once each. Venue generations stay
unchanged. Admission and publication reject old matching observations through
those clocks. Historical published receipts remain historical; they do not
supply current matching authority. Reader versions and query hashes are unchanged.

On rollback, stop incompatible writers and retain the derived views, index,
assertions and history. A compatible binary must continue using current alias
projections. The migration refuses a down operation that would expose withdrawn
names. No automatic policy is activated by applying the migration.

## Verification

Verify rejection, retraction, replacement history, same-original restoration,
other-original acceptance and merge reversal through canonical services,
matching facts and HTTP person search/fetch. Check the requested retired UUID
still resolves to the same survivor while excluded aliases disappear.

The genuine nonempty upgrade must apply actual migrations in order and preserve
raw row images, support, owner and review history. It must advance exactly the
two global candidate generations and make an old Work proof stale. Concurrent
review/reversal tests must preserve the original live snapshot and reject its
later admission. A PostgreSQL lock barrier must show the actual migration and
canonical review complete without reversing candidate/history lock order.

Retain actual `EXPLAIN (ANALYZE, BUFFERS)` results for candidate selection,
matching names, witnesses, support owners and both support-count branches.
Include merged components, same-original restoration, unrelated people and
retained history. Inspect component loops and history/index access before
accepting the query cost for backfill. Synthetic plans establish those local
query costs, not catalog throughput or identity accuracy.

The final two-index migration passes 28 focused cases. The broader 336-case run
passed before adding the name-first index; that addition changes no view or
authority rule. Each of the two final probes retains 84 complete plans. The
larger fixture has 2,048 unrelated people and 2,048 distinct rejected wordings
on one component owner. For restored matching-name counts, the history-index
node uses 38 shared-buffer hits across 13 loops, compared with 332 in the
owner-first-only control. Both indexes remain available to writers.

These are warm synthetic runs on a shared host, with different generated
identities. Existing global candidate and support scans remain visible in the
plans. The probes do not cover large merged components, common-name history
across many owners, full publication throughput or matching accuracy. Verify
those costs before catalog backfill.
