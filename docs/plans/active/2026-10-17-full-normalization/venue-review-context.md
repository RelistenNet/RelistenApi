# Create reviewed venues and retain assignment context

Automatic venue resolution needs both the global candidate pool and the imported
row's accepted assignments and reviewed exclusions. Name retrieval can omit a
previously assigned venue, especially after a correction. Keep that authority
visible before comparing alternatives.

Operators also need a way to create a physical venue when no suitable identity
exists. Reviewed creation supplies that operation without treating an imported
label or its catalog UUID as proof of physical identity. Automatic creation remains
subject to a separately evaluated policy.

## Reviewed first-identity creation

`NormalizedVenueService.CreateReviewedVenueAsync(command, trustedOperator, ct)`
accepts a `VenueCreationDecision`. The trusted host supplies the operator identity.
The command names a stable operation UUID, registered identity owner, expected
editorial revision, venue UUID, display name, explicit `site` or `room` kind,
evidence, metadata assertions and reason.

Names must include an evidenced display name. Assertions have stable distinct
UUIDs and belong to this venue. Their cited resources must already be configured
on its `(venues, venue, venueUuid)` owner. The service snapshots command collections
before starting asynchronous work, then records identity, metadata and review
receipt atomically through the existing review service.

A room may name a `VenueCreationParent` with an existing terminal site's UUID and
expected revision. A redirected, changed or non-site parent requires another
review. The global venue write scope prevents parent changes racing creation.

Reusing the same operation and content returns its original receipt. Another
operation cannot silently reuse an existing venue UUID. Creation makes no imported
venue or show assignment. Publish those relationships separately under their own
owners. Later metadata corrections use the existing reviewed history contracts.

## Imported assignment context

`NormalizationCandidateService.FindImportedVenueResolutionContextAsync(
importedVenueUuid, query, limits?, maximumAssignments = 1000, ct)` returns
`NormalizationImportedVenueResolutionContext` from one primary read-only
repeatable-read transaction.

It includes all current assignment rows, historical reviewed rejection revisions,
exact revision citations, stored and resolved targets, ordinary global candidate
facts and facts for assignment targets omitted by name retrieval. Equivalent rows
remain separate after redirects. `IsHistorical` and the superseding operation
distinguish an old rejection from current authority after a later correction.

The complete read set includes original candidate generations, target/parent
observations and the imported-assignment owner's original stamp. A missing owner
is explicit and makes the context incomplete; this reader creates no owner or
clock. Pass these observations unchanged into admission alongside the original
imported-venue binding and its catalog clocks.

`ContextHash` identifies the query recipe. Freshness comes from `ReadSet`, not from
that hash. Reading assignment context does not refresh imported wording or prove
that the catalog venue still exists. Use the existing checked
[imported-catalog binding](imported-catalog.md) for those facts.

## Bounds, migration and verification

The assignment bound includes current rows and retained reviewed rejection
history; callers can request at most 10,000. Assignment or exact-citation overflow
fails the whole read. Ordinary candidate and fact bounds retain their explicit
blocking issues. An omitted rival or rejection cannot become a successful partial
context.

Migration 40, `AddImportedVenueResolutionIndexes`, indexes all-state assignment
lookup, reviewed rejection history and exact assignment-revision evidence. The
reviewed creation operation uses existing venue and review tables. Retain those
tables, assertions, review history and indexes on binary rollback.

Verify first creation through normalized reads, explicit room/site semantics,
parent revisions, exact replay, changed requests, evidence ownership and atomic
rollback. Verify assignment context through current and historical corrections,
redirects, omitted name candidates, bounds and concurrent review. Original
observations must stale admission or publication after a later correction.

These services activate no venue matching policy, import or public reader.
Evaluated global matching and full backfill remain requirements of the
[main plan](README.md).
