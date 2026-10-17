# Retry catalog lookups when their evidence changes

A missing catalog recording is a result that can become obsolete. Preserve the
lookup and the facts it consumed so a later source arrival, removed duplicate or
changed Archive assignment can make it pending. This contract covers catalog
association. Empty identity candidate searches already use
[candidate-universe generations](candidate-universes.md).

## The clock contracts

`NormalizationSourceIdentityDependency(UpstreamIdentifier, Generation)` fences a
global provider-identifier search. Archive and Phish bindings must return it for
`Missing`, `Ambiguous` and `ProviderConflict`, as well as `Bound`. An unresolved lookup with no clock
reads as generation zero. A bound provider recording still requires an existing
identifier clock; a missing bound clock is an operational error. Its key remains the stored identifier string, so equal
identifiers from different providers can cause conservative invalidation.

`NormalizationSourceUuidDependency(SourceUuid, Generation)` fences an exact
catalog recording UUID. `NormalizationRecordingBindingSnapshot.SourceUuids`
contains the UUID clocks consumed by that lookup. Legacy bindings consume their
requested source UUID. Archive bindings also consume retained canonical/published
source UUIDs that their resolver reads, including missing or conflicting
endpoints. A retained UUID can later arrive under a different upstream identifier;
the requested identifier clock alone cannot detect that change.

`ProbeRecordingUuidAsync(sourceUuid)` returns exact source presence and its UUID
clock from one primary snapshot, without requiring a retained input. After
`RefreshRecordingAsync` returns null, use this probe: if the source is present,
restart the refresh; if absent, its original dependency can fence a negative
lookup. The probe creates neither work nor clock rows.

Clock rows survive source deletion. Source and related catalog changes advance
them transactionally. Artist presence or UUID changes, provider qualification,
and retained Archive decisions can change a binding without editing its source
row, so those changes must also invalidate the relevant lookup clocks. Ignored
operational timestamps and rating counters do not establish new association
facts. Database rollback restores both the catalog mutation and its clock changes.

A malformed retained empty UUID is a provider conflict, not a lookup for a real
identity. Return `invalid_retained_assignment` and the provider-identifier fence;
do not create a clock for the empty UUID.

## Admit the original read set

Pass every returned dependency unchanged into work admission, including the
trailing optional `sourceUuids` argument to `BeginWorkAsync` or
`BeginContributedWorkAsync`. Admission initializes absent identifier/UUID clocks
at zero under the catalog lock order, then compares the caller's original
observations. A concurrent arrival rejects admission rather than giving old
absence evidence a fresh clock. Publication checks the same original read set.
Input expectations separately pin resource and revision UUID; Work captures
resource generation at admission. An unchanged-byte fetch before admission may
therefore be accepted. This does not relax original catalog-clock comparisons.

The work ticket retains source UUID observations. Each input contributor owns
its complete source UUID key set; replacing one contribution cannot remove a
peer's keys. Membership changes require the existing configuration and
contributor compare-and-swap checks. An omitted set preserves earlier serialized
request and ticket forms. Pending queries compare published UUID watermarks with
current clocks; successful publication and its watermarks commit together.

Locks follow artist revisions, source identifiers, source UUIDs, imported venue
UUIDs, mapping registry, candidate scopes, retained resources, work owners and
domain rows. Identifier keys use PostgreSQL's `C` ordering; UUID keys use UUID
ordering. Declare complete sets before locking. Catalog bulk maintenance must
prelock all affected sets in this order or retry the whole transaction after a
deadlock. Truncation requires stopped writers and publication; retained
lookup clocks still advance.

The checked legacy binder validates the original producer artist clocks in the
same repeatable-read snapshot as current associations and source UUID clocks.
That validation establishes that retained wording is unchanged. The new UUID
clocks fence the checked association; they do not replace the producer clocks
or justify stale wording.

## Keep lookup outcomes separate from extraction

The remaining coordinator uses a separate `lookups / recording` work owner,
keyed by a durable lookup registration UUID. The stable registration key contains
lookup mode, exact provider resource or legacy source UUID, and nullable expected
artist. Input revisions and processing version belong to each immutable attempt,
so new evidence can create another attempt for the same lookup.

The proposed entrypoints are `LookupLegacyAsync(attemptUuid, sourceUuid,
expectedArtistUuid?, ct)` and `LookupRetainedAsync(attemptUuid, inputReference,
expectedArtistUuid?, ct)`. Retained provider lookup requires actual evidence.
Missing provider input belongs to the capture coordinator; do not fabricate an
input revision or admit that condition as a catalog absence. A real retained
input can produce a supported negative catalog binding.

Persist the original preparation before work admission. Reuse Work admission,
publication and pending queries; the lookup store owns immutable requests,
preparations, outcomes and handoffs rather than a second clock protocol. A
processed negative acknowledges only its lookup watermark. It must not
acknowledge a successful extraction owner's inputs or replace accepted data.

For a bound result, write the outcome and immutable downstream handoff in the
same publication callback transaction. Dispatch happens after commit. Each
handoff retains a stable downstream attempt UUID, original input/binding read
set and delivery state. A crash after lookup publication must not lose the
handoff; a crash after downstream publication replays that same downstream UUID.
Callback failure rolls back the outcome, handoff and lookup watermark together.

A legacy handoff retains the original `NormalizationRecordingPreparation`.
Dispatch calls proposal-store `GetOrCreateAttemptAsync`, `SavePreparationAsync`,
then processor `ProcessAsync`. The saved preparation makes processing skip a
second refresh. Calling the processor before installing that preparation would
advance the input generation again and could repeatedly make the lookup pending.
Archive/Phish handoffs need the direct-provider consumer: current legacy
preparation validation cannot process their retained bindings. Keep those
handoffs durable and actionable until that consumer exists. Enrollment alone
does not establish completed provider extraction.

Local and setlist coordinators need the same enrollment discipline for their
existing mapping/artist fences. These coordinators remain implementation work;
installing clocks alone does not schedule a retry.

## Compatibility and verification

Apply migration 33 before consumers. Drain older normalization
attempts before changing binaries. Recording processing uses a new version for
the changed preparation/read-set contract. A saved terminal result keeps exact
replay for its source. An unfinished older-version attempt ends explicitly with
`processing_version_changed`; use a new attempt UUID to prepare current evidence.
This must not publish or clear the older extraction owner.

Verify source arrival between observation and admission, arrival before
publication, settled negatives becoming pending, duplicate removal, retained UUID
arrival under a different identifier, contributor replacement, rollback,
cancellation and exact replay through the public binder and work services.
Keep production writes, automatic matching and public normalized reads disabled
until the full rollout requirements are met. Binary rollback stops normalization
publication and retains clocks, evidence and contribution ownership.
