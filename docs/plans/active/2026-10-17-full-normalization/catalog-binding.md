# Bind normalization evidence to the current catalog

Catalog binding now resolves supported recording and file observations to existing
catalog UUIDs and supplies revisions that publication must check. This protects
normalization from a moved source, changed media URL or newly conflicting provider
identifier. It does not accept a person, venue, song or composition match.

The separate [setlist-account binder](setlist-binding.md) associates retained
Jerry, Setlist.fm, Phantasy Tour and explicitly artist-scoped Phish.net accounts.
Artist, imported-setlist and performance results are independent; a missing
recording does not discard the account's ordered passages.

Read [provider decoding](provider-decoding.md) for retained assertions and exact
citations, and the [domain contracts](../../../design/2026-10-17-normalization-contracts.md)
for accepted data and review. This guide covers the binding, catalog invalidation
and shared-owner input contracts. Local binds its root and directory together under the
[joint-input contract](local-binding.md). Use [catalog refresh](catalog-refresh.md)
for changed legacy wording. Automatic resolution and the remaining provider
bindings still need implementation.

## Resolve a recording and its files

[`NormalizationCatalogBindingService.BindRecordingAsync`](../../../../RelistenApi/Services/Data/NormalizationCatalogBindingService.cs)
accepts one decoded recording, an optional expected artist UUID and cancellation.
It reads the primary database in one read-only repeatable-read transaction. It
rejects an ambient catalog transaction and file origins from different input
revisions. A request can contain at most 10,000 files. It expects decoder output;
domain publication still validates the original citations.

The returned `NormalizationRecordingBindingSnapshot` contains the original input
revision, the recording result, one result for each file, catalog revision
lists and any supporting input or mapping-registry dependencies. A result is `Bound`, `Missing`, `Ambiguous` or `ProviderConflict`, with a
reason. Only `Bound` supplies catalog identities. Original file observations remain
available alongside unresolved results.

| Input | Recording association | File association |
| --- | --- | --- |
| `legacy_catalog / recording` | The stored source UUID. Configured artist mappings do not establish its provider. | The stored track UUID, unchanged MP3 URL and valid current ownership. |
| `archive.org / item_metadata` | Exact item identifier and an Archive link with `for_source=true`. Retained intake and collection decisions must agree with that recording and artist. | Qualified item/filename identity and the Archive writer's exact download URL. |
| `phish.in / show` | Canonical positive numeric show identifier and a Phish.in link with `for_source=true`. | Canonical numeric track identity and the exact media URL after the writer's `http:` to `https:` replacement. |

Archive and Phish recording lookups detect global ambiguity **before** applying
the expected artist. Filtering to that artist first could hide another recording
with the same qualified provider identity. Titles, slugs, dates, track positions,
computed UUIDs, URL redirects and general URI canonicalization are not substitutes
for these associations. Duplicate file identities or multiple active rows for the
same media remain ambiguous. An explicitly conflicting file recording owner
produces `ProviderConflict` before URL matching.

Local has no recording-qualified source link. Its joint binder uses the retained
root mapping and exact valid media ownership, carrying both input revisions.
The [Local contract](local-binding.md) specifies unresolved and shared-directory
behavior.

An active file binding requires the source, artist and source set to agree, a
non-orphaned track and a nonempty MP3 URL. Missing or conflicting show and source
venue context is reported separately. It does not prevent source-level credit
work. The recording reference follows the source's current show; it does not
replace the source venue with the show's venue.

A recording belongs to one performance. Its files can contain several songs or
sets within that performance. Early and late performances have separate shows and
recordings. Binding preserves those ownership rules and existing playback UUIDs;
it adds no alternate playback references.

## Carry the catalog read set into publication

A bound recording supplies an artist revision in `CatalogInputs`. Archive and
Phish also supply a revision in `SourceIdentities` for their upstream identifier.
The artist revision detects changes to known catalog rows. The global identifier
revision also detects a new rival recording arriving under another artist after
the lookup. Its key is the stored identifier string, so unrelated providers can
cause conservative invalidation when their identifiers coincide.

Local additionally returns `MappingRegistry`, a singleton revision that detects
a root acquiring another artist before that artist has any recordings. It
returns this dependency and known-artist revisions even for unresolved bindings.
An unrelated mapping change can conservatively invalidate Local work.

Pass **every returned catalog dependency** to `BeginWorkAsync` or
`BeginContributedWorkAsync`, plus `Inputs` as `expectedInputRevisions`. Admission
checks the exact consumed revision UUIDs under resource locks; a pre-bound root
cannot acquire a ticket for a newer root revision. The caller's observed catalog
generations are preconditions; reading fresh clocks cannot make older binding
facts current. A changed revision raises `NormalizationConflictException`
at work admission. After admission, `TryPublishAsync` checks the captured catalog,
input and accepted-identity revisions atomically. Stale work does not invoke the
domain write callback. A new attempt must reload the affected evidence and
bindings and use a new work UUID. An exact retry reuses its original UUID and
request; replay returns its saved result.

Legacy, Archive and Phish recording lookups supply revisions for positive
bindings. Local also supplies lookup dependencies for unresolved outcomes. A
coordinator must still register durable owners and retries for those outcomes.
Preserve unresolved results for reconciliation; absence from the pending-owner
query does not establish completion.

## Preserve every contributor's inputs

An artist repertoire can depend on hundreds of recordings. Updating one recording
must not replace the repertoire owner's inputs with that recording's inputs.
[`BeginContributedWorkAsync`](../../../../RelistenApi/Services/Data/NormalizationWorkService.InputBindings.cs)
replaces one contributor's **complete** resource, accepted-identity dependency and
catalog binding sets, then creates work over the union owned by all contributors.

Use a stable contributor key for each recording. Dependency UUIDs identify existing
normalization work owners, rather than person or song UUIDs. Read the contributor's
state with `GetInputContributionAsync`; revision zero means no contribution. Supply that
revision in `NormalizationInputContribution.ExpectedRevision`. Another work UUID
requires the current contributor revision, including when its bindings are
unchanged. A stale revision fails without replacing the union. An empty
replacement removes that contributor's bindings but retains a revisioned header,
so an older worker cannot restore them as a new contribution.

The returned ticket can contain more inputs and dependencies than the incoming
contribution. **Consume the complete ticket read set.** Reload immutable inputs by
the ticket's revision UUIDs and load the accepted and catalog facts needed by the
whole owner before extraction or matching. A result computed only from the newly
updated recording cannot replace the full repertoire.

Contribution rows retain catalog keys, not each producer's original generations.
An aggregator must also check the producer read sets saved with its extraction
batches. Capturing today's union clocks cannot make another contributor's old
catalog snapshot current. A dependency on an extraction owner alone is
insufficient: catalog changes can make that owner pending before its published
state changes.

Contribution replacement and union configuration commit together. When adopting
an existing unmanaged owner, the service retains its bindings in an explicit
`relisten / preexisting_owner_bindings` baseline contribution. Reconciliation can
replace that baseline through the same revision check. Ordinary `BeginWorkAsync`
cannot bypass contributor ownership by installing a partial union. Changing the
policy still requires the owner's expected configuration generation.

## Locking and invalidation

[`Migration 24`](../../../../RelistenApi/Migrations/24_AddNormalizationCatalogBindings.cs)
adds durable artist and source-identifier revision tables, owner read-set tables,
contributor tables and catalog triggers. Relevant inserts, updates and deletes
advance the affected clocks in the catalog transaction. Updates limited to
ignored timestamps or source rating counters do not. Rollback restores the clocks;
delete and recreate operations retain their history.

[`Migration 25`](../../../../RelistenApi/Migrations/25_AddNormalizationMappingRegistry.cs)
adds the retained mapping-registry revision and its owner and contributor
memberships. Provider and artist-mapping changes advance that clock even when
no source exists yet. The registry row cannot be deleted or truncated.

Generations are opaque counters. They are not counts of recordings, edits or
transactions. Triggers update clocks rather than writing one dirty message for
every dependent owner. `GetPendingSubjectsAsync` compares each registered owner's
published watermarks with current catalog clocks, retained input generations and
accepted-identity dependencies. This requires a worker to page pending subjects;
a trigger does not start normalization work.

Work admission and publication acquire locks in this order:

1. Shared locks on artist revision rows, ordered by UUID.
2. Shared locks on source-identifier revision rows, ordered with PostgreSQL's `C` collation.
3. A shared lock on the mapping-registry revision, when required.
4. Exclusive locks on retained input resource rows.
5. Exclusive locks on work owners and their accepted-identity dependencies.

Admission takes its configuration and dependency-graph locks before this sequence.
Each catalog trigger callback updates artist clocks before source-identifier clocks. Publication
callbacks must use the supplied transaction for **normalization domain writes
only**. They may read catalog rows but must not lock or mutate them, fetch providers
or call external services. Catalog writers can already hold catalog row locks
while waiting for a clock; a publication callback that takes those row locks would
reverse the order. Callback failure or cancellation rolls back its domain writes
and publication receipt together.

Bulk provider or mapping maintenance must prelock all affected clocks in this
order or retry its whole transaction after a PostgreSQL deadlock. A later
statement can need an artist clock after an earlier statement acquired the
registry lock; retrying only that statement does not recover the transaction.

## Rollout requirements and remaining work

Before enabling publication, install migrations 24–25 and their triggers with
catalog writers and normalization publishers stopped. Drain or explicitly
recreate older work tickets that lack catalog fences; adding the migration does
not repair those tickets. Validate trigger coverage and writer overhead against
representative imports before starting the pending-owner worker. Keep accepted
data, evidence, clocks and contribution revisions on rollback; disable the new
publication path rather than dropping them.

`TRUNCATE` and schema changes require offline maintenance with catalog writers
and normalization publishers stopped. Truncation triggers preserve and advance
retained clocks, but their relation locks are acquired before the callback. They
are not an online substitute for the row-write protocol.

Binding refreshes associations and clocks, **not retained wording**. The
[legacy refresh path](catalog-refresh.md) now retains changed fields and checks
its producer clocks before binding. Historical seed and association-only APIs
retain their original behavior.

Remaining work includes imported venue and repertoire binding,
unresolved-lookup registration, complete-ticket coordinators,
multi-candidate resolution and evaluated automatic acceptance. A recording read
set does not authorize unrelated unbound context facts; each new domain binder
must define their ownership and freshness. Model comparison, backfill and rollout
rehearsal remain requirements of the [main plan](README.md). This increment does
not enable a production resolver or establish matching accuracy.

### Imported venue and repertoire prerequisites

Imported catalog facts require retained snapshots and their original producer
revisions before they can support canonical assignments. Bind stored UUIDs.
`SetlistSongService` can update a song's upstream identifier without replacing its
UUID; `VenueService` can replace a venue UUID during an ownership/identifier edit.
Recomputing either identity from current labels would lose those contracts.

An imported venue can have no artist and no catalog users. Artist clocks alone
cannot protect that row. Add a retained per-venue UUID clock, including old/new
UUIDs on replacement and tombstones after deletion. Integrate its dependency into
work admission, publication, contributor unions and pending-owner queries before
publishing from an imported-venue snapshot. Do not assign a global venue to an
arbitrary artist to obtain a clock. Legacy coordinates have unknown precision:
the Setlist.fm importer stores city coordinates in venue columns. Preserve
`past_names` as original prose until an evidenced interpretation is available.

An artist's imported repertoire can use the existing artist clock. Retain one
bounded, complete snapshot of its song rows; reject oversized snapshots instead
of truncating them. Individual song observations keep their stored UUIDs, but
their evidence and admission revision belong to the containing repertoire input
resource. The legacy song/show membership table supplies neither repeated
occurrences nor order. Those require retained provider accounts or file evidence.

Unresolved provider lookups also need durable outcomes, separate from current
extraction batches. Reuse global source-identifier clocks for Archive/Phish
absence checks, with generation zero admitted under the same lock protocol.
Missing legacy sources need a source-UUID lookup clock because their upstream
identifier is unknown. A processed lookup can consume its watermark and sleep
until evidence changes; it must not replace a prior successful extraction or an
accepted relationship. Retry enrollment must preserve other contributors' inputs.

## Verify the contracts

Build the solution, then run focused PostgreSQL checks:

```sh
./test-local.sh --no-build --filter 'FullyQualifiedName~TestNormalizationCatalog|FullyQualifiedName~TestNormalizationInputBindings|FullyQualifiedName~TestNormalizationPhishBinding|FullyQualifiedName~TestNormalizationProviderDecoderPhish|FullyQualifiedName~TestLocalNormalizationCapture'
./test-local.sh --no-build --filter 'FullyQualifiedName~TestNormalizationLegacyRefresh|FullyQualifiedName~TestNormalizationLegacyBinding|FullyQualifiedName~TestNormalizationLocalBinding|FullyQualifiedName~TestNormalizationMappingRegistry|FullyQualifiedName~TestNormalizationWork|FullyQualifiedName~TestNormalizationInput'
```

The relevant cases cover exact current ownership, global ambiguity, retained
Archive conflicts, stale publication, rolled-back DML, cross-artist identifier
arrival, source moves, contributed-input replacement and retry. Broaden to the
normalization, collection and performance contracts before committing changes.

The [main plan](README.md#progress) records current test results. The
solution builds with existing package advisory warnings; repository formatting
passes. Fresh correctness and readability reviews cover success, stale work,
rollback and cancellation. These checks verify binding and publication behavior;
matching accuracy needs the separate evaluation strategy.
