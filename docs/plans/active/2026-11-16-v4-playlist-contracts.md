# Private playlist contracts

Status: frozen 2026-11-16 by Alec, after a fresh-reader review; restore and
quota recovery amended the same day after a second review. Change it only
through the plan's Decision Log. This document freezes the wire contracts and
server invariants for delivery 1 (private playlists) of
[Deliver playlists using v4 catalog identities](2026-11-06-v4-playlists.md). The
plan owns product decisions and rationale; this document owns shapes, statuses and
ordering rules. Publication and collaboration add to these contracts later; nothing
here is dormant schema for them.

IDs in examples are illustrative TypeIDs (`<prefix>_<26 lowercase base32 chars>`).

## Conventions

These match the favorites contracts already served by `RelistenUserService`.

- Host and version: `https://accounts.relisten.net/v1/...`, authenticated with the
  existing library access policy. The playlist contract version starts at `1`.
- JSON is `snake_case`. Unknown members are rejected (`UnmappedMemberHandling.Disallow`).
  Absent and `null` differ only where a field says so.
- Errors are RFC 9457 problem bodies with a stable `code` extension, as
  `LibraryController.LibraryProblem` returns today. The framework's request-size
  `413` is the one exception and carries no `code`, as with favorites.
- A playlist the caller cannot access returns `404 playlist_not_found`, whether it
  is missing or owned by someone else.
- Every ID is a complete typed string. Client-created domain IDs are UUIDv7 and
  globally unique. `track_id` is a catalog `TrackId`, validated only syntactically.
- Every playlist and library response also carries `history_generation` (see
  History generation). The examples below omit it for brevity.
- Receipts are scoped to the account that created them. Another account reusing an
  `operation_id` or `command_id` gets `409 idempotency_conflict`, never the stored
  result.

| Type | Prefix | Created by |
| --- | --- | --- |
| Playlist | `playlist_` | client |
| Segment | `playlist_segment_` | client |
| Entry | `playlist_entry_` | client |
| Operation | `playlist_operation_` | client |
| Command | `command_` (existing) | client |
| Track | `track_` (existing catalog ID) | catalog |

## Data model

A playlist holds an ordered list of **segments**. A segment holds an ordered list of
**entries**, and every entry belongs to exactly one segment. A lone track is a
one-track segment. An entry is one authored position: the same `track_id` may
appear in many entries. Segments have no `kind`; the plan's earlier `kind` field
and `initial_segments` name are dropped.

- An empty playlist is valid. An empty live segment is not: removing or moving a
  segment's last entry deletes that segment in the same operation.
- Segments and entries keep their IDs across moves. An entry's `track_id` never
  changes; replacing a track is delete plus insert.
- Deleted segments and entries remain as tombstones for the playlist's lifetime. An
  ID that exists anywhere, live or tombstoned, can never be created again.
- Order is defined only by server-assigned ranks (see Ranks). Clients send anchors,
  never ranks.

### Limits

| Limit | Value | Error |
| --- | --- | --- |
| Playlists per user, including archived | 250 | `422 quota_exceeded` |
| Live entries per playlist | 5,000 | `422 quota_exceeded` |
| Live entries across all owned playlists, including archived | 25,000 | `422 quota_exceeded` |
| Operations per batch | 500 | `422 limit_exceeded` |
| Request body (batch or create) | 2 MiB | `413` |
| `name` | 1–100 characters after trimming | `422 invalid_playlist_operation` |
| `description` | 0–1,000 characters | `422 invalid_playlist_operation` |

Names are trimmed and NFC-normalized. They need not be unique. An empty
description is stored as `null`.

## Endpoints

### Create a playlist

`POST /v1/playlists`

```json
{
  "contract_version": 1,
  "command_id": "command_06ehx2v8q4f1k9m3t5w7z0r2yb",
  "playlist_id": "playlist_06ehx2v8q3c8n2p4r6t8v0x2za",
  "metadata": { "name": "Cornell + friends", "description": null },
  "segments": [
    {
      "segment_id": "playlist_segment_06ehx2v8q5a1c3e5g7j9k1m3np",
      "entries": [
        { "entry_id": "playlist_entry_06ehx2v8q6b2d4f6h8k0m2p4rq", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4a" },
        { "entry_id": "playlist_entry_06ehx2v8q6c3e5g7j9m1p3r5ts", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4b" }
      ]
    }
  ]
}
```

`segments` may be empty. Each listed segment needs at least one entry, and the
array order is the initial order. `description` may be omitted or `null`.

Responses:

- `201` on first success and `200` on an exact replay of the same `command_id`.
  Both return the same body:

  ```json
  { "contract_version": 1, "playlist_id": "playlist_06ehx2v8q3c8n2p4r6t8v0x2za", "revision": 1 }
  ```

- `409 idempotency_conflict` if `command_id` was used with a different payload.
- `409 id_conflict` if the playlist, segment or entry IDs already exist. The body
  lists `conflicting_ids`.
- `422 quota_exceeded`, `422 invalid_playlist_operation`, `422 invalid_contract_version`.

Creating a playlist produces revision `1`. Mobile sends dependent operations only
after this command is acknowledged; a lost response is retried with the identical
body.

### List playlists

`GET /v1/playlists?view=active` (or `view=archived`; default `active`)

For clients that want the current list without running library sync, such as a
web page or a one-off screen (Alec, 2026-11-16). Syncing clients use the library
feed instead.

```json
{
  "contract_version": 1,
  "playlists": [
    {
      "playlist_id": "playlist_06ehx2v8q3c8n2p4r6t8v0x2za",
      "name": "Cornell and friends",
      "entry_count": 2,
      "archived": false,
      "revision": 43,
      "updated_at": "2026-11-16T19:40:03Z"
    }
  ]
}
```

Items use the same summary shape as the library feed, sorted by `updated_at`
descending. There is no paging, because the 250-playlist quota bounds the list.
The response is `Cache-Control: private, no-store`.

### Read a snapshot

`GET /v1/playlists/{playlist_id}/snapshot`

```json
{
  "contract_version": 1,
  "playlist_id": "playlist_06ehx2v8q3c8n2p4r6t8v0x2za",
  "revision": 41,
  "archived": false,
  "role": "owner",
  "metadata": { "name": "Cornell + friends", "description": null },
  "segments": [
    {
      "segment_id": "playlist_segment_06ehx2v8q5a1c3e5g7j9k1m3np",
      "rank": "a0",
      "entries": [
        { "entry_id": "playlist_entry_06ehx2v8q6b2d4f6h8k0m2p4rq", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4a", "rank": "a0" },
        { "entry_id": "playlist_entry_06ehx2v8q6c3e5g7j9m1p3r5ts", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4b", "rank": "a1" }
      ]
    }
  ],
  "created_at": "2026-11-16T18:02:11Z",
  "updated_at": "2026-11-16T19:40:03Z"
}
```

Arrays are in rank order. The snapshot contains only live segments and entries,
and no catalog data. `role` is always `owner` in delivery 1. The owner can read an
archived playlist's snapshot. The response is `Cache-Control: private, no-store`.

A device that has no local copy of a playlist always starts from its snapshot,
never from changes.

### Read changes

`GET /v1/playlists/{playlist_id}/changes?after_revision=41`

```json
{
  "contract_version": 1,
  "playlist_id": "playlist_06ehx2v8q3c8n2p4r6t8v0x2za",
  "head_revision": 43,
  "revisions": [
    {
      "revision": 42,
      "kind": "content",
      "changes": [
        { "type": "entry_upserted", "entry_id": "playlist_entry_06ehx2v8q9d4f6h8k0m2p4r6tv", "segment_id": "playlist_segment_06ehx2v8q5a1c3e5g7j9k1m3np", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4c", "rank": "a0V" }
      ]
    },
    {
      "revision": 43,
      "kind": "content",
      "changes": [
        { "type": "entry_deleted", "entry_id": "playlist_entry_06ehx2v8q6b2d4f6h8k0m2p4rq" },
        { "type": "metadata_updated", "name": "Cornell and friends", "description": "Spring '77" }
      ]
    }
  ],
  "next_after_revision": 43,
  "has_more": false
}
```

Changes are state, not operations, so a client applies them without re-running
operation semantics:

| `type` | Fields | Meaning |
| --- | --- | --- |
| `metadata_updated` | `name`, `description` | Full current metadata. |
| `segment_upserted` | `segment_id`, `rank` | The segment exists at this rank (insert, move or rebalance). |
| `segment_deleted` | `segment_id` | The segment and every entry still in it are gone. No separate `entry_deleted` is emitted for those entries. |
| `entry_upserted` | `entry_id`, `segment_id`, `track_id`, `rank` | The entry exists here (insert, move or rebalance). |
| `entry_deleted` | `entry_id` | The entry is gone. |

Within a revision, changes are listed in apply order: `segment_upserted` before
any `entry_upserted` that names that segment, and an entry's move out of a segment
before that segment's `segment_deleted`. Clients apply them in array order.

`kind` is `content` for edits and `rebalance` for server rank rewrites. A rebalance
changes only ranks and is complete on its own.

Paging: a page holds whole revisions only, never part of one. It is capped at 500
changes, except that a single revision larger than that is returned alone. Clients
continue from `next_after_revision` while `has_more` is true.

| `after_revision` (head is 43) | Result |
| --- | --- |
| 41 | Revisions 42 and 43. |
| 43 | `200` with empty `revisions`. |
| 44 or more | `410 snapshot_required`. The server history is behind the client, for example after a backup restore. |
| 0 | `410 snapshot_required`. Revision 1 (create) has no change envelope; start from a snapshot. |

`410 snapshot_required` is also returned if `after_revision` is older than retained
history. Nothing is compacted in delivery 1, but clients must handle it (see Client
model). The plan's earlier `409 snapshot_required` is replaced by `410`, matching
the library feed's `410 sync_cursor_expired`.

### History generation

Revision numbers alone cannot detect a restore. Suppose device A holds revision 43
containing entry X, the server is restored to revision 40, and device B then makes
three edits that bring the server back to 43. A would pull after 43, get an empty
page, keep X and miss B's edits. Every later delta would apply to the wrong base.

So the User Service keeps one server-wide **history generation**: an opaque string
stored in a single-row `user_data.history_generation` table. It never changes in
normal operation.

- Every playlist response (create, snapshot, changes, batch, archive and list) and
  every library response (snapshot and changes) includes `history_generation`.
- Mobile stores the generation with each playlist base and with its library cursor.
  Before applying any response, it compares the generation. On a mismatch it
  applies nothing from that response and runs restore recovery (see Client model),
  whatever the revision numbers say.
- The library cursor also embeds the generation, so the server answers a cursor
  from another generation with `410 sync_cursor_expired`. This also protects
  favorites, which share the same revision-reuse problem.
- **Restoring the User Service database from any backup or point in time must
  rotate the generation** (write a new UUIDv7) before the service takes traffic.
  This step belongs in the account recovery runbook. A restore that skips it is
  unsafe for every syncing client.

Test: revision reuse after a restore, before an offline device reconnects. The
device must replace its base by snapshot even though the revisions compare equal.

### Apply operations

`POST /v1/playlists/{playlist_id}/operations:batch`

```json
{
  "contract_version": 1,
  "operations": [
    {
      "operation_id": "playlist_operation_06ehx3a1b2c3d4e5f6g7h8j9ka",
      "base_revision": 41,
      "operation_type": "insert_entries",
      "payload": {
        "segment_id": "playlist_segment_06ehx2v8q5a1c3e5g7j9k1m3np",
        "after_entry_id": "playlist_entry_06ehx2v8q6b2d4f6h8k0m2p4rq",
        "before_entry_id": "playlist_entry_06ehx2v8q6c3e5g7j9m1p3r5ts",
        "entries": [
          { "entry_id": "playlist_entry_06ehx2v8q9d4f6h8k0m2p4r6tv", "track_id": "track_3k9w2m7q8x4p1n6r5t0v2y8z4c" }
        ]
      }
    }
  ]
}
```

`base_revision` is required and must be a non-negative integer, but it is
diagnostic only. It records the revision the client had when it authored the
operation, and the server never rejects or reorders by it.

Response `200`, one result per operation in request order:

```json
{
  "contract_version": 1,
  "head_revision": 42,
  "results": [
    { "operation_id": "playlist_operation_06ehx3a1b2c3d4e5f6g7h8j9ka", "status": "applied", "revision": 42 }
  ]
}
```

- `revision` is the content revision the operation produced, or `null` when it
  changed nothing.
- `head_revision` is the playlist's head after the request. It may exceed every
  result `revision` because of rebalances or other devices' edits. For a batch made
  entirely of replays, it is the current head.
- Results never carry structure. Clients learn the canonical state by pulling
  changes.

Whole-batch failures apply nothing:

| Status | `code` | When |
| --- | --- | --- |
| 404 | `playlist_not_found` | Missing or not accessible. |
| 409 | `playlist_archived` | The playlist is archived. Every operation type is refused, including `update_metadata`. Replays are still answered (see Transactions). |
| 409 | `idempotency_conflict` | An `operation_id` was used before with a different payload, playlist or account. Body lists `conflicting_operation_ids`. |
| 422 | `invalid_contract_version` | Unsupported version. |
| 422 | `invalid_playlist_operation` | Malformed envelope or payload, such as a wrong prefix, an unknown type or field, empty `entries`, or a duplicate `operation_id` or created ID within the request. |
| 422 | `quota_exceeded` | Applying the batch would cross a limit (see Quota preflight). |
| 422 | `limit_exceeded` | More than 500 operations. |

## Operations

Seven operation types.

### Placement anchors

Inserts and moves take optional `after_*_id` and `before_*_id` anchors among the
destination parent's children: segments for segment placement, entries in the
destination segment for entry placement. The server temporarily removes the moving
object, resolves anchors against the state before this operation tombstones
anything, then applies the first matching rule. An anchor equal to the moving
object is `invalid_playlist_operation`.

Placement never fails (Alec, 2026-11-16). Concurrent edits from other devices can
move or delete an anchor, and an offline addition must still land somewhere:

- An anchor that is tombstoned, or that now sits in a different parent, counts as
  absent.
- If both anchors are present but `after` no longer comes before `before`, only
  `after` counts.

Then:

1. Both present: place immediately after `after`. They need not be adjacent; if
   another device inserted items between them, the new item goes right after
   `after`, ahead of those items.
2. One present: place immediately after `after`, or immediately before `before`.
3. None present (none sent, or none survive): append at the end of the parent.

### Operation types

| `operation_type` | Payload | Effect |
| --- | --- | --- |
| `update_metadata` | `name?`, `description?` | Absent = unchanged. `description: null` clears it. `name` cannot be null. |
| `insert_segment` | `segment_id`, `after_segment_id?`, `before_segment_id?`, `entries` (1–5,000 `{entry_id, track_id}`) | New segment with entries in array order. Covers "add a track" (one entry) and "add these tracks from a show". |
| `move_segment` | `segment_id`, `after_segment_id?`, `before_segment_id?` | Reposition a segment with its entries. |
| `delete_segment` | `segment_id` | Tombstone the segment and all its live entries. |
| `insert_entries` | `segment_id`, `after_entry_id?`, `before_entry_id?`, `entries` (1–5,000) | Add entries into an existing segment, contiguous, in array order. |
| `move_entry` | `entry_id`, `destination` | Move one entry. See below. |
| `delete_entry` | `entry_id` | Tombstone the entry. If it was its segment's last live entry, also tombstone the segment. |

`move_entry` takes exactly one of two destinations:

```json
{ "entry_id": "playlist_entry_…", "destination": { "segment_id": "playlist_segment_…", "after_entry_id": "playlist_entry_…", "before_entry_id": null } }
```

```json
{ "entry_id": "playlist_entry_…", "destination": { "new_segment_id": "playlist_segment_…", "after_segment_id": "playlist_segment_…", "before_segment_id": null } }
```

The first moves into an existing segment. The second pulls the entry out into a
new one-entry segment placed among the segments. If the entry leaves its old
segment empty, that segment is tombstoned in the same operation, after anchors are
resolved, so the old segment is still a valid anchor for the new one.

A new segment is placed between whole segments. Splitting a block in place, with
the pulled-out track landing between the two halves, is not expressible in v1; it
would need a later `split_segment` operation.

### Results

| `status` | Accepted? | When |
| --- | --- | --- |
| `applied` | yes | State changed; `revision` is set. |
| `no_op` | yes | Valid but changed nothing, such as a move to the same place or metadata equal to the current value. |
| `target_deleted` | yes | The segment or entry being moved, updated or deleted is tombstoned. Nothing changes. |
| `target_missing` | yes | That segment or entry never existed, for example because the operation that would have created it was rejected. Nothing changes. |
| `parent_missing` | no | The destination segment is missing or tombstoned. The server never recreates it. |
| `id_conflict` | no | A created ID already exists, live or tombstoned, anywhere. |

Rejected operations do not stop the batch. Later operations run against the state
left by earlier ones, in request order. Each applied operation produces exactly one
content revision; accepted no-ops and rejections produce none.

An exact replay of an `operation_id` returns its original stored result and
`revision`, even when it arrives inside a batch with new operations.

**Client handling of rejections.** `parent_missing` and
`id_conflict` mean the operation's intent was not applied. Mobile removes it from
the outbox, drops its local overlay, and shows a non-blocking notice for added
tracks that did not land ("2 tracks couldn't be added to Cornell + friends") with
a one-tap retry that re-adds them at the end as new entries. Accepted statuses
need no notice.

### Quota preflight

Before applying anything, the server dry-runs the batch's new operations in
submitted order against the current state. Exact replays count nothing. Only
operations that would apply count, and deletes earlier in the batch free room for
later inserts. If the dry run crosses any limit, the whole batch fails with
`422 quota_exceeded` and nothing is applied.

Retrying an unchanged batch cannot clear a quota failure. Mobile then splits the
playlist's outbox:

- **Held:** operations that add entries (`insert_segment`, `insert_entries`), plus
  any later operation that references an ID one of them creates.
- **Free to send:** everything else, in their original order. This includes
  `delete_entry`, `delete_segment`, moves of existing items and `update_metadata`.
  None of these can cross a limit.

Mobile keeps sending free operations, including deletions the user makes to fix
the problem. It applies their canonical changes, rebuilds the overlay with the
held additions still on top, and then retries the held additions. It also retries
after any deletion in another playlist, since the account-wide limit is shared.
The UI shows the limit until the additions land or the user discards them. There
is no playlist delete in v1, so freeing space means deleting entries.

Test: on one device, an insert fails quota, the user deletes an existing entry,
the deletion syncs, and the original pending insert then applies without being
re-authored.

## Ranks

Ranks are base-62 strings over `0-9A-Za-z` (ASCII order), using the algorithm from
the `fractional-indexing` npm package by Rocicorp (`generateKeyBetween`). The server
ports it to C# with fixtures generated from the npm package, so both sides agree on
every key they generate.

- Segment ranks order segments within a playlist. Entry ranks order entries within
  their segment. Only live items are ranked.
- PostgreSQL columns are `text COLLATE "C"` with a unique constraint on
  `(parent, rank)` among live rows, so order is plain byte order.
- Mobile compares ranks with plain JavaScript string comparison (`<`), which equals
  byte order for ASCII. Realm `sorted()` on a rank property gives the same order:
  on 2026-11-16, Realm 20.2.0 under Node sorted 20,000 keys over this alphabet
  (every two-character pair plus random keys up to 12 characters) identically to
  byte order. Screens may query Realm sorted by rank. Keep that comparison as a
  mobile regression test so a Realm upgrade cannot silently change it.
- Maximum rank length is 48 characters. If placing an item would need a longer
  rank, the server first rebalances that parent's live children to evenly spaced
  short keys as a `rebalance` revision, then applies the operation as the next
  content revision with a short rank. A stored rank never exceeds 48 characters.
  The operation's result `revision` is the content revision.
- Mobile uses the npm package locally for optimistic placement. Its local ranks are
  display-only and are replaced by the server's when the change arrives.

## Archive

`PUT /v1/playlists/{playlist_id}/archive-state`

```json
{ "contract_version": 1, "command_id": "command_06ehx4…", "archived": true }
```

Response `200`: `{ "contract_version": 1, "playlist_id": "playlist_…", "archived": true, "changed": true }`.

- An exact replay returns the stored result. Changed reuse of `command_id` is
  `409 idempotency_conflict`.
- Archive is online-only and never queued offline.
- It does not change the content revision; it changes the library summary.
- Archived playlists keep counting toward quotas. There is no delete endpoint in v1.

While a playlist is archived, other devices' pending operations for it stay queued
and get `409 playlist_archived`. Mobile shows the playlist as archived, keeps the
pending edits, and sends them after an unarchive. A user who does not want them can
discard them.

## Library feed (additive, still version 1)

Playlist summaries are added to the existing library feed without a version bump
(Alec, 2026-11-16). Routes, the `contract_version: 1` value, favorite shapes and
the favorite mutation endpoint are unchanged. `library_revision` is the existing
per-user counter on `user_data.library_states`. Favorites are dev-alpha only;
current alpha builds reject unknown change types and must update (see below).

The snapshot gains a `playlists` array of summaries, active and archived:

```json
{
  "contract_version": 1,
  "library_revision": 118,
  "next_cursor": "opaque",
  "favorites": [ "…unchanged…" ],
  "playlists": [
    {
      "playlist_id": "playlist_06ehx2v8q3c8n2p4r6t8v0x2za",
      "name": "Cornell and friends",
      "entry_count": 2,
      "archived": false,
      "revision": 43,
      "updated_at": "2026-11-16T19:40:03Z"
    }
  ]
}
```

Changes gain one `change_type`, `playlist_summary`, carrying a `playlist` object.
Favorite changes keep their flat `favorite_id` and `target`; those two fields are
absent on playlist changes:

```json
{
  "contract_version": 1,
  "library_revision": 121,
  "changes": [
    { "change_id": "library_change_…", "revision": 120, "change_type": "favorite_added", "favorite_id": "favorite_…", "target": { "kind": "track", "id": "track_…" }, "changed_at": "…" },
    { "change_id": "library_change_…", "revision": 121, "change_type": "playlist_summary", "playlist": { "playlist_id": "playlist_…", "name": "…", "entry_count": 3, "archived": false, "revision": 44, "updated_at": "…" }, "changed_at": "…" }
  ],
  "next_cursor": "opaque",
  "has_more": false
}
```

- In C#, `FavoriteLibraryChange` becomes a library change record with optional
  `favorite_id`/`target` and optional `playlist`, and the snapshot record gains
  `playlists`. The favorite types can be renamed to `Library*` in the same change.
- `playlist_summary` is the full current summary (an upsert). Every committed
  create, archive change or content batch that changes a playlist writes one
  summary change for it in the same transaction: one per playlist per transaction,
  not one per operation.
- Mobile compares a summary's `revision` with its local `applied_revision` for that
  playlist. An unknown playlist is fetched by snapshot; a known one that is behind
  pulls changes.
- `playlist_removed` is reserved for later deliveries (lost access, account
  deletion) and is not emitted in delivery 1. Clients must ignore unknown
  `change_type` values from now on. Mobile's `favorite_sync_contract.ts` currently
  throws on them and must change.
- The existing page size (500) and `410 sync_cursor_expired` behavior are unchanged.

## Transactions and locks

Every playlist write (create, batch, archive) runs in one transaction. Locks follow
the favorites order (`FavoriteMutationService`):

1. Transaction advisory locks on every submitted `operation_id` or `command_id` and
   every ID the request would create, in sorted order. This turns concurrent reuse
   of an ID into a clean `idempotency_conflict` or `id_conflict` rather than a
   unique-key violation.
2. The owner's `user_data.library_states` row, `FOR UPDATE`. This serializes quota
   checks and assigns the library revision. Favorites take the same lock, so one
   user's favorite and playlist writes serialize with each other.
3. The playlist row, `FOR UPDATE` (not for create).

Then, in this order:

1. Check access (`404`).
2. Look up receipts. Conflicting reuse fails the batch with `409 idempotency_conflict`.
   If every operation is an exact replay, return the stored results now, even if the
   playlist is archived.
3. Check archive state (`409 playlist_archived`).
4. Run quota preflight.
5. Apply the new operations in order, alongside replayed results.
6. Write revision changes, receipts and one library summary change.
7. Commit. An infrastructure failure rolls back the whole request.

Collaboration later locks other members' library states after the playlist, in ID
order. Account deletion later adds its fence ahead of the library state lock. Both
attach to this single write entrypoint.

## Receipts and hashing

Each create command, archive command and operation stores a receipt: account, ID,
playlist, SHA-256 semantic hash and stored result. Receipts are kept for the
playlist's lifetime.

The hash is SHA-256 over UTF-8 lines joined by `\n`, like `FavoriteMutationHasher`.
Because playlist payloads contain free text, every value line is tagged:

| Line | Content |
| --- | --- |
| 1 | Domain: `relisten-playlist-operation-v1`, `relisten-playlist-create-v1` or `relisten-playlist-archive-v1` |
| 2 | Contract version |
| 3 | Playlist ID |
| 4 | `operation_type`, or `create` / `archive` for commands |
| 5+ | One line per payload field, in the declaration order of the typed payload record |

Field lines are encoded as follows:

- An absent field is written `A`.
- A `null` field is written `N`.
- A value is written `V:` followed by its JSON encoding. Strings are first trimmed
  and NFC-normalized exactly as stored, then encoded as JSON string literals, so
  embedded newlines and `|` are escaped.
- An array is written as a line `L:<count>`, then one line per element. An
  `{entry_id, track_id}` element is `entry_id|track_id`. A create segment is
  `S:<segment_id>|<entry count>`, followed by its entries.
- A `move_entry` destination is `D:segment|<segment_id>`, followed by its two anchor
  lines, or `D:new_segment|<new_segment_id>`, followed by its two anchor lines.
- `operation_id`, `command_id` and `base_revision` are excluded.

Clients never compute or send hashes. Tests cover absent vs `null`, literal `A`,
`N` and `V:` strings, embedded newlines, normalization, array order and every
operation type.

## Client model

These are the server's promises that mobile relies on, and the client behavior
they assume.

- Mobile keeps, per playlist, a **canonical base** (the last fully applied server
  state and its `applied_revision`) and an ordered **pending overlay** of unsent or
  unacknowledged operations. The UI shows the base with the overlay replayed on
  top. The overlay is rebuilt after every pull.
- `applied_revision` advances only by applying changes. If a device at 41 gets
  result revision 43 for its own operation while another device made 42, it still
  pulls after 41.
- An operation stays in the overlay until a pull reaches its result `revision`.
  Operations that were accepted without a revision leave the overlay when their
  result arrives.
- Each changes page, including its cursor, is applied in one Realm transaction.
- After a snapshot at revision R, drop overlay operations whose result revision is
  at most R; they are already in the snapshot. Keep the rest and replay them.
- **Restore recovery** runs on a `history_generation` mismatch, or on a `410`
  showing the client ahead of the server. Mobile discards the library cursor and
  every playlist base, and fetches a fresh library snapshot and fresh playlist
  snapshots. Acknowledged operations not reflected in the new snapshot may have
  been lost with the server's receipts. Mobile moves them back to unsent and
  resubmits them with their original `operation_id`s. A surviving receipt returns
  its stored result; otherwise the operation applies fresh, and any IDs that
  already exist return `id_conflict` and are dropped.
- Tracks missing from Realm are hydrated through `POST /api/v4/catalog/resolve` in
  chunks of 500. Hydration never blocks applying changes and never deletes entries.
