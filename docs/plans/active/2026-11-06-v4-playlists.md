# Deliver playlists using v4 catalog identities

Status: private-playlist API implemented and locally verified 2026-11-17;
mobile integration and release qualification pending. Private-playlist contracts
were frozen 2026-11-16. This plan owns
the playlist contract and delivery work. It supersedes the playlist portions of
the July 18 server architecture, server implementation, mobile architecture,
mobile implementation, and mobile-first delivery documents. Those documents
remain background for other features; their raw UUID examples, availability
clock, and Queue V2 requirements are not playlist implementation instructions.

## Purpose / Big Picture

A listener can create a private playlist, add the same playable track more than
once, arrange tracks into ordered segments, edit offline, and use the same
playlist on another signed-in device. Later deliveries add public links,
following and cloning, then collaboration. Private playlists must work before
the later features are implemented.

Playlists are a new typed-ID contract for v4 clients. Alec confirmed on November
6 that no older-client support or backwards compatibility is required. They
remain owned by the User Service at `accounts.relisten.net/v1`; `/v1` versions
account resources independently of the catalog's `/api/v4`. No playlist endpoint,
UUID wire alias, dual reader, or migration for an older playlist client is needed.
Existing unrelated catalog and account endpoints retain their own contracts.

## Progress

- [x] (2026-11-06) Compare the July plans with current account IDs, catalog
  resources, queue persistence, and the November 5 mobile simplification decisions.
- [x] (2026-11-06) Confirm v4-only clients and User Service route ownership with Alec.
- [x] (2026-11-16) Resolve the product choices below.
- [x] (2026-11-16) Freeze the private-playlist contracts:
  [Private playlist contracts](2026-11-16-v4-playlist-contracts.md). Where the two
  disagree, the contracts document wins.
- [ ] Implement and locally verify private playlists through the real mobile/API path.
  - [x] (2026-11-17) Implement and locally verify the private User Service API,
    persistence, library summaries, history generation and recovery command.
  - [ ] Integrate and qualify the mobile consumer against this API.
- [ ] Qualify and release private playlists; record the build and environment.
- [ ] Implement publication, following and cloning with their public web consumer.
- [ ] Implement collaboration after private editing has been exercised.

## Context and Orientation

`RelistenUserService/` already owns authentication and favorite/library sync.
`Relisten.Accounts.Contracts/Identifiers/AccountIds.cs` stores application IDs as
complete canonical typed strings backed by UUIDv7. `CommandId` already exists.
`Relisten.Identifiers/` owns typed catalog IDs. `track_` identifies a playable
file; a canonical `song_` identifies music, and an `imported_song_` identifies
upstream setlist evidence. Neither song identity substitutes for a track.

`RelistenApi/Models/Api/V4/` and `RelistenApi/Services/Catalog/` define current
catalog representations and reads. Normalization enriches a recording without
replacing its imported title, playable file identity, or original ordering.
Imported venues, artist venues and canonical venues are distinct relationships.

The sibling `relisten-mobile` checkout already persists and restores distinct
queue occurrences. Its November 5 relationship simplification keeps ordinary Realm
catalog records, live reference wrappers, and existing repositories. Playlist
segments and entries are new authored data; copied catalog presentation
models and duplicate HTTP membership lists are not needed.

This plan describes intended behavior, not deployed capability. History, Timescale,
Sonos, a generic sync engine, and a player replacement are not technical
prerequisites. The existing delivery order puts history first; changing that
release priority requires Alec's decision, not an invented code dependency.

## Interfaces and Dependencies

### Typed identities and ownership

Use `playlist_id`, `segment_id`, `entry_id`, `operation_id`, `command_id`,
and `track_id` in playlist payloads and routes. Before implementation, add the
playlist domain types alongside existing account identifiers, and freeze their
prefixes in one place. Frozen prefixes (2026-11-16) are `playlist`,
`playlist_segment`, `playlist_entry`, and `playlist_operation`; reuse `command`
and `user`. Whole-word prefixes match every existing account and catalog ID.
"Entry" names a playlist position; "occurrence" stays reserved for the mobile
queue's own per-enqueue identity.
Client-created domain IDs use UUIDv7 through those types. Validate canonical
strings and prefixes; never infer that every catalog ID is a UUIDv7.

New playlist domain keys and references to users are stored as complete typed
strings, matching AccountsDbContext conventions. A typed `TrackId` losslessly
encodes the existing catalog UUID. Decode it only in the catalog adapter; database
joins may use the existing internal UUID and integer keys. No numeric catalog
key or raw catalog UUID appears in the playlist wire contract. Do not change a
prefix to turn an imported entity into a canonical entity.

An entry is one authored position, including duplicates. A segment is an
ordered group of entries. A segment is an authored block that may contain tracks from different
recordings; selecting a source run is one way to create it. Keep an empty playlist valid. Authored empty
segments are not valid; deleting or moving the last
entry tombstones its segment in the same operation. Temporary catalog
unavailability does not tombstone an authored entry or segment.

### Private API

Keep the account contract version independent of the catalog route version. Use
one playlist `contract_version` starting at 1; do not reuse a favorites version.
The private delivery exposes:

- `GET /v1/playlists?view=active|archived`: summaries for the current account;
  archived listings are owner-only.
- `POST /v1/playlists`: `{contract_version, command_id, playlist_id, metadata,
  initial_segments}`. Metadata contains name and optional description; initial
  segments contain IDs, kind and ordered `{entry_id, track_id}` items.
- `GET /v1/playlists/{playlist_id}/snapshot`: complete authorized authored
  structure, permissions and content revision. It carries no catalog data.
- `GET /v1/playlists/{playlist_id}/changes?after_revision=...`: complete ordered
  revision envelopes, bounded pages and a continuation revision.
- `POST /v1/playlists/{playlist_id}/operations:batch`: one contract version and
  ordered `{operation_id, base_revision, operation_type, payload}` items.
- `PUT /v1/playlists/{playlist_id}/archive-state`: acknowledged
  `{contract_version, command_id, archived}`.

Content operations are metadata update, segment insert/move/delete, and entry
insert/move/delete. Define their typed payloads and examples in
`Relisten.Accounts.Contracts/Playlists/` before writing persistence. Do not expose
an untyped arbitrary JSON operation bag. Metadata fields use absent = unchanged,
description null = clear, and non-null name. IDs survive moves. Catalog identity
cannot change through a move; replacing a track means deleting and inserting.

Inserts and moves name a live parent and optional before/after anchors. Temporarily
remove the moving object when calculating its new position. Both surviving anchors
must form a valid interval in that parent; one surviving anchor is sufficient;
no surviving anchors means append. Wrong-parent anchors count as absent and an
inverted pair keeps only `after`, so placement never fails (Alec, 2026-11-16;
replaces `anchor_conflict`). A deleted target returns accepted no-op `target_deleted`;
a missing/deleted destination returns terminal `parent_missing`. Never resurrect
a tombstoned ID through an insert. Freeze same-ID insertion conflict/replay cases.

The server assigns fractional ranks under the playlist lock. Ranks are opaque
ordering strings with the same restricted alphabet and bytewise comparison in
PostgreSQL and mobile. Select an existing implementation or a small domain helper
using adversarial insert fixtures; record its maximum length and rebalance trigger
before shipping. Rebalance is a system revision carrying every changed rank; it
changes no identity and a delta page never splits that revision.

### Transactions, batches and receipts

Use one documented lock order for owner quota, playlist, affected account-library
states, and command receipts. Cardinality changes serialize on the owner boundary
before the playlist; affected account states lock in stable ID order. Apply this
same order to creation, archive, cloning, and later deletion/member operations.
Account deletion is not implemented yet (`User.LifecycleGeneration` exists but is
unread), and private playlists do not wait for it. When deletion lands, it adds its
fence to this lock order and a purge handler for playlist rows; keep playlist
writes behind one entrypoint so that fence has a single place to attach.

Authorize the entire request before mutation. Preflight malformed envelopes,
unsupported versions, reused IDs with different payloads, and aggregate quota
failure before applying new operations. Exact receipt replays consume no quota;
preflight the proposed new effects in submitted order without persisting them.
Process a valid batch in submitted order
under one playlist lock. Each state change advances content revision once; no-ops
do not. Store per-operation semantic rejections and continue independent siblings.
Commit materialized state, revision envelopes, library changes and receipts in one
transaction. An infrastructure failure rolls back the entire batch. Return one
result per operation in request order, including exact replayed results.

Store a server-computed semantic hash with each command/operation receipt. The
receipt is scoped to its actor and aggregate; a different actor cannot replay
another actor's result. Hash material includes contract version, command/operation
type, canonical playlist ID and typed payload; exclude its receipt ID and the
diagnostic base revision. Clients persist payloads and never compute hashes. Freeze
one deterministic typed serialization and test field absence, nulls, text and
array order; do not require a general JSON canonicalization framework by default.
Reject ambiguous/unknown fields at deserialization. Exact same-ID/payload retries
return the stored outcome; changed reuse conflicts, and a fresh insert reusing a
tombstoned domain ID conflicts. Authorization still applies to receipt reads.

Retain compact receipt identity, hash and outcome for the aggregate's lifetime
initially. Delta compaction must never delete retry knowledge. Full revision
history and restore checkpoints are separate storage/product choices; no restore
UI or permanent full event history is required for the first delivery. Do not add
compaction until needed; before enabling it, define the retained revision boundary
and prove `409 snapshot_required` falls back without losing pending work.

### Pull state and optimistic edits

Persist the last completely applied contiguous revision separately from individual
operation acknowledgements and any observed server head. Suppose a device has
revision 41, another device commits 42, and its own push returns receipt 43. It
still pulls after 41. Receipt 43 acknowledges that operation; it cannot skip 42.

Apply each complete delta page's structure, catalog patches and cursor in one
Realm transaction. Snapshot replacement similarly establishes a complete revision,
then reapplies unresolved local intent in order. A push result must not apply a
structural change twice when the matching delta arrives. Keep acknowledged intent
visible until canonical pull state incorporates its revision. Use existing account
generation and request ownership to reject stale completions after scope change
or a newer snapshot; switching away cannot acknowledge another scope's outbox.

The existing detailed plan permits offline creation, while the mobile architecture
calls it online-only. Accepted resolution: persist creation as a separate immutable
command and permit a provisional local playlist. Initial command payload/IDs remain
fixed for retry; later local edits are dependent operations. Send those only after
create acknowledgement at revision 1. A lost response retries creation exactly.
A terminal create failure leaves a visible local draft with an actionable error;
pause children and offer explicit discard or correction under a new command,
not silent data loss or an endless retry. Alec accepted offline creation.
Archive, publication, membership and invitation commands remain online acknowledged.

### Catalog hydration and unavailable tracks

The User Service and catalog stay conceptually separate so their databases can be
split later (Alec, 2026-11-16). Playlist code never joins, references or reads
catalog schemas, and the User Service gains no project reference to catalog
readers. Playlist snapshots and deltas carry authored structure with typed
`track_id`s only; they contain no catalog sidecar and make no consistency claim
about catalog state.

Mobile hydrates playlist tracks through the existing anonymous catalog resolver,
`POST /api/v4/catalog/resolve`, which already accepts `track` references and
returns per-reference `available`/`unavailable` status plus v4 entities. It is
the path favorites already use (`relisten/library/favorite_metadata_hydrator.ts`).
The resolver accepts at most 500 references per request, so a 5,000-entry
playlist (de-duplicated by track) needs up to ten requests. Measure that path
before adding a larger batch or a playlist-specific catalog route; any such route
stays catalog-owned and takes only track IDs.

Resolved entities update ordinary Realm objects and relationships. They do not own
the full children of a show/source or soft-prune artist browse membership. Never
clear richer cached children because a resolve response omits them. Playlist
ordering lives only in authored segments/entries, not a second catalog cache.
Hydration is best-effort and separate from sync: a failed or partial resolve never
blocks applying a playlist revision and never deletes authored entries.

Keep authored track references when the catalog cannot currently resolve them.
Member views show resolver-reported unavailable entries as unavailable; public
projections (delivery 2) omit those entries and empty rendered segments. Do not
infer licensing removal from missing metadata or mobile browse soft-pruning. The
current orphan filter is not a comprehensive licensing/stream-authorization system.
Label precise reasons only when an authoritative catalog state supports them.

The July global `availability_revision` does not exist. A playlist content revision
never validates catalog metadata or availability; those come only from the catalog
resolver. Mobile resolves only missing tracks and re-resolves on playback
failure (decision 3).
Server-side public reads and clone (delivery 2) need current availability without
reading catalog schemas; choose between a server-to-catalog resolve call and
client/web-side filtering when that delivery is designed.

Playlist rows store typed track references with no foreign key into catalog
schemas, so new authored references survive later catalog absence. Neither missing
data nor a failed hydration deletes user intent.

### Playback and downloads

Extend `relisten/player/relisten_player_queue.tsx` and existing checkpoint storage.
Every enqueue creates a fresh queue occurrence identity, even when the same playlist
entry is queued twice. Store playlist/segment/entry IDs only as context.
Restore and reorder preserve queue occurrence identity. A queued snapshot is independent
of later playlist edits; playlist sync does not rewrite an active queue.

Add a distinct queue segment instance per enqueue group. Segment shuffle shuffles
those instances while retaining their internal order; queueing a playlist twice
does not merge its repeated segment IDs. Preserve existing native/Cast ownership,
download lookup and playback-instance semantics. No new server queue or Sonos work
is needed to play a private playlist. Keep internal catalog storage adaptation at
the existing boundary; it is not support for an older playlist client.

Playback failure is the removal signal (decision 3). Today
`RelistenPlayer.onNativePlayerError` (`relisten/player/relisten_player.tsx`) logs,
records a Statsig event and shows a 10-second error toast; it neither re-resolves
the track nor advances the queue. Private playlists add: on a playback error for a
remote (not downloaded) track, re-resolve that one track; if the catalog reports it
`unavailable`, mark the entry unavailable and skip to the next queue occurrence.
Throttle so an offline device or repeated failure does not loop through the queue.

Retain downloaded files and existing device-global offline metadata. Account switch
clears scoped playlist/queue access without deleting media. Before private bulk
download, disclose that track titles appear in the shared Offline Library, while
playlist name/order/segments remain account-scoped. Strictly blocking future media
URLs after licensing removal requires enforcement outside playlist rendering; it
must not be claimed solely from snapshot filtering.

## Plan of Work

### 1. Private playlists

Resolve the decisions below, freeze typed examples and limits, then add only the
private domain types, migrations and `RelistenUserService/Playlists/` services.
Controllers translate HTTP; the domain service owns authorization, operation
semantics and transactions; mobile hydrates tracks from the catalog resolver.
Extend the library snapshot/change protocol for playlist heads and the mobile
coordinator that consumes it; favorites and playlist revisions remain distinct.
The current `FavoriteLibrarySnapshot`/`FavoriteLibraryChange` contracts are
favorite-only. Extend them additively at contract version 1: a `playlists`
summary array in the snapshot and a `playlist_summary` change type, with favorite
shapes unchanged (Alec, 2026-11-16). Favorites are dev-alpha only, so no
compatibility layer is needed for alpha builds that reject unknown change types.
The mobile change to `relisten/library/favorite_sync_service.ts` ships with it.

Use domain phases at orchestration entrypoints: authorize/lock, claim receipts,
apply intent, publish revisions, commit. Explain retry ownership and locks beside
the implementing code. Introduce no interface merely to make a test mockable.

Then add scoped Realm playlist/segment/entry/outbox state, a small sync
coordinator, editor and player integration. Reuse existing catalog repositories
and live wrappers. Deliver the real API-to-mobile path before adding publication
or dormant collaboration schema.

### 2. Publish, follow and clone

Add stable public codes and publication, anonymous snapshot, follow/unfollow and
idempotent clone routes. A private playlist is the default. Public reads return
the same 404 for missing, private and archived playlists. A public URL is not a
secret. Follow state binds to playlist ID and survives unpublish/republish; no
per-edit follower fan-out. Clone copies the current available order into a private
aggregate with fresh segment/entry IDs, never permissions or history.

Build the minimal public web page using the same hydrated projection. Keep public
responses free of private structure, receipts and actor data. Archive hides all
normal reads but preserves publication/follows; unarchive restores them. Account
deletion purges owned playlists and leaves an unavailable follow record without a
foreign key to the deleted aggregate. Add no credentialed web editing facade
without a consuming screen and explicit browser policy.

### 3. Collaboration

Add viewer/editor/manager roles and separate one-time collaborator invitations.
Owner alone archives; editors change content; managers also publish and manage
members. Invitations contain a random fragment secret, stored only as a hash;
anonymous exchange grants a bounded preview, explicit authenticated acceptance
creates membership. Preserve exact acceptance retries without logging secrets.

Enforce access at receipt time, serialize role/revocation changes with edits, and
emit affected users' library changes. Retain former-member evidence so exact
`collaborator_access_revoked` is returned only to a known revoked collaborator;
unrelated callers receive 404. Record a membership generation and bind persisted
pending operations and their batch to the grant under which they were authored.
Compare that grant with the current membership on the server, so removal then
re-invitation cannot silently upload intent from the prior grant. Freeze how a
client acknowledges/discards that old branch before resuming under the new grant.
Archive is temporary unavailability, not revocation: preserve pending edits and
resume after unarchive. Demotion refreshes permissions without pretending all
access was revoked. Only explicit acknowledgement of permanent revocation discards
unsynced work. Deleting a collaborator does not delete another owner's playlist.

## Product decisions and remaining freshness question

1. **Offline creation: accepted.** Persist a provisional-create command and permit
   local creation/editing while disconnected. Dependent operations wait for its
   receipt. Archive and access changes remain online acknowledged commands.
2. **Availability and insertion: accepted.** Accept syntactically valid track
   references and retain unresolved entries. Playlist writes remain independent
   of catalog outages; no restrictive catalog FK or importer-retention work is
   required by this playlist contract. Hydration determines current display data.
3. **Removal freshness: accepted (2026-11-16) — same rule as favorites.** Mobile
   resolves only tracks missing from Realm; it never proactively re-checks cached
   tracks, and there is no freshness window or availability counter. Removals are
   very rare. When playback of a playlist track fails, mobile re-resolves that
   track; an `unavailable` result marks the entry unavailable in the
   rendered playlist (never deleting it), and the player skips to the next queue occurrence
   rather than stopping the queue. Normal catalog browsing also refreshes cached
   tracks. A refresh window can be added later without server or storage changes.
   Strict prevention of new playback still requires enforcement at media access;
   downloaded files stay playable.
4. **Quotas: accepted.** Keep the July defaults: 250 playlists, 5,000 entries per
   playlist, 25,000 owned entries, and 500 operations/2 MiB per batch. Count
   non-user-deleted authored entries, including unresolved ones and archived
   playlists. Catalog recovery and unarchive therefore cannot cross the limit.
   Begin with serialized indexed counts, without a cross-domain usage ledger.
5. **Segment edits: accepted.** A segment is an authored ordered block. Selecting
   adjacent tracks from one recording initially creates that block; moving a track
   from another recording into it is allowed. Segment shuffle preserves the order
   inside each block. Freeze the payload vocabulary before implementation.

Alec accepted decisions 1, 2, 4 and 5 on 2026-11-06 and decision 3 on 2026-11-16.
Collaboration-specific invitation expiry,
revocation retention and role examples can be frozen with that later delivery.

## Validation and Acceptance

Keep a small behavioral suite at real boundaries. Parameterize cases when they
protect the same invariant; do not prescribe a test count or private call order.

- PostgreSQL: exact retry after response loss has one effect; changed reuse
  conflicts; mixed semantic failure preserves successful siblings; infrastructure
  failure leaves no partial commit. Race concurrent quota changes.
- Ordering: duplicates, cross-segment move, last-item deletion, surviving/missing
  anchors and rebalance preserve authored identity and deterministic order.
- HTTP/catalog boundary: deserialize real typed payloads; reject wrong prefixes;
  prove playlist code touches no catalog schema; hydrate a large playlist through
  chunked resolver calls without full-show expansion or clearing richer cached
  children, and keep authored entries when a resolve chunk fails.
- Realm sync: a restore that reuses revision numbers forces snapshot replacement;
  a quota-failed insert into a single-entry segment applies when resent with the
  deletion of that entry; receipt 43 before
  delta 42 still pulls after 41; a lost-create
  response retries before children; snapshot fallback retains pending edits;
  late completions after scope change cannot apply or acknowledge them.
- Player: enqueue the same playlist twice, remove one duplicate, shuffle segments,
  restart and restore the exact selected queue occurrence/checkpoint. Later playlist
  edits leave that queue unchanged. Downloaded playback still works offline.

Publication adds privacy projection, follow restoration and clone replay proofs.
Collaboration adds role/revocation/re-invitation transaction proofs and a manual
two-device offline edit/reconnect exercise. Test success, semantic partial failure,
and recovery from the entrypoints, including the resulting caller-visible state.

Measure a 5,000-entry response with repeated tracks on representative iOS
and Android devices: compressed bytes, query/parse/apply time, peak memory and
first useful render. Compare a normal playlist too. Record hardware/build and
name a budget before claiming a result requires pagination or another cache.

In the API checkout use `dotnet build RelistenApi.sln` and
`dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj`; read that
suite's PostgreSQL setup before running integration cases. Start the shared local
services with `./start-local-databases.sh` and verify the integration cases ran;
the suite can skip them when PostgreSQL is unavailable. `./test-local.sh`
runs the catalog suite, not User Service tests. Use it for changed catalog readers.
In mobile run affected Vitest tests, `yarn ts:check` and `yarn lint`, then the
relevant simulator/device checklist. No production catalogs, schedules or mutations
are needed for verification. Before commits/pushes run `dotnet csharpier check .`.

After implementation and focused checks, run the code-simplifier and deslop
passes, then use a fresh reviewer to trace success, partial failure and recovery.
Review readability separately from correctness; formatters do not establish it.

## Idempotence and Recovery

Migrations are additive and add only delivered tables. Rolling back an application
image preserves playlist rows, pending operations and receipts. Do not repair an
operation protocol by replacing whole playlists or discarding outboxes. Stop the
affected writer if necessary, diagnose retained intent/outcomes, and deploy a
compatible fix. A failed pull leaves the prior complete state and cursor intact.
Transient transport/authentication failures retain pending work; domain rejection
requires an actionable correction/discard state rather than silent loss.

No Temporal workflow sits on a playlist request path. As each domain ships, extend
the account deletion implementation and restored-backup handling to cover its new
rows. Record local implementation, local proof, published review and deployed
qualification separately. No deployment or publication is authorized by this plan.

## Surprises & Discoveries

- The July mobile architecture calls creation online-only, while its implementation
  plan permits provisional offline creation. This plan makes that choice explicit.
- The queue already has durable queue occurrence/checkpoint behavior; reusing playlist
  entry IDs would break its duplicate guarantees rather than provide them.
- Neither a global availability clock nor comprehensive licensing enforcement is
  implemented by the current v4 catalog reader. They require separate writer work.
- Current catalog reads open their own repeatable-read session, and catalog
  readers live inside the `RelistenApi` web project, which the User Service does
  not reference. Single-snapshot hydration would have required extracting a shared
  library and coupling the schemas; it was dropped on 2026-11-16 instead.
- (2026-11-16) Production's User Service connects as the same `app` role as the
  catalog, so no database-role boundary exists yet. Accepted for now.

## Decision Log

- 2026-11-17, implementation under Alec's authorization: complete the unspecified
  private-contract details without changing its product scope. Names and
  descriptions are trimmed and NFC-normalized before storage, equality or hashing;
  lengths count Unicode scalar values after normalization. Empty descriptions
  become null. NUL and invalid Unicode are rejected. Empty operation batches,
  missing/negative/non-integer `after_revision`, and invalid `view` return
  `422 invalid_playlist_operation`; malformed route IDs return the ordinary
  `404 playlist_not_found`. Missing/deleted move targets take precedence over a
  conflicting new destination ID; an insert into a missing destination reports
  `parent_missing` before checking its created IDs. Domain and body-validation
  problems carry current `history_generation`; framework authentication,
  authorization, content-type and request-size failures may have no generation.
  Frozen v1 hash field order remains explicit in `PlaylistHasher`, independent of
  later C# refactoring. Rank fixtures pin `fractional-indexing@4.0.0` with default
  base-62 digits and the classic A–Z/a–z integer heads.
- 2026-11-06, Alec: support only v4 clients and typed IDs; no older-client or
  backwards-compatibility requirement for playlists.
- 2026-11-06, Alec: retain User Service routes rather than moving playlists into
  the anonymous catalog API.
- 2026-11-06, review: preserve staged private/public/collaboration delivery,
  explicit segments, distinct playlist entries and queue occurrences, server ordering,
  durable receipts, ordinary Realm catalog records and device-global downloads.
  The review initially left product choices open for Alec.
- 2026-11-06, Alec: accept offline creation, unresolved track references, authored
  entry quota counting and mixed-recording ordered segments (decisions 1, 2,
  4 and 5). Removal freshness (decision 3) remains open for explanation/discussion.
- 2026-11-16, Alec: keep user data and catalog conceptually separate so the
  databases can be split later. No cross-schema reads, joins or foreign keys from
  playlist code; drop the single-snapshot hydration rule; mobile hydrates through
  the catalog resolver.
- 2026-11-16, Alec: accept the shared `app` database role for now.
- 2026-11-16, Alec: account deletion is deferred; private playlists do not wait
  for it.
- 2026-11-16, Alec: favorites are dev-alpha only, so the library sync contract may
  break to carry playlist heads.
- 2026-11-16, Alec: every entry belongs to a segment; a lone track is a
  one-track segment. Playlists are archive-only in v1 (no delete endpoint; purge
  only with account deletion). Name max 100 characters, optional description max
  1,000, names not unique per user, no cover art in v1. Track-level shuffle that
  ignores segments is deferred; segment shuffle ships. Library feed playlist
  items carry a summary (name, entry count, archived state, revision) so
  the list renders offline.
- 2026-11-16, Alec: rename the playlist "occurrence" to "entry" and freeze the
  prefixes `playlist_segment_` and `playlist_entry_`, avoiding a clash with
  mobile queue occurrences.
- 2026-11-16, Alec: freeze the private-playlist contracts. Placement never fails
  (missing or wrong-parent anchors count as absent), `GET /v1/playlists` stays for
  non-syncing clients, the library feed is extended additively at version 1, and
  Realm may sort by rank (verified identical to byte order on Realm 20.2.0).
- 2026-11-16, review: amend the frozen contracts. A server-wide
  `history_generation`, rotated by every database restore, forces snapshot
  replacement when restored revisions reuse numbers. During a quota pause,
  count-neutral and deleting operations still sync while additions are held.
- 2026-11-16, review: replace that quota split. Limits now apply to a batch's
  final counts, so a corrective delete travels in the same ordered batch as the
  held insert and cannot tombstone its destination first. Overlay pruning compares
  revisions only within one generation; restore recovery resubmits every
  operation still held locally, and makes no promise for edits already retired.
- 2026-11-16, Alec: a paused outbox too large for one batch keeps its edits and
  asks the user to contact team@relisten.net, rather than prompting them to
  discard additions.
- 2026-11-16, Alec: decision 3 follows favorites — hydrate only missing tracks, no
  proactive re-check; re-resolve a track when its playback fails. Removals are
  very rare.

## Outcomes & Retrospective

The private API is implemented in `RelistenUserService/Playlists/` and
`PlaylistsController`. It includes all delivery-1 routes, authored structure and
tombstones, whole-revision deltas, durable retries, final-state quotas, rank
rebalancing, library discovery and restore generations. The catalog remains
independent. Commits `ccd8ccf88` and `d0c7d3131` contain the contracts/ranks and
the API implementation.

Local verification on 2026-11-17: the solution built; all 223 User Service tests
passed with no skips; the final focused API run passed after extending the
last-entry move scenario. EF reported no pending model changes. Two independent
review passes traced correctness and readability without remaining findings.
See [private playlist operations](../../api/private-playlists.md) for capacity
measurements, restore commands, and rollout/rollback constraints. No deployment
or mobile implementation was performed; the real mobile/API qualification remains
open.

The subsequent adversarial review found a request-size error hidden by the initial
test host: Kestrel's `413` became `500` in the application exception handler. The
fix preserves `413`; the HTTP fixture now starts `AccountsApplication` on real
Kestrel and retains the production authorization policy. The size regression failed
before the fix and passed afterward. Three serializer-only rejection cases were
removed after moving their assertions to HTTP, and fixed hash vectors now protect
the persisted v1 format. The revised User Service suite passed all 223 tests with
no skips. The verification guide now distinguishes tested guarantees, observed
measurements and release/recovery checks still requiring an operator or client.

A production-aware performance review then verified the live database routing,
PgBouncer limits and pod budgets with read-only checks. Commits `5981e5f44` and
`11d3d5be3` separate pooled runtime traffic from direct migrations/refresh locks,
remove read-side library row locks and initialization, reduce unchanged polls to
one service SQL statement, and skip structure loading for metadata-only edits.
Owner write locks still enforce quotas and revision ordering. All 229 User Service
tests passed with no skips; deliberately replacing repeatable read with read
committed failed the three interleaving regressions. Flux commit `d9b6830` on
`codex/v4-playlist-perf` prepares matching routing, network policies and resource
budgets from the current Flux main branch. No production change was applied;
rollout requires a compatible built User Service image digest.

## Review evidence

The November 6 review read server checkout `4dbda9461` and mobile checkout
`16f099e`, the current account/catalog contracts and queue/checkpoint code, and
the later corrections in [Simplify v4 catalog plan](codex://threads/01a1ae23-aa4b-72a1-90d7-bf1441129995).
The initial review was independently validated, then a fresh reader traced this
replacement's success, mixed-result batch and offline recovery paths. That pass
corrected segment-policy assumptions, quota replay wording and membership-grant
binding. The old sync diagram was already correct; its ambiguous cursor prose
was a documentation gap, not evidence of an implemented data-loss bug.
