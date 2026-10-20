# Recording music delivery

`RecordingMusicDeliveryService` connects a committed recording lookup to accepted
song discovery. It resumes the existing extraction, repertoire and file-content
processors. The coordinator contains no matching rules and grants no accepted
music authority. Import enrollment and a background worker remain separate work.

An extraction acknowledgement means that proposals were retained. Music delivery
finishes only after recording file contents have a terminal publication receipt.
This distinction prevents an interrupted worker from losing the remaining work
after the extraction handoff disappears from its pending queue.

## Public service contract

`ProcessAsync(handoffUuid, cancellationToken)` advances one durable phase and
returns a compact `RecordingMusicDelivery`. Call it again with the same handoff
while `Result` is null. `GetAsync` reads its state. `GetPendingAsync(limit=100,
afterHandoffUuid=null)` returns unfinished deliveries in UUID order, with a maximum
page size of 200 and a continuation cursor. These are operator/worker services;
this change adds no HTTP write endpoint or automatic worker.

Migration 61 stores one delivery per committed lookup handoff. The first caller
allocates a Work UUID and three child attempt UUIDs. Concurrent callers recover
that same header. The coordinator registers an `extractions /
recording_music_delivery / handoffUUID` owner before dispatching extraction.
Header storage and owner admission can be retried independently; extraction has
not been acknowledged at that point.

The phases are:

1. Admit the delivery owner against the handoff's original input references.
2. Dispatch extraction with its existing saved processing UUID.
3. Select a complete imported artist dictionary capture. Reuse a genuine current
   producer receipt only after `NormalizationRepertoireSourceService` validates
   its original catalog fences. Otherwise capture the artist's complete dictionary
   through `NormalizationImportedCatalogService`. An existing artist with no songs
   produces a genuine empty dictionary; recordings still proceed to title-derived
   song resolution.
4. Resolve the dictionary with the saved dictionary attempt UUID.
5. Resolve the recording's titles with the saved repertoire attempt UUID.
6. Publish file contents with the saved materialization attempt UUID.
7. Close the delivery Work watermark, then save its terminal receipt.

Each processor owns its transactions, evidence, candidate checks, reviewed-data
protection, allocations and publication. The coordinator invokes one processor
phase per call. It holds no SQL transaction during resolution or accepted writes. Dictionary
resolution precedes recording title resolution so imported song identities can
be reused. Original setlist IDs and separate repeated plays remain intact.

Dictionary selection uses a short artist-scoped advisory lock in a dedicated
transaction. Inside that lock, reread the delivery phase and the latest genuine
capture before deciding to refresh. Persist the selected fetch before releasing
the lock. This prevents concurrent recordings from refreshing identical dictionary
bytes and invalidating each other's original capture generations. The lock covers
only local snapshot/capture work; no HTTP, matching, Work publication or accepted
writer runs inside it. Catalog changes outside the coordinator remain subject to
the ordinary capture and publication fences.

## Recovery and failure behavior

Cancellation or an exception leaves the current phase and saved children available
for retry. A host restart can enumerate unfinished deliveries even when extraction
was already acknowledged. A crash after a domain publication replays that child's
terminal receipt. A crash after the delivery Work watermark but before its final
row replays the same watermark and then saves the receipt.

The complete pipeline version is frozen in the header. An incompatible deployed
version cannot replace unfinished children. Resume the original version or create
a fresh checked lookup. Changed source facts also require a fresh lookup; replay
does not attach new clocks to old evidence.

`Completed` means the file-content stage published and reported no review or
unresolved items. `Review` and `Unresolved` retain explicit attention outcomes.
`Stale` requests a new checked lookup. None withdraws previously accepted music or
changes playback ownership. A terminal receipt leaves the immediate delivery
queue; domain subscriptions and their existing pending/retry APIs retain later
source, policy, candidate and correction invalidation. The future enrollment worker
must drain those subscriptions as well as discover new recordings.

## Verification and rollout

The focused disposable-database fixture follows delivery through actual HTTP v3
song search, song shows and track contents. It checks recovery after extraction
acknowledgement, complete empty dictionaries, preserved playback URLs, stale catalog
facts and unchanged later handoff reuse. Fixture policies are restricted by the
existing policy services to explicitly enabled `relisten_test_*` databases. They
do not establish production matching accuracy or authorize production activation.

Apply migration 61 before enabling callers. Registration alone starts no processing.
Roll back by stopping delivery callers and retaining the table and Work owner kind,
so unfinished children and their evidence remain recoverable. Ordinary importer
enrollment and a captured-catalog v3 rehearsal are still required before claiming
automatic runtime delivery across imports.
